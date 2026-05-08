using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Infrastructure;

internal static class ProfileTestSeed
{
    /// <summary>
    /// Inserts a UserProfile directly via DbContext (bypasses repository to keep
    /// seed deterministic and to avoid triggering smart-resource side effects).
    /// </summary>
    public static async Task<UserProfile> SeedProfileAsync(
        IServiceProvider services,
        string id,
        string? displayName = null,
        string? email = null,
        string targetLanguage = "Korean",
        string nativeLanguage = "English",
        string? displayLanguage = "English",
        string? targetCefrLevel = "A1",
        int preferredSessionMinutes = 20)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profile = new UserProfile
        {
            Id = id,
            Name = displayName ?? $"Test {id}",
            Email = email ?? $"{id}@test.local",
            NativeLanguage = nativeLanguage,
            TargetLanguage = targetLanguage,
            DisplayLanguage = displayLanguage,
            TargetCEFRLevel = targetCefrLevel,
            PreferredSessionMinutes = preferredSessionMinutes,
            CreatedAt = DateTime.UtcNow
        };

        db.UserProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }
}
