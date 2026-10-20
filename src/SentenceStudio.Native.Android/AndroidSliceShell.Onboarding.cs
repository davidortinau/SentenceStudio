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
        if (_onboardingStep == 3
            && string.IsNullOrWhiteSpace(OnboardingName.Value.Text))
            return;
        _onboardingStep = Math.Min(_onboardingStep + 1, 5);
    });

    private void OnboardingPrevious() => Publish(() =>
        _onboardingStep = Math.Max(_onboardingStep - 1, 0));

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
                    content.Add(Body(Localize("Onboarding_GeneratingNames"), palette));
                else if (_suggestedNames.Length > 0)
                {
                    content.Add(Body(Localize("Onboarding_OrChooseSuggested"),
                        palette, secondary: true));
                    foreach (var suggestion in _suggestedNames)
                    {
                        var name = suggestion;
                        content.Add(OnboardingOption(name,
                            OnboardingName.Value.Text == name,
                            $"native.auth.onboarding.suggestion.{Array.IndexOf(_suggestedNames, name)}",
                            () => OnboardingName.Value =
                                ComposeExtensions.NewTextFieldValue(name)));
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
                content.Add(StatusAlert(Localize("NativeResources_Unavailable"),
                    palette, error: true,
                    automationId: "native.auth.onboarding.unavailable"));
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
