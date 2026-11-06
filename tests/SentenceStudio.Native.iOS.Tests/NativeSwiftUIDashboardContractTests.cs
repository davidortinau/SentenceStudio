using System.Runtime.CompilerServices;
using System.Xml.Linq;
using SentenceStudio.Native;
using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeSwiftUIDashboardContractTests
{
    [Fact]
    public void StudyLibrarySettingsAndSamAreIndependentSwiftUITabsWithNativeStacks()
    {
        // iOS tab shell trial (Captain, 2026-11-05).
        var scene = Source("App/ApplicationScene.cs");
        var library = Source("App/ApplicationScene.Library.cs");
        Assert.Equal(4, scene.Split("Views.Tab(", StringSplitOptions.None).Length - 1);
        Assert.Contains("Views.Tab(localize(\"Nav_Study\"), \"graduationcap\", _dashboardStack)", scene);
        Assert.Contains("Views.Tab(localize(\"Nav_Library\"), \"books.vertical\", library)", scene);
        Assert.Contains("Views.Tab(localize(\"Settings_Title\"), \"gearshape\", _settingsStack)", scene);
        Assert.Contains("\"bubble.left.and.bubble.right.fill\"", scene);
        Assert.Contains(".WithTabRole(SwiftTabRole.Search)", scene);
        // Shown to every signed-in owner. A tap that does not open Sam reads availability
        // again; only the server's answer says "not available", offline and an unreachable
        // server have their own text.
        Assert.Contains(".WithTabHidden(!_app.IsSamSlotShown)", scene);
        Assert.Contains("_app.SamSlotNoticeText, string.Empty,", scene);
        var libraryController = File.ReadAllText(Path.Combine(HeadPath, "App/ApplicationController.Library.cs"));
        // Only this read's answer decides; the shared mapping gives the persona-aware text.
        Assert.Contains("answer = await sam.RefreshAvailabilityAsync(token);", libraryController);
        Assert.Contains("NativeSamAvailabilityText.NoticeKey(answer)", libraryController);
        Assert.Contains("SamSlotText(NativeSamAvailabilityText.Checking)", libraryController);
        // Checking is the busy overlay (not a presentation); the alert presents once, with the answer.
        Assert.Contains("_starterOverlay.IsPresented = _app.IsBlockingOverlayPresented;", scene);
        var samScene = File.ReadAllText(Path.Combine(HeadPath, "App/ApplicationScene.Sam.cs"));
        Assert.Contains("notice.Text = _app.SamSlotNoticeText;", samScene);
        Assert.DoesNotContain("DispatchAfter", samScene);
        Assert.Contains(".WithTabViewStyle(SwiftTabViewStyle.SidebarAdaptable)", scene);
        // iPhone: the tab bar (Sam's slot with it) minimizes while a page scrolls down.
        Assert.Contains("if (NativeDeviceIdiom.IsPhone)\n"
            + "            _tabs.WithTabBarMinimizeBehavior(SwiftTabBarMinimizeBehavior.OnScrollDown);", scene);
        // Sam's check can be cancelled; while it shows it blocks navigation like any busy overlay.
        Assert.Contains("Views.StarterBlockingOverlay(\n"
            + "                _samUnavailableNotice, _app.BlockingOverlayTitle, _overlayCancel)", scene);
        Assert.Contains("\"native.tab.sam.checking.cancel\"", scene);
        Assert.Contains("_overlayCancel.WithHidden(!_renderedOverlayCancellable);", scene);
        Assert.Contains("|| IsSamSlotCheckingShown;", Source("App/ApplicationController.cs"));
        Assert.Contains("internal void CancelSamSlotCheck()", libraryController);
        Assert.Contains("if (_disposed || !ReferenceEquals(check, _samSlotCheck))", libraryController);
        foreach (var id in new[] { "native.tab.study", "native.tab.library", "native.tab.settings", "native.tab.sam" })
            Assert.Contains($"\"{id}\"", scene);
        Assert.Contains("Views.TabView(_app.SelectedTab, _app.SelectTab,", scene);
        Assert.Contains("if (crossedDesktopBreakpoint)", scene);
        Assert.Contains("if (_dashboardSlot is not null)", scene);
        Assert.Contains("Views.NavigationStack(_selectorSheet,", scene);
        Assert.Contains("Views.NavigationStack(_libraryRoot,", library);
        Assert.Contains("Views.NavigationStack(_settingsSlot,", scene);
        Assert.Contains("_dashboardStack.Push(\"quiz\", WithSamToolbar(_dashboardStack, \"quiz\", _quizSheet))", scene);
        Assert.Contains("_libraryStack.Push(\"resource-form\",", scene);
        Assert.Contains("_dashboardStack.CanNavigateBack =\n"
            + "                !_app.NavigationBlocked || _app.CanNavigateBackDuringQuizAudio;",
            scene);
        Assert.Contains("_libraryStack.CanNavigateBack = !_app.NavigationBlocked;", scene);
        Assert.Contains("_settingsStack.CanNavigateBack = !_app.NavigationBlocked;", scene);
        // Sam's slot is an action: the selection stays on the current tab.
        var tabs = Source("App/ApplicationController.Library.cs");
        Assert.Contains("if (index == NativeTabs.Sam)", tabs);
        Assert.Contains("OpenSamFromTab();", tabs);
        Assert.DoesNotContain("UINavigationController", scene);
        Assert.DoesNotContain("UITabBarController", scene);
    }

    [Fact]
    public void DashboardRefreshRegenerateAndSignOutUseActualPresenterCommands()
    {
        var scene = Source("App/ApplicationScene.cs");
        var app = Source("App/ApplicationController.cs");
        var toolbar = Source("Features/Dashboard/DashboardView.cs");

        Assert.Contains("WithAccessibilityIdentifier(\"native.dashboard.refresh\")",
            toolbar);
        Assert.Contains("Views.Toolbar(page,", toolbar);
        Assert.Contains("_app.RenderDashboardContent(_style)", scene);
        Assert.Contains(": Views.Image(\"arrow.clockwise\");", toolbar);
        Assert.Contains("Views.Button(indicator, refresh)", toolbar);
        Assert.Contains("new DashboardToolbarActions(RefreshDashboard, RegeneratePlan,\n"
            + "                OpenActivityLog));", app);
        // The More menu is gone: History and Regenerate (where it applies) are toolbar buttons;
        // Sign out is the last row of the Settings tab.
        Assert.DoesNotContain("Views.Menu(", toolbar);
        Assert.Contains("\"native.dashboard.regenerate\"", toolbar);
        Assert.Contains("\"native.study.history\"", toolbar);
        Assert.Contains("text(\"Nav_History\")", toolbar);
        Assert.Contains("if (canRegeneratePlan)", toolbar);
        Assert.Contains("\"native.settings.sign-out\"", Source("Features/Settings/SettingsView.cs"));
        Assert.Contains("SettingsView.SignOut(style, SignOutTitle, enabled: true, SignOut)", app);
        Assert.Contains("await _dashboard.RefreshAsync(_lifetime.Token)", app);
        Assert.Contains("await _dashboard!.RegenerateAsync(_lifetime.Token)", app);
        Assert.Contains("_auth.SignOutAsync(_lifetime.Token)", app);
        Assert.Contains("preferences.Remove(UserProfileRepository.ActiveProfileIdKey)",
            app);
        Assert.Contains("scope.SetActiveUser(null)", app);
    }

    [Fact]
    public void DashboardRefreshShowsProgressOnlyDuringTheRefreshAction()
    {
        var toolbar = Source("Features/Dashboard/DashboardView.cs");
        var app = Source("App/ApplicationController.cs");
        var refresh = app[app.IndexOf("internal void RefreshDashboard()", StringComparison.Ordinal)
            ..app.IndexOf("internal void RegeneratePlan()", StringComparison.Ordinal)];
        Assert.Contains("if (_disposed || _busy || _dashboard is null)", refresh);
        Assert.Contains("_dashboardRefreshing = true;", refresh);
        Assert.Contains("await _dashboard.RefreshAsync(_lifetime.Token)", refresh);
        Assert.Contains("finally\n            {\n                _dashboardRefreshing = false;",
            refresh);
        Assert.Contains("var indicator = isRefreshing", toolbar);
        Assert.Contains("Views.IndeterminateProgress(text(\"Common_Refresh\"))",
            toolbar);
        Assert.Contains("\"native.dashboard.refresh.spinner\"", toolbar);
        Assert.Contains("button.IsEnabled = !isBusy;", toolbar);
        Assert.Contains("_dashboardState.IsNewUser, _busy, _dashboardRefreshing,",
            app);
    }

    [Fact]
    public void DashboardFailure_FormatsSourcePlanErrorInsteadOfDisplayingPlaceholder()
    {
        var feature = Source("Features/Dashboard/TodaysPlanView.cs");
        var dashboard = Source("Features/Dashboard/DashboardView.cs");
        Assert.Contains("Views.Text(DashboardView.DashboardErrorText(state.ErrorKey, text))",
            feature);
        Assert.Contains("key == NativeText.PlanError", dashboard);
        Assert.Contains("string.Format(CultureInfo.CurrentCulture, localized,",
            dashboard);
        Assert.Contains("text(NativeText.UnexpectedError)", dashboard);

        var resx = XDocument.Load(Path.Combine(HeadPath,
            "../SentenceStudio.Shared/Resources/Strings/AppResources.resx"));
        string Localized(string key) => resx.Descendants("data")
            .Single(data => (string?)data.Attribute("name") == key)
            .Element("value")!.Value;
        var rendered = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            Localized(NativeText.PlanError),
            Localized(NativeText.UnexpectedError));
        Assert.DoesNotContain("{0}", rendered);
        Assert.Contains("Unable to load plan:", rendered);
    }

    [Fact]
    public void ReadyDashboardWithUnconfirmedSync_ShowsThePendingSyncText()
    {
        var dashboard = Source("Features/Dashboard/DashboardView.cs");
        var caption = dashboard[dashboard.IndexOf("if (state.HasPendingSync)", StringComparison.Ordinal)..];
        Assert.Contains("text(\"Native_SyncPending\")", caption);
        Assert.Contains("native.dashboard.sync.caption", caption);
        Assert.DoesNotContain("DebugHealth_CoreSync", dashboard);
    }

    [Fact]
    public void DashboardPendingSyncIsNotPassiveCopyAndProgressFitsPhoneViewport()
    {
        var feature = Source("Features/Dashboard/TodaysPlanView.cs");
        var dashboard = Source("Features/Dashboard/DashboardView.cs");
        Assert.DoesNotContain("native.plan.pending", feature);
        Assert.Contains("case NativePhase.PendingSync:", feature);
        Assert.Contains("native.dashboard.sync.pending", feature);
        Assert.Contains("WithAdaptiveStack(480)", feature);
        Assert.True(480 > 402 - 2 * SourceUiLayout.PageGutter(402)
            - 2 * SourceUiLayout.CardInset);
        var dashboardPage = dashboard[dashboard.IndexOf(
                "private static SwiftView DashboardPage(", StringComparison.Ordinal)
            ..dashboard.IndexOf("private static SwiftView ModeButton(",
                StringComparison.Ordinal)];
        Assert.Contains("Views.VStack(items.ToArray())", dashboardPage);
        Assert.Contains("Views.Scroll(content)", dashboardPage);
        Assert.Contains("WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))",
            dashboardPage);
        Assert.True(SourceUiTypography.For(SourceTextRole.Badge, 402, 1,
            "seoul-pop").LineHeight > 1);
        Assert.Equal(1, SourceUiTypography.For(SourceTextRole.BadgeSmall,
            402, 1, "seoul-pop").LineHeight);
    }

    [Fact]
    public void StudyInsightWholeCardTogglesWithAccessibleExpansionStatus()
    {
        var feature = Source("Features/Dashboard/PlanInsightView.cs");
        var app = Source("App/ApplicationController.cs");
        Assert.Contains("private bool _insightExpanded;", app);
        Assert.Contains("if (insightExpanded)", feature);
        Assert.Contains("Views.Button(", feature);
        Assert.Contains("toggleInsight)", feature);
        Assert.Contains("WithAccessibilityIdentifier(\"native.plan.insight.toggle\")",
            feature);
        Assert.Contains("Dashboard_InsightExpanded", feature);
        Assert.Contains("Dashboard_InsightCollapsed", feature);
        Assert.Contains("narrative.Story", feature);
        Assert.Contains("resource.SelectionReason", feature);
        Assert.Contains("vocab.TotalDue", feature);
        Assert.Contains("narrative.FocusAreas", feature);
        Assert.Contains("var hasStruggling = !state.StrugglingCategories.IsEmpty;",
            feature);
        Assert.Contains("\"arrow.turn.down.right\" : \"sparkles\"", feature);
        Assert.Contains("\"native.plan.insight.sample-words\"", feature);
        Assert.Contains("ToggleInsight: () =>", app);
        Assert.DoesNotContain("Preview plan vocabulary", feature);

        foreach (var resource in new[] { "AppResources.resx", "AppResources.ko.resx" })
        {
            var strings = XDocument.Load(Path.GetFullPath(Path.Combine(HeadPath,
                "../SentenceStudio.Shared/Resources/Strings", resource)));
            var keys = strings.Descendants("data")
                .Select(data => data.Attribute("name")?.Value).ToArray();
            Assert.Contains("Dashboard_InsightExpanded", keys);
            Assert.Contains("Dashboard_InsightCollapsed", keys);
        }
    }

    [Fact]
    public void PlanCardsRetainMetadataCompletionAndFullCardQuizActivation()
    {
        var feature = Source("Features/Dashboard/PlanCardView.cs");
        var progress = Source("Features/Dashboard/DashboardLearningProgressView.cs");
        var dashboard = Source("Features/Dashboard/DashboardView.cs");
        var app = Source("App/ApplicationController.Quiz.cs");

        Assert.Contains("card.ResourceTitle", feature);
        Assert.Contains("card.MinutesSpent", feature);
        Assert.Contains("card.IsCompleted", feature);
        Assert.Contains("card.Activity == PlanActivityType.VocabularyReview", feature);
        Assert.Contains("NativeDashboardCardAccessibility.Label(", feature);
        Assert.Contains("Views.Button(content,", feature);
        Assert.Contains("openQuiz!(card.Id);", feature);
        Assert.Contains("var canOpen = (card.Activity == PlanActivityType.VocabularyReview",
            feature);
        // Other plan types open only when registered in NativeActivityCatalog.
        Assert.Contains("|| NativeActivityCatalog.IsImplemented(card.Activity))", feature);
        Assert.DoesNotContain("!card.IsCompleted && openQuiz", feature);
        Assert.Contains(".WithAccessibilityLabel(accessibility)", feature);
        Assert.Contains("_dashboardState.Cards.FirstOrDefault(card => card.Id == cardId)",
            app);
        Assert.Contains("is not { Activity: PlanActivityType.VocabularyReview }",
            app);
        Assert.Contains("DashboardLearningProgressView.AddSections(items, state, style, text)",
            dashboard);
        Assert.Contains("VocabularyTile(summary.Known,", progress);
        Assert.DoesNotContain("native.plan.card.unavailable", feature);
    }

    [Fact]
    public void NewUserStarterUsesOwnedResourceCommandAndAddOpensRealForm()
    {
        var feature = Source("Features/Dashboard/DashboardWelcomeView.cs");
        var app = Source("App/ApplicationController.cs");
        var scene = Source("App/ApplicationScene.cs");

        Assert.Contains("Views.Text(text(\"Dashboard_QuickStart\"))", feature);
        Assert.Contains("native.dashboard.newuser.starter.card", feature);
        Assert.Contains("NativeDashboard_QuickStartDescription", feature);
        Assert.Contains("Dashboard_CreateYourOwnDescription", feature);
        Assert.Contains("Dashboard_CreateStarterResource", feature);
        Assert.Contains("CreateStarterAsync(_lifetime.Token)", app);
        Assert.Contains("string.IsNullOrWhiteSpace(profile.TargetLanguage)", app);
        Assert.Contains("_dashboardStarterErrorKey = \"NativeResources_SetTargetLanguage\";",
            app);
        Assert.DoesNotContain("NativeDashboard_ActivityNotAvailable", feature);
        Assert.Contains("text(\"Dashboard_AddAResource\"),",
            feature);
        Assert.Contains("AddResource: OpenResourceAddFromDashboard", app);
        Assert.Contains("_selectedTab = NativeTabs.Library;", app);
        // Study's Add opens the form above the Library's Resources page (the saved resource shows).
        Assert.Contains("_libraryResourcesOpen = true;", app);
        Assert.Contains("_resources.OpenCreate();", app);
        Assert.Contains("_libraryStack.Push(\"resource-form\",", scene);
    }

    [Fact]
    public void InsightResourcesLinkToOwnedDetailAndReturnToDashboard()
    {
        var feature = Source("Features/Dashboard/PlanInsightView.cs");
        var app = Source("App/ApplicationController.cs");
        var resources = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        Assert.Contains("openResource(resourceId);", feature);
        Assert.Contains("native.plan.insight.resource.{Uri.EscapeDataString(resourceId)}",
            feature);
        Assert.Contains("OpenResource: OpenResourceFromDashboard", app);
        Assert.Contains("_dashboardState.Narrative?.Resources.Any(resource =>", app);
        Assert.Contains("_resources.Open(resourceId);", app);
        Assert.Contains("ReturnToDashboardAfterResourceForm();", app);
        Assert.Contains("internal void Open(string id)", resources);

        Assert.Contains("vocab.PreviewWords.Count > 0", feature);
        Assert.Contains("text(\"Dashboard_PreviewPlanVocabulary\")", feature);
        // D07: the button opens Flashcard Preview for the shown plan date.
        Assert.Contains("() => previewPlanVocabulary?.Invoke())", feature);
        Assert.Contains("preview.IsEnabled = previewPlanVocabulary is not null;", feature);
        Assert.Contains("PreviewPlanVocabulary: OpenFlashcardPreview", app);
        var activities = Source("App/ApplicationController.Activities.cs");
        Assert.Contains(".FromPlanPreview(planDate);", activities);
        Assert.Contains("NativeActivityCatalog.FlashcardPreview => new FlashcardScreen(host)",
            Source("Features/Activities/ActivityScreens.cs"));
    }

    [Fact]
    public void VocabularyAndNumbersSectionsHaveIndependentStates()
    {
        var progress = Source("Features/Dashboard/DashboardLearningProgressView.cs");
        Assert.Contains("NativeDashboardPresenter.VocabularySectionErrorKey(state) is { } vocabError",
            progress);
        Assert.Contains("if (!state.NumbersApplicable && state.NumberErrorKey is null)",
            progress);
        Assert.Contains("\"native.dashboard.vocab.empty\"", progress);
    }

    [Fact]
    public void InsightProjectsAllThreeSourceCategoryTiersWithoutColorAlone()
    {
        var feature = Source("Features/Dashboard/PlanInsightView.cs");
        Assert.Contains("state.StrugglingCategories", feature);
        Assert.Contains("state.NewCategories", feature);
        Assert.Contains("state.DoingWellCategories", feature);
        Assert.Contains("native.plan.insight.struggling", feature);
        Assert.Contains("native.plan.insight.new", feature);
        Assert.Contains("native.plan.insight.doingwell", feature);
        Assert.Contains("category.AverageAccuracy:P0", feature);
        Assert.DoesNotContain("NativeDashboard_DoingWell", feature);
    }

    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void NativeDashboardCardsAndNumberSectionsHaveLocalizedCopy(string resource)
    {
        var strings = XDocument.Load(Path.GetFullPath(Path.Combine(HeadPath,
            "../SentenceStudio.Shared/Resources/Strings", resource)));
        var keys = strings.Descendants("data").Select(data =>
            data.Attribute("name")?.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var key in new[]
        {
            "NativeDashboard_QuickStartDescription",
            "NativeDashboard_NumbersMastery",
            "NativeDashboard_NumbersEmptyTitle",
            "NativeDashboard_NumbersEmptyDescription",
            "NativeDashboard_NumbersDue",
            "NativeDashboard_NumbersCorrect",
            "NativeResources_SetTargetLanguage",
            "NativeDashboard_VocabUnavailable",
            "Dashboard_PreviewPlanVocabulary"
        })
            Assert.Contains(key, keys);
    }

    [Fact]
    public void ChooseOwnUsesOwnerValidatedSelectionAndNativeSearchableSheet()
    {
        var feature = Source("Features/Dashboard/ChooseOwnView.cs");
        var dashboard = Source("Features/Dashboard/DashboardView.cs");
        var selector = Source("Features/Dashboard/ChooseOwnSelectorView.cs");
        var app = Source("App/ApplicationController.cs");
        var scene = Source("App/ApplicationScene.cs");

        Assert.Contains("actions.SelectMode(NativeDashboardMode.TodaysPlan)", dashboard);
        Assert.Contains("actions.SelectMode(NativeDashboardMode.ChooseOwn)", dashboard);
        Assert.Contains("state.Mode == NativeDashboardMode.ChooseOwn", dashboard);
        Assert.Contains("choice.SelectedResourceIds", feature);
        Assert.Contains("choice.SelectedSkillId", feature);
        Assert.Contains("choice.ShowAllActivities", feature);
        Assert.DoesNotContain("NativeDashboard_ActivityNotAvailable", feature);
        Assert.Contains("ChooseOwnSymbol(activity.Route)", feature);
        Assert.Contains("launchChooseOwnActivity!(activity.Route)", feature);
        Assert.Contains("native.dashboard.chooseown.retry", feature);
        Assert.Contains("_dashboard.SetMode(mode)", app);
        Assert.Contains("_dashboard.LoadChooseOwnAsync(_lifetime.Token)", app);
        Assert.Contains("_dashboard.SelectChooseOwn(", app);
        Assert.Contains("Run(OpenChooseOwnQuizAsync)", app);
        Assert.Contains("SelectChooseOwnQuizAsync(_lifetime.Token)",
            Source("App/ApplicationController.Quiz.cs"));
        Assert.Contains("GetRequiredService<NativeQuizLaunchService>()",
            Source("App/ApplicationController.Quiz.cs"));
        Assert.Contains(".CreateChooseOwnAsync(selection.UserId, selection.ResourceIds,",
            Source("App/ApplicationController.Quiz.cs"));
        Assert.Contains("state.SelectedResourceIds.ToList()", app);
        Assert.Contains("Views.List(items.ToArray())", selector);
        Assert.Contains("Views.Searchable(_selectorSlot,", scene);
        Assert.Contains("_selectorSheet = Views.Sheet(", scene);
        Assert.Contains("NativeDashboardPresenter.ChooseOwnActivities.Take(6)", feature);
    }

    [Fact]
    public void ChooseOwnActivityCataloguePreservesOriginalOrder()
    {
        Assert.Equal(
        [
            "conversation", "scene", "translation", "cloze", "writing", "diary",
            "reading", "vocab-quiz", "vocab-matching", "shadowing",
            "how-do-you-say", "minimal-pairs", "word-association", "numberdrill"
        ], NativeDashboardPresenter.ChooseOwnActivities.Select(activity =>
            activity.Route).ToArray());
    }

    private static string HeadPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string Source(string name) => File.ReadAllText(Path.Combine(
        HeadPath, name));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
