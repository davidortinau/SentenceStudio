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
