using System.Globalization;
using CoreFoundation;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// SwiftUI Minimal Pairs (source MinimalPairs.razor, MinimalPairCreate.razor,
/// MinimalPairSession.razor). The pair list is the activity page (Back to the
/// Dashboard) with a SwiftUI alert for the delete confirmation; the create
/// page and the listening session are a child page pushed on the same
/// NavigationStack, so system Back returns to the list. The create page's
/// search fields only swap their result rows while typing, so Korean IME
/// composition is never interrupted. Session tiles show the target term only
/// (spoken as Korean); which tile was played is never shown, labelled or
/// announced until Check Answer.
/// </summary>
internal sealed class MinimalPairsScreen : IActivityScreen
{
    private const string SpeechLanguage = "ko-KR";
    private readonly ActivityScreenHost _host;
    private readonly NativeMinimalPairsPresenter _presenter;
    private NativeMinimalPairsState _state = new(NativePhase.Loading);
    private long _childVersion;
    private long _pageVersion;
    private bool _childDismissed;
    private SwiftView? _deleteAlert;
    private SourceUiStyle? _style;
    // Mounted create-page result slots (valid until the next RenderChild).
    private SwiftView? _rowsA;
    private SwiftView? _rowsB;

    public MinimalPairsScreen(ActivityScreenHost host)
    {
        _host = host;
        _presenter = host.Services.GetRequiredService<NativeMinimalPairsPresenter>();
        _presenter.StateChanged += OnStateChanged;
    }

    public string TitleKey => NativeMinimalPairsText.Title;

    public NativeMessage? Toast => NativeMessages.LatestToast(_state.Notices);

    public bool HasExited => _state.Destination == NativeDestination.TodaysPlan;

    public bool HasChildPage => !HasExited && !_childDismissed
        && _state.Phase == NativePhase.Ready && _state.Page != NativeMinimalPairsPage.List;

    public string ChildTitle => _host.Text(_state.Page == NativeMinimalPairsPage.Create
        ? NativeMinimalPairsText.CreateTitle : NativeMinimalPairsText.Title);

    public long ChildVersion => _childVersion;

    public long PageVersion => _pageVersion;

    /// <summary>The list's Create Pair, for a host that opens the create page directly (Library quick add).</summary>
    public void OpenCreate() => Fire(_presenter.OpenCreateAsync);

    /// <summary>The Library's open threw: the list shows its error with Go Back (never a spinner).</summary>
    public void OnOpenFailed()
    {
        if (_state.Phase is not (NativePhase.Idle or NativePhase.Loading))
            return;
        Apply(_state with { Phase = NativePhase.Error, ErrorKey = NativeMinimalPairsText.Unavailable });
    }

    public async Task OpenAsync(NativeActivityLaunch launch, CancellationToken cancellationToken) =>
        Apply(await _presenter.OpenAsync(launch, cancellationToken));

    public async Task ExitAsync(CancellationToken cancellationToken) =>
        Apply(await _presenter.ExitAsync(cancellationToken));

    public void OnChildPopped()
    {
        _childDismissed = true;
        Fire(_presenter.BackToListAsync);
    }

    public SwiftView PresentOver(SwiftView page)
    {
        var text = _host.Text;
        var cancel = Views.Button(text(NativeMinimalPairsText.Cancel), () => Fire(_presenter.CancelDeleteAsync))
            .WithAccessibilityIdentifier(NativeMinimalPairsAccess.DeleteCancel);
        var delete = Views.Button(text(NativeMinimalPairsText.Delete), () => Fire(_presenter.ConfirmDeleteAsync))
            .WithAccessibilityIdentifier(NativeMinimalPairsAccess.DeleteConfirm);
        _deleteAlert = Views.Alert(page, text(NativeMinimalPairsText.ConfirmDelete), string.Empty, cancel, delete)
            .WithAccessibilityIdentifier(NativeMinimalPairsAccess.DeleteDialog);
        UpdateDeleteAlert();
        return _deleteAlert.WithTabBarHidden();
    }

    /// <summary>The popped page disposed the alert with it; later renders must not touch it.</summary>
    public void OnPagePopped() => _deleteAlert = null;

    private void OnStateChanged(NativeMinimalPairsState state)
    {
        if (NSThread.IsMain)
            ApplyAndPublish(state);
        else
            DispatchQueue.MainQueue.DispatchAsync(() => ApplyAndPublish(state));
    }

    private void ApplyAndPublish(NativeMinimalPairsState state)
    {
        Apply(state);
        _host.Publish();
    }

    /// <summary>Every published change re-renders the child page; typing never publishes.</summary>
    private void Apply(NativeMinimalPairsState state)
    {
        if (ListPageChanged(_state, state))
            _pageVersion++;
        _state = state;
        if (state.Page == NativeMinimalPairsPage.List)
            _childDismissed = false;
        _childVersion++;
        UpdateDeleteAlert();
    }

    /// <summary>
    /// What the list page renders: its phase and error, the list, and the notices (shown
    /// only while the list is the current page). Create and session changes leave it alone.
    /// </summary>
    private static bool ListPageChanged(NativeMinimalPairsState previous, NativeMinimalPairsState next) =>
        previous.Phase != next.Phase || previous.ErrorKey != next.ErrorKey
        || !Equals(previous.List, next.List)
        || (previous.Page == NativeMinimalPairsPage.List) != (next.Page == NativeMinimalPairsPage.List)
        || next.Page == NativeMinimalPairsPage.List && previous.Notices != next.Notices;

    /// <summary>The source modal: "Confirm Delete" with "Delete A vs B?".</summary>
    private void UpdateDeleteAlert()
    {
        if (_deleteAlert is null)
            return;
        var pending = _state.Page == NativeMinimalPairsPage.List && !HasExited
            ? _state.List?.PendingDelete : null;
        if (pending is not null)
            _deleteAlert.Text = string.Format(CultureInfo.CurrentCulture,
                _host.Text(NativeMinimalPairsText.DeleteMessage), pending.WordA, pending.WordB);
        _deleteAlert.IsPresented = pending is not null && _state.List?.IsDeleting != true;
    }

    /// <summary>Commands bypass the controller's busy gate so speech never disables Back; the presenter serializes them.</summary>
    private void Fire(Func<CancellationToken, Task<NativeMinimalPairsState>> action) => _ = FireAsync(action);

    private async Task FireAsync(Func<CancellationToken, Task<NativeMinimalPairsState>> action)
    {
        try
        {
            Apply(await action(_host.Lifetime));
        }
        catch (OperationCanceledException) when (_host.Lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _host.Logger?.LogError(ex, "Native iOS Minimal Pairs action failed.");
            Apply(_state with
            {
                Notices = [new(NativeMessageKind.Error, NativeText.UnexpectedError)]
            });
        }
        _host.Publish();
    }

    // ---- List (activity page) ----

    public SwiftView Render(SourceUiStyle style)
    {
        _style = style;
        UpdateDeleteAlert();
        var text = _host.Text;
        var state = _state;
        var items = new List<SwiftView>();
        if (state.Phase is NativePhase.Blocked or NativePhase.Error)
        {
            items.Add(style.Text(Views.Text(text(state.ErrorKey ?? NativeMinimalPairsText.Unavailable)),
                    SourceTextRole.Body1, SourceTextTone.Danger)
                .WithAccessibilityIdentifier(NativeMinimalPairsAccess.Error));
            items.Add(style.SecondaryButton(text(NativeMinimalPairsText.GoBack), _host.Exit)
                .WithAccessibilityIdentifier(NativeMinimalPairsAccess.GoBack));
            return Page(items, style, NativeMinimalPairsAccess.List);
        }
        var list = state.List;
        if (state.Phase is NativePhase.Idle or NativePhase.Loading || list is null)
        {
            items.Add(Spinner(text(NativeMinimalPairsText.Title)));
            return Page(items, style, NativeMinimalPairsAccess.List);
        }
        if (state.Page == NativeMinimalPairsPage.List && !state.Notices.IsDefaultOrEmpty)
            if (Notices(state, style) is { } notices)
                items.Add(notices);

        var create = style.LabelButton(IconText("plus", text(NativeMinimalPairsText.CreatePair)),
                () => Fire(_presenter.OpenCreateAsync), SourceButtonRole.Primary)
            .WithAccessibilityLabel(text(NativeMinimalPairsText.CreatePair))
            .WithAccessibilityIdentifier(NativeMinimalPairsAccess.CreatePair);
        SourceUiStyle.SetCustomButtonAvailability(create, style.Appearance, SourceButtonRole.Primary,
            !list.IsLoading && !list.IsDeleting);
        items.Add(Views.HStack(Views.VStack().WithFlexibleWidth(), create).WithFlexibleWidth());
        items.Add(ModeRow(list, style));

        if (list.IsLoading)
            items.Add(Spinner(text(NativeMinimalPairsText.Title)));
        else if (list.IsEmpty)
        {
            var empty = Views.VStack(
                    style.Text(Views.Text(text(NativeMinimalPairsText.NoPairs)), SourceTextRole.Body1,
                        SourceTextTone.Secondary),
                    style.PrimaryButton(text(NativeMinimalPairsText.CreateFirstPair),
                            () => Fire(_presenter.OpenCreateAsync))
                        .WithAccessibilityIdentifier(NativeMinimalPairsAccess.CreateFirstPair))
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                .WithPadding(24)
                .WithAccessibilityIdentifier(NativeMinimalPairsAccess.Empty);
            empty.Spacing = 16;
            items.Add(empty);
        }
        else
        {
            if (list.IsDeleting)
                items.Add(Spinner(text(NativeMinimalPairsText.Delete))
                    .WithAccessibilityIdentifier(NativeMinimalPairsAccess.Deleting));
            // Source col-12 / col-md-6 / col-lg-4.
            var columns = style.ViewportWidth >= 992 ? 3 : style.ViewportWidth >= 768 ? 2 : 1;
            var grid = Views.LazyVGrid(columns, list.Pairs.Select(pair => PairCard(pair, list, style)).ToArray())
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier(NativeMinimalPairsAccess.Pairs);
            grid.Spacing = 12;
            items.Add(grid);
        }
        return Page(items, style, NativeMinimalPairsAccess.List);
    }

    private SwiftView ModeRow(NativeMinimalPairsListState list, SourceUiStyle style)
    {
        var text = _host.Text;
        SwiftView ModeButton(string label, NativeMinimalPairsMode mode, SourceButtonGroupPosition position, string id)
        {
            var selected = list.Mode == mode;
            Action action = () => Fire(token => _presenter.SetModeAsync(mode, token));
            var button = selected
                ? style.PrimaryButton(label, action, groupPosition: position)
                : style.SecondaryButton(label, action, groupPosition: position);
            return button.WithAccessibilitySelected(selected).WithAccessibilityIdentifier(id);
        }
        var modes = Views.ButtonGroup([
                ModeButton(text(NativeMinimalPairsText.Focus), NativeMinimalPairsMode.Focus,
                    SourceButtonGroupPosition.First, NativeMinimalPairsAccess.ModeFocus),
                ModeButton(text(NativeMinimalPairsText.Mixed), NativeMinimalPairsMode.Mixed,
                    SourceButtonGroupPosition.Last, NativeMinimalPairsAccess.ModeMixed)],
                selectedIndex: list.Mode == NativeMinimalPairsMode.Focus ? 0 : 1)
            .WithButtonGroupLayout(style.Appearance.BootstrapBaseButtonBorderWidth);
        var row = new List<SwiftView>
        {
            style.Text(Views.Text(text(NativeMinimalPairsText.Mode)), SourceTextRole.Body1Bold)
                .WithAccessibilityIdentifier(NativeMinimalPairsAccess.ModeLabel),
            modes,
        };
        if (list.ShowsStart)
        {
            var label = list.Mode == NativeMinimalPairsMode.Mixed
                ? string.Format(CultureInfo.CurrentCulture, text(NativeMinimalPairsText.StartSessionCount),
                    list.Pairs.Length)
                : text(NativeMinimalPairsText.StartSession);
            var start = style.PrimaryButton(label, () => Fire(_presenter.StartSessionAsync))
                .WithAccessibilityIdentifier(NativeMinimalPairsAccess.StartSession);
            SourceUiStyle.SetCustomButtonAvailability(start, style.Appearance, SourceButtonRole.Primary,
                list.CanStart);
            row.Add(start);
        }
        var stack = Views.HStack(row.ToArray())
            .WithAdaptiveStack(420)
            .WithFlexibleWidth();
        stack.Spacing = 16;
        return stack;
    }

    private SwiftView PairCard(NativeMinimalPairItem pair, NativeMinimalPairsListState list, SourceUiStyle style)
    {
        var text = _host.Text;
        var appearance = style.Appearance;
        var vs = text(NativeMinimalPairsText.Vs);
        var words = Views.HStack(
            style.Text(Views.Text(pair.WordA), SourceTextRole.Title3).WithSpeechLanguage(SpeechLanguage),
            style.Text(Views.Text(vs), SourceTextRole.Small, SourceTextTone.Secondary),
            style.Text(Views.Text(pair.WordB), SourceTextRole.Title3).WithSpeechLanguage(SpeechLanguage));
        words.Spacing = 8;
        var details = new List<SwiftView> { words };
        if (!string.IsNullOrEmpty(pair.ContrastLabel))
            details.Add(style.Text(Views.Text(pair.ContrastLabel), SourceTextRole.Small, SourceTextTone.Secondary));
        var detailStack = Views.VStack(details.ToArray()).WithFlexibleWidth();
        detailStack.Spacing = 4;
        var highlighted = list.IsHighlighted(pair.Id);
        var label = string.IsNullOrEmpty(pair.ContrastLabel)
            ? $"{pair.WordA} {vs} {pair.WordB}" : $"{pair.WordA} {vs} {pair.WordB}. {pair.ContrastLabel}";
        var select = Views.Button(detailStack, () => Fire(token => _presenter.SelectPairAsync(pair.Id, token)))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth()
            .WithAccessibilityLabel(label)
            .WithAccessibilitySelected(highlighted)
            .WithAccessibilityIdentifier(pair.AccessibilityId)
            .WithSpeechLanguage(SpeechLanguage);
        var delete = Views.Button(Views.Image("trash")
                    .WithForegroundColor(appearance.Danger.Light, appearance.Danger.Dark)
                    .WithPadding(6, 10, 6, 10)
                    .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight),
                () => Fire(token => _presenter.RequestDeleteAsync(pair.Id, token)))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithButtonAppearance(null, null, appearance.Danger.Light, appearance.Danger.Dark,
                appearance.BootstrapRadius)
            .WithAccessibilityLabel($"{text(NativeMinimalPairsText.Delete)} {label}")
            .WithAccessibilityIdentifier(NativeMinimalPairsAccess.PairDelete(pair.Id));
        var row = Views.HStack(select, delete).WithPadding(SourceUiLayout.CardInset);
        row.Spacing = 16;
        var border = highlighted ? appearance.Primary : appearance.Border;
        return row.WithFlexibleWidth().WithRoundedSurface(appearance.CardSurface.Light, appearance.CardSurface.Dark,
            16, border.Light, border.Dark, highlighted ? 2 : 1);
    }

    // ---- Child page: create or session ----

    public SwiftView RenderChild(SourceUiStyle style)
    {
        _style = style;
        _rowsA = _rowsB = null;
        var state = _state;
        if (state.Page == NativeMinimalPairsPage.Create && state.Create is { } create)
            return RenderCreate(state, create, style);
        if (state.Page == NativeMinimalPairsPage.Session && state.Session is { } session)
            return RenderSession(state, session, style);
        return Page([Spinner(_host.Text(NativeMinimalPairsText.Title))], style, NativeMinimalPairsAccess.Loading);
    }

    private SwiftView RenderCreate(NativeMinimalPairsState state, NativeMinimalPairsCreateState create,
        SourceUiStyle style)
    {
        // Captain 2026-11-13 (the native form pattern): one grouped Form; Create is the
        // toolbar's plain action; each word is a Section with its search field and the words
        // to choose from as rows (a checkmark on the chosen one).
        var text = _host.Text;
        var sections = new List<SwiftView>();
        var submit = Views.Button(text(NativeMinimalPairsText.Create), () => Fire(_presenter.CreatePairAsync))
            .WithAccessibilityIdentifier(NativeMinimalPairsAccess.CreateSubmit);
        submit.IsEnabled = create.CanCreate;
        if (create.IsLoading)
        {
            // The last draw's word rows are disposed with it: typing must not refill them.
            _rowsA = null;
            _rowsB = null;
            sections.Add(Views.Section(string.Empty, Spinner(text(NativeMinimalPairsText.CreateTitle))));
            return Views.Toolbar(CreateForm(sections, style), submit);
        }
        var notices = new List<SwiftView>();
        if (create.IsSaving)
            notices.Add(Views.IndeterminateProgress(text(NativeMinimalPairsText.Create)));
        if (!state.Notices.IsDefaultOrEmpty && Notices(state, style) is { } banners)
            notices.Add(banners);
        if (create.ErrorKey is { } error)
            notices.Add(NativeForm.Notice(style, text(error), NativeMinimalPairsAccess.CreateError, error: true));
        if (notices.Count > 0)
            sections.Add(Views.Section(string.Empty, notices.ToArray()));
        sections.AddRange(WordSections('A', create, style));
        sections.AddRange(WordSections('B', create, style));
        sections.Add(Views.Section(text(NativeMinimalPairsText.ContrastLabel),
            Views.TextField(NativeMinimalPairsText.ContrastPlaceholder, create.ContrastLabel,
                    value => _presenter.SetContrastLabel(value))
                .WithAccessibilityLabel(text(NativeMinimalPairsText.ContrastLabel))
                .WithAccessibilityIdentifier(NativeMinimalPairsAccess.ContrastLabel)));
        return Views.Toolbar(CreateForm(sections, style), submit);
    }

    private static SwiftView CreateForm(List<SwiftView> sections, SourceUiStyle style) =>
        NativeForm.Page(style, sections, $"{NativeMinimalPairsAccess.Create}.content",
            NativeMinimalPairsAccess.Create);

    /// <summary>
    /// A word's Sections: its title over the search field and the chosen word, then the words to
    /// choose from (their own Section, which typing refills in place; the field stays mounted).
    /// </summary>
    private IEnumerable<SwiftView> WordSections(char slot, NativeMinimalPairsCreateState create, SourceUiStyle style)
    {
        var text = _host.Text;
        var isA = slot == 'A';
        var title = text(isA ? NativeMinimalPairsText.SelectFirstWord : NativeMinimalPairsText.SelectSecondWord);
        var search = Views.TextField(text(NativeMinimalPairsText.SearchVocabulary),
                isA ? create.SearchA : create.SearchB, value => OnSearchChanged(slot, value))
            .WithAccessibilityLabel($"{title}. {text(NativeMinimalPairsText.SearchVocabulary)}")
            .WithAccessibilityIdentifier(isA ? NativeMinimalPairsAccess.SearchA : NativeMinimalPairsAccess.SearchB);
        var head = new List<SwiftView> { search };
        if ((isA ? create.WordA : create.WordB) is { } chosen)
        {
            // The word's Text carries its speech language (a Label cannot).
            var word = Views.Text(chosen.Target).WithSpeechLanguage(SpeechLanguage)
                .WithAccessibilityIdentifier(isA ? NativeMinimalPairsAccess.SelectedA : NativeMinimalPairsAccess.SelectedB);
            var chosenRow = Views.HStack(Views.Image("checkmark.circle.fill"), word)
                .WithForegroundColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark);
            chosenRow.Spacing = SourceUiLayout.ItemGap;
            head.Add(chosenRow);
        }
        yield return Views.Section(title, head.ToArray());
        var rows = Views.Section(string.Empty, WordRows(slot, create, style));
        if (isA)
            _rowsA = rows;
        else
            _rowsB = rows;
        yield return rows;
    }

    /// <summary>Typing only refills the word rows' Section; the field itself is never re-mounted.</summary>
    private void OnSearchChanged(char slot, string value)
    {
        if (_presenter.SetSearch(slot, value) is not { } create || _style is null)
            return;
        _state = _state with { Create = create };
        if ((slot == 'A' ? _rowsA : _rowsB) is not { } rows)
            return;
        while (rows.Children.Count > 0)
        {
            var old = rows.Children[0];
            rows.RemoveAt(0);
            old.Dispose();
        }
        foreach (var row in WordRows(slot, create, _style))
            rows.Add(row);
    }

    /// <summary>The words to choose from (at most MaxRows; then the refine hint), each a row.</summary>
    private SwiftView[] WordRows(char slot, NativeMinimalPairsCreateState create, SourceUiStyle style)
    {
        var appearance = style.Appearance;
        var filtered = create.Filter(slot == 'A' ? create.SearchA : create.SearchB);
        var selectedId = slot == 'A' ? create.WordAId : create.WordBId;
        var rows = filtered.Take(NativeMinimalPairsCreateState.MaxRows).Select(word =>
        {
            var selected = word.Id == selectedId;
            var check = Views.Image("checkmark")
                .WithForegroundColor(appearance.Primary.Light, appearance.Primary.Dark)
                .WithHidden(!selected);
            var line = Views.HStack(
                    style.Text(Views.Text($"{word.Target} — {word.Native}"), SourceTextRole.Body1)
                        .WithLineLimit(1)
                        .WithFlexibleWidth(),
                    check)
                .WithFlexibleWidth();
            return Views.Button(line, () => Fire(token => _presenter.SelectWordAsync(slot, word.Id, token)))
                .WithForegroundColor(appearance.Text.Light, appearance.Text.Dark)
                .WithFlexibleWidth()
                .WithAccessibilityLabel($"{word.Target}, {word.Native}")
                .WithAccessibilitySelected(selected)
                .WithAccessibilityIdentifier(NativeMinimalPairsAccess.WordOption(slot, word.Id));
        }).ToList();
        if (filtered.Length > NativeMinimalPairsCreateState.MaxRows)
            rows.Add(style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                        _host.Text(NativeMinimalPairsText.RefineSearch), NativeMinimalPairsCreateState.MaxRows,
                        filtered.Length)), SourceTextRole.Small, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier(NativeMinimalPairsAccess.RefineSearch));
        return rows.ToArray();
    }

    private SwiftView RenderSession(NativeMinimalPairsState state, NativeMinimalPairsSessionState session,
        SourceUiStyle style)
    {
        var text = _host.Text;
        var appearance = style.Appearance;
        var items = new List<SwiftView>();
        if (!state.Notices.IsDefaultOrEmpty)
            if (Notices(state, style) is { } notices)
                items.Add(notices);
        if (session.ShowSummary)
        {
            items.Add(Summary(session, style));
            return Page(items, style, NativeMinimalPairsAccess.Session);
        }
        if (session.IsInitializing)
        {
            items.Add(Spinner(text(NativeMinimalPairsText.Title)));
            return Page(items, style, NativeMinimalPairsAccess.Session);
        }
        if (!session.HasTrial)
        {
            var empty = Views.VStack(
                    style.Text(Views.Text(text(NativeMinimalPairsText.SessionNoPairs)), SourceTextRole.Body1,
                        SourceTextTone.Secondary),
                    style.SecondaryButton(text(NativeMinimalPairsText.GoBack), () => Fire(_presenter.BackToListAsync))
                        .WithAccessibilityIdentifier(NativeMinimalPairsAccess.GoBack))
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                .WithPadding(24)
                .WithAccessibilityIdentifier(NativeMinimalPairsAccess.SessionEmpty);
            empty.Spacing = 16;
            items.Add(empty);
            return Page(items, style, NativeMinimalPairsAccess.Session);
        }

        var header = new List<SwiftView>
        {
            style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture, text(NativeMinimalPairsText.Trial),
                    session.TrialNumber, session.TotalTrials)), SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier(NativeMinimalPairsAccess.Trial),
        };
        var correct = Views.HStack(Views.Image("checkmark").WithForegroundColor(appearance.Success.Light, appearance.Success.Dark),
                style.Text(Views.Text(session.CorrectCount.ToString(CultureInfo.CurrentCulture)), SourceTextRole.Title3,
                    SourceTextTone.Success))
            .WithAccessibilityLabel($"{text(NativeMinimalPairsText.Correct)} {session.CorrectCount}")
            .WithAccessibilityIdentifier(NativeMinimalPairsAccess.CorrectCount);
        var incorrect = Views.HStack(Views.Image("xmark").WithForegroundColor(appearance.Danger.Light, appearance.Danger.Dark),
                style.Text(Views.Text(session.IncorrectCount.ToString(CultureInfo.CurrentCulture)), SourceTextRole.Title3,
                    SourceTextTone.Danger))
            .WithAccessibilityLabel($"{text(NativeMinimalPairsText.Incorrect)} {session.IncorrectCount}")
            .WithAccessibilityIdentifier(NativeMinimalPairsAccess.IncorrectCount);
        var counts = Views.HStack(correct, incorrect);
        counts.Spacing = 24;
        header.Add(counts);
        if (session.AudioNoticeKey is { } notice)
            header.Add(style.Text(Views.Text(text(notice)), SourceTextRole.Small)
                .WithForegroundColor(appearance.Warning.Light, appearance.Warning.Dark)
                .WithAccessibilityIdentifier(NativeMinimalPairsAccess.AudioNotice));
        var headerStack = Views.VStack(header.ToArray())
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
        foreach (var child in headerStack.Children)
            child.WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
        headerStack.Spacing = 8;
        items.Add(headerStack);

        var tiles = Views.HStack(
                Tile(session.Left!, session, NativeMinimalPairsAccess.LeftTile, style),
                Tile(session.Right!, session, NativeMinimalPairsAccess.RightTile, style))
            .WithPadding(24, 0, 24, 0)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
        tiles.Spacing = 24;
        items.Add(tiles);

        var replayLabel = IconText("speaker.wave.2", text(NativeMinimalPairsText.Replay));
        var replay = style.LabelButton(replayLabel, () => Fire(_presenter.ReplayAsync), SourceButtonRole.FilledSecondary)
            .WithAccessibilityLabel(text(NativeMinimalPairsText.Replay))
            .WithAccessibilityIdentifier(NativeMinimalPairsAccess.Replay);
        SourceUiStyle.SetCustomButtonAvailability(replay, appearance, SourceButtonRole.FilledSecondary, session.CanReplay);
        var check = style.PrimaryButton(text(NativeMinimalPairsText.CheckAnswer), () => Fire(_presenter.CheckAnswerAsync))
            .WithAccessibilityIdentifier(NativeMinimalPairsAccess.CheckAnswer);
        SourceUiStyle.SetCustomButtonAvailability(check, appearance, SourceButtonRole.Primary, session.CanCheck);
        var actions = Views.HStack(replay, check).WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
        actions.Spacing = 12;
        items.Add(actions);
        return Page(items, style, NativeMinimalPairsAccess.Session);
    }

    /// <summary>
    /// An answer tile: the target term only. Before Check Answer its label and
    /// state say nothing about which word was spoken; afterwards the source
    /// border colours and check icon appear, with matching spoken feedback.
    /// </summary>
    private SwiftView Tile(NativeMinimalPairTile tile, NativeMinimalPairsSessionState session, string id,
        SourceUiStyle style)
    {
        var text = _host.Text;
        var appearance = style.Appearance;
        var mark = session.MarkFor(tile);
        var (border, fill) = mark switch
        {
            NativeMinimalPairTileMark.Selected => (appearance.Success, appearance.CardSurface),
            NativeMinimalPairTileMark.SelectedCorrect or NativeMinimalPairTileMark.Answer =>
                (appearance.InfoAlertBorder, appearance.InfoAlertFill),
            NativeMinimalPairTileMark.SelectedWrong => (appearance.DangerAlertBorder, appearance.DangerAlertFill),
            _ => (appearance.Border, appearance.CardSurface),
        };
        // The check icon exists only after Check Answer; before that both tiles are identical.
        var check = session.ShowsCheck(tile)
            ? Views.Image("checkmark").WithForegroundColor(appearance.InfoAlertText.Light,
                appearance.InfoAlertText.Dark)
            : Views.VStack().WithMinimumHeight(20);
        var term = style.Text(Views.Text(tile.Term), SourceTextRole.Title2)
            .WithSpeechLanguage(SpeechLanguage)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
        var body = Views.VStack(
                Views.HStack(Views.VStack().WithFlexibleWidth(), check).WithFlexibleWidth(),
                Views.VStack().WithMinimumHeight(24),
                term,
                Views.VStack().WithMinimumHeight(24))
            .WithPadding(8)
            .WithFlexibleWidth(150, SwiftHorizontalAlignment.Center)
            .WithMinimumHeight(150)
            .WithRoundedSurface(fill.Light, fill.Dark, 16, border.Light, border.Dark, 3);
        var label = mark switch
        {
            NativeMinimalPairTileMark.SelectedCorrect => $"{tile.Term}. {text(NativeMinimalPairsText.AnswerCorrect)}",
            NativeMinimalPairTileMark.SelectedWrong => $"{tile.Term}. {text(NativeMinimalPairsText.AnswerIncorrect)}",
            NativeMinimalPairTileMark.Answer => $"{tile.Term}. {text(NativeMinimalPairsText.AnswerCorrect)}",
            _ => tile.Term,
        };
        var button = Views.Button(body, () => Fire(token => _presenter.SelectAnswerAsync(tile.WordId, token)))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithAccessibilityLabel(label)
            .WithAccessibilitySelected(session.SelectedWordId == tile.WordId)
            .WithAccessibilityIdentifier(id)
            .WithSpeechLanguage(SpeechLanguage);
        button.IsEnabled = session.CanSelect;
        return button;
    }

    private SwiftView Summary(NativeMinimalPairsSessionState session, SourceUiStyle style)
    {
        var text = _host.Text;
        SwiftView Line(string key, string value, SourceTextTone tone, string id)
        {
            var row = Views.HStack(
                    style.Text(Views.Text(text(key)), SourceTextRole.Body1),
                    style.Text(Views.Text(value), SourceTextRole.Title2, tone))
                .WithAccessibilityLabel($"{text(key)} {value}")
                .WithAccessibilityIdentifier(id);
            row.Spacing = 8;
            return row;
        }
        var card = Views.VStack(
                Line(NativeMinimalPairsText.Correct, session.CorrectCount.ToString(CultureInfo.CurrentCulture),
                    SourceTextTone.Success, NativeMinimalPairsAccess.SummaryCorrect),
                Line(NativeMinimalPairsText.Incorrect, session.IncorrectCount.ToString(CultureInfo.CurrentCulture),
                    SourceTextTone.Danger, NativeMinimalPairsAccess.SummaryIncorrect),
                Line(NativeMinimalPairsText.Accuracy, $"{session.Accuracy.ToString("F1", CultureInfo.CurrentCulture)}%",
                    SourceTextTone.Body, NativeMinimalPairsAccess.SummaryAccuracy),
                Line(NativeMinimalPairsText.Duration, string.Format(CultureInfo.CurrentCulture,
                        text(NativeMinimalPairsText.DurationValue), session.DurationMinutes),
                    SourceTextTone.Body, NativeMinimalPairsAccess.SummaryDuration))
            .WithPadding(SourceUiLayout.CardInset);
        card.Spacing = 16;
        // Source centers the summary column: title, card and Done.
        var page = Views.VStack(
                style.Text(Views.Text(text(NativeMinimalPairsText.SessionSummary)), SourceTextRole.Title1,
                        sourceHeading: true)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center),
                style.Card(card, maxWidth: 360),
                Views.HStack(style.PrimaryButton(text(NativeMinimalPairsText.Done),
                            () => Fire(_presenter.BackToListAsync))
                        .WithAccessibilityIdentifier(NativeMinimalPairsAccess.Done))
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center))
            .WithPadding(24, 0, 24, 0)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier(NativeMinimalPairsAccess.Summary);
        page.Spacing = 24;
        return page;
    }

    // ---- Shared pieces ----

    /// <summary>Errors and warnings stay inline until the next action; success and info are the scene's toast.</summary>
    private SwiftView? Notices(NativeMinimalPairsState state, SourceUiStyle style)
    {
        var text = _host.Text;
        return style.MessageBanners(state.Notices, text, index => $"{NativeMinimalPairsAccess.Notice}.{index}",
            () => Fire(_presenter.DismissNoticesAsync), NativeMinimalPairsAccess.DismissNotices,
            text(NativeMinimalPairsText.Dismiss));
    }

    private static SwiftView IconText(string symbol, string label)
    {
        var row = Views.HStack(Views.Image(symbol), Views.Text(label));
        row.Spacing = 4;
        return row;
    }

    private static SwiftView Spinner(string label) =>
        Views.IndeterminateProgress(label)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier(NativeMinimalPairsAccess.Loading);

    private static SwiftView Page(List<SwiftView> items, SourceUiStyle style, string id)
    {
        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var content = Views.VStack(items.ToArray())
            .WithPadding(16, gutter, 16, gutter)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier($"{id}.content");
        content.Spacing = SourceUiLayout.PageGap;
        return style.Page(Views.Scroll(content).WithAccessibilityIdentifier(id));
    }

    public void Dispose()
    {
        _presenter.StateChanged -= OnStateChanged;
        _presenter.Dispose();
    }
}
