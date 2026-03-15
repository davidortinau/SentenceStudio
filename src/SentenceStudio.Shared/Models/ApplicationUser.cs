#if !IOS && !ANDROID && !MACCATALYST
using Microsoft.AspNetCore.Identity;
#endif

namespace SentenceStudio.Shared.Models;

#if IOS || ANDROID || MACCATALYST
// On mobile, ApplicationUser is a plain DTO — Identity types aren't available
public class ApplicationUser
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? UserName { get; set; }
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? UserProfileId { get; set; }
}
#else
public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
    public string? UserProfileId { get; set; }
}
#endif
