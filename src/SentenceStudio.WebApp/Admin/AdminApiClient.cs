using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SentenceStudio.WebApp.Admin;

/// <summary>Who the admin portal is talking to, as <c>GET /api/admin/session</c> reports it.</summary>
public sealed record AdminSession(
    string UserId,
    string? Email,
    string? DisplayName,
    DateTimeOffset? StepUpExpiresAtUtc);

/// <summary>A password-again proof, as <c>POST /api/admin/step-up</c> issues it.</summary>
public sealed record AdminStepUpProof(string Proof, DateTimeOffset ExpiresAtUtc);

/// <summary>One audit row, as <c>GET /api/admin/audit</c> reports it.</summary>
public sealed record AdminAuditRow(
    long Id,
    DateTime OccurredAtUtc,
    string ActorKind,
    string ActorId,
    string? ActorEmail,
    string Action,
    string? TargetKind,
    string? TargetId,
    string Outcome,
    int? StatusCode,
    string? FailureCode);

/// <summary>One page of audit rows, newest first.</summary>
public sealed record AdminAuditPage(IReadOnlyList<AdminAuditRow> Items, long? NextBefore);

/// <summary>The audit log's wire codes. Mirrors the API's <c>AdminAuditWire</c>.</summary>
public static class AdminAuditCodes
{
    public const string ActorAdmin = "admin";
    public const string ActorAgentToken = "agent_token";
    public const string OutcomeStarted = "started";
    public const string OutcomeSucceeded = "succeeded";
    public const string OutcomeFailed = "failed";
}

/// <summary>Whether the signed-in person is an admin, as far as one probe could tell.</summary>
public enum AdminAccess
{
    /// <summary>The API could not be asked, or gave an answer that is neither yes nor no.</summary>
    Unknown = 0,

    NotAdmin = 1,

    Admin = 2
}

/// <summary>How an admin API call ended. The zero value is a failure, never a success.</summary>
public enum AdminApiStatus
{
    /// <summary>The call failed for a reason the API did not name.</summary>
    Failed = 0,

    Ok = 1,

    /// <summary>
    /// The API answered 404: this person is not an admin (or no longer is), or the thing asked
    /// for does not exist. The two are the same answer on purpose.
    /// </summary>
    NotFound = 2,

    /// <summary>The API admitted the caller and refused the request; <c>Code</c> says why.</summary>
    Refused = 3
}

/// <summary>An admin API call's outcome.</summary>
/// <param name="Code">The API's failure code for a refusal, for example <c>password_incorrect</c>.</param>
public readonly record struct AdminApiResult<T>(AdminApiStatus Status, T? Value, string? Code = null)
{
    public bool IsOk => Status == AdminApiStatus.Ok && Value is not null;
}

/// <summary>
/// Typed client for the API's <c>/api/admin</c> routes, called as the signed-in person.
/// </summary>
/// <remarks>
/// <para>
/// The API decides who is an admin on every call. This client never throws for a refusal: a 404
/// is normal traffic, because every learner's sidebar asks <see cref="ProbeAsync"/> once. A call
/// that could not be made is reported as that (<see cref="AdminApiStatus.Failed"/>,
/// <see cref="AdminAccess.Unknown"/>), never as a refusal.
/// </para>
/// <para>
/// Registered without the standard resilience pipeline. Every admitted call leaves an audit row
/// and a failed password check counts toward lockout, so one click must be one request. That
/// pipeline also held the only short timeout, so the client keeps its own
/// (<see cref="RequestTimeout"/>): an API that accepts the connection and never answers becomes
/// "not answered" in seconds, not after HttpClient's default 100.
/// </para>
/// </remarks>
public sealed class AdminApiClient
{
    /// <summary>The header a password-again proof travels in. Mirrors the API's constant.</summary>
    public const string ProofHeader = "X-Admin-Proof";

    /// <summary>How long one admin request may take before it counts as not answered.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>This client's deadline for one request. Only a test sets it.</summary>
    public TimeSpan Deadline { get; init; } = RequestTimeout;

    private const string BasePath = "/api/admin";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<AdminApiClient> _logger;

    public AdminApiClient(HttpClient httpClient, ILogger<AdminApiClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Whether the signed-in person is an admin right now. Leaves no audit row.</summary>
    public async Task<AdminAccess> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var deadline = StartDeadline(cancellationToken);
            using var response = await _httpClient.GetAsync($"{BasePath}/access", deadline.Token);
            switch (response.StatusCode)
            {
                case HttpStatusCode.NoContent:
                    return AdminAccess.Admin;
                case HttpStatusCode.NotFound:
                    return AdminAccess.NotAdmin;
                default:
                    _logger.LogWarning("[Admin] The access probe answered {StatusCode}", (int)response.StatusCode);
                    return AdminAccess.Unknown;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("[Admin] The access probe failed. ExceptionType={ExceptionType}", ex.GetType().Name);
            return AdminAccess.Unknown;
        }
    }

    public Task<AdminApiResult<AdminSession>> GetSessionAsync(
        string? proof, CancellationToken cancellationToken = default) =>
        SendAsync<AdminSession>(HttpMethod.Get, $"{BasePath}/session", body: null, proof, cancellationToken);

    public Task<AdminApiResult<AdminStepUpProof>> StepUpAsync(
        string password, CancellationToken cancellationToken = default) =>
        SendAsync<AdminStepUpProof>(
            HttpMethod.Post, $"{BasePath}/step-up", JsonContent.Create(new { password }), proof: null, cancellationToken);

    public Task<AdminApiResult<AdminAuditPage>> GetAuditAsync(
        long? before, int take, CancellationToken cancellationToken = default) =>
        SendAsync<AdminAuditPage>(
            HttpMethod.Get,
            before is { } cursor ? $"{BasePath}/audit?take={take}&before={cursor}" : $"{BasePath}/audit?take={take}",
            body: null, proof: null, cancellationToken);

    private async Task<AdminApiResult<T>> SendAsync<T>(
        HttpMethod method, string path, HttpContent? body, string? proof, CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = StartDeadline(cancellationToken);
            using var request = new HttpRequestMessage(method, path) { Content = body };
            if (!string.IsNullOrEmpty(proof))
            {
                request.Headers.TryAddWithoutValidation(ProofHeader, proof);
            }

            using var response = await _httpClient.SendAsync(request, deadline.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var value = await response.Content.ReadFromJsonAsync<T>(SerializerOptions, deadline.Token);
                return value is null
                    ? new AdminApiResult<T>(AdminApiStatus.Failed, default)
                    : new AdminApiResult<T>(AdminApiStatus.Ok, value);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new AdminApiResult<T>(AdminApiStatus.NotFound, default);
            }

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                return new AdminApiResult<T>(
                    AdminApiStatus.Refused, default, await ReadCodeAsync(response, deadline.Token));
            }

            _logger.LogWarning("[Admin] {Method} {Path} answered {StatusCode}", method, path, (int)response.StatusCode);
            return new AdminApiResult<T>(AdminApiStatus.Failed, default);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Shape only: a request body here can hold a password.
            _logger.LogWarning("[Admin] {Method} {Path} failed. ExceptionType={ExceptionType}",
                method, path, ex.GetType().Name);
            return new AdminApiResult<T>(AdminApiStatus.Failed, default);
        }
    }

    /// <summary>
    /// The caller's cancellation plus this client's deadline. A request that runs out of time is
    /// reported as failed; only the caller's own cancellation is thrown back to the caller.
    /// </summary>
    private CancellationTokenSource StartDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        return deadline;
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("code", out var code)
                   && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
