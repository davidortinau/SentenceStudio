using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

public sealed partial class NativeDashboardPresenter
{
    /// <summary>
    /// Source CreateStarterResourceAsync step 1 (Index.razor:826-837): when the
    /// Dashboard Quick Start has created a starter resource and the owner has no
    /// skill profile, create "{target} Basics". Only the Dashboard Quick Start
    /// calls this; onboarding and the Resources starter keep their own rules.
    /// Returns false, and writes nothing, without an active owner, a target
    /// language, or when the save fails.
    /// </summary>
    public async Task<bool> EnsureStarterSkillAsync(CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var owner))
        {
            logger.LogWarning("Native starter skill refused an absent owner.");
            return false;
        }

        string? target;
        using (var lease = plans.ForOwner(owner))
            target = await lease.Database.UserProfiles.AsNoTracking()
                .Where(profile => profile.Id == owner)
                .Select(profile => profile.TargetLanguage)
                .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(target))
            return false;

        var owned = await skills.ListAsync(owner);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryCurrentUser(out var current) || current != owner)
            return false;
        if (owned.Count > 0)
            return true;

        var saved = await skills.SaveAsync(new SkillProfile
        {
            Title = $"{target} Basics",
            Description = $"Foundational {target} vocabulary and grammar",
            Language = target
        }, owner, triggerSync: false);
        if (string.IsNullOrWhiteSpace(saved))
        {
            logger.LogWarning("Native starter skill profile could not be saved.");
            return false;
        }
        pendingWrites.MarkPending(owner);
        return true;
    }

    /// <summary>
    /// Source sidebar entries (NavMenu.razor, for example Minimal Pairs) open
    /// their page with no resource or skill. Returns that empty selection for the
    /// active owner and the user-local day, or null without an owner.
    /// </summary>
    public NativeQuizLaunch? SelectNavigationEntry()
    {
        if (!TryCurrentUser(out var owner))
            return null;
        using var lease = plans.ForOwner(owner);
        return new NativeQuizLaunch(owner, lease.LocalDate, string.Empty, [], null, []);
    }
}
