using System.Collections.Immutable;
using SentenceStudio.Contracts.Coach;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Sam;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// Wires the shared <see cref="NativeSamPresenter"/> into the native shell. Sam
/// keeps its own busy state so a running turn never blocks app navigation, and
/// the presenter, not this head, decides owner, offline and availability.
/// </summary>
internal sealed partial class ApplicationController
{
    private NativeSamPresenter? _sam;
    private SamActions? _samActions;

    internal NativeSamState SamState => _sam?.State ?? new NativeSamState();

    /// <summary>
    /// Source CoachSurfaceGate: signed in, past onboarding, not syncing. Every
    /// screen of the signed-in tab shell qualifies (the Dashboard and Resources
    /// tabs, the screens pushed over them, the quiz and every activity); sign-in,
    /// the post-login sync and onboarding do not.
    /// </summary>
    internal static bool IsSamShellDestination(NativeDestination destination) =>
        destination is NativeDestination.Dashboard or NativeDestination.VocabularyQuiz
            or NativeDestination.QuizSummary or NativeDestination.Activity;

    /// <summary>Sam is offered to this owner on the current screen (open or not).</summary>
    internal bool IsSamAvailable =>
        IsSamShellDestination(_destination) && !_preview && SamState.IsVisible;

    internal bool IsSamEntryVisible => IsSamAvailable && !SamState.IsOpen;

    /// <summary>
    /// The review quiz opens over the Study tab's root, so an activity, the quiz, the
    /// History page pushed over Study, or an editor with unsaved edits on another tab
    /// (a resource, a word, a skill, a new minimal pair, the profile) must close first.
    /// A smart resource being read is not an editor.
    /// </summary>
    internal bool IsSamReviewBlockedByScreen =>
        _destination != NativeDestination.Dashboard || IsActivityLogOpen
        || IsProfileEditing || HasLibraryEditor;

    internal bool IsSamPresented => IsSamShellDestination(_destination)
        && SamState.IsOpen;

    internal SamActions SamActions => _samActions ??= new SamActions(
        Open: () => RunSam(sam => sam.OpenAsync(
            _samCompactDefault ? NativeSamPanelSize.Compact : NativeSamPanelSize.Expanded, _lifetime.Token)),
        Close: () => _sam?.Close(),
        ToggleSize: () => _sam?.SetSize(SamState.Size == NativeSamPanelSize.Compact
            ? NativeSamPanelSize.Expanded : NativeSamPanelSize.Compact),
        ToggleConversations: () => RunSam(sam => sam.ToggleConversationsAsync(_lifetime.Token)),
        NewConversation: () => RunSam(sam => sam.NewConversationAsync(_lifetime.Token)),
        OpenConversation: id => RunSam(sam => sam.OpenConversationAsync(id, _lifetime.Token)),
        DraftChanged: text => _sam?.SetDraft(text),
        Send: () => RunSam(sam => sam.SendAsync(_lifetime.Token)),
        Retry: () => RunSam(sam => sam.RetryAsync(_lifetime.Token)),
        Stop: () => RunSam(sam => sam.StopAsync(_lifetime.Token)),
        Dismiss: () => _sam?.DismissNotice(),
        ReloadPlan: () => RunSam(sam => sam.ReloadPlanAsync(_lifetime.Token)),
        Accept: () => RunSam(sam => sam.AcceptSuggestionAsync(_lifetime.Token)),
        Reject: () => RunSam(sam => sam.RejectSuggestionAsync(_lifetime.Token)),
        Undo: () => RunSam(sam => sam.UndoAsync(_lifetime.Token)),
        Starter: (starter, text) => RunSam(sam => sam.SendStarterAsync(starter, text, _lifetime.Token)),
        ToggleEvidence: id => _sam?.ToggleEvidence(id),
        ApproveSet: () => RunSam(sam => sam.ApproveVocabularySetAsync(_lifetime.Token)),
        DeclineSet: () => RunSam(sam => sam.DeclineVocabularySetAsync(_lifetime.Token)),
        StartReview: () => _sam?.StartVocabularyReview(),
        OpenWord: wordId => _sam?.OpenListedWord(wordId),
        StartActivity: () => _sam?.StartActivityLaunch(),
        CancelActivity: () => _sam?.CancelActivityCountdown(),
        DismissActivity: () => _sam?.DismissActivityLaunch(),
        Writes: new SamWriteActions(
            Accept: id => RunSam(sam => sam.AcceptWriteAsync(id, _lifetime.Token)),
            Review: id => RunSam(sam => sam.BeginWriteConfirmationAsync(id, _lifetime.Token)),
            Confirm: () => RunSam(sam => sam.ConfirmWriteAsync(_lifetime.Token)),
            CancelConfirm: () => _sam?.CancelWriteConfirmation(),
            Decline: id => RunSam(sam => sam.DeclineWriteAsync(id, _lifetime.Token)),
            Undo: id => RunSam(sam => sam.UndoWriteAsync(id, _lifetime.Token)),
            Refresh: id => RunSam(sam => sam.RefreshWriteAsync(id, _lifetime.Token))),
        Memory: new SamMemoryActions(
            Save: () => RunSam(sam => sam.SaveMemoryCandidateAsync(_lifetime.Token)),
            BeginEdit: () => _sam?.BeginMemoryCandidateEdit(),
            EditChanged: text => _sam?.SetMemoryCandidateEdit(text),
            SaveEdited: () => RunSam(sam => sam.SaveEditedMemoryCandidateAsync(_lifetime.Token)),
            CancelEdit: () => _sam?.CancelMemoryCandidateEdit(),
            Decline: () => RunSam(sam => sam.DeclineMemoryCandidateAsync(_lifetime.Token))),
        Messages: new SamMessageActions(
            Copy: id => RunSam(sam => CopySamMessageAsync(sam, id)),
            OpenReport: id => _sam?.OpenReport(id),
            SelectReason: reason => _sam?.SelectReportReason(reason),
            SubmitReport: () => RunSam(sam => sam.SubmitReportAsync(_lifetime.Token)),
            CancelReport: () => _sam?.CancelReport()),
        ManageConversations: new SamConversationActions(
            Begin: (kind, id) => _sam?.BeginConversationDialog(kind, id),
            RenameChanged: text => _sam?.SetRenameText(text),
            Confirm: () => RunSam(sam => sam.ConfirmConversationDialogAsync(_lifetime.Token)),
            Cancel: () => _sam?.CancelConversationDialog(),
            Reopen: id => RunSam(sam => sam.ReopenConversationAsync(id, _lifetime.Token))));

    private SamSettingsActions? _samSettingsActions;

    internal SamSettingsActions SamSettingsActions => _samSettingsActions ??= new SamSettingsActions(
        AskDelete: () => EnsureSam()?.AskDeleteHistory(),
        CancelDelete: () => _sam?.CancelDeleteHistory(),
        ConfirmDelete: () => RunSam(sam => sam.ConfirmDeleteHistoryAsync(_lifetime.Token)),
        SelectTab: tab => _sam?.SelectMemoryTab(tab),
        Approve: id => RunSam(sam => sam.ApproveMemoryAsync(id, _lifetime.Token)),
        Reject: id => RunSam(sam => sam.RejectMemoryAsync(id, _lifetime.Token)),
        Forget: id => RunSam(sam => sam.ForgetMemoryAsync(id, _lifetime.Token)),
        BeginEdit: id => _sam?.BeginMemoryEdit(id),
        EditChanged: text => _sam?.SetMemoryEditText(text),
        SaveEdit: () => RunSam(sam => sam.SaveMemoryEditAsync(_lifetime.Token)),
        CancelEdit: () => _sam?.CancelMemoryEdit(),
        AskForgetAll: () => _sam?.AskForgetAllMemories(),
        CancelForgetAll: () => _sam?.CancelForgetAllMemories(),
        ConfirmForgetAll: () => RunSam(sam => sam.ForgetAllMemoriesAsync(_lifetime.Token)));

    /// <summary>
    /// Settings re-renders on Learning Coach or memory changes only; typed edit text is
    /// excluded so the field keeps focus while the learner types.
    /// </summary>
    internal int SamSettingsVersion => HashCode.Combine(SamState.SafeSettings,
        SamState.SafeMemory with { EditText = string.Empty });

    /// <summary>Settings offers the Learning Coach (not in the preview, nor before services exist).</summary>
    internal bool IsSamSettingsShown => !(_preview || _sam is null && _services is null);

    internal IEnumerable<SwiftView>? RenderSamSettings(SourceUiStyle style) =>
        !IsSamSettingsShown ? null
            : SamSettingsView.Sections(style, SamState, Text, SamSettingsActions);

    /// <summary>Opening Settings reads availability and what Sam remembers for the current owner.</summary>
    private void OpenSamSettings()
    {
        if (_preview)
            return;
        RunSam(async sam =>
        {
            await sam.RefreshAvailabilityAsync(_lifetime.Token);
            await sam.LoadMemoryAsync(_lifetime.Token);
        });
    }

    /// <summary>Copy uses the Essentials clipboard (non-UI service); the control states the outcome.</summary>
    private async Task CopySamMessageAsync(NativeSamPresenter sam, string messageId)
    {
        if (sam.CopyTextFor(messageId) is not { } text)
            return;
        try
        {
            await NativeClipboard.SetTextAsync(text);
            sam.ReportCopyResult(messageId, copied: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Sam message could not be copied.");
            sam.ReportCopyResult(messageId, copied: false);
        }
    }

    private bool _samCompactDefault = true;
    private double _samViewportWidth;

    /// <summary>The panel opens compact below 992 pt and expanded at or above it (source J1).</summary>
    internal void SetSamViewport(double width)
    {
        _samViewportWidth = width;
        _samCompactDefault = width < 992;
    }

    internal void DismissSam() => _sam?.Close();

    private NativeSamPresenter? EnsureSam()
    {
        if (_sam is not null || _services is null)
            return _sam;
        _sam = _services.Provider.GetRequiredService<NativeSamPresenter>();
        _sam.ScreenContext = SamScreenContext;
        _sam.Changed += Publish;
        _sam.PlanChanged += OnSamPlanChanged;
        _sam.VocabularyReviewRequested += OnSamVocabularyReview;
        _sam.WordRequested += OnSamWordRequested;
        _sam.ActivityRequested += OnSamActivityRequested;
        // An activity opens by itself only from the Study tab, with nothing else open, and never
        // under VoiceOver: the reply has to be read before anything moves.
        _sam.CanAutoOpenActivity = () => !NativeSystemState.IsVoiceOverRunning
            && _destination == NativeDestination.Dashboard && !IsActivityOpen && !NavigationBlocked;
        return _sam;
    }

    /// <summary>
    /// What the shell is showing, for a bug report filed through Sam and for Sam's
    /// get_current_screen: the most specific open screen, shared screen names with Android, the
    /// shell state behind it, and what that screen shows (<see cref="NativeSamScreen"/>), all private.
    /// </summary>
    private SentenceStudio.Contracts.Coach.CoachClientScreenContext SamScreenContext()
    {
        var screen =
            IsVocabularyWordOpen ? "vocabulary.word-form"
            : IsVocabularyOpen ? "vocabulary.list"
            : IsSkillFormOpen ? "skills.form"
            : IsSkillsOpen ? "skills.list"
            : IsResourcesFormOpen ? "resources.form"
            : IsLibraryResourcesOpen ? "resources.list"
            : IsLibraryPairsOpen ? "library.minimal-pairs"
            : IsYouTubeChannelOpen ? "youtube.channel"
            : IsYouTubeImportOpen ? "youtube.import"
            : IsImportWordOpen ? "import.word-form"
            : IsImportContentOpen ? "import.content"
            : IsProfileOpen && _selectedTab == NativeTabs.Settings ? "profile"
            : IsActivityLogOpen ? "activity-log"
            : IsRecentLookupsOpen ? "recent-lookups"
            : NativeSamScreenContext.ForDestination(_destination, _selectedTab switch
            {
                NativeTabs.Library => NativeSamScreenContext.LibraryTab,
                NativeTabs.Settings => NativeSamScreenContext.SettingsTab,
                _ => NativeSamScreenContext.StudyTab
            });

        var shown = SamScreenSnapshot(screen);

        // The app's own choice first; the system style only when the app follows it, and only on the
        // main thread, where the system may be asked (NativeSystemState answers null elsewhere).
        var theme = Appearance.Selection.Mode switch
        {
            SentenceStudio.Contracts.Theme.ThemeMode.Light => "light",
            SentenceStudio.Contracts.Theme.ThemeMode.Dark => "dark",
            _ => NativeSystemState.IsSystemStyleDark switch
            {
                true => "dark",
                false => "light",
                null => "system"
            }
        };
        return NativeSamScreenContext.Build(
            "ios",
            screen,
            typeof(ApplicationController).Assembly,
            "iOS " + NativeSystemState.SystemVersion,
            NativeSystemState.Model,
            theme,
#if DEBUG
            debugBuild: true,
#else
            debugBuild: false,
#endif
            [
                ("destination", _destination.ToString()),
                ("tab", _selectedTab.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("activity", _activity?.GetType().Name),
                ("samPanel", SamState.Size.ToString()),
            ],
            shown);
    }

    /// <summary>
    /// What <paramref name="screen"/> shows, from the presenter behind it. Chosen from the screen
    /// name itself, so the description can never be of a different screen from the one named.
    /// </summary>
    private NativeSamScreenSnapshot SamScreenSnapshot(string screen)
    {
        object? source = screen switch
        {
            "vocabulary.word-form" => _vocabulary,
            "skills.form" => _skills,
            "resources.form" or "resources.detail" => _resources?.Presenter,
            "library.minimal-pairs" => _libraryPairs?.SamSource,
            "activity" => _activity?.SamSource,
            "vocabulary-quiz" or "quiz-summary" => _quiz,
            NativeSamScreenContext.StudyTab => _dashboard,
            _ => null
        };
        var shown = NativeSamScreen.Describe(source);
        if (screen != "activity" || _activity is null || _activityLaunch is not { } launch)
            return shown;
        // The activity itself and what it was opened with, after what it shows. Named by its
        // route, not a localized title, so every client names the same activity the same way.
        return NativeSamScreen.WithLaunch(shown, launch, null);
    }

    private void RefreshSamAvailability()
    {
        if (_preview)
            return;
        RunSam(sam => sam.RefreshAvailabilityAsync(_lifetime.Token));
    }

    private void ClearSamForAccountBoundary() => _sam?.ClearForAccountBoundary();

    private async void RunSam(Func<NativeSamPresenter, Task> action)
    {
        if (_disposed || EnsureSam() is not { } sam)
            return;
        try
        {
            await action(sam);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Native iOS Sam action failed.");
        }
        Publish();
    }

    /// <summary>A server-side plan revision must reach the device's Today's Plan (sync, then reload).</summary>
    private void OnSamPlanChanged()
    {
        if (_dashboard is null || _destination != NativeDestination.Dashboard)
            return;
        _ = RefreshDashboardForSamAsync();
    }

    private async Task RefreshDashboardForSamAsync()
    {
        try
        {
            _dashboardRefreshing = true;
            Publish();
            _dashboardState = await _dashboard!.RefreshAsync(_lifetime.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_disposed)
        {
            _logger?.LogWarning(ex, "Dashboard refresh after a Sam plan change failed.");
        }
        finally
        {
            _dashboardRefreshing = false;
            Publish();
        }
    }

    /// <summary>
    /// Start Vocab Review on a Sam-approved set: the set was created on the server,
    /// so it must sync to this device before the native Vocabulary Quiz validates it
    /// against the owner's local resources.
    /// </summary>
    private void OnSamVocabularyReview(string owner, string resourceId)
    {
        if (IsSamReviewBlockedByScreen)
        {
            if (IsSamShellDestination(_destination))
                _sam?.ReportVocabularyReviewBlockedByScreen();
            return;
        }
        Run(async () =>
        {
            if (_services is null || _destination != NativeDestination.Dashboard)
                return;
            var outcome = await _services.Provider.GetRequiredService<INativeSyncReadiness>()
                .SynchronizeAsync(owner, _lifetime.Token);
            var launch = outcome == NativeSyncOutcome.Confirmed
                ? await _services.Provider.GetRequiredService<NativeQuizLaunchService>()
                    .CreateChooseOwnAsync(owner, [resourceId], skillId: null,
                        cancellationToken: _lifetime.Token)
                : null;
            if (launch is null)
            {
                _logger?.LogWarning("Sam vocabulary set {Resource} is not launchable yet ({Outcome}).",
                    resourceId, outcome);
                _sam?.ReportVocabularyReviewNotReady();
                return;
            }
            _sam?.Close();
            CloseResourceOpenedFromStudy();
            _selectedTab = NativeTabs.Study;
            await StartQuizAsync(launch);
        });
    }

    /// <summary>
    /// The activity Sam prepared opens (the countdown ran out or the learner tapped Start), as
    /// its Choose My Own tile would, with the resources, skill, scenario and language the server
    /// checked. Not over a quiz or another activity: Sam says to leave it first.
    /// </summary>
    private void OnSamActivityRequested(string owner, CoachActivityLaunchDto launch)
    {
        if (_destination != NativeDestination.Dashboard || NavigationBlocked || IsActivityOpen
            || _services is null || CoachActivityRoutes.Route(launch.Activity) is not { } route)
        {
            _sam?.ReportActivityBlockedByScreen();
            return;
        }
        Run(async () =>
        {
            var resources = launch.ResourceIds.ToImmutableArray();
            if (launch.Activity == CoachActivityKind.VocabularyQuiz)
            {
                var quiz = await _services.Provider.GetRequiredService<NativeQuizLaunchService>()
                    .CreateChooseOwnAsync(owner, resources, launch.SkillId, cancellationToken: _lifetime.Token);
                if (quiz is null)
                {
                    _sam?.ReportActivityNotOpened();
                    return;
                }
                _sam?.Close();
                CloseResourceOpenedFromStudy();
                _selectedTab = NativeTabs.Study;
                await StartQuizAsync(quiz);
                return;
            }
            var selection = new NativeQuizLaunch(owner, DateOnly.FromDateTime(DateTime.Today), string.Empty,
                resources, launch.SkillId, []);
            var activity = await _services.Provider.GetRequiredService<NativeActivityLaunchService>()
                .FromChooseOwnAsync(selection, route, _lifetime.Token);
            if (activity is null)
            {
                _sam?.ReportActivityNotOpened();
                return;
            }
            _sam?.Close();
            CloseResourceOpenedFromStudy();
            _selectedTab = NativeTabs.Study;
            StartActivity(activity with { ScenarioId = launch.ScenarioId, Language = launch.Language });
        });
    }

    /// <summary>
    /// A word row in one of Sam's lists: Sam closes and the Library opens that word's editor, as
    /// a tap on the word in the Vocabulary page would. Not from a quiz or activity, which the
    /// learner leaves themselves.
    /// </summary>
    private void OnSamWordRequested(string owner, string wordId)
    {
        if (_destination != NativeDestination.Dashboard || NavigationBlocked || _services is null)
        {
            _sam?.ReportWordOpenBlockedByScreen();
            return;
        }
        EnsureResources();
        EnsureVocabulary();
        RunLibrary(async () =>
        {
            // Read again when already open: the word may be newer than the list on screen.
            var listed = IsVocabularyOpen
                ? await ReloadThenTrue(_vocabulary!)
                : await _vocabulary!.OpenAsync(owner);
            // Sam stays open, with a notice, until the word's editor has actually opened.
            if (!listed || !await _vocabulary!.OpenWordAsync(wordId))
            {
                _logger?.LogWarning("A word Sam listed could not open in the vocabulary editor.");
                _sam?.ReportListedWordNotOpened();
                return;
            }
            _sam?.Close();
            _selectedTab = NativeTabs.Library;
        });

        static async Task<bool> ReloadThenTrue(SentenceStudio.Native.iOS.Features.Vocabulary.VocabularyCoordinator vocabulary)
        {
            await vocabulary.ReloadAsync();
            return true;
        }
    }

    /// <summary>
    /// A resource detail Study opened (Back returns to Study) closes when Study starts Sam's
    /// review quiz: it would otherwise stay in the Library and its Back would jump to Study.
    /// A smart resource is only read, so nothing unsaved is lost.
    /// </summary>
    private void CloseResourceOpenedFromStudy()
    {
        if (!IsResourceFormFromStudy || !IsResourceFormReadOnly)
            return;
        _resources!.OnFormPopped();
        // OnFormPopped refuses while the detail is busy: then it stays Study's.
        if (!IsResourcesFormOpen)
            _resourceFormFromDashboard = false;
    }

    // The floating Sam button on the Dashboard and Resources roots is replaced by Sam's
    // tab slot (iOS tab shell trial, 2026-11-05); screens that hide the tab bar (activities,
    // editors) keep the toolbar entry.

    internal SwiftView RenderSamStream(SourceUiStyle style) =>
        SamView.Stream(style, SamState, Text, SamActions);

    internal SwiftView RenderSamConversations(SourceUiStyle style) =>
        SamView.Conversations(style, SamState, Text, SamActions);
}
