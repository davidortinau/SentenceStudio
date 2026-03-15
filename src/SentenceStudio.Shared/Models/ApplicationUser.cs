using Microsoft.AspNetCore.Identity;

namespace SentenceStudio.Shared.Models;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
    public string? UserProfileId { get; set; }
}
