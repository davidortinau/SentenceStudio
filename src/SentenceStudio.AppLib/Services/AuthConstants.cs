namespace SentenceStudio.Services;

/// <summary>
/// Shared authentication constants used by the auth service and HTTP handler.
/// </summary>
public static class AuthConstants
{
    public static readonly string[] DefaultScopes =
    [
        "api://8c051bcf-bd3a-4051-9cd3-0556ba5df2d8/user.read",
        "api://8c051bcf-bd3a-4051-9cd3-0556ba5df2d8/sync.readwrite"
    ];
}
