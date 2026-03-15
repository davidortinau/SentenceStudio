using System.ComponentModel.DataAnnotations;

namespace SentenceStudio.Shared.Models;

public class RefreshToken
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public string Token { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }

    public ApplicationUser? User { get; set; }
}
