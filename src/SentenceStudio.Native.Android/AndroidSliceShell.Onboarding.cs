using System.Collections.Immutable;
using System.Globalization;
using AndroidX.Compose;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Android;

internal sealed partial class AndroidSliceShell
{
    private static ImmutableArray<string> OnboardingLanguages =>
        NativeOnboardingOptions.NativeLanguages;
    private static ImmutableArray<string> TargetLanguages =>
        NativeOnboardingOptions.TargetLanguages;
    private static ImmutableArray<int> SessionMinutes =>
        NativeOnboardingOptions.SessionMinutes;
    private static ImmutableArray<string> CefrLevels =>
        NativeOnboardingOptions.CefrLevels;

    private async Task LoadOnboardingAsync(
        string? owner, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner) || _scope is null)
            return;
        var trusted = _scope.ServiceProvider
            .GetRequiredService<DeviceUserScopeProvider>();
        if (!trusted.TryGetUserProfileId(out var active)
            || !string.Equals(active, owner, StringComparison.Ordinal))
            return;

        var profile = await _scope.ServiceProvider
            .GetRequiredService<UserProfileRepository>()
            .GetByIdAsync(owner, cancellationToken);
        if (!trusted.TryGetUserProfileId(out active)
            || !string.Equals(active, owner, StringComparison.Ordinal))
            return;
        Publish(() =>
        {
            if (Destination != NativeDestination.Onboarding
                || Auth.UserId != owner)
                return;
            _onboardingStep = 0;
            _onboardingMinutes = 15;
            _onboardingCefr = "A2";
            _onboardingStepsDone = 0;
            _onboardingFinishing = false;
            _onboardingCreatingStarter = false;
            CancelSuggestedNames();
            OnboardingName.Value = ComposeExtensions.NewTextFieldValue(
                profile?.Name ?? DisplayName.Value.Text ?? string.Empty);
            NativeLanguage.Value = OnboardingLanguages.Contains(profile?.NativeLanguage)
                ? profile!.NativeLanguage : "English";
            TargetLanguage.Value = TargetLanguages.Contains(profile?.TargetLanguage)
                ? profile!.TargetLanguage : "Korean";
            _suggestedNames = [];
        });
    }

    private void OnboardingNext() => Publish(() =>
    {
        if (Busy.Value || _onboardingFinishing
            || _onboardingStep == 3
                && string.IsNullOrWhiteSpace(OnboardingName.Value.Text))
            return;
        // Source Onboarding.razor NextStep: leaving the target language step loads names.
        if (_onboardingStep == 2)
            LoadSuggestedNames(TargetLanguage.Value);
        _onboardingStep = Math.Min(_onboardingStep + 1, 5);
    });

    private void OnboardingPrevious() => Publish(() =>
    {
        if (Busy.Value || _onboardingFinishing)
            return;
        _onboardingStep = Math.Max(_onboardingStep - 1, 0);
    });

    private CancellationTokenSource? _suggestedNamesLoad;
    private int _onboardingStepsDone;
    private bool _onboardingFinishing;
    private bool _onboardingCreatingStarter;

    private void CancelSuggestedNames()
    {
        _suggestedNamesLoad?.Cancel();
        _suggestedNamesLoad?.Dispose();
        _suggestedNamesLoad = null;
        _suggestedNames = [];
        _loadingSuggestedNames = false;
    }

    /// <summary>
    /// Source LoadSuggestedNames, beside the steps and outside the action gate so
    /// the learner can type or go on while it runs; a newer request wins.
    /// </summary>
    private void LoadSuggestedNames(string targetLanguage)
    {
        if (_auth is not { } auth)
            return;
        CancelSuggestedNames();
        var load = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _suggestedNamesLoad = load;
        _loadingSuggestedNames = true;
        _ = LoadSuggestedNamesAsync(auth, targetLanguage, load);
    }

    private async Task LoadSuggestedNamesAsync(
        NativeAuthPresenter auth, string targetLanguage, CancellationTokenSource load)
    {
        ImmutableArray<string> names = [];
        try
        {
            names = await auth.SuggestOnboardingNamesAsync(targetLanguage, load.Token);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("SentenceStudio.Native",
                $"Onboarding name suggestions could not be loaded ({ex.GetType().Name}).");
        }
        Publish(() =>
        {
            if (!ReferenceEquals(_suggestedNamesLoad, load))
                return;
            _loadingSuggestedNames = false;
            _suggestedNames = Destination == NativeDestination.Onboarding
                ? [.. names] : [];
        });
    }

    /// <summary>
    /// Source CreateStarterContent / FinishOnboarding through the shared presenter:
    /// save the profile on the server and device, optionally create the starter
    /// resource and skill, then return to the Dashboard. A failure keeps the
    /// learner on this step with the reason; a changed owner returns to sign-in.
    /// </summary>
    private void FinishOnboarding(bool createStarter) => Execute(async cancellationToken =>
    {
        if (_auth is not { } auth || Destination != NativeDestination.Onboarding
            || _onboardingFinishing)
            return;
        var draft = new NativeOnboardingDraft(
            OnboardingName.Value.Text?.Trim() ?? string.Empty,
            NativeLanguage.Value, TargetLanguage.Value,
            _onboardingMinutes, _onboardingCefr);
        Publish(() =>
        {
            _onboardingFinishing = true;
            _onboardingCreatingStarter = createStarter;
            _onboardingStepsDone = 0;
        });
        try
        {
            var saved = await auth.SaveOnboardingProfileAsync(draft, cancellationToken);
            Publish(() => _onboardingStepsDone = saved.ContentStepsDone);
            if (saved.Outcome != NativeOnboardingOutcome.Success)
            {
                await HandleOnboardingFailureAsync(auth, saved, cancellationToken);
                return;
            }
            var completed = await auth.FinishOnboardingAsync(createStarter, cancellationToken);
            Publish(() => _onboardingStepsDone = completed.ContentStepsDone);
            if (completed.Outcome != NativeOnboardingOutcome.Success)
            {
                await HandleOnboardingFailureAsync(auth, completed, cancellationToken);
                return;
            }
            await HandleAuthAsync(auth.State, cancellationToken);
        }
        finally
        {
            Publish(() => _onboardingFinishing = false);
        }
    });

    private async Task HandleOnboardingFailureAsync(NativeAuthPresenter auth,
        NativeOnboardingResult result, CancellationToken cancellationToken)
    {
        if (result.Outcome == NativeOnboardingOutcome.Blocked)
        {
            await HandleAuthAsync(auth.State, cancellationToken);
            return;
        }
        ShowError(Localize(result.ErrorKey is "Resources_StarterFailed"
            ? "NativeOnboarding_StarterFailed"
            : result.ErrorKey ?? "NativeOnboarding_SaveFailed"));
    }

    /// <summary>
    /// Source Index.razor CreateStarterResourceAsync: the shared presenter sends a
    /// Dashboard learner whose profile has no target language to onboarding.
    /// </summary>
    private async Task<bool> RouteToOnboardingForMissingTargetLanguageAsync(
        CancellationToken cancellationToken)
    {
        if (_auth is not { } auth
            || !await auth.BeginOnboardingForMissingTargetLanguageAsync(cancellationToken))
            return false;
        Post(CloseSam);
        await HandleAuthAsync(auth.State, cancellationToken);
        return true;
    }

    private ComposableNode RenderOnboarding(AndroidVisualPalette palette)
    {
        var content = new List<ComposableNode>();
        if (Auth.HasPendingSync)
            content.Add(StatusAlert(
                $"{Localize("DebugHealth_CoreSync")}: " +
                Localize("DebugHealth_Pending"), palette,
                automationId: "native.auth.onboarding.pending"));
        switch (_onboardingStep)
        {
            case 0:
                content.Add(Heading(Localize("Onboarding_Welcome"), palette, 27));
                content.Add(Body(Localize("Onboarding_SetupJourney"), palette));
                content.Add(Action(Localize("Onboarding_GetStarted"),
                    "native.auth.onboarding.get-started", OnboardingNext, !Busy.Value));
                break;
            case 1:
                content.Add(Heading(Localize("Onboarding_NativeLanguage"), palette, 23));
                foreach (var language in OnboardingLanguages)
                {
                    var selection = language;
                    content.Add(OnboardingOption(selection,
                        NativeLanguage.Value == selection,
                        $"native.auth.onboarding.native.{selection}",
                        () =>
                        {
                            NativeLanguage.Value = selection;
                            OnboardingNext();
                        }));
                }
                break;
            case 2:
                content.Add(Heading(Localize("Onboarding_TargetLanguage"), palette, 23));
                foreach (var language in TargetLanguages)
                {
                    var selection = language;
                    content.Add(OnboardingOption(selection,
                        TargetLanguage.Value == selection,
                        $"native.auth.onboarding.target.{selection}",
                        () =>
                        {
                            TargetLanguage.Value = selection;
                            OnboardingNext();
                        }));
                }
                break;
            case 3:
                content.Add(Heading(Localize("Onboarding_YourName"), palette, 23));
                content.Add(new OutlinedTextField(
                    OnboardingName, enabled: !Busy.Value, singleLine: true)
                {
                    Modifier = Modifier.FillMaxWidth()
                        .TestTag("native.auth.onboarding.name"),
                    Label = new Text(Localize("Onboarding_EnterName"))
                });
                if (_loadingSuggestedNames)
                    content.Add(new Row(Arrangement.SpacedBy(8),
                        Alignment.Vertical.CenterVertically)
                    {
                        Modifier.FillMaxWidth()
                            .TestTag("native.auth.onboarding.names.loading"),
                        new CircularProgressIndicator { Modifier = Modifier.Size(16) },
                        Body(Localize("Onboarding_GeneratingNames"), palette, secondary: true),
                    });
                else if (_suggestedNames.Length > 0)
                {
                    content.Add(Body(Localize("Onboarding_OrChooseSuggested"),
                        palette, secondary: true));
                    // Source: a two-column grid (col-6) of name buttons.
                    for (var start = 0; start < _suggestedNames.Length; start += 2)
                    {
                        var row = new Row(Arrangement.SpacedBy(8))
                        {
                            Modifier.FillMaxWidth()
                        };
                        for (var index = start; index < start + 2; index++)
                        {
                            if (index >= _suggestedNames.Length)
                            {
                                row.Add(new Box { Modifier.Weight(1f) });
                                continue;
                            }
                            var name = _suggestedNames[index];
                            row.Add(OnboardingOption(name,
                                OnboardingName.Value.Text == name,
                                $"native.auth.onboarding.suggestion.{index}",
                                () => Publish(() => OnboardingName.Value =
                                    ComposeExtensions.NewTextFieldValue(name)),
                                inline: true));
                        }
                        content.Add(row);
                    }
                }
                break;
            case 4:
                content.Add(Heading(Localize("Onboarding_LearningPreferences"), palette, 23));
                content.Add(Body(Localize("Onboarding_StudyDuration"), palette));
                for (var start = 0; start < SessionMinutes.Length; start += 3)
                {
                    var options = new Row(Arrangement.SpacedBy(8))
                    {
                        Modifier.FillMaxWidth()
                    };
                    for (var index = start;
                         index < Math.Min(start + 3, SessionMinutes.Length); index++)
                    {
                        var minutes = SessionMinutes[index];
                        options.Add(OnboardingOption(
                            string.Format(CultureInfo.CurrentCulture,
                                "{0} {1}", minutes, Localize("Common_MinutesShort")),
                            _onboardingMinutes == minutes,
                            $"native.auth.onboarding.minutes.{minutes}",
                            () => Publish(() => _onboardingMinutes = minutes),
                            inline: true));
                    }
                    content.Add(options);
                }
                content.Add(Body(Localize("Onboarding_TargetLevel"), palette));
                for (var start = 0; start < CefrLevels.Length; start += 3)
                {
                    var options = new Row(Arrangement.SpacedBy(8))
                    {
                        Modifier.FillMaxWidth()
                    };
                    for (var index = start;
                         index < Math.Min(start + 3, CefrLevels.Length); index++)
                    {
                        var level = CefrLevels[index];
                        options.Add(OnboardingOption(level,
                            _onboardingCefr == level,
                            $"native.auth.onboarding.cefr.{level}",
                            () => Publish(() => _onboardingCefr = level),
                            inline: true));
                    }
                    content.Add(options);
                }
                break;
            case 5:
                content.Add(Heading(Localize("Onboarding_AllSet"), palette, 23));
                content.Add(Body(Localize("Onboarding_CreateStarterContent"), palette));
                if (_onboardingFinishing)
                    content.Add(RenderOnboardingProgress(palette));
                else
                {
                    content.Add(Action(Localize("Onboarding_CreateStarter"),
                        "native.auth.onboarding.finish.starter",
                        () => FinishOnboarding(createStarter: true), !Busy.Value));
                    content.Add(Action(Localize("Onboarding_SkipSetup"),
                        "native.auth.onboarding.finish.skip",
                        () => FinishOnboarding(createStarter: false), !Busy.Value,
                        primary: false));
                }
                break;
        }

        if (!string.IsNullOrWhiteSpace(Error.Value))
            content.Add(StatusAlert(Error.Value, palette, error: true,
                automationId: "native.auth.onboarding.error"));

        if (_onboardingStep is > 0 and < 5)
        {
            content.Add(Action(Localize("Onboarding_Back"),
                "native.auth.onboarding.back", OnboardingPrevious,
                !Busy.Value, primary: false));
            content.Add(Action(Localize("Onboarding_Next"),
                "native.auth.onboarding.next", OnboardingNext,
                !Busy.Value && (_onboardingStep != 3
                    || !string.IsNullOrWhiteSpace(OnboardingName.Value.Text))));
        }
        content.Add(RenderOnboardingSteps(palette));
        content.Add(Action(Localize("Nav_Logout"),
            "native.auth.onboarding.signout", SignOut,
            !Busy.Value, primary: false));

        var viewport = new Column(Arrangement.SpacedBy(16))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_onboardingScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding,
                    vertical: AndroidVisualTheme.PagePadding)
                .TestTag("native.auth.onboarding")
        };
        foreach (var item in content)
            viewport.Add(item);
        return new Column
        {
            Modifier.FillMaxSize(),
            ScreenHeader(Localize("Onboarding_Welcome"), palette,
                back: _onboardingStep > 0),
            viewport
        };
    }

    private ComposableNode OnboardingOption(
        string label, bool selected, string tag, Action command, bool inline = false)
    {
        var modifier = (inline ? Modifier.Weight(1f) : Modifier.FillMaxWidth())
            .HeightIn(min: AndroidVisualTheme.ControlHeight)
            .TestTag(tag)
            .Semantics(semantics =>
            {
                semantics.ContentDescription(label);
                semantics.Selected(selected);
            });
        if (selected)
        {
            var button = new AndroidX.Compose.Button(
                command, enabled: !Busy.Value)
            {
                Modifier = modifier
            };
            button.Add(new Text(label));
            return button;
        }
        var outlined = new OutlinedButton(command, enabled: !Busy.Value)
        {
            Modifier = modifier
        };
        outlined.Add(new Text(label));
        return outlined;
    }

    /// <summary>
    /// Source Onboarding.razor:117-166: saving the profile, then (with the
    /// starter) generating vocabulary and creating the skill profile; each row is
    /// done, in progress or waiting.
    /// </summary>
    private ComposableNode RenderOnboardingProgress(AndroidVisualPalette palette)
    {
        string[] steps = _onboardingCreatingStarter
            ? ["Onboarding_SavingProfile", "Onboarding_GeneratingVocab",
                "Onboarding_CreatingSkillProfile"]
            : ["Onboarding_SavingProfile"];
        var rows = new List<ComposableNode>();
        for (var index = 0; index < steps.Length; index++)
        {
            var label = Localize(steps[index]);
            ComposableNode marker = index < _onboardingStepsDone
                ? new Icon(Icons.Default.CheckCircle, null)
                {
                    Modifier = Modifier.Size(20),
                    Tint = palette.Success,
                }
                : index == _onboardingStepsDone
                    ? new CircularProgressIndicator { Modifier = Modifier.Size(18) }
                    : new Box { Modifier.Size(16).Clip(8).Background(palette.Border) };
            var state = Localize(index < _onboardingStepsDone ? "NativeOnboarding_StepDone"
                : index == _onboardingStepsDone ? "NativeOnboarding_StepInProgress"
                : "NativeOnboarding_StepWaiting");
            rows.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth()
                    .TestTag($"native.auth.onboarding.progress.{index}")
                    .Semantics(semantics => semantics.ContentDescription($"{label}, {state}")),
                marker,
                Body(label, palette),
            });
        }
        return Card(palette, "native.auth.onboarding.progress", [.. rows]);
    }

    private ComposableNode RenderOnboardingSteps(AndroidVisualPalette palette)
    {
        var steps = new Row(Arrangement.Center,
            Alignment.Vertical.CenterVertically)
        {
            Modifier = Modifier.FillMaxWidth()
                .TestTag("native.auth.onboarding.step")
                .Semantics(semantics => semantics.ContentDescription(
                    $"{_onboardingStep + 1} / 6"))
        };
        for (var index = 0; index < 6; index++)
        {
            steps.Add(new Box
            {
                Modifier = Modifier.Padding(horizontal: 4)
                    .Size(8).Clip(4)
                    .Background(index == _onboardingStep
                        ? palette.Primary : palette.Border)
            });
        }
        return steps;
    }
}
