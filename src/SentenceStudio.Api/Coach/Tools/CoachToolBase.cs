using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// The shared scope rule for every read-only coach tool.
/// A tool resolves the trusted user before it reads any data.
/// A tool never accepts a user identifier as an argument.
/// </summary>
public abstract class CoachToolBase
{
    private readonly IUserScopeProvider _userScope;

    protected CoachToolBase(IUserScopeProvider userScope)
    {
        _userScope = userScope;
    }

    /// <summary>The name of the tool. The allow-list checks this name.</summary>
    public abstract string ToolName { get; }

    /// <summary>
    /// Resolves the trusted user for this request.
    /// Throws a typed unauthorized failure when the request has no user scope.
    /// Call this method first, before any data read.
    /// </summary>
    protected string RequireUserProfileId()
    {
        try
        {
            var id = _userScope.UserProfileId;
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new CoachToolException(
                    CoachToolFailureKind.Unauthorized, ToolName, "The request has no user scope.");
            }
            return id;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CoachToolException(
                CoachToolFailureKind.Unauthorized, ToolName, "The request has no user scope.", ex);
        }
    }

    /// <summary>Raises a typed invalid-argument failure.</summary>
    protected CoachToolException InvalidArgument(string reason) =>
        new(CoachToolFailureKind.InvalidArgument, ToolName, reason);

    /// <summary>
    /// Wraps a data failure as a typed failure.
    /// A tool never turns a data failure into an empty answer.
    /// </summary>
    protected CoachToolException DataAccessFailure(Exception inner) =>
        new(CoachToolFailureKind.DataAccess, ToolName, "The data read failed.", inner);

    /// <summary>
    /// Makes text from learner data safe to show in a tool answer.
    /// The method removes control characters and cuts the text to the limit.
    /// Imported metadata can hold instructions, so the tool treats it as data only.
    /// </summary>
    protected static string SanitizeMetadata(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Span<char> buffer = value.Length <= 512 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;
        foreach (var c in value)
        {
            buffer[length++] = char.IsControl(c) ? ' ' : c;
        }

        var cleaned = new string(buffer[..length]).Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }
}
