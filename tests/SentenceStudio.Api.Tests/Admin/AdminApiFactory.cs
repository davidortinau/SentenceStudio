using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Admin;
using SentenceStudio.Api.Admin.Persistence;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Api.Security.DataProtection;
using SentenceStudio.Api.Tests.Coach;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Contracts.Coach;
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
    private readonly string _coachDbPath = Path.Combine(
        Path.GetTempPath(), $"sentencestudio_admin_coach_{Guid.NewGuid():N}.db");
    private readonly string _feedbackDbPath = Path.Combine(
        Path.GetTempPath(), $"sentencestudio_admin_feedback_{Guid.NewGuid():N}.db");
    private readonly string? _adminConnectionString;

    public AdminApiFactory(string? adminConnectionString = null)
    {
        _adminConnectionString = adminConnectionString;
    }

    /// <summary>The live <c>Admin:Emails</c> list.</summary>
    public TestOptionsMonitor<AdminOptions> AdminList { get; } = new(new AdminOptions());

    /// <summary>Watches the host's commands against the account database, for a test that needs to act between two of them.</summary>
    public IInterceptor? AccountDatabaseInterceptor { get; init; }

    /// <summary>
    /// Puts the coach and feedback contexts on the same PostgreSQL database as the admin one,
    /// which the test has already migrated for all three. Needs the connection string.
    /// </summary>
    public bool ReportsOnPostgres { get; init; }

    /// <summary>
    /// Whether the host says its Data Protection key ring is durable. A flagged exchange is opened
    /// only when it is; a test sets this false to see the refusal.
    /// </summary>
    public bool DurableKeyRing { get; init; } = true;

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
            TestApiHostConfigurator.ConfigureSqliteDatabaseAndSync(services, _dbPath, AccountDatabaseInterceptor);
            TestApiHostConfigurator.AddStubChatClientWhenAiUnconfigured(services, context.Configuration);

            if (_adminConnectionString is null)
            {
                services.RemoveAll<DbContextOptions<AdminDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AdminDbContext>>();
                services.AddDbContext<AdminDbContext>(options => options.UseSqlite($"Data Source={_auditDbPath}"));
            }

            // Sam's usage lives in the coach context; SQLite stands in for it, as in CoachApiFactory.
            foreach (var descriptor in services
                         .Where(d => d.ServiceType == typeof(DbContextOptions<CoachDbContext>)
                                     || (d.ServiceType.IsGenericType
                                         && d.ServiceType.GenericTypeArguments.Contains(typeof(CoachDbContext))))
                         .ToList())
            {
                services.Remove(descriptor);
            }

            services.RemoveAll<CoachDbContext>();

            // The reports learners filed to GitHub live in the feedback context; SQLite stands in
            // for it too.
            services.RemoveAll<DbContextOptions<FeedbackDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<FeedbackDbContext>>();

            if (ReportsOnPostgres)
            {
                services.AddDbContext<CoachDbContext>(options => options.UseNpgsql(
                    _adminConnectionString,
                    npgsql => npgsql.MigrationsHistoryTable(SentenceStudio.Api.Startup.StartupDbContextFactory.CoachMigrationsHistoryTable)));
                services.AddDbContext<FeedbackDbContext>(options => options.UseNpgsql(
                    _adminConnectionString,
                    npgsql => npgsql.MigrationsHistoryTable(FeedbackSchema.MigrationsHistoryTable)));
            }
            else
            {
                services.AddDbContext<CoachDbContext>(options => options.UseSqlite($"Data Source={_coachDbPath}"));
                services.AddDbContext<FeedbackDbContext>(options => options.UseSqlite($"Data Source={_feedbackDbPath}"));
            }

            services.RemoveAll<CoachKeyRingPlan>();
            services.AddSingleton(new CoachKeyRingPlan
            {
                Mode = DurableKeyRing ? CoachKeyRingMode.AzureBlobConnectionString : CoachKeyRingMode.HostDefault,
                ApplicationName = "SentenceStudio.Coach",
                ContainerName = "coach-keys",
                BlobName = "keyring.xml"
            });

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
        if (!ReportsOnPostgres)
        {
            using var scope = host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<CoachDbContext>().Database.EnsureCreated();
            scope.ServiceProvider.GetRequiredService<FeedbackDbContext>().Database.EnsureCreated();
        }

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

    /// <summary>
    /// Confirms <paramref name="client"/>'s password with the API and puts the proof on every
    /// later request it makes.
    /// </summary>
    public static async Task ConfirmPasswordAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/admin/step-up", new AdminStepUpRequest(Password));
        response.EnsureSuccessStatusCode();
        var proof = await response.Content.ReadFromJsonAsync<AdminStepUpResponse>();
        client.DefaultRequestHeaders.Remove(AdminStepUpService.HeaderName);
        client.DefaultRequestHeaders.Add(AdminStepUpService.HeaderName, proof!.Proof);
    }

    /// <summary>
    /// Creates an agent token through the real route, as <paramref name="admin"/> with a confirmed
    /// password, and returns it as the admin is shown it.
    /// </summary>
    public async Task<AdminTokenCreated> IssueTokenAsync(ApplicationUser admin, params string[] permissions)
    {
        using var client = CreateClientFor(admin);
        await ConfirmPasswordAsync(client);
        var response = await client.PostAsJsonAsync(
            "/api/admin/tokens", new AdminTokenCreateRequest("test agent", permissions));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AdminTokenCreated>())!;
    }

    /// <summary>A client that signs in with an agent token and nothing else.</summary>
    public HttpClient CreateClientForToken(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(AdminAgentTokenService.HeaderName, token);
        return client;
    }

    /// <summary>
    /// One Sam answer a learner flagged: a real conversation with the question and the answer
    /// encrypted in the coach's store, the learner's flag, and the inbox row it raised.
    /// </summary>
    /// <returns>The inbox row's id.</returns>
    public async Task<string> SeedFlaggedAnswerAsync(
        ApplicationUser learner,
        string question,
        string answer,
        CoachResponseReportReason reason = CoachResponseReportReason.IncorrectOrMisleading,
        DateTime? flaggedAtUtc = null,
        string? addToReportId = null)
    {
        using var scope = Services.CreateScope();
        var owner = CoachOwner.ForUser(learner.UserProfileId!);
        var conversationId = Guid.NewGuid().ToString("n");
        var conversations = scope.ServiceProvider.GetRequiredService<ICoachConversationStore>();
        var messages = scope.ServiceProvider.GetRequiredService<ICoachMessageStore>();

        (await conversations.CreateAsync(owner, new CreateCoachConversationRequest(
            Title: "Grammar",
            TitleSource: CoachConversationTitleSource.Generated,
            TargetLanguageCode: "ko",
            ConversationId: conversationId))).Status.Should().Be(CoachHistoryStatus.Success);

        var asked = await messages.AppendAsync(owner, new AppendCoachMessageRequest(
            conversationId, CoachMessageRole.Learner, CoachMessageKind.Text, Payload(question), "op-1"));
        var answered = await messages.AppendAsync(owner, new AppendCoachMessageRequest(
            conversationId, CoachMessageRole.Coach, CoachMessageKind.Text, Payload(answer), "op-1"));
        asked.Status.Should().Be(CoachHistoryStatus.Success);
        answered.Status.Should().Be(CoachHistoryStatus.Success);

        var at = flaggedAtUtc ?? Time.GetUtcNow().UtcDateTime;
        var db = scope.ServiceProvider.GetRequiredService<CoachDbContext>();
        var reportId = addToReportId;
        if (reportId is null)
        {
            reportId = Guid.NewGuid().ToString("n");
            db.CoachOpportunities.Add(new CoachOpportunity
            {
                Id = reportId,
                UserProfileId = learner.UserProfileId!,
                ConversationId = conversationId,
                Kind = CoachOpportunityKind.UserReportedResponse,
                Disposition = CoachOpportunityDisposition.Product,
                Surface = CoachOpportunitySurface.TurnOutcome,
                CapabilityCode = CoachOpportunityCapabilityCodes.ForReportReason(reason),
                StopReason = CoachStopReason.Completed,
                EvidenceMessageId = asked.Message!.Id,
                EvidenceMessageSequence = asked.Message.Sequence,
                EvidenceOfferMessageId = answered.Message!.Id,
                EvidenceOfferMessageSequence = answered.Message.Sequence,
                Fingerprint = Guid.NewGuid().ToString("n"),
                DedupBucketDate = DateOnly.FromDateTime(at),
                OccurrenceCount = 1,
                FirstObservedAtUtc = at,
                LastObservedAtUtc = at
            });
        }
        else
        {
            var row = await db.CoachOpportunities.SingleAsync(o => o.Id == reportId);
            row.OccurrenceCount++;
            row.LastObservedAtUtc = at;
            row.Version++;
        }

        db.CoachResponseReports.Add(new CoachResponseReport
        {
            Id = Guid.NewGuid().ToString("n"),
            UserProfileId = learner.UserProfileId!,
            ConversationId = conversationId,
            CoachMessageId = answered.Message!.Id,
            CoachMessageSequence = answered.Message.Sequence,
            RequestMessageId = asked.Message!.Id,
            RequestMessageSequence = asked.Message.Sequence,
            Reason = reason,
            ResponseKind = CoachMessageKind.PedagogicalAnswer,
            StopReason = CoachStopReason.Completed,
            TurnAttemptCount = 1,
            OpportunityId = reportId,
            ReportedAtUtc = at
        });
        await db.SaveChangesAsync();
        return reportId;
    }

    /// <summary>One more learner message in a conversation, which nobody flags.</summary>
    public async Task AppendUnflaggedMessageAsync(ApplicationUser learner, string conversationId, string text)
    {
        using var scope = Services.CreateScope();
        var appended = await scope.ServiceProvider.GetRequiredService<ICoachMessageStore>().AppendAsync(
            CoachOwner.ForUser(learner.UserProfileId!),
            new AppendCoachMessageRequest(
                conversationId, CoachMessageRole.Learner, CoachMessageKind.Text, Payload(text), "op-2"));
        appended.Status.Should().Be(CoachHistoryStatus.Success);
    }

    private CoachMessagePayload Payload(string text) => new()
    {
        Kind = CoachMessagePayloadKind.CoachText,
        Text = text,
        CreatedAtUtc = Time.GetUtcNow().UtcDateTime
    };

    /// <summary>Runs <paramref name="work"/> against the host's coach database.</summary>
    public async Task WithCoachDbAsync(Func<CoachDbContext, Task> work)
    {
        using var scope = Services.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<CoachDbContext>());
    }

    /// <summary>Runs <paramref name="work"/> against the host's feedback database.</summary>
    public async Task WithFeedbackDbAsync(Func<FeedbackDbContext, Task> work)
    {
        using var scope = Services.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<FeedbackDbContext>());
    }

    /// <summary>Runs <paramref name="work"/> against the host's admin database.</summary>
    public async Task WithAdminDbAsync(Func<AdminDbContext, Task> work)
    {
        using var scope = Services.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<AdminDbContext>());
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
        foreach (var path in new[] { _dbPath, _auditDbPath, _coachDbPath, _feedbackDbPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
