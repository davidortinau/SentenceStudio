using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SentenceStudio.Api.Tests.Infrastructure;

namespace SentenceStudio.Api.Tests.Feedback;

/// <summary>
/// Stands in for GitHub, and counts.
/// </summary>
/// <remarks>
/// <para>
/// The counter is the point of the whole family. "Exactly one public issue" is a claim about how
/// many times the external call was made, and the database cannot show it: a second call that
/// created a second issue leaves our ledger looking identical to one that did not. Only counting
/// the calls makes the difference observable.
/// </para>
/// <para>
/// It also captures each request body, so the preview-to-post binding can be asserted against the
/// bytes that actually left rather than against what the endpoint intended to send.
/// </para>
/// </remarks>
internal sealed class FakeGitHubHandler : HttpMessageHandler
{
    private int _calls;
    private int _nextIssueNumber = 100;

    /// <summary>How many issue-creation calls were made.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>The JSON bodies posted, in arrival order.</summary>
    public ConcurrentQueue<string> Bodies { get; } = new();

    /// <summary>Widens the window in which a second caller arrives while the first is in flight.</summary>
    public TimeSpan Dwell { get; set; } = TimeSpan.Zero;

    /// <summary>When set, every call answers with this status instead of creating an issue.</summary>
    public HttpStatusCode? FailWith { get; set; }

    /// <summary>When true, every call throws as if the connection dropped mid-flight.</summary>
    public bool ThrowTransport { get; set; }

    /// <summary>
    /// The body returned for a "created" response. Set to something unparseable to exercise the
    /// path where an issue exists but its identity cannot be read.
    /// </summary>
    public string? CreatedBodyOverride { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);

        if (request.Content is not null)
        {
            Bodies.Enqueue(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }

        if (Dwell > TimeSpan.Zero)
        {
            await Task.Delay(Dwell, cancellationToken).ConfigureAwait(false);
        }

        if (ThrowTransport)
        {
            throw new HttpRequestException("Simulated connection failure after the request left.");
        }

        if (FailWith is { } status)
        {
            return new HttpResponseMessage(status)
            {
                // Deliberately echoes the submitted body, which is what GitHub does on a 422.
                // A test asserting that we do not log it needs the echo to be present.
                Content = new StringContent(
                    "{\"message\":\"Validation Failed\",\"errors\":[{\"resource\":\"Issue\"}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        }

        var number = Interlocked.Increment(ref _nextIssueNumber);
        var body = CreatedBodyOverride
            ?? $$"""
                {"number":{{number}},"html_url":"https://github.com/davidortinau/SentenceStudio/issues/{{number}}","title":"Filed"}
                """;

        return new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>
/// The API host, wired to a real PostgreSQL feedback schema and a counting GitHub stand-in.
/// </summary>
/// <remarks>
/// <c>ApplicationDbContext</c> still runs on SQLite, as it does for every other endpoint test —
/// nothing here touches learning data. The feedback context is the one that must be real, because
/// the primary-key arbitration and the compare-and-swap are what is under test and SQLite would
/// prove neither for two processes.
/// </remarks>
internal sealed class FeedbackApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sentencestudio_feedback_{Guid.NewGuid():N}.db");

    public FeedbackApiFactory(string feedbackConnectionString)
    {
        FeedbackConnectionString = feedbackConnectionString;
    }

    public string FeedbackConnectionString { get; }

    public FakeGitHubHandler GitHub { get; } = new();

    /// <summary>Extra host settings, applied after the defaults so a test can override limits.</summary>
    public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:sentencestudio",
            TestApiHostConfigurator.DummyPostgresConnectionString);
        builder.UseSetting("ConnectionStrings:feedback", FeedbackConnectionString);
        builder.UseSetting("Database:SkipMigrateOnStartup", "true");
        builder.UseSetting("Auth:SeedDevTestAccounts", "false");
        builder.UseSetting("Auth:EnableDevAuthFallback", "false");
        builder.UseSetting("Jwt:SigningKey", TestJwtGenerator.TestSigningKeyValue);
        builder.UseSetting("Jwt:Issuer", TestJwtGenerator.TestIssuer);
        builder.UseSetting("Jwt:Audience", TestJwtGenerator.TestAudience);
        builder.UseSetting("AI:OpenAI:ApiKey", "test-openai-key");
        builder.UseSetting("ElevenLabsKey", "test-elevenlabs-key");
        builder.UseSetting("GitHub:Pat", "test-github-pat");

        // A dedicated key, distinct from the JWT one, exactly as a deployment must configure it.
        builder.UseSetting("Feedback:HmacKey", "feedback-test-hmac-key-at-least-32-chars!!");

        // The sweep is driven directly by the retention tests; a background timer racing them
        // would make a deletion count depend on when the test ran.
        builder.UseSetting("Feedback:RetentionSweepEnabled", "false");

        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices((context, services) =>
        {
            TestApiHostConfigurator.ConfigureSqliteDatabaseAndSync(services, _dbPath);
            TestApiHostConfigurator.AddStubChatClientWhenAiUnconfigured(services, context.Configuration);

            services.AddHttpClient("GitHub")
                    .ConfigurePrimaryHttpMessageHandler(() => GitHub);

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
        return host;
    }

    /// <summary>A client whose every request carries a bearer token for <paramref name="profileId"/>.</summary>
    public HttpClient CreateClientFor(string profileId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                TestJwtGenerator.GenerateToken(userProfileId: profileId, userId: profileId));
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
