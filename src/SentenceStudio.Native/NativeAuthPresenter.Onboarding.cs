using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

public sealed partial class NativeAuthPresenter
{
    public async Task<NativeOnboardingResult> SaveOnboardingProfileAsync(
        NativeOnboardingDraft draft, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (draft is null || string.IsNullOrWhiteSpace(draft.Name)
                || !NativeOnboardingOptions.NativeLanguages.Contains(draft.NativeLanguage)
                || !NativeOnboardingOptions.TargetLanguages.Contains(draft.TargetLanguage)
                || !NativeOnboardingOptions.SessionMinutes.Contains(draft.PreferredSessionMinutes)
                || !NativeOnboardingOptions.CefrLevels.Contains(draft.TargetCefrLevel))
                return new(NativeOnboardingOutcome.Invalid,
                    "NativeOnboarding_Incomplete");
            if (!TryOnboardingOwner(out var owner)
                || !await HasPublishedTokenOwnerAsync(owner, cancellationToken))
                return new(NativeOnboardingOutcome.Blocked,
                    NativeText.InvalidCredentials);

            var changed = await profiles.SaveOnboardingProfileForOwnerAsync(
                owner, draft.Name, draft.NativeLanguage, draft.TargetLanguage,
                draft.PreferredSessionMinutes, draft.TargetCefrLevel, auth.UserName,
                cancellationToken);
            if (changed > 0)
                pendingWrites.MarkPending(owner);
            if (!TryOnboardingOwner(out var currentOwner) || currentOwner != owner)
                return new(NativeOnboardingOutcome.Blocked,
                    NativeText.InvalidCredentials);
            return changed >= 0
                ? new(NativeOnboardingOutcome.Success, ContentStepsDone: 1)
                : new(NativeOnboardingOutcome.Failed,
                    "NativeOnboarding_SaveFailed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Native onboarding profile could not be saved.");
            return new(NativeOnboardingOutcome.Failed,
                "NativeOnboarding_SaveFailed");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeOnboardingResult> FinishOnboardingAsync(
        bool createStarter, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!TryOnboardingOwner(out var owner)
                || !await HasPublishedTokenOwnerAsync(owner, cancellationToken))
                return new(NativeOnboardingOutcome.Blocked,
                    NativeText.InvalidCredentials);
            var profile = await profiles.GetByIdAsync(owner, cancellationToken);
            if (!TryOnboardingOwner(out var currentOwner) || currentOwner != owner)
                return new(NativeOnboardingOutcome.Blocked,
                    NativeText.InvalidCredentials);
            if (profile is null || string.IsNullOrWhiteSpace(profile.Name)
                || string.IsNullOrWhiteSpace(profile.NativeLanguage)
                || string.IsNullOrWhiteSpace(profile.TargetLanguage))
                return new(NativeOnboardingOutcome.Invalid,
                    "NativeOnboarding_Incomplete");

            var steps = 1;
            if (createStarter)
            {
                var starter = await resources.CreateStarterAsync(cancellationToken);
                if (starter.Result is not (NativeResourceMutationResult.Success
                    or NativeResourceMutationResult.AlreadyPresent))
                    return new(NativeOnboardingOutcome.Failed,
                        starter.ErrorKey ?? "NativeOnboarding_StarterFailed", steps);
                if (!TryOnboardingOwner(out currentOwner) || currentOwner != owner)
                    return new(NativeOnboardingOutcome.Blocked,
                        NativeText.InvalidCredentials, steps);
                steps++;
                var ownedSkills = await skills.ListAsync(owner);
                if (!TryOnboardingOwner(out currentOwner) || currentOwner != owner)
                    return new(NativeOnboardingOutcome.Blocked,
                        NativeText.InvalidCredentials, steps);
                if (ownedSkills.Count == 0)
                {
                    var skillId = await skills.SaveAsync(new SkillProfile {
                        Title = $"Beginner {profile.TargetLanguage}",
                        Language = profile.TargetLanguage
                    }, owner, triggerSync: false);
                    if (string.IsNullOrWhiteSpace(skillId))
                        return new(NativeOnboardingOutcome.Failed,
                            "NativeOnboarding_StarterFailed", steps);
                    pendingWrites.MarkPending(owner);
                }
                steps++;
            }

            if (!await HasPublishedTokenOwnerAsync(owner, cancellationToken)
                || !TryOnboardingOwner(out currentOwner) || currentOwner != owner)
                return new(NativeOnboardingOutcome.Blocked,
                    NativeText.InvalidCredentials, steps);
            preferences.Set("is_onboarded", true);
            SetState(new(NativePhase.Ready, NativeDestination.Dashboard, owner,
                HasPendingSync: pendingWrites.IsPending(owner)));
            return new(NativeOnboardingOutcome.Success, ContentStepsDone: steps);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Native onboarding completion failed.");
            return new(NativeOnboardingOutcome.Failed,
                "NativeOnboarding_StarterFailed");
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryOnboardingOwner(out string owner)
    {
        if (State is {
                Phase: NativePhase.Ready,
                Destination: NativeDestination.Onboarding,
                UserId: { } scopedOwner }
            && _published is { } published && scope.IsCurrent(published)
            && scope.TryGetUserProfileId(out var active)
            && string.Equals(active, scopedOwner, StringComparison.Ordinal)
            && string.Equals(
                preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
                scopedOwner, StringComparison.Ordinal))
        {
            owner = scopedOwner;
            return true;
        }
        owner = string.Empty;
        return false;
    }
}
