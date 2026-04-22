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
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.Tokens;
using OpenAI;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SentenceStudio;
using SentenceStudio.Abstractions;
using SentenceStudio.Api;
using SentenceStudio.Api.Auth;
using SentenceStudio.Api.Platform;
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
    && builder.Configuration.GetValue("Auth:EnableDevAuthFallback", true);
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

builder.Services.AddScoped<ITenantContext, TenantContext>();

// Platform abstractions for API server
var appDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sentencestudio", "api");
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

// Vocabulary progress services
builder.Services.AddSingleton<VocabularyProgressRepository>();
builder.Services.AddSingleton<VocabularyLearningContextRepository>();
builder.Services.AddSingleton<VocabularyProgressService>();
builder.Services.AddSingleton<IVocabularyProgressService>(provider =>
    provider.GetRequiredService<VocabularyProgressService>());

var openAiApiKey = builder.Configuration["AI:OpenAI:ApiKey"];
if (!string.IsNullOrWhiteSpace(openAiApiKey))
{
    builder.Services.AddSingleton<IChatClient>(_ =>
        new OpenAIClient(openAiApiKey).GetChatClient("gpt-4o-mini").AsIChatClient());
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
    builder.Services.AddSingleton(new ElevenLabsClient(elevenLabsKey));
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
    }

    // Apply CoreSync provisioning (creates change-tracking tables if missing)
    var syncProvider = app.Services.GetRequiredService<ISyncProvider>();
    await syncProvider.ApplyProvisionAsync();
}

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
app.UseCoreSyncHttpServer(optionsConfigure: options =>
{
    // CoreSync syncs per-user data, so its endpoints must enforce the same auth pipeline as the rest of the API.
    options.AllEndpoints = endpoint => endpoint.RequireAuthorization();
});

// Auth endpoints (anonymous — they handle login/register)
app.MapAuthEndpoints();

// YouTube channel monitoring endpoints
app.MapChannelEndpoints();
app.MapImportEndpoints();

// Feedback endpoints (GitHub issue creation)
app.MapFeedbackEndpoints();

// Version and release notes endpoints (public)
app.MapVersionEndpoints();

app.MapGet("/api/v1/auth/bootstrap", (ClaimsPrincipal user, ITenantContext tenantContext) =>
    Results.Ok(new BootstrapResponse
    {
        TenantId = tenantContext.TenantId ?? user.FindFirstValue("tenant_id"),
        UserId = tenantContext.UserId ?? user.FindFirstValue(ClaimTypes.NameIdentifier),
        DisplayName = tenantContext.DisplayName ?? user.FindFirstValue(ClaimTypes.Name),
        Email = tenantContext.Email ?? user.FindFirstValue(ClaimTypes.Email)
    }))
    .RequireAuthorization();

app.MapPost("/api/v1/ai/chat", async (ChatRequest request, [FromServices] IChatClient? chatClient, CancellationToken cancellationToken) =>
    {
        if (chatClient == null)
        {
            return Results.Problem("OpenAI client is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var responseType = ResolveResponseType(request.ResponseType);
        // If type resolution fails, fall back to string/text response rather than 400.
        // The client prompt already requests JSON format, so the client can parse it.

        if (responseType == null || responseType == typeof(string))
        {
            var options = string.IsNullOrWhiteSpace(request.Scenario)
                ? null
                : new ChatOptions { Instructions = request.Scenario };

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

        var imageBytes = Convert.FromBase64String(request.ImageBase64);
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
            ? "21m00Tcm4TlvDq8ikWAM"
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

app.MapPost("/api/v1/plans/generate", (GeneratePlanRequest request) =>
        Results.Ok(BuildPlanResponse(request)))
    .RequireAuthorization();

app.MapPost("/api/v1/vocabulary/{wordId}/status", async (
    string wordId,
    SetVocabularyStatusRequest request,
    ITenantContext tenantContext,
    IVocabularyProgressService progressService) =>
{
    var userId = tenantContext.UserId;
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
    Type responseType,
    CancellationToken cancellationToken)
{
    var method = typeof(Program)
        .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
        .FirstOrDefault(m => m.Name.Contains(nameof(GetTypedResponseCoreAsync)) && m.IsGenericMethodDefinition)
        ?? throw new InvalidOperationException($"Could not find method {nameof(GetTypedResponseCoreAsync)} via reflection.");

    var genericMethod = method.MakeGenericMethod(responseType);

    var task = (Task<object?>)genericMethod.Invoke(null, new object?[] { chatClient, message, instructions, cancellationToken })!;
    return await task;
}

static async Task<object?> GetTypedResponseCoreAsync<T>(
    IChatClient chatClient,
    string message,
    string? instructions,
    CancellationToken cancellationToken)
{
    var options = string.IsNullOrWhiteSpace(instructions)
        ? null
        : new ChatOptions { Instructions = instructions };

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

    // Try full assembly-qualified name first (works for simple and generic types)
    var resolved = Type.GetType(responseType, throwOnError: false);
    if (resolved != null) return resolved;

    // Strip outer assembly info while preserving generic type arguments.
    // For generic types like "List`1[[..., Assembly]], mscorlib, ..."
    // we need to strip only the OUTER assembly, not the inner ones.
    var normalized = StripOuterAssembly(responseType);
    resolved = Type.GetType(normalized, throwOnError: false);
    if (resolved != null) return resolved;

    // Scan loaded assemblies
    return AppDomain.CurrentDomain
        .GetAssemblies()
        .Select(assembly => assembly.GetType(normalized, throwOnError: false))
        .FirstOrDefault(type => type != null);
}

static string StripOuterAssembly(string aqn)
{
    // Find the end of the type name portion (after any generic args in [[ ]])
    int depth = 0;
    for (int i = 0; i < aqn.Length; i++)
    {
        if (aqn[i] == '[') depth++;
        else if (aqn[i] == ']') depth--;
        else if (aqn[i] == ',' && depth == 0)
        {
            return aqn[..i].Trim();
        }
    }
    return aqn.Trim();
}

app.Run();

// Enable WebApplicationFactory<Program> in integration tests
public partial class Program { }
