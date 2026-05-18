using Microsoft.AspNetCore.Http;
using SentenceStudio.Services.Plans;
using SentenceStudio.Contracts;

namespace SentenceStudio.Api.Plans;

/// <summary>
/// API-side <see cref="IUserScopeProvider"/>. Reads the <c>user_profile_id</c>
/// claim off the current request principal and fails closed (throws
/// <see cref="UnauthorizedAccessException"/>) when missing. Registered as a
/// scoped service so each request gets its own resolution.
/// </summary>
public sealed class HttpUserScopeProvider : IUserScopeProvider
{
    private readonly IHttpContextAccessor _httpContext;

    public HttpUserScopeProvider(IHttpContextAccessor httpContext)
    {
        _httpContext = httpContext;
    }

    public string UserProfileId
    {
        get
        {
            if (TryGetUserProfileId(out var id))
            {
                return id;
            }
            throw new UnauthorizedAccessException(
                "No authenticated user profile is present on the current request.");
        }
    }

    public bool TryGetUserProfileId(out string userProfileId)
    {
        var principal = _httpContext.HttpContext?.User;
        var id = principal?.FindFirst(AuthClaimTypes.UserProfileId)?.Value;
        if (string.IsNullOrWhiteSpace(id))
        {
            userProfileId = string.Empty;
            return false;
        }
        userProfileId = id;
        return true;
    }
}
