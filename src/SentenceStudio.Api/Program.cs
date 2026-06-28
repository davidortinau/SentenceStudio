using System.Security.Claims;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CoreSync;
using CoreSync.Http.Server;
using CoreSync.PostgreSQL;
using ElevenLabs;
using Microsoft.EntityFrameworkCore;
using ElevenLabs.Models;
using ElevenLabs.TextToSpeech;
using ElevenLabs.Voices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.Tokens;
using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SentenceStudio;
using SentenceStudio.Abstractions;
using SentenceStudio.Api;
using SentenceStudio.Api.Auth;
using SentenceStudio.Api.Diagnostics;
using SentenceStudio.Api.Platform;
using SentenceStudio.Api.Plans;
using SentenceStudio.Contracts;
using SentenceStudio.Contracts.Ai;
using SentenceStudio.Contracts.Auth;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Contracts.Speech;
using SentenceStudio.Contracts.Vocabulary;
using SentenceStudio.Data;
using SentenceStudio.Domain.Abstractions;
using SentenceStudio.Infrastructure;
using SentenceStudio.Services;
using SentenceStudio.Services.LanguageSegmentation;
using SentenceStudio.Shared.Models;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("SentenceStudio.Api");

// ASP.NET Core request instrumentation — added here (not in ServiceDefaults) because the
// `OpenTelemetry.Instrumentation.AspNetCore` package references `Microsoft.AspNetCore.App`,
// which has no runtime pack for MAUI RIDs (maccatalyst/ios/android). ServiceDefaults stays
// MAUI-safe; each web host opts in locally.
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddAspNetCoreInstrumentation())
    .WithTracing(t => t.AddAspNetCoreInstrumentation());

// PostgreSQL requires UTC DateTimes — enable legacy mode for SQLite-era DateTime.Now values
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// ASP.NET Core Identity for local account management
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedEmail = true;
        options.Password.RequireDigit = true;
        options.Password.RequireUppercase = true;
        options.Password.RequiredLength = 8;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

const string JwtOrDevAuthScheme = "JwtOrDev";

// JWT Bearer authentication for Identity-issued tokens
var jwtSigningKey = builder.Configuration["Jwt:SigningKey"];
var enableDevAuthFallback = builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue("Auth:EnableDevAuthFallback", false);
if (!string.IsNullOrWhiteSpace(jwtSigningKey))
{
    var defaultScheme = enableDevAuthFallback
        ? JwtOrDevAuthScheme
        : JwtBearerDefaults.AuthenticationScheme;

    var authBuilder = builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = defaultScheme;
        options.DefaultAuthenticateScheme = defaultScheme;
        options.DefaultChallengeScheme = defaultScheme;
    });

    if (enableDevAuthFallback)
    {
        authBuilder.AddPolicyScheme(JwtOrDevAuthScheme, "JWT or development auth", options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                var authorizationHeader = context.Request.Headers.Authorization.ToString();
                return authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? JwtBearerDefaults.AuthenticationScheme
                    : DevAuthHandler.SchemeName;
            };
        });

        authBuilder.AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(
            DevAuthHandler.SchemeName,
            _ => { });
    }

    authBuilder.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "SentenceStudio",
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "SentenceStudio.Api",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtSigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
}
else if (builder.Environment.IsDevelopment())
{
    // Fallback to dev auth handler when no JWT key is configured
    builder.Services.AddAuthentication(DevAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, _ => { });
}
else
{
    throw new InvalidOperationException(
        "Jwt:SigningKey must be configured in non-development environments.");
}

builder.Services.AddAuthorization();

builder.Services.AddScoped<JwtTokenService>();

// Email sender — ConsoleEmailSender logs to Aspire structured logs in development;
// swap for SmtpEmailSender in production.
builder.Services.AddSingleton<IAppEmailSender, ConsoleEmailSender>();

if (builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue("Auth:SeedDevTestAccounts", true))
{
    builder.Services.AddHostedService<DevTestAccountSeeder>();
}

builder.Services.AddScoped<ITenantContext, TenantContext>();

// Platform abstractions for API server. In Linux containers LocalApplicationData
// resolves under /app, which is read-only for the ACA runtime user.
var appDataDirectory = Path.Combine(GetWritableAppDataRoot(), "sentencestudio", "api");
builder.Services.AddSingleton<IConnectivityService, ApiConnectivityService>();
builder.Services.AddSingleton<IFileSystemService>(_ => new ApiFileSystemService(appDataDirectory));

// Language segmenters
builder.Services.AddSingleton<KoreanLanguageSegmenter>();
builder.Services.AddSingleton<GenericLatinSegmenter>();
builder.Services.AddSingleton<FrenchLanguageSegmenter>();
builder.Services.AddSingleton<GermanLanguageSegmenter>();
builder.Services.AddSingleton<SpanishLanguageSegmenter>();
builder.Services.AddSingleton<IEnumerable<ILanguageSegmenter>>(provider =>
    new List<ILanguageSegmenter>
    {
        provider.GetRequiredService<KoreanLanguageSegmenter>(),
        provider.GetRequiredService<GenericLatinSegmenter>(),
        provider.GetRequiredService<FrenchLanguageSegmenter>(),
        provider.GetRequiredService<GermanLanguageSegmenter>(),
        provider.GetRequiredService<SpanishLanguageSegmenter>()
    });

// YouTube channel monitoring services
builder.Services.AddSingleton<ChannelMonitorService>();
builder.Services.AddSingleton<VideoImportPipelineService>();
builder.Services.AddSingleton<YouTubeImportService>();
builder.Services.AddSingleton<AudioAnalyzer>();
builder.Services.AddSingleton<TranscriptFormattingService>();
builder.Services.AddSingleton<AiService>();

// Server chat uses Azure AI Foundry keyless auth through DefaultAzureCredential below.
// Settings:OpenAIKey is retained only for the OpenAI audio fallback client, so production
// deploys must not require an opaquely named OpenAI key secret for Foundry chat.
var openAiApiKey = builder.Configuration["Settings:OpenAIKey"];
if (string.IsNullOrWhiteSpace(openAiApiKey))
{
    openAiApiKey = Environment.GetEnvironmentVariable("AI__OpenAI__ApiKey");
}
if (string.IsNullOrWhiteSpace(openAiApiKey))
{
    openAiApiKey = "not-configured";
}
builder.Configuration["Settings:OpenAIKey"] = openAiApiKey;

// Release notes service
builder.Services.AddSingleton<ReleaseNotesService>();

// CORS — basic policies for known callers.
// Production fine-tuning is tracked in issue #62.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
builder.Services.AddCors(options =>
{
    if (allowedOrigins?.Length > 0)
    {
        options.AddPolicy("AllowWebApp", policy =>
        {
            policy.WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        });
    }

    if (builder.Environment.IsDevelopment())
    {
        options.AddPolicy("AllowDevClients", policy =>
        {
            policy.SetIsOriginAllowed(origin =>
                    Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                    && uri.Host is "localhost" or "127.0.0.1")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        });
    }
});

builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
    options.Preload = true;
});

// Database - Aspire-managed PostgreSQL
builder.AddNpgsqlDbContext<ApplicationDbContext>("sentencestudio", configureDbContextOptions: options =>
{
    options.ConfigureWarnings(w =>
        w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

// NumberDrill content seeder — populates NumberContext / NumberSubMode / NumberCounter
// from lib/content/numbers/{language}.json (idempotent upsert by natural key).
builder.Services.AddScoped<SentenceStudio.Services.Numbers.NumberContentSeeder>();

// Multi-worktree footgun: if the API binds to a fresh Postgres volume (different worktree
// or freshly-provisioned Aspire environment), AspNetUsers will be empty and login will return
// 401 with no obvious cause. Surface this loudly via a Degraded health check on the dashboard.
// The startup banner (logged after Build()) is the louder companion. Read-only by design.
builder.Services.AddHealthChecks()
    .AddCheck<EmptyUsersHealthCheck>(
        name: "aspnet-users-populated",
        failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
        tags: new[] { "db", "users", "diagnostics" });

// CoreSync server — allows mobile clients to sync through the API endpoint
// (mobile devices can't reach the separate 'web' service directly)
builder.Services.AddCoreSyncHttpServer();
builder.Services.AddSingleton<ISyncProvider>(sp =>
{
    var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("sentencestudio")!;
    var configurationBuilder = new PostgreSQLSyncConfigurationBuilder(connectionString)
        .ConfigureSyncTables();
    return new PostgreSQLSyncProvider(configurationBuilder.Build(), ProviderMode.Remote);
});

// User profile repository (lives in SentenceStudio.Shared; not pulled in via the
// AppLib core-services extension because the API project does not reference AppLib).
builder.Services.AddSingleton<UserProfileRepository>();

// Plan-generation scope + date context — both per-request (scoped). Fail-closed
// on missing user; X-Timezone header (IANA, Windows ids also accepted) drives
// per-request "today" so the daily plan rolls over at the device's local
// midnight, not server UTC.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<SentenceStudio.Services.Plans.IUserScopeProvider,
    SentenceStudio.Api.Plans.HttpUserScopeProvider>();
builder.Services.AddScoped<SentenceStudio.Services.Plans.IPlanDateContext,
    SentenceStudio.Api.Plans.HttpPlanDateContext>();

// Daily-plan service surface (see plan.md §7). Persistence + progress are
// wired against ApplicationDbContext. Phase B (this commit) wires the full
// DeterministicPlanBuilder on the API head: every repo call now threads
// the request-scoped UserProfileId so no data leaks across users. The MAUI
// Blazor head registers the same DeterministicPlanGenerator via AppLib's
// CoreServiceExtensions and falls back to IPreferences when no id is passed.
builder.Services.AddSingleton<SentenceStudio.Services.Plans.IPlanCopyProvider,
    SentenceStudio.Services.Plans.EnglishPlanCopyProvider>();

// Repos required by DeterministicPlanBuilder. Registered as Singleton to
// match AppLib (they manage their own EF scopes internally via
// IServiceProvider.CreateScope()). NOTE: when IPreferencesService is not
// registered (this host), each repo's `ActiveUserId` returns empty — so
// EVERY caller from here must pass an explicit userProfileId or the query
// will return rows across all users. DeterministicPlanBuilder does so.
builder.Services.AddSingleton<LearningResourceRepository>();
builder.Services.AddSingleton<SkillProfileRepository>();

// DeterministicPlanBuilder is Scoped on the API because it resolves the
// per-request IPlanDateContext from the injected IServiceProvider; AppLib
// registers it Singleton because its IPlanDateContext is transient-from-
// singleton. The interface adapter below stays Scoped to match.
builder.Services.AddScoped<SentenceStudio.Services.PlanGeneration.DeterministicPlanBuilder>();
builder.Services.AddScoped<SentenceStudio.Services.Plans.IDeterministicPlanGenerator,
    SentenceStudio.Services.Plans.DeterministicPlanGenerator>();
builder.Services.AddScoped<SentenceStudio.Services.Plans.IPlanService,
    SentenceStudio.Services.Plans.PlanService>();


// Voice discovery (ElevenLabs) — registered here for the same reason as above.
builder.Services.AddSingleton<SentenceStudio.Services.Speech.IVoiceDiscoveryService, SentenceStudio.Services.Speech.VoiceDiscoveryService>();

// Vocabulary progress services
builder.Services.AddSingleton<VocabularyProgressRepository>();
builder.Services.AddSingleton<VocabularyLearningContextRepository>();
builder.Services.AddSingleton<VocabularyProgressService>();
builder.Services.AddSingleton<VocabularyClassificationBackfillService>();
builder.Services.AddSingleton<IVocabularyProgressService>(provider =>
    provider.GetRequiredService<VocabularyProgressService>());

// Server → Foundry uses keyless Entra auth (DefaultAzureCredential): `az login` locally,
// managed identity in Azure. No OpenAI API key required for chat.
var aiEndpoint = builder.Configuration["AI:OpenAI:Endpoint"];
if (!string.IsNullOrWhiteSpace(aiEndpoint))
{
    // Resilient HttpClient for OpenAI — server defaults (AddServiceDefaults) provide
    // Polly retry/circuit-breaker via ConfigureHttpClientDefaults.
    builder.Services.AddResilientOpenAIHttpClient();

    // Default (fast) + keyed fast/reasoning chat clients. AzureOpenAIClient derives from
    // OpenAIClient, so the tiered registration is shared with the MAUI (key-based) path.
    var azureEndpoint = AiClientRegistration.AzureResourceEndpoint(builder.Configuration);
    builder.Services.AddTieredChatClients(builder.Configuration, sp =>
    {
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("openai");
        var options = new AzureOpenAIClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
        };
        return new AzureOpenAIClient(new Uri(azureEndpoint), new DefaultAzureCredential(), options);
    });
}

// GitHub API client for feedback issue creation
builder.Services.AddHttpClient("GitHub", client =>
{
    client.BaseAddress = new Uri("https://api.github.com");
    client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    client.DefaultRequestHeaders.Add("User-Agent", "SentenceStudio");
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
});

var elevenLabsKey = builder.Configuration["ElevenLabsKey"];
if (!string.IsNullOrWhiteSpace(elevenLabsKey))
{
    // Korean transcripts can run several thousand chars; the SDK's default HttpClient
    // timeout (100s) trips on /synthesize-timestamped with long input. Bump to 5 min.
    var elevenLabsHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    builder.Services.AddSingleton(new ElevenLabsClient(elevenLabsKey, httpClient: elevenLabsHttp));
}

var app = builder.Build();
var skipDatabaseInitialization = builder.Configuration.GetValue("Database:SkipMigrateOnStartup", false);

if (!skipDatabaseInitialization)
{
    // Apply EF Core migrations (creates tables if missing)
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();

        // Run vocabulary classification backfill (idempotent)
        var backfillService = scope.ServiceProvider.GetRequiredService<VocabularyClassificationBackfillService>();
        await backfillService.BackfillLexicalUnitTypesAsync();
        
        // Run phrase constituent backfill (idempotent, after classification)
        await backfillService.BackfillPhraseConstituentsAsync();

        // Seed Korean number content (NumberDrill activity — idempotent upsert by natural key)
        var numberSeeder = scope.ServiceProvider.GetRequiredService<SentenceStudio.Services.Numbers.NumberContentSeeder>();
        await numberSeeder.SeedAsync("ko");
    }

    // Apply CoreSync provisioning (creates change-tracking tables if missing)
    var syncProvider = app.Services.GetRequiredService<ISyncProvider>();
    await syncProvider.ApplyProvisionAsync();
}

// Startup detection: warn loudly if AspNetUsers is empty. Catches the multi-worktree
// case where Aspire spun up a fresh Postgres volume separate from the one holding real
// user data. Read-only — SELECT COUNT(*) only, never mutates.
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var startupLogger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("SentenceStudio.Api.Diagnostics.EmptyUsersStartupCheck");
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (EmptyUsersDetector.IsPostgres(db))
        {
            var userCount = await db.Users.CountAsync();
            if (userCount == 0)
            {
                var connectionInfo = EmptyUsersDetector.DescribeConnection(db);
                var banner = EmptyUsersDetector.BuildMessage(connectionInfo);
                startupLogger.LogCritical("{EmptyUsersBanner}", banner);
            }
            else
            {
                startupLogger.LogInformation(
                    "AspNetUsers populated at startup: {UserCount} user(s) on {Connection}.",
                    userCount,
                    EmptyUsersDetector.DescribeConnection(db));
            }
        }
        else
        {
            startupLogger.LogDebug(
                "Skipping empty-users startup check; provider is {Provider} (not Postgres).",
                db.Database.ProviderName);
        }
    }
    catch (Exception ex)
    {
        // Don't let a diagnostic check abort startup — the API still needs to come up so
        // the existing DbContext health check can surface the underlying connectivity error.
        startupLogger.LogWarning(ex, "Empty-users startup check failed; continuing.");
    }

    // Startup assertion: log JWT expiry and refresh-token grace window configuration
    var jwtExpiryMinutes = int.TryParse(app.Configuration["Jwt:ExpiryMinutes"], out var expiry) ? expiry : 1440;
    var graceWindowSeconds = int.TryParse(app.Configuration["RefreshToken:GraceWindowSeconds"], out var grace) ? grace : 60;
    startupLogger.LogInformation(
        "JWT lifetime: {JwtExpiryMinutes} minutes. Refresh-token grace window: {GraceWindowSeconds} seconds.",
        jwtExpiryMinutes,
        graceWindowSeconds);
}

// Global unhandled-exception handler — MUST be the first middleware in the pipeline so it
// catches exceptions from auth, CORS, CoreSync, and endpoint code alike. ASP.NET Core OTel
// instrumentation tags exceptions on the request span but does NOT emit them as `exceptions`
// telemetry rows. Logging via ILogger.LogError pushes them through the OTel log exporter,
// which DOES land them in App Insights' `exceptions` table for KQL.
app.UseExceptionHandler(exceptionHandlerApp =>
{
    exceptionHandlerApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        if (feature?.Error is { } ex)
        {
            var logger = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("UnhandledException");

            logger.LogError(ex,
                "Unhandled exception in {Method} {Path}",
                context.Request.Method,
                context.Request.Path);
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync("""
            {"type":"about:blank","title":"Internal Server Error","status":500,"detail":"An unexpected error occurred."}
            """);
    });
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// Skip HTTPS redirect in development — Aspire may terminate TLS at the proxy.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseSecurityHeaders();
app.UseCors(app.Environment.IsDevelopment() ? "AllowDevClients" : "AllowWebApp");
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantContextMiddleware>();

// Health endpoints — exposed in Development so the Aspire dashboard can render check
// status (including aspnet-users-populated). Skipped in Production to avoid leaking
// internal diagnostics; production health is observed via App Insights / OTEL instead.
if (app.Environment.IsDevelopment())
{
    app.MapHealthChecks("/health");
    app.MapHealthChecks("/alive", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = static r => r.Tags.Contains("live")
    });
}
app.UseCoreSyncHttpServer(optionsConfigure: options =>
{
    // CoreSync syncs per-user data, so its endpoints must enforce the same auth pipeline as the rest of the API.
    options.AllEndpoints = endpoint => endpoint.RequireAuthorization();
});

// Auth endpoints (anonymous — they handle login/register)
app.MapAuthEndpoints();

// Daily plan v2 endpoints — see docs/daily-plan-server-contract.md /
// plan.md. Replaces the legacy /api/v1/plans/generate stub below (kept
// for backward compat during the MAUI Blazor v2 flip).
app.MapPlans();
app.MapActivityLog();

// YouTube channel monitoring endpoints
app.MapChannelEndpoints();
app.MapImportEndpoints();

// Feedback endpoints (GitHub issue creation)
app.MapFeedbackEndpoints();

// Version and release notes endpoints (public)
app.MapVersionEndpoints();

// User profile endpoints (per-user GET/PUT)
app.MapProfileEndpoints();

// Speech / voice discovery
app.MapSpeechEndpoints();

app.MapGet("/api/v1/auth/bootstrap", (ClaimsPrincipal user, ITenantContext tenantContext) =>
    Results.Ok(new BootstrapResponse
    {
        TenantId = tenantContext.TenantId ?? user.FindFirstValue("tenant_id"),
        UserId = tenantContext.UserId ?? user.FindFirstValue(ClaimTypes.NameIdentifier),
        DisplayName = tenantContext.DisplayName ?? user.FindFirstValue(ClaimTypes.Name),
        Email = tenantContext.Email ?? user.FindFirstValue(ClaimTypes.Email)
    }))
    .RequireAuthorization();

app.MapPost("/api/v1/ai/chat", async (ChatRequest request, [FromServices] IServiceProvider sp, CancellationToken cancellationToken) =>
    {
        var chatClient = ResolveTieredChatClient(sp, request.Tier);
        if (chatClient == null)
        {
            return Results.Problem("OpenAI client is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var responseType = ResolveResponseType(request.ResponseType);
        // If type resolution fails, fall back to string/text response rather than 400.
        // The client prompt already requests JSON format, so the client can parse it.
        if (!AiChatOptionsFactory.IsSupportedReasoningEffort(request.ReasoningEffort))
        {
            return Results.BadRequest("ReasoningEffort must be one of: minimal, low, medium, high.");
        }

        if (responseType == null || responseType == typeof(string))
        {
            var options = AiChatOptionsFactory.Create(request.Scenario, request.ReasoningEffort);

            var response = await chatClient.GetResponseAsync(
                new[]
                {
                    new ChatMessage(ChatRole.User, request.Message)
                },
                options,
                cancellationToken);

            return Results.Ok(new SentenceStudio.Contracts.Ai.ChatResponse
            {
                Response = response.Text ?? string.Empty,
                Language = null
            });
        }

        var typedResult = await GetTypedResponseAsync(
            chatClient,
            request.Message,
            request.Scenario,
            request.ReasoningEffort,
            responseType,
            cancellationToken);

        var json = JsonSerializer.Serialize(typedResult, responseType);
        return Results.Ok(new SentenceStudio.Contracts.Ai.ChatResponse
        {
            Response = json,
            Language = null
        });
    })
    .RequireAuthorization();

app.MapPost("/api/v1/ai/chat-messages", async (ChatMessagesRequest request, [FromServices] IChatClient? chatClient, CancellationToken cancellationToken) =>
    {
        if (chatClient == null)
        {
            return Results.Problem("OpenAI client is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var messages = request.Messages.Select(m => new ChatMessage(
            m.Role switch
            {
                "assistant" => ChatRole.Assistant,
                "system" => ChatRole.System,
                _ => ChatRole.User
            },
            m.Content)).ToList();

        var responseType = ResolveResponseType(request.ResponseType);

        var options = string.IsNullOrWhiteSpace(request.Instructions)
            ? null
            : new ChatOptions { Instructions = request.Instructions };

        if (responseType == null || responseType == typeof(string))
        {
            var response = await chatClient.GetResponseAsync(messages, options, cancellationToken);
            return Results.Ok(new SentenceStudio.Contracts.Ai.ChatResponse
            {
                Response = response.Text ?? string.Empty
            });
        }

        // Typed response via reflection
        var method = typeof(Program)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name.Contains(nameof(GetTypedResponseFromMessagesAsync)) && m.IsGenericMethodDefinition)
            ?? throw new InvalidOperationException($"Could not find method {nameof(GetTypedResponseFromMessagesAsync)} via reflection.");
        var genericMethod = method.MakeGenericMethod(responseType);
        var task = (Task<object?>)genericMethod.Invoke(null, new object?[] { chatClient, messages, request.Instructions, cancellationToken })!;
        var typedResult = await task;

        var json = JsonSerializer.Serialize(typedResult, responseType);
        return Results.Ok(new SentenceStudio.Contracts.Ai.ChatResponse
        {
            Response = json
        });
    })
    .RequireAuthorization();

app.MapPost("/api/v1/ai/analyze-image", async (AnalyzeImageRequest request, [FromServices] IChatClient? chatClient, CancellationToken cancellationToken) =>
    {
        if (chatClient == null)
        {
            return Results.Problem("OpenAI client is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (string.IsNullOrWhiteSpace(request.ImageBase64))
        {
            return Results.BadRequest("ImageBase64 is required.");
        }

        // Hard cap on the base64-encoded payload size. 14 MB of base64 decodes
        // to ~10 MB of raw image bytes, well above any photo the client would
        // legitimately send. Strings of unbounded size let a caller hold a
        // request thread + AI quota hostage.
        const int MaxImageBase64Length = 14 * 1024 * 1024;
        if (request.ImageBase64.Length > MaxImageBase64Length)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // Allow-list of media types the downstream multimodal model actually
        // accepts. Anything else gets rejected before we burn a token on it.
        var allowedMediaTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg",
            "image/png",
            "image/webp",
            "image/gif"
        };
        if (string.IsNullOrWhiteSpace(request.MediaType) || !allowedMediaTypes.Contains(request.MediaType))
        {
            return Results.BadRequest("MediaType must be one of: image/jpeg, image/png, image/webp, image/gif.");
        }

        byte[] imageBytes;
        try
        {
            imageBytes = Convert.FromBase64String(request.ImageBase64);
        }
        catch (FormatException)
        {
            return Results.BadRequest("ImageBase64 is not valid base64.");
        }

        var dataUri = $"data:{request.MediaType};base64,{request.ImageBase64}";

        var message = new ChatMessage(ChatRole.User, request.Prompt);
        message.Contents.Add(new DataContent(new Uri(dataUri), mediaType: request.MediaType));

        var response = await chatClient.GetResponseAsync(
            new[] { message },
            cancellationToken: cancellationToken);

        return Results.Ok(new SentenceStudio.Contracts.Ai.ChatResponse
        {
            Response = response.Text ?? string.Empty
        });
    })
    .RequireAuthorization();

app.MapPost("/api/v1/speech/synthesize", async (SynthesizeRequest request, [FromServices] ElevenLabsClient? client) =>
    {
        if (client == null)
        {
            return Results.Problem("ElevenLabs client is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var voiceId = string.IsNullOrWhiteSpace(request.VoiceId)
            ? app.Configuration["AI:ElevenLabs:DefaultVoice"] ?? "21m00Tcm4TlvDq8ikWAM"
            : request.VoiceId;

        var voice = await client.VoicesEndpoint.GetVoiceAsync(voiceId);
        var ttsRequest = new TextToSpeechRequest(
            voice,
            request.Text,
            model: Model.MultiLingualV2);

        var audioBytes = await client.TextToSpeechEndpoint.TextToSpeechAsync(ttsRequest);
        var base64 = Convert.ToBase64String(audioBytes.ClipData.ToArray());

        return Results.Ok(new SynthesizeResponse
        {
            AudioUrl = $"data:audio/mpeg;base64,{base64}"
        });
    })
    .RequireAuthorization();

// Maps friendly voice slugs used by Flutter clients to ElevenLabs voice IDs.
// Keep in sync with VoiceOptions in ElevenLabsSpeechService (MAUI in-process service).
// Hard-coded allow-list: unknown slugs fall back to the default voice rather
// than being forwarded verbatim to ElevenLabs (prevents callers from smuggling
// arbitrary voice IDs through the API).
static string ResolveTimestampedVoiceId(string? requested, IConfiguration config)
{
    var fallback = config["AI:ElevenLabs:DefaultVoice"] ?? "21m00Tcm4TlvDq8ikWAM";
    if (string.IsNullOrWhiteSpace(requested)) return fallback;

    // Slug map mirrors ElevenLabsSpeechService.VoiceOptions in AppLib.
    return requested switch
    {
        // Korean
        "yuna"     => "xi3rF0t7dg7uN2M0WUhr",
        "jiyoung"  => "AW5wrnG1jVizOYY7R1Oo",
        "hyunbin"  => "s07IwTCOrCDCaETjUVjx",
        "jennie"   => "z6Kj0hecH20CdetSElRT",
        "jina"     => "sSoVF9lUgTGJz0Xz3J9y",
        "dohyeon"  => "FQ3MuLxZh0jHcZmA5vW1",
        "yohankoo" => "4JJwo477JUAx3HV0T7n7",
        // English
        "echo"    or "rachel"  => "21m00Tcm4TlvDq8ikWAM",
        "onyx"    or "antoni"  => "ED0k6LqFEfpMua5GXpMG",
        "nova"    or "elli"    => "jsCqWAovK2LkecY7zXl4",
        "shimmer" or "adam"    => "kgG8YXSrynzpPIncHKrx",
        "fable"   or "dorothy" => "5Q0t7uMcjvnagumLfvZi",
        _ => fallback
    };
}

app.MapPost("/api/v1/speech/synthesize-timestamped", async (
        SynthesizeTimestampedRequest request,
        ClaimsPrincipal user,
        [FromServices] ElevenLabsClient? client,
        [FromServices] ApplicationDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
    {
        var logger = loggerFactory.CreateLogger("SpeechEndpoints");

        if (client == null)
        {
            return Results.Problem("ElevenLabs client is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var userProfileId = user.FindFirstValue(AuthClaimTypes.UserProfileId);
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.ResourceId))
        {
            return Results.BadRequest(new { error = "ResourceId is required." });
        }

        // Scope by caller so an authenticated user can't synthesize another
        // user's resource by GUID (IDOR). Matches LearningResourceRepository
        // contract (r.UserProfileId == userId).
        var resource = await db.LearningResources
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.Id == request.ResourceId && r.UserProfileId == userProfileId,
                cancellationToken);

        if (resource is null)
        {
            return Results.NotFound(new { error = $"LearningResource '{request.ResourceId}' not found." });
        }

        if (string.IsNullOrWhiteSpace(resource.Transcript))
        {
            return Results.BadRequest(new { error = "LearningResource has no transcript to synthesize." });
        }

        // Cap synthesis size: ElevenLabs is paid per character and long
        // transcripts already push the 5-minute HTTP timeout. ~20k chars is
        // a generous upper bound (a typical chapter is well under that).
        const int kMaxTranscriptChars = 20_000;
        if (resource.Transcript.Length > kMaxTranscriptChars)
        {
            return Results.BadRequest(new
            {
                error = $"Transcript exceeds maximum length of {kMaxTranscriptChars} characters."
            });
        }

        var voiceId = ResolveTimestampedVoiceId(request.VoiceId, app.Configuration);

        // Clamp VoiceSettings to documented ElevenLabs ranges so callers can't
        // pass NaN / out-of-band values that either error from upstream or
        // produce undefined behaviour.
        var stability = Math.Clamp(float.IsFinite(request.Stability) ? request.Stability : 0.5f, 0f, 1f);
        var similarityBoost = Math.Clamp(float.IsFinite(request.SimilarityBoost) ? request.SimilarityBoost : 0.75f, 0f, 1f);
        var speed = Math.Clamp(float.IsFinite(request.Speed) ? request.Speed : 1.0f, 0.7f, 1.2f);

        try
        {
            var voice = await client.VoicesEndpoint.GetVoiceAsync(voiceId, cancellationToken: cancellationToken);

            var ttsRequest = new TextToSpeechRequest(
                voice: voice,
                text: resource.Transcript,
                voiceSettings: new VoiceSettings(stability, similarityBoost) { Speed = speed },
                model: Model.MultiLingualV2,
                withTimestamps: true);

            var voiceClip = await client.TextToSpeechEndpoint.TextToSpeechAsync(ttsRequest, cancellationToken: cancellationToken);

            var chars = voiceClip.TimestampedTranscriptCharacters;
            var characters = new List<CharacterTimestamp>(chars?.Length ?? 0);
            if (chars is not null)
            {
                foreach (var c in chars)
                {
                    characters.Add(new CharacterTimestamp
                    {
                        Char = c.Character.ToString() ?? string.Empty,
                        StartMs = c.StartTime * 1000.0,
                        EndMs = c.EndTime * 1000.0
                    });
                }
            }

            var durationSeconds = chars is { Length: > 0 } ? chars[^1].EndTime : 0.0;
            var base64 = Convert.ToBase64String(voiceClip.ClipData.ToArray());

            logger.LogInformation(
                "synthesize-timestamped: resource {ResourceId} user {UserProfileId} voice {VoiceId} chars {CharCount} duration {Duration:F2}s",
                request.ResourceId, userProfileId, voiceId, characters.Count, durationSeconds);

            return Results.Ok(new SynthesizeTimestampedResponse
            {
                AudioUrl = $"data:audio/mpeg;base64,{base64}",
                DurationSeconds = durationSeconds,
                Characters = characters
            });
        }
        catch (Exception ex)
        {
            // Log full exception server-side; return generic message to caller
            // so we don't leak internal types / URLs / upstream auth details.
            logger.LogError(ex, "synthesize-timestamped failed for resource {ResourceId}", request.ResourceId);
            return Results.Problem(
                detail: "Failed to synthesize timestamped audio.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    })
    .RequireAuthorization();

app.MapPost("/api/v1/plans/generate", (
        GeneratePlanRequest request,
        ClaimsPrincipal user,
        ILogger<PlansLog> logger) =>
    {
        var userProfileId = user.FindFirstValue(AuthClaimTypes.UserProfileId);
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            return Results.Unauthorized();
        }

        logger.LogInformation("plans/generate for {UserProfileId}", userProfileId);
        return Results.Ok(BuildPlanResponse(request));
    })
    .RequireAuthorization();

app.MapPost("/api/v1/vocabulary/{wordId}/status", async (
    string wordId,
    SetVocabularyStatusRequest request,
    ClaimsPrincipal user,
    IVocabularyProgressService progressService) =>
{
    var userId = user.FindFirstValue(AuthClaimTypes.UserProfileId);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    if (!Enum.TryParse<LearningStatus>(request.Status, ignoreCase: true, out var status))
    {
        return Results.BadRequest($"Invalid status '{request.Status}'. Valid values: Unknown, Learning, Familiar.");
    }

    if (status == LearningStatus.Known)
    {
        return Results.BadRequest("Known status must be earned through practice.");
    }

    var progress = await progressService.SetUserDeclaredStatusAsync(wordId, userId, status);

    return Results.Ok(new VocabularyProgressResponse
    {
        Id = progress.Id,
        VocabularyWordId = progress.VocabularyWordId,
        Status = progress.Status.ToString(),
        MasteryScore = progress.MasteryScore,
        IsUserDeclared = progress.IsUserDeclared,
        UserDeclaredAt = progress.UserDeclaredAt,
        VerificationState = progress.VerificationState.ToString(),
        CurrentStreak = progress.CurrentStreak,
        TotalAttempts = progress.TotalAttempts
    });
})
.RequireAuthorization();




static GeneratePlanResponse BuildPlanResponse(GeneratePlanRequest request)
{
    var totalMinutes = Math.Clamp(request.Minutes ?? 30, 10, 90);
    var vocabMinutes = Math.Clamp(totalMinutes / 3, 6, 12);
    var conversationMinutes = Math.Clamp(totalMinutes / 3, 5, 15);
    var remainingMinutes = totalMinutes - vocabMinutes - conversationMinutes;

    if (remainingMinutes < 5)
    {
        conversationMinutes += remainingMinutes;
        remainingMinutes = 0;
    }

    var activities = new List<GeneratePlanActivity>
    {
        new()
        {
            ActivityType = "VocabularyReview",
            EstimatedMinutes = vocabMinutes,
            Priority = 1,
            VocabWordCount = Math.Clamp(vocabMinutes * 2, 8, 24)
        },
        new()
        {
            ActivityType = "Conversation",
            EstimatedMinutes = conversationMinutes,
            Priority = 2
        }
    };

    if (remainingMinutes > 0)
    {
        activities.Add(new GeneratePlanActivity
        {
            ActivityType = "Reading",
            EstimatedMinutes = remainingMinutes,
            Priority = 3
        });
    }

    return new GeneratePlanResponse
    {
        PlanId = Guid.NewGuid().ToString("N")[..16],
        Rationale = "Locally generated API plan balancing review and production activities.",
        Activities = activities
    };
}

static async Task<object?> GetTypedResponseAsync(
    IChatClient chatClient,
    string message,
    string? instructions,
    string? reasoningEffort,
    Type responseType,
    CancellationToken cancellationToken)
{
    var method = typeof(Program)
        .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
        .FirstOrDefault(m => m.Name.Contains(nameof(GetTypedResponseCoreAsync)) && m.IsGenericMethodDefinition)
        ?? throw new InvalidOperationException($"Could not find method {nameof(GetTypedResponseCoreAsync)} via reflection.");

    var genericMethod = method.MakeGenericMethod(responseType);

    var task = (Task<object?>)genericMethod.Invoke(null, new object?[] { chatClient, message, instructions, reasoningEffort, cancellationToken })!;
    return await task;
}

static async Task<object?> GetTypedResponseCoreAsync<T>(
    IChatClient chatClient,
    string message,
    string? instructions,
    string? reasoningEffort,
    CancellationToken cancellationToken)
{
    var options = AiChatOptionsFactory.Create(instructions, reasoningEffort);

    var response = await chatClient.GetResponseAsync<T>(
        new[]
        {
            new ChatMessage(ChatRole.User, message)
        },
        options,
        cancellationToken: cancellationToken);
    return response.Result;
}

static async Task<object?> GetTypedResponseFromMessagesAsync<T>(
    IChatClient chatClient,
    IList<ChatMessage> messages,
    string? instructions,
    CancellationToken cancellationToken)
{
    var options = string.IsNullOrWhiteSpace(instructions)
        ? null
        : new ChatOptions { Instructions = instructions };

    var response = await chatClient.GetResponseAsync<T>(
        messages,
        options,
        cancellationToken: cancellationToken);
    return response.Result;
}

// Resolves the tier-keyed IChatClient for an incoming gateway request, falling back to the
// default (fast) client when the tier is unset/unknown or no keyed client is registered.
static IChatClient? ResolveTieredChatClient(IServiceProvider sp, string? requestedTier)
{
    var tier = string.Equals(requestedTier, nameof(AiTier.Reasoning), StringComparison.OrdinalIgnoreCase)
        ? AiTier.Reasoning
        : AiTier.Fast;

    return Microsoft.Extensions.DependencyInjection.ServiceProviderKeyedServiceExtensions
            .GetKeyedService<IChatClient>(sp, tier.ToKey())
        ?? (IChatClient?)sp.GetService(typeof(IChatClient));
}

static Type? ResolveResponseType(string? responseType)
{
    if (string.IsNullOrWhiteSpace(responseType))
    {
        return null;
    }

    if (responseType == typeof(string).FullName || responseType == typeof(string).AssemblyQualifiedName)
    {
        return typeof(string);
    }

    // Allow-list lookup by FullName only. The client transmits
    // typeof(T).AssemblyQualifiedName, but we strip the assembly suffix and
    // match against a hard-coded set so a caller cannot coax the server into
    // hydrating arbitrary loaded types via System.Text.Json. Adding a new DTO
    // requires editing AiResponseTypeRegistry.AllowedTypes.
    var fullName = StripAssemblyQualification(responseType);
    return AiResponseTypeRegistry.AllowedTypes.TryGetValue(fullName, out var type) ? type : null;
}

static string StripAssemblyQualification(string assemblyQualifiedName)
{
    // Find the end of the type name portion (after any generic args in [[ ]])
    int depth = 0;
    for (int i = 0; i < assemblyQualifiedName.Length; i++)
    {
        if (assemblyQualifiedName[i] == '[') depth++;
        else if (assemblyQualifiedName[i] == ']') depth--;
        else if (assemblyQualifiedName[i] == ',' && depth == 0)
        {
            return assemblyQualifiedName[..i].Trim();
        }
    }
    return assemblyQualifiedName.Trim();
}

app.Run();

static string GetWritableAppDataRoot()
{
    var configuredRoot = Environment.GetEnvironmentVariable("SENTENCESTUDIO_APPDATA_ROOT");
    if (!string.IsNullOrWhiteSpace(configuredRoot))
    {
        return configuredRoot;
    }

    return string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
        ? Path.GetTempPath()
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}

// Marker types for ILogger<T> category names on top-level endpoint delegates.
internal sealed class PlansLog { }

// Enable WebApplicationFactory<Program> in integration tests
public partial class Program { }
