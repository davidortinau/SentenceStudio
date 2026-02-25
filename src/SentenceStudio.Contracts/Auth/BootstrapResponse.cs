namespace SentenceStudio.Contracts.Auth;

public sealed class BootstrapResponse
{
    public string? TenantId { get; set; }
    public string? UserId { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
}
