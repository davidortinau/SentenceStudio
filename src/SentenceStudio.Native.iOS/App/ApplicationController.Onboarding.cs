using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using SentenceStudio.Native.iOS.Features.Onboarding;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// Onboarding name suggestions (source Onboarding.razor LoadSuggestedNames) and
/// the Dashboard's route back to onboarding for a learner with no target language
/// (source Index.razor CreateStarterResourceAsync).
/// </summary>
internal sealed partial class ApplicationController
{
    private ImmutableArray<string> _onboardingNames = [];
    private bool _onboardingNamesLoading;
    private CancellationTokenSource? _onboardingNamesLoad;

    private OnboardingNameSuggestions? OnboardingSuggestions =>
        _onboardingStep == 3
            ? new(_onboardingNamesLoading, _onboardingNames, ChooseOnboardingName)
            : null;

    /// <summary>A suggested name fills the name field (source: name = sn).</summary>
    private void ChooseOnboardingName(string name)
    {
        if (_busy || _destination != NativeDestination.Onboarding || _onboardingStep != 3)
            return;
        _onboardingDraft = _onboardingDraft with { Name = name };
        Publish();
    }

    private void ResetOnboardingNames()
    {
        _onboardingNamesLoad?.Cancel();
        _onboardingNamesLoad?.Dispose();
        _onboardingNamesLoad = null;
        _onboardingNames = [];
        _onboardingNamesLoading = false;
    }

    /// <summary>
    /// Runs beside the onboarding steps, never through the busy gate, so the
    /// learner can type a name or go on while the names load. A newer request,
    /// or leaving onboarding, drops an older result.
    /// </summary>
    private async void LoadOnboardingNames(string targetLanguage)
    {
        if (_auth is null || _disposed)
            return;
        ResetOnboardingNames();
        var load = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _onboardingNamesLoad = load;
        _onboardingNamesLoading = true;
        var names = ImmutableArray<string>.Empty;
        try
        {
            names = await _auth.SuggestOnboardingNamesAsync(targetLanguage, load.Token);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Onboarding name suggestions could not be loaded.");
        }
        if (!ReferenceEquals(_onboardingNamesLoad, load) || _disposed)
            return;
        _onboardingNamesLoading = false;
        _onboardingNames = _destination == NativeDestination.Onboarding ? names : [];
        Publish();
    }

    /// <summary>
    /// The shared presenter moves a Dashboard learner whose profile has no
    /// target language to onboarding; the draft is read again from the profile.
    /// </summary>
    private async Task<bool> RouteToOnboardingForMissingTargetLanguageAsync()
    {
        if (_auth is null
            || !await _auth.BeginOnboardingForMissingTargetLanguageAsync(_lifetime.Token))
            return false;
        _sam?.Close();
        _onboardingOwner = null;
        _authState = _auth.State;
        await RouteAfterAuthAsync();
        return _destination == NativeDestination.Onboarding;
    }
}
