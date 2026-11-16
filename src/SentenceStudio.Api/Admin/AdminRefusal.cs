namespace SentenceStudio.Api.Admin;

/// <summary>
/// The closed set of reasons an admitted admin request did not do what was asked. Stored in the
/// audit log and returned as the problem's <c>code</c>, so the web app can react without parsing text.
/// </summary>
public static class AdminFailureCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string PasswordIncorrect = "password_incorrect";
    public const string LockedOut = "locked_out";
    public const string StepUpRequired = "step_up_required";
    public const string StepUpUnavailable = "step_up_unavailable";
    public const string NotAllowedForToken = "not_allowed_for_token";
    public const string TransitionRefused = "transition_refused";
    public const string Conflict = "conflict";
    public const string KeyRingNotDurable = "key_ring_not_durable";
    public const string TooManyTokens = "too_many_tokens";
    public const string Cancelled = "cancelled";
    public const string Exception = "exception";
}

/// <summary>Builds a refusal for an admitted admin and leaves its code for the audit row.</summary>
internal static class AdminRefusal
{
    private static readonly object FailureCodeKey = new();

    public static IResult Create(HttpContext http, int statusCode, string code, string detail)
    {
        http.Items[FailureCodeKey] = code;
        return Results.Problem(
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["code"] = code });
    }

    public static string? FindCode(HttpContext http) =>
        http.Items.TryGetValue(FailureCodeKey, out var code) ? code as string : null;
}

/// <summary>Reads an admin request's JSON body where the route, not the framework, answers a bad one.</summary>
internal static class AdminRequestBody
{
    /// <summary>
    /// The body as <typeparamref name="T"/>, or null when it is missing, is not JSON or is not
    /// the expected shape. Read here rather than bound by the framework so that a bad body is the
    /// route's own 400 with a code, in every environment.
    /// </summary>
    public static async Task<T?> ReadAsync<T>(HttpContext http, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await http.Request.ReadFromJsonAsync<T>(cancellationToken);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return null;
        }
    }
}
