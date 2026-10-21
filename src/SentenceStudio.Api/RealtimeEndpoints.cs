#pragma warning disable OPENAI002 // OpenAI.Realtime is experimental in the OpenAI SDK.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using SentenceStudio.Contracts.Ai;
using SentenceStudio.Services.Realtime;
using OaiRealtimeClient = OpenAI.Realtime.RealtimeClient;
using OaiRealtimeClientOptions = OpenAI.Realtime.RealtimeClientOptions;

namespace SentenceStudio.Api;

/// <summary>
/// Issues short-lived realtime client secrets so devices can open a voice session directly with the
/// model provider without holding a key or an Entra identity.
/// </summary>
/// <remarks>
/// This follows the provider guidance for mobile clients: standard credentials stay on the server,
/// and the device receives an ephemeral secret that is only good for opening one session for a
/// short time. The model is fixed when the secret is minted. Anything else the device sends in its
/// own session setup (instructions, tools) is visible to the device, so it must not carry secrets.
/// </remarks>
public static class RealtimeEndpoints
{
    public static IServiceCollection AddRealtimeClientSecrets(this IServiceCollection services)
    {
        services.AddSingleton<RealtimeClientSecretIssuer>();
        return services;
    }

    public static WebApplication MapRealtimeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/realtime/client-secret", IssueClientSecret).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> IssueClientSecret(
        ClaimsPrincipal user,
        RealtimeClientSecretIssuer issuer,
        CancellationToken cancellationToken)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var result = await issuer.IssueAsync(userId, cancellationToken);
        return result.Status switch
        {
            RealtimeSecretStatus.Issued => Results.Ok(result.Response),
            RealtimeSecretStatus.Throttled => Results.Problem(
                "Too many voice sessions started. Try again in a minute.",
                statusCode: StatusCodes.Status429TooManyRequests),
            _ => Results.Problem("Realtime voice is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }
}

internal enum RealtimeSecretStatus { Issued, Throttled, NotConfigured }

internal sealed record RealtimeSecretResult(RealtimeSecretStatus Status, RealtimeClientSecretResponse? Response = null);

/// <summary>Mints ephemeral realtime client secrets with the API's own Entra identity.</summary>
internal sealed class RealtimeClientSecretIssuer
{
    /// <summary>How long a secret can be used to open a session. Sessions themselves outlive it.</summary>
    internal static readonly TimeSpan SecretLifetime = TimeSpan.FromSeconds(120);

    /// <summary>Per-learner cap on new voice sessions, so a leaked app token cannot mint without limit.</summary>
    internal const int MaxSecretsPerWindow = 10;
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    private readonly IConfiguration _configuration;
    private readonly ILogger<RealtimeClientSecretIssuer> _logger;
    private readonly TokenCredential _credential = new DefaultAzureCredential();
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _recent = new();

    public RealtimeClientSecretIssuer(IConfiguration configuration, ILogger<RealtimeClientSecretIssuer> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<RealtimeSecretResult> IssueAsync(string userId, CancellationToken cancellationToken)
    {
        var endpoint = _configuration["AI:OpenAI:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new(RealtimeSecretStatus.NotConfigured);
        }

        if (!TryReserve(userId))
        {
            _logger.LogWarning("Realtime client secret throttled for user {UserId}", userId);
            return new(RealtimeSecretStatus.Throttled);
        }

        var model = RealtimeVoiceRegistration.RealtimeModel(_configuration);
        var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
        var client = new OaiRealtimeClient(
            new ApiKeyCredential(token.Token),
            new OaiRealtimeClientOptions { Endpoint = new Uri(endpoint) });

        var body = BinaryContent.Create(BinaryData.FromObjectAsJson(new
        {
            expires_after = new { anchor = "created_at", seconds = (int)SecretLifetime.TotalSeconds },
            session = new { type = "realtime", model },
        }));

        var minted = await client.CreateRealtimeClientSecretAsync(body, new RequestOptions { CancellationToken = cancellationToken });
        using var json = JsonDocument.Parse(minted.GetRawResponse().Content);
        var root = json.RootElement;

        var response = new RealtimeClientSecretResponse
        {
            ClientSecret = root.GetProperty("value").GetString() ?? string.Empty,
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("expires_at").GetInt64()),
            Endpoint = endpoint,
            Model = model,
            TranscriptionModel = RealtimeVoiceRegistration.TranscriptionModel(_configuration),
        };

        // Never log the secret itself.
        _logger.LogInformation("Issued realtime client secret for user {UserId}, model {Model}, expires {ExpiresAt:O}",
            userId, model, response.ExpiresAt);
        return new(RealtimeSecretStatus.Issued, response);
    }

    private bool TryReserve(string userId)
    {
        var now = DateTimeOffset.UtcNow;
        var queue = _recent.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= MaxSecretsPerWindow)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}
