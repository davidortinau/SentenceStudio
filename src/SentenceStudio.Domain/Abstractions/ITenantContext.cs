namespace SentenceStudio.Domain.Abstractions;

public interface ITenantContext
{
    string? TenantId { get; }
    string? UserId { get; }
    string? DisplayName { get; }
    string? Email { get; }
}
