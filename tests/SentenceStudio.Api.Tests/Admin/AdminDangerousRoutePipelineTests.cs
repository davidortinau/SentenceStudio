using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Admin;
using SentenceStudio.Api.Admin.Persistence;
using SentenceStudio.Api.Tests.Coach;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Admin;

/// <summary>
/// A dangerous admin route, end to end, before any real one exists.
/// </summary>
/// <remarks>
/// <para>
/// Phase 0 ships the pieces a dangerous route is built from (the admin group, the password-again
/// filter, the audit target) and no route that uses them, so nothing in the shipped route table
/// drives them through a pipeline. This host maps one learner-content-shaped route with the
/// production registration (<c>AddAdminPortal</c>), middleware (<c>UseAdminPortal</c>) and group
/// (<c>MapAdminGroup</c>), behind real JWT authentication, and proves the first such route added
/// later inherits all of it: 404 for everyone else, refused without the proof, recorded with the
/// learner it names.
/// </para>
/// <para>
/// A small host of its own rather than the API's <c>Program</c>, because the API's route table is
/// fixed at startup and has no dangerous route to drive yet.
/// </para>
/// </remarks>
public sealed class AdminDangerousRoutePipelineTests : IAsyncLifetime
{
    private const string LearnerId = "7384f806-0000-4000-8000-000000000001";
    private const string Route = $"/api/admin/test-learners/{LearnerId}/words";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sentencestudio_admin_pipe_{Guid.NewGuid():N}.db");
    private readonly string _auditDbPath = Path.Combine(Path.GetTempPath(), $"sentencestudio_admin_pipe_audit_{Guid.NewGuid():N}.db");
    private readonly TestTimeProvider _time = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly TestOptionsMonitor<AdminOptions> _adminList = new(new AdminOptions());
    private WebApplication _app = default!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddDbContext<ApplicationDbContext>(options => options
            .UseSqlite($"Data Source={_dbPath}")
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        builder.Services.AddDataProtection();
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options => options.TokenValidationParameters = TestJwtGenerator.CreateValidationParameters());
        builder.Services.AddAuthorization();

        // Production registration, then the same two swaps AdminApiFactory makes.
        builder.Services.AddAdminPortal(builder.Configuration);
        builder.Services.RemoveAll<DbContextOptions<AdminDbContext>>();
        builder.Services.RemoveAll<IDbContextOptionsConfiguration<AdminDbContext>>();
        builder.Services.AddDbContext<AdminDbContext>(options => options.UseSqlite($"Data Source={_auditDbPath}"));
        builder.Services.RemoveAll<IOptionsMonitor<AdminOptions>>();
        builder.Services.AddSingleton<IOptionsMonitor<AdminOptions>>(_adminList);
        builder.Services.RemoveAll<TimeProvider>();
        builder.Services.AddSingleton<TimeProvider>(_time);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseAdminPortal();

        _app.MapAdminGroup()
            .MapGet("/test-learners/{learnerId}/words", (string learnerId) => Results.Ok(new { learnerId, words = 3 }))
            .RequireAdminStepUp()
            .WithAdminAction("test.learner.words.read", AdminTargetKinds.Learner, "learnerId");

        using (var scope = _app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreatedAsync();
            await scope.ServiceProvider.GetRequiredService<AdminDbContext>().Database.EnsureCreatedAsync();
        }

        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _auditDbPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private async Task<ApplicationUser> SeedAsync(string email, bool admin)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        db.Users.Add(user);

        if (admin)
        {
            var role = await db.Roles.SingleOrDefaultAsync(r => r.NormalizedName == "ADMIN");
            if (role is null)
            {
                role = new IdentityRole { Id = Guid.NewGuid().ToString(), Name = "Admin", NormalizedName = "ADMIN" };
                db.Roles.Add(role);
            }

            db.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.Id });
            _adminList.Set(new AdminOptions { Emails = [.. _adminList.CurrentValue.Emails, email] });
        }

        await db.SaveChangesAsync();
        return user;
    }

    private HttpClient ClientFor(ApplicationUser? user)
    {
        var client = _app.GetTestClient();
        if (user is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", TestJwtGenerator.GenerateToken(userId: user.Id, email: user.Email));
        }

        return client;
    }

    /// <summary>The proof <paramref name="user"/> would hold after confirming their password now.</summary>
    private string ProofFor(ApplicationUser user) =>
        _app.Services.GetRequiredService<AdminStepUpService>()
            .Issue(new AdminActor(
                AdminActorKind.Admin, user.Id, user.Email, null, AdminStepUpService.DigestOf(user.SecurityStamp)))!
            .Token;

    private async Task<List<AdminAuditEntry>> AuditAsync()
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AdminDbContext>()
            .AuditEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
    }

    [Fact]
    public async Task A_dangerous_route_is_404_for_everyone_but_an_admin_even_with_a_proof()
    {
        var learner = await SeedAsync("learner@admin-tests.local", admin: false);
        var admin = await SeedAsync("captain@admin-tests.local", admin: true);
        using var anonymous = ClientFor(null);
        using var learnerClient = ClientFor(learner);
        learnerClient.DefaultRequestHeaders.Add(AdminStepUpService.HeaderName, ProofFor(admin));

        (await anonymous.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await learnerClient.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a proof is not a credential: without admin access it opens nothing");
        (await AuditAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Without_the_password_again_an_admin_is_refused_and_the_attempt_names_the_learner()
    {
        var admin = await SeedAsync("captain@admin-tests.local", admin: true);
        using var client = ClientFor(admin);

        var response = await client.GetAsync(Route);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var problem = JsonDocument.Parse(body);
        problem.RootElement.GetProperty("code").GetString().Should().Be(AdminFailureCodes.StepUpRequired);
        body.Should().NotContain("words");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();

        var row = (await AuditAsync()).Should().ContainSingle().Subject;
        row.Action.Should().Be("test.learner.words.read");
        row.ActorId.Should().Be(admin.Id);
        row.TargetKind.Should().Be(AdminTargetKinds.Learner);
        row.TargetId.Should().Be(LearnerId);
        row.Outcome.Should().Be(AdminAuditOutcome.Failed);
        row.StatusCode.Should().Be(403);
        row.FailureCode.Should().Be(AdminFailureCodes.StepUpRequired);
    }

    [Fact]
    public async Task With_a_live_proof_the_read_happens_and_is_recorded_and_it_stops_when_the_proof_expires()
    {
        var admin = await SeedAsync("captain@admin-tests.local", admin: true);
        using var client = ClientFor(admin);
        client.DefaultRequestHeaders.Add(AdminStepUpService.HeaderName, ProofFor(admin));

        var allowed = await client.GetAsync(Route);

        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await allowed.Content.ReadAsStringAsync()).Should().Contain(LearnerId);
        var read = (await AuditAsync()).Should().ContainSingle().Subject;
        read.Outcome.Should().Be(AdminAuditOutcome.Succeeded);
        read.TargetId.Should().Be(LearnerId);

        _time.Advance(AdminStepUpService.Lifetime);

        (await client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Another_admins_proof_does_not_open_the_route()
    {
        var first = await SeedAsync("captain@admin-tests.local", admin: true);
        var second = await SeedAsync("mate@admin-tests.local", admin: true);
        using var client = ClientFor(second);
        client.DefaultRequestHeaders.Add(AdminStepUpService.HeaderName, ProofFor(first));

        (await client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_proof_dies_with_the_password_it_was_earned_with()
    {
        var admin = await SeedAsync("captain@admin-tests.local", admin: true);
        using var client = ClientFor(admin);
        client.DefaultRequestHeaders.Add(AdminStepUpService.HeaderName, ProofFor(admin));
        (await client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.OK);

        // What Identity does on every password change or reset.
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Users.Where(u => u.Id == admin.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.SecurityStamp, Guid.NewGuid().ToString()));
        }

        (await client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
