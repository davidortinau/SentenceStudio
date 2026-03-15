using Microsoft.AspNetCore.Identity;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// ASP.NET Core Identity user linked to the existing UserProfile.
/// Uses string PK (GUID) by default, which matches the CoreSync convention.
/// </summary>
public class ApplicationUser : IdentityUser
{
    public string? UserProfileId { get; set; }
    public UserProfile? UserProfile { get; set; }
    public string? DisplayName { get; set; }
}
