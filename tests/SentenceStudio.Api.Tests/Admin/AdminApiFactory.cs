using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Admin;
using SentenceStudio.Api.Admin.Persistence;
using SentenceStudio.Api.Tests.Coach;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Admin;

/// <summary>
/// The real API host with real JWT authentication, for the admin routes.
/// </summary>
/// <remarks>
/// <para>
/// Nothing about the gate is replaced: the policy, the evaluator's database read, the 404 result
/// handler and the audit middleware are the production ones. Accounts and role rows are written to
/// the host's own Identity tables, the way <c>scripts/admin-grant.sh</c> writes them.
/// </para>
/// <para>
/// The audit log runs on SQLite by default so the whole family runs on a machine with no server.
/// Pass a PostgreSQL connection string to run it on the provider it ships on; that database must
/// already be migrated. <see cref="AdminList"/> stands in for <c>Admin:Emails</c> so a test can
/// change the list while the host is running.
/// </para>
/// </remarks>
internal sealed class AdminApiFactory : WebApplicationFactory<Program>
{
    public const string Password = "AdminTest1234!";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sentencestudio_admin_{Guid.NewGuid():N}.db");
    private readonly string _auditDbPath = Path.Combine(
        Path.GetTempPath(), $"sentencestudio_admin_audit_{Guid.NewGuid():N}.db");
    private readonly string? _adminConnectionString;

    public AdminApiFactory(string? adminConnectionString = null)
    {
        _adminConnectionString = adminConnectionString;
    }

    /// <summary>The live <c>Admin:Emails</c> list.</summary>
    public TestOptionsMonitor<AdminOptions> AdminList { get; } = new(new AdminOptions());

    /// <summary>The host's clock. Moves only when a test moves it.</summary>
    public TestTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:sentencestudio",
            TestApiHostConfigurator.DummyPostgresConnectionString);
        if (_adminConnectionString is not null)
        {
            builder.UseSetting("ConnectionStrings:admin", _adminConnectionString);
        }

        builder.UseSetting("Database:SkipMigrateOnStartup", "true");
        builder.UseSetting("Auth:SeedDevTestAccounts", "false");
        builder.UseSetting("Auth:EnableDevAuthFallback", "false");
        builder.UseSetting("Jwt:SigningKey", TestJwtGenerator.TestSigningKeyValue);
        builder.UseSetting("Jwt:Issuer", TestJwtGenerator.TestIssuer);
        builder.UseSetting("Jwt:Audience", TestJwtGenerator.TestAudience);
        builder.UseSetting("AI:OpenAI:ApiKey", "test-openai-key");
        builder.UseSetting("ElevenLabsKey", "test-elevenlabs-key");
        TestApiHostConfigurator.RelaxAuthRateLimits(builder);

        builder.ConfigureServices((context, services) =>
        {
            TestApiHostConfigurator.ConfigureSqliteDatabaseAndSync(services, _dbPath);
            TestApiHostConfigurator.AddStubChatClientWhenAiUnconfigured(services, context.Configuration);

            if (_adminConnectionString is null)
            {
                services.RemoveAll<DbContextOptions<AdminDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AdminDbContext>>();
                services.AddDbContext<AdminDbContext>(options => options.UseSqlite($"Data Source={_auditDbPath}"));
            }

            services.RemoveAll<IOptionsMonitor<AdminOptions>>();
            services.AddSingleton<IOptionsMonitor<AdminOptions>>(AdminList);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);

            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            });
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        TestApiHostConfigurator.InitializeSqliteDatabaseAndSync(host.Services);
        if (_adminConnectionString is null)
        {
            using var scope = host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<AdminDbContext>().Database.EnsureCreated();
        }

        return host;
    }

    /// <summary>Puts exactly these addresses on the list.</summary>
    public void SetAdminEmails(params string[] emails) =>
        AdminList.Set(new AdminOptions { Emails = [.. emails] });

    /// <summary>Creates an account. No role and not listed: an ordinary learner.</summary>
    public async Task<ApplicationUser> SeedAccountAsync(string email, bool emailConfirmed = true)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = email,
            EmailConfirmed = emailConfirmed,
            UserProfileId = Guid.NewGuid().ToString()
        };

        var created = await users.CreateAsync(user, Password);
        created.Succeeded.Should().BeTrue("seeding the test account must succeed");
        return user;
    }

    /// <summary>Writes the role row for <paramref name="user"/>, as the grant script does.</summary>
    public async Task GrantRoleAsync(ApplicationUser user, string roleName = AdminAccessEvaluator.RoleName)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var normalized = roleName.ToUpperInvariant();
        var role = await db.Roles.SingleOrDefaultAsync(r => r.NormalizedName == normalized);
        if (role is null)
        {
            role = new IdentityRole { Id = Guid.NewGuid().ToString(), Name = roleName, NormalizedName = normalized };
            db.Roles.Add(role);
        }

        db.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
    }

    public async Task RevokeRoleAsync(ApplicationUser user)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.UserRoles.Where(ur => ur.UserId == user.Id).ExecuteDeleteAsync();
    }

    /// <summary>An account that holds the role and is on the list: an admin.</summary>
    public async Task<ApplicationUser> SeedAdminAsync(string email)
    {
        var user = await SeedAccountAsync(email);
        await GrantRoleAsync(user);
        SetAdminEmails([.. AdminList.CurrentValue.Emails, email]);
        return user;
    }

    /// <summary>A client whose every request carries a bearer token naming <paramref name="user"/>.</summary>
    public HttpClient CreateClientFor(ApplicationUser user)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestJwtGenerator.GenerateToken(userId: user.Id, email: user.Email, userProfileId: user.UserProfileId));
        return client;
    }

    /// <summary>Every audit row, oldest first, read through its own context.</summary>
    public async Task<List<AdminAuditEntry>> ReadAuditAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
        return await db.AuditEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var path in new[] { _dbPath, _auditDbPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
