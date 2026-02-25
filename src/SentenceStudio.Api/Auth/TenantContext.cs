using SentenceStudio.Domain.Abstractions;

namespace SentenceStudio.Api.Auth;

public sealed class TenantContext : ITenantContext
{
    public string? TenantId { get; set; }
    public string? UserId { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
}
