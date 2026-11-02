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
/// SwiftUI Diary (source Diary.razor, DiaryEditor.razor, DiaryViewer.razor).
/// The list is the activity page; today's editor and the entry viewer are a
/// child page pushed on the same NavigationStack, so system Back returns to
/// the list and the list's Back returns to the Dashboard. The editor's
/// TextEditor is mounted once per visit: typing, autosave status, stars,
/// font size, prompt and notices update sibling slots in place, so Korean IME
/// composition is never interrupted by a re-render.
/// </summary>
internal sealed class DiaryScreen : IActivityScreen
{
    private readonly ActivityScreenHost _host;
    private readonly NativeDiaryPresenter _presenter;
    private NativeDiaryState _state = new(NativePhase.Loading);
    private long _childVersion;
    private (NativeDiaryPage Page, bool Loading) _childKey;
    private bool _childDismissed;
    private SourceUiStyle? _style;
    // Mounted editor nodes (valid until the next RenderChild).
    private SwiftView? _editor;
    private SwiftView? _editorNotices;
    private SwiftView? _editorPrompt;
    private SwiftView? _editorBar;

    public DiaryScreen(ActivityScreenHost host)
    {
        _host = host;
        _presenter = host.Services.GetRequiredService<NativeDiaryPresenter>();
        _presenter.StateChanged += OnStateChanged;
    }

    public string TitleKey => NativeDiaryText.Title;

    public NativeMessage? Toast => NativeMessages.LatestToast(_state.Notices);

    public bool HasExited => _state.Destination == NativeDestination.TodaysPlan;

    public bool HasChildPage => !HasExited && !_childDismissed
        && _state.Phase == NativePhase.Ready && _state.Page != NativeDiaryPage.List;

    public string ChildTitle => _state.Page == NativeDiaryPage.Viewer
        && _state.Viewer is { IsLoading: false, NotFound: false } viewer
            ? NativeDiaryText.FormatDate(viewer.EntryDate)
            : _host.Text(_state.Page == NativeDiaryPage.Editor
                ? NativeDiaryText.TodayTitle : NativeDiaryText.Title);

    public long ChildVersion => _childVersion;

    public async Task OpenAsync(NativeActivityLaunch launch, CancellationToken cancellationToken) =>
        Apply(await _presenter.OpenAsync(launch, cancellationToken));

    public async Task ExitAsync(CancellationToken cancellationToken) =>
        Apply(await _presenter.ExitAsync(cancellationToken));

    public void OnChildPopped()
    {
        _childDismissed = true;
        Fire(_presenter.BackToListAsync);
    }

    private void OnStateChanged(NativeDiaryState state)
    {
        if (NSThread.IsMain)
            ApplyAndPublish(state);
        else
            DispatchQueue.MainQueue.DispatchAsync(() => ApplyAndPublish(state));
    }

    private void ApplyAndPublish(NativeDiaryState state)
    {
        Apply(state);
        _host.Publish();
    }

    /// <summary>
    /// Editor changes other than the page or loading phase are applied to the
    /// mounted slots; anything else bumps ChildVersion so the scene re-renders.
    /// </summary>
    private void Apply(NativeDiaryState state)
    {
        _state = state;
        if (state.Page == NativeDiaryPage.List)
            _childDismissed = false;
        var key = (state.Page, state.Editor?.IsLoading ?? state.Viewer?.IsLoading ?? false);
        if (state.Page == NativeDiaryPage.Editor && key == _childKey && _editor is not null
            && !_childDismissed && _style is not null && state.Editor is { } editor)
        {
            UpdateEditorSlots(state, editor, _style);
            return;
        }
        _childKey = key;
        _childVersion++;
    }

    /// <summary>Commands bypass the controller's busy gate so AI calls never disable Back; the presenter serializes them.</summary>
    private void Fire(Func<CancellationToken, Task<NativeDiaryState>> action) => _ = FireAsync(action);

    private async Task FireAsync(Func<CancellationToken, Task<NativeDiaryState>> action)
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
            _host.Logger?.LogError(ex, "Native iOS Diary action failed.");
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
        var text = _host.Text;
        var state = _state;
        var items = new List<SwiftView>();
        if (state.Page == NativeDiaryPage.List && !state.Notices.IsDefaultOrEmpty)
            if (Notices(state, style) is { } notices)
                items.Add(notices);
        var list = state.List;
        if (state.Phase is NativePhase.Blocked or NativePhase.Error)
        {
            items.Add(style.Text(Views.Text(text(state.ErrorKey ?? NativeDiaryText.Unavailable)),
                    SourceTextRole.Body1, SourceTextTone.Danger)
                .WithAccessibilityIdentifier(NativeDiaryAccess.Error));
            items.Add(style.SecondaryButton(text(NativeDiaryText.GoBack), _host.Exit)
                .WithAccessibilityIdentifier(NativeDiaryAccess.GoBack));
        }
        else if (state.Phase is NativePhase.Idle or NativePhase.Loading || list is null || list.IsLoading)
            items.Add(Spinner(text(NativeDiaryText.Title)));
        else
        {
            items.Add(TodayCard(list, style));
            if (!list.PastEntries.IsDefaultOrEmpty)
            {
                items.Add(style.Text(Views.Text(text(NativeDiaryText.PastEntries)),
                    SourceTextRole.Title2, sourceHeading: true));
                var rows = Views.VStack(list.PastEntries.Select(entry => EntryCard(entry, style)).ToArray())
                    .WithFlexibleWidth()
                    .WithAccessibilityIdentifier(NativeDiaryAccess.PastEntries);
                rows.Spacing = 8;
                items.Add(rows);
            }
            else if (list.Today is null)
            {
                var empty = Views.VStack(
                        Views.Image("book.closed").WithForegroundColor(
                            style.Appearance.SecondaryText.Light, style.Appearance.SecondaryText.Dark),
                        style.Text(Views.Text(text(NativeDiaryText.EmptyState)), SourceTextRole.Body2,
                            SourceTextTone.Secondary))
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithPadding(24)
                    .WithAccessibilityIdentifier(NativeDiaryAccess.Empty);
                foreach (var child in empty.Children)
                    child.WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
                empty.Spacing = 8;
                items.Add(empty);
            }
        }
        return style.Page(Views.Scroll(Content(items, style, "native.diary.list.content"))
            .WithAccessibilityIdentifier(NativeDiaryAccess.List));
    }

    private SwiftView TodayCard(NativeDiaryListState list, SourceUiStyle style)
    {
        var text = _host.Text;
        var title = text(list.Today is null ? NativeDiaryText.StartToday : NativeDiaryText.ContinueToday);
        var detail = list.Today is null ? text(NativeDiaryText.StartTodayHint)
            : string.Format(CultureInfo.CurrentCulture, text(NativeDiaryText.WordCount), list.Today.WordCount);
        var labels = Views.VStack(
                style.Text(Views.Text(title), SourceTextRole.Body1Strong),
                style.Text(Views.Text(detail), SourceTextRole.Small, SourceTextTone.Secondary)
                    .WithAccessibilityIdentifier(NativeDiaryAccess.TodayWordCount))
            .WithFlexibleWidth();
        labels.Spacing = 2;
        var primary = style.Appearance.Primary;
        var row = Views.HStack(
                Views.Image("square.and.pencil").WithForegroundColor(primary.Light, primary.Dark),
                labels,
                Views.Image("chevron.right").WithForegroundColor(
                    style.Appearance.SecondaryText.Light, style.Appearance.SecondaryText.Dark))
            .WithPadding(SourceUiLayout.CardInset);
        row.Spacing = 16;
        return Views.Button(style.Card(row), () => Fire(_presenter.OpenTodayAsync))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithAccessibilityLabel($"{title}. {detail}")
            .WithAccessibilityIdentifier(NativeDiaryAccess.Today);
    }

    private SwiftView EntryCard(NativeDiaryEntrySummary entry, SourceUiStyle style)
    {
        var text = _host.Text;
        var id = entry.AccessibilityId;
        var date = entry.DateKey is null ? NativeDiaryText.FormatDate(entry.EntryDate) : text(entry.DateKey);
        var left = new List<SwiftView>
        {
            style.Text(Views.Text(date), SourceTextRole.Body1Strong).WithAccessibilityIdentifier($"{id}.date")
        };
        if (!string.IsNullOrWhiteSpace(entry.Snippet))
            left.Add(style.Text(Views.Text(entry.Snippet), SourceTextRole.Small, SourceTextTone.Secondary)
                .WithLineLimit(1)
                .WithAccessibilityIdentifier($"{id}.snippet"));
        var leftStack = Views.VStack(left.ToArray()).WithFlexibleWidth();
        leftStack.Spacing = 4;
        var words = string.Format(CultureInfo.CurrentCulture, text(NativeDiaryText.WordCount), entry.WordCount);
        var right = new List<SwiftView>
        {
            style.Text(Views.Text(words), SourceTextRole.BadgeSmall, SourceTextTone.Secondary)
                .WithPadding(2, 8, 2, 8)
                .WithRoundedSurface(style.Appearance.ListRowSurface.Light, style.Appearance.ListRowSurface.Dark, 8)
                .WithAccessibilityIdentifier($"{id}.word-count")
        };
        if (entry.HasFeedback)
        {
            var primary = style.Appearance.Primary;
            right.Add(Views.Image("sparkles").WithForegroundColor(primary.Light, primary.Dark)
                .WithAccessibilityLabel(text(NativeDiaryText.HasFeedback))
                .WithAccessibilityIdentifier($"{id}.feedback"));
        }
        var rightStack = Views.VStack(right.ToArray());
        rightStack.Spacing = 4;
        var row = Views.HStack(leftStack, rightStack).WithPadding(SourceUiLayout.CardInset);
        row.Spacing = 16;
        var label = entry.HasFeedback ? $"{date}. {words}. {text(NativeDiaryText.HasFeedback)}" : $"{date}. {words}";
        return Views.Button(style.Card(row), () => Fire(token => _presenter.OpenEntryAsync(entry.Id, token)))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier(id);
    }

    // ---- Child page: editor or viewer ----

    public SwiftView RenderChild(SourceUiStyle style)
    {
        _style = style;
        _editor = _editorNotices = _editorPrompt = _editorBar = null;
        var state = _state;
        if (state.Page == NativeDiaryPage.Editor && state.Editor is { IsLoading: false } editor)
            return RenderEditor(state, editor, style);
        if (state.Page == NativeDiaryPage.Viewer && state.Viewer is { IsLoading: false } viewer)
            return RenderViewer(state, viewer, style);
        return style.Page(Views.Scroll(Content([Spinner(_host.Text(NativeDiaryText.Title))], style,
            "native.diary.child.content")).WithAccessibilityIdentifier(NativeDiaryAccess.Loading));
    }

    private SwiftView RenderEditor(NativeDiaryState state, NativeDiaryEditorState editor, SourceUiStyle style)
    {
        var text = _host.Text;
        _editorNotices = Views.VStack(NoticesOrEmpty(state, style)).WithFlexibleWidth();
        _editorPrompt = Views.VStack(PromptCard(editor, style)).WithFlexibleWidth();
        _editorBar = Views.VStack(InputBar(editor, style)).WithFlexibleWidth();
        var ink = style.Appearance.Text;
        _editor = Views.TextEditor(editor.Content, OnContentChanged)
            .WithTextEditorPlaceholder(text(NativeDiaryText.EditorPlaceholder))
            .WithTextEditorScrollBackgroundHidden()
            .WithPlaceholderColor(style.Appearance.TertiaryText.Light, style.Appearance.TertiaryText.Dark)
            .WithForegroundColor(ink.Light, ink.Dark)
            .WithFlexibleWidth()
            .WithMinimumHeight(160)
            .WithAccessibilityLabel(text(NativeDiaryText.EditorPlaceholder))
            .WithAccessibilityIdentifier(NativeDiaryAccess.Input);
        ApplyFontSize(_editor, editor.FontSize, style);
        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var body = Views.VStack(_editorNotices, _editorPrompt, _editor)
            .WithPadding(16, gutter, 8, gutter)
            .WithFlexibleWidth();
        body.Spacing = 12;
        var page = Views.VStack(body, _editorBar)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(NativeDiaryAccess.Editor);
        page.Spacing = 0;
        return style.Page(page);
    }

    /// <summary>Typing: no publish and no re-render; only the bar (count, stars, status) is swapped.</summary>
    private void OnContentChanged(string value)
    {
        if (_presenter.SetContent(value) is { } editor && _style is not null && _editorBar is not null)
        {
            _state = _state with { Editor = editor };
            Swap(_editorBar, InputBar(editor, _style));
        }
    }

    private void UpdateEditorSlots(NativeDiaryState state, NativeDiaryEditorState editor, SourceUiStyle style)
    {
        if (_editorNotices is not null)
            Swap(_editorNotices, NoticesOrEmpty(state, style));
        if (_editorPrompt is not null)
            Swap(_editorPrompt, PromptCard(editor, style));
        if (_editorBar is not null)
            Swap(_editorBar, InputBar(editor, style));
        if (_editor is not null)
            ApplyFontSize(_editor, editor.FontSize, style);
    }

    private static void ApplyFontSize(SwiftView editor, int size, SourceUiStyle style) =>
        editor.WithTypography(size, 400, 1.5, style.Appearance.FontPostScriptName(400));

    private static void Swap(SwiftView slot, SwiftView next)
    {
        while (slot.Children.Count > 0)
        {
            var old = slot.Children[0];
            slot.RemoveAt(0);
            old.Dispose();
        }
        slot.Add(next);
    }

    private SwiftView PromptCard(NativeDiaryEditorState editor, SourceUiStyle style)
    {
        var text = _host.Text;
        var secondary = style.Appearance.SecondaryText;
        var collapsed = editor.IsPromptCollapsed;
        var toggleLabel = new List<SwiftView>
        {
            Views.Image(collapsed ? "chevron.right" : "chevron.down")
                .WithForegroundColor(secondary.Light, secondary.Dark),
            style.Text(Views.Text(text(NativeDiaryText.Prompt).ToUpper(CultureInfo.CurrentCulture)),
                SourceTextRole.Small, SourceTextTone.Secondary)
        };
        if (collapsed && !string.IsNullOrWhiteSpace(editor.PromptText))
            toggleLabel.Add(style.Text(Views.Text(editor.PromptText), SourceTextRole.Small, SourceTextTone.Secondary)
                .WithLineLimit(1)
                .WithOpacity(0.75));
        var toggleRow = Views.HStack(toggleLabel.ToArray()).WithFlexibleWidth();
        toggleRow.Spacing = 8;
        var toggle = Views.Button(toggleRow, () => Fire(_presenter.TogglePromptCollapsedAsync))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithAccessibilityLabel(text(NativeDiaryText.TogglePrompt))
            .WithAccessibilitySelected(!collapsed)
            .WithAccessibilityIdentifier(NativeDiaryAccess.PromptToggle);
        var header = new List<SwiftView> { toggle };
        if (!collapsed)
        {
            SwiftView label = editor.IsLoadingPrompt
                ? Views.IndeterminateProgress(text(NativeDiaryText.NewPrompt))
                : style.ViewportWidth >= 768
                    ? Views.HStack(Views.Image("arrow.clockwise"), Views.Text(text(NativeDiaryText.NewPrompt)))
                    : Views.Image("arrow.clockwise");
            var refresh = style.LabelButton(label, () => Fire(_presenter.RefreshPromptAsync),
                    SourceButtonRole.FilledSecondary, 10)
                .WithAccessibilityLabel(text(NativeDiaryText.NewPrompt))
                .WithAccessibilityIdentifier(NativeDiaryAccess.NewPrompt);
            SourceUiStyle.SetCustomButtonAvailability(refresh, style.Appearance,
                SourceButtonRole.FilledSecondary, !editor.IsLoadingPrompt);
            header.Add(refresh);
        }
        var headerRow = Views.HStack(header.ToArray()).WithFlexibleWidth();
        headerRow.Spacing = 8;
        var rows = new List<SwiftView> { headerRow };
        if (!collapsed)
        {
            if (!string.IsNullOrWhiteSpace(editor.PromptText))
            {
                rows.Add(style.Text(Views.Text(editor.PromptText), SourceTextRole.Body1)
                    .WithAccessibilityIdentifier(NativeDiaryAccess.PromptText));
                if (!string.IsNullOrWhiteSpace(editor.PromptHint))
                    rows.Add(style.Text(Views.Text(editor.PromptHint), SourceTextRole.Small, SourceTextTone.Secondary)
                        .WithAccessibilityIdentifier(NativeDiaryAccess.PromptHint));
            }
            else
                rows.Add(style.Text(Views.Text(text(NativeDiaryText.NoPromptYet)), SourceTextRole.Body2,
                        SourceTextTone.Secondary)
                    .WithAccessibilityIdentifier(NativeDiaryAccess.PromptEmpty));
        }
        var card = Views.VStack(rows.ToArray()).WithPadding(SourceUiLayout.CardInset);
        card.Spacing = 4;
        return style.Card(card).WithAccessibilityIdentifier(NativeDiaryAccess.PromptCard);
    }

    private SwiftView InputBar(NativeDiaryEditorState editor, SourceUiStyle style)
    {
        var text = _host.Text;
        var appearance = style.Appearance;
        var stars = Enumerable.Range(1, 3).Select(index => index <= editor.Stars
                ? Views.Image("star.fill").WithForegroundColor(appearance.Warning.Light, appearance.Warning.Dark)
                : Views.Image("star").WithForegroundColor(appearance.SecondaryText.Light, appearance.SecondaryText.Dark))
            .ToArray();
        var starRow = Views.HStack(stars)
            .WithAccessibilityLabel(
                $"{string.Format(CultureInfo.CurrentCulture, text(NativeDiaryText.StarsLabel), editor.Stars)}. {text(NativeDiaryText.StarsTooltip)}")
            .WithAccessibilityIdentifier(NativeDiaryAccess.Stars);
        starRow.Spacing = 2;
        var count = style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                text(NativeDiaryText.Stars), editor.WordCount)), SourceTextRole.Body2, SourceTextTone.Secondary)
            .WithLineLimit(1)
            .WithAccessibilityIdentifier(NativeDiaryAccess.WordCount);
        var leftItems = new List<SwiftView> { starRow, count };
        var wide = style.ViewportWidth >= 600;

        var decrease = style.LabelButton(Views.Image("minus"),
                () => Fire(_presenter.DecreaseFontSizeAsync), SourceButtonRole.FilledSecondary, 10)
            .WithAccessibilityLabel(text(NativeDiaryText.DecreaseFontSize))
            .WithAccessibilityIdentifier(NativeDiaryAccess.FontDecrease);
        SourceUiStyle.SetCustomButtonAvailability(decrease, appearance, SourceButtonRole.FilledSecondary,
            editor.CanDecreaseFont);
        // Flat in the row: a text nested in an inner HStack beside buttons is compressed to nothing.
        var size = style.Text(Views.Text($"{editor.FontSize}px"), SourceTextRole.Small)
            .WithAccessibilityLabel($"{text("Reading_FontSize")} {editor.FontSize}")
            .WithAccessibilityIdentifier(NativeDiaryAccess.FontSize);
        var increase = style.LabelButton(Views.Image("plus"),
                () => Fire(_presenter.IncreaseFontSizeAsync), SourceButtonRole.FilledSecondary, 10)
            .WithAccessibilityLabel(text(NativeDiaryText.IncreaseFontSize))
            .WithAccessibilityIdentifier(NativeDiaryAccess.FontIncrease);
        SourceUiStyle.SetCustomButtonAvailability(increase, appearance, SourceButtonRole.FilledSecondary,
            editor.CanIncreaseFont);
        // Kept flat in one HStack: nested stacks and flexible spacers compress the texts.
        var right = new List<SwiftView> { decrease, size, increase };
        // Narrow screens wrap like the source flex-wrap: the status leads the font/save row.
        var statusRow = new List<SwiftView>();
        var status = wide ? right : statusRow;
        if (editor.SaveStatus == NativeDiarySaveStatus.Saving)
            status.Add(Views.HStack(Views.IndeterminateProgress(text(NativeDiaryText.Saving)),
                    style.Text(Views.Text(text(NativeDiaryText.Saving)), SourceTextRole.Small, SourceTextTone.Secondary))
                .WithAccessibilityIdentifier(NativeDiaryAccess.SaveStatus));
        else if (editor.SaveStatus == NativeDiarySaveStatus.Saved)
            status.Add(Views.HStack(
                    Views.Image("checkmark.circle").WithForegroundColor(appearance.Success.Light, appearance.Success.Dark),
                    style.Text(Views.Text(text(NativeDiaryText.Saved)), SourceTextRole.Small, SourceTextTone.Success))
                .WithAccessibilityLabel(text(NativeDiaryText.Saved))
                .WithAccessibilityIdentifier(NativeDiaryAccess.SaveStatus));
        var left = Views.HStack(leftItems.ToArray());
        left.Spacing = 8;
        SwiftView saveLabel = style.ViewportWidth >= 768
            ? Views.HStack(Views.Image("square.and.arrow.down"), Views.Text(text(NativeDiaryText.Save)))
            : Views.Image("square.and.arrow.down");
        var save = style.LabelButton(saveLabel, () => Fire(token => _presenter.SaveAsync(false, token)),
                SourceButtonRole.Primary, 12)
            .WithAccessibilityLabel(text(NativeDiaryText.Save))
            .WithAccessibilityIdentifier(NativeDiaryAccess.Save);
        SourceUiStyle.SetCustomButtonAvailability(save, appearance, SourceButtonRole.Primary, editor.CanSave);
        right.Add(save);
        static SwiftView FlatRow(SwiftView[] items)
        {
            var row = Views.HStack(items);
            row.Spacing = 8;
            return row;
        }
        var rightRow = wide ? FlatRow([.. right]) : null;

        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        // Source flex-wrap: the two groups share a row from 600 pt, otherwise they stack.
        SwiftView bar = wide
            ? Views.HStack(left, Views.VStack().WithFlexibleWidth(), rightRow!)
            : Views.VStack(left.WithFlexibleWidth(),
                FlatRow([.. statusRow, .. right]).WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Trailing));
        bar.WithPadding(8, gutter, 8, gutter)
            .WithFlexibleWidth()
            .WithBackgroundColor(appearance.BodyBackground.Light, appearance.BodyBackground.Dark);
        bar.Spacing = 8;
        return bar;
    }

    private SwiftView RenderViewer(NativeDiaryState state, NativeDiaryViewerState viewer, SourceUiStyle style)
    {
        var text = _host.Text;
        var items = new List<SwiftView>();
        if (!state.Notices.IsDefaultOrEmpty)
            if (Notices(state, style) is { } notices)
                items.Add(notices);
        if (viewer.NotFound)
        {
            var warning = style.Appearance.Warning;
            var missing = Views.VStack(
                    Views.Image("exclamationmark.triangle").WithForegroundColor(warning.Light, warning.Dark),
                    style.Text(Views.Text(text(NativeDiaryText.NotFound)), SourceTextRole.Body1,
                        SourceTextTone.Secondary),
                    style.SecondaryButton(text(NativeDiaryText.GoBack), () => Fire(_presenter.BackToListAsync))
                        .WithAccessibilityIdentifier(NativeDiaryAccess.GoBack))
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                .WithPadding(24)
                .WithAccessibilityIdentifier(NativeDiaryAccess.NotFound);
            missing.Spacing = 8;
            items.Add(missing);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(viewer.PromptText))
            {
                var prompt = new List<SwiftView>
                {
                    style.Text(Views.Text(text(NativeDiaryText.Prompt).ToUpper(CultureInfo.CurrentCulture)),
                        SourceTextRole.Small, SourceTextTone.Secondary),
                    style.Text(Views.Text(viewer.PromptText), SourceTextRole.Body1)
                };
                if (!string.IsNullOrWhiteSpace(viewer.PromptHint))
                    prompt.Add(style.Text(Views.Text(viewer.PromptHint), SourceTextRole.Small, SourceTextTone.Secondary));
                var card = Views.VStack(prompt.ToArray()).WithPadding(SourceUiLayout.CardInset);
                card.Spacing = 4;
                items.Add(style.Card(card).WithAccessibilityIdentifier(NativeDiaryAccess.ViewerPrompt));
            }
            var meta = Views.HStack(
                style.Text(Views.Text(NativeDiaryText.FormatDate(viewer.EntryDate)), SourceTextRole.Small,
                    SourceTextTone.Secondary).WithAccessibilityIdentifier(NativeDiaryAccess.ViewerDate),
                Views.VStack().WithFlexibleWidth(),
                style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture, text(NativeDiaryText.WordCount),
                        viewer.WordCount)), SourceTextRole.Small, SourceTextTone.Secondary)
                    .WithAccessibilityIdentifier(NativeDiaryAccess.ViewerWordCount));
            var content = Views.VStack(meta,
                    style.Text(Views.Text(viewer.Content), SourceTextRole.Body1)
                        .WithAccessibilityIdentifier(NativeDiaryAccess.ViewerContent))
                .WithPadding(SourceUiLayout.CardInset);
            content.Spacing = 8;
            items.Add(style.Card(content));
            items.Add(FeedbackCard(viewer, style));
        }
        return style.Page(Views.Scroll(Content(items, style, "native.diary.viewer.content-stack"))
            .WithAccessibilityIdentifier(NativeDiaryAccess.Viewer));
    }

    private SwiftView FeedbackCard(NativeDiaryViewerState viewer, SourceUiStyle style)
    {
        var text = _host.Text;
        var primary = style.Appearance.Primary;
        var title = Views.HStack(Views.Image("sparkles").WithForegroundColor(primary.Light, primary.Dark),
                style.Text(Views.Text(text(NativeDiaryText.AiFeedback)), SourceTextRole.Title3, sourceHeading: true))
            .WithFlexibleWidth();
        var buttonText = viewer.IsLoadingFeedback ? text(NativeDiaryText.Analyzing)
            : text(viewer.HasFeedback ? NativeDiaryText.Regenerate : NativeDiaryText.GetFeedback);
        SwiftView label = viewer.IsLoadingFeedback
            ? Views.HStack(Views.IndeterminateProgress(buttonText), Views.Text(buttonText))
            : Views.HStack(Views.Image("arrow.clockwise"), Views.Text(buttonText));
        var button = style.LabelButton(label, () => Fire(_presenter.RequestFeedbackAsync),
                SourceButtonRole.FilledSecondary, 12)
            .WithAccessibilityLabel(buttonText)
            .WithAccessibilityIdentifier(NativeDiaryAccess.FeedbackButton);
        SourceUiStyle.SetCustomButtonAvailability(button, style.Appearance, SourceButtonRole.FilledSecondary,
            !viewer.IsLoadingFeedback);
        var header = Views.HStack(title, button);
        header.Spacing = 8;
        var rows = new List<SwiftView> { header };
        if (viewer.HasFeedback)
        {
            void Section(string key, string? value, SourceTextRole role, string id)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return;
                var section = Views.VStack(
                        style.Text(Views.Text(text(key).ToUpper(CultureInfo.CurrentCulture)), SourceTextRole.Small,
                            SourceTextTone.Secondary),
                        style.Text(Views.Text(value), role))
                    .WithFlexibleWidth()
                    .WithAccessibilityIdentifier(id);
                section.Spacing = 2;
                rows.Add(section);
            }
            Section(NativeDiaryText.Recommended, viewer.FeedbackRecommended, SourceTextRole.Body1,
                NativeDiaryAccess.FeedbackRecommended);
            Section(NativeDiaryText.Notes, viewer.FeedbackNotes, SourceTextRole.Body2, NativeDiaryAccess.FeedbackNotes);
            Section(NativeDiaryText.Strengths, viewer.FeedbackStrengths, SourceTextRole.Body2,
                NativeDiaryAccess.FeedbackStrengths);
        }
        else if (!viewer.IsLoadingFeedback)
            rows.Add(style.Text(Views.Text(text(NativeDiaryText.FeedbackHint)), SourceTextRole.Body2,
                    SourceTextTone.Secondary)
                .WithAccessibilityIdentifier(NativeDiaryAccess.FeedbackHint));
        var card = Views.VStack(rows.ToArray()).WithPadding(SourceUiLayout.CardInset);
        card.Spacing = 12;
        return style.Card(card).WithAccessibilityIdentifier(NativeDiaryAccess.Feedback);
    }

    // ---- Shared pieces ----

    private SwiftView NoticesOrEmpty(NativeDiaryState state, SourceUiStyle style) =>
        Notices(state, style) ?? Views.VStack();

    /// <summary>Errors and warnings stay inline until the next action; success and info are the scene's toast.</summary>
    private SwiftView? Notices(NativeDiaryState state, SourceUiStyle style)
    {
        var text = _host.Text;
        return style.MessageBanners(state.Notices, text, index => $"{NativeDiaryAccess.Notice}.{index}",
            () => Fire(_presenter.DismissNoticesAsync), NativeDiaryAccess.DismissNotices,
            text(NativeDiaryText.Dismiss));
    }

    private static SwiftView Spinner(string label) =>
        Views.IndeterminateProgress(label).WithAccessibilityIdentifier(NativeDiaryAccess.Loading);

    private static SwiftView Content(List<SwiftView> items, SourceUiStyle style, string id)
    {
        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var content = Views.VStack(items.ToArray())
            .WithPadding(16, gutter, 16, gutter)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(id);
        content.Spacing = SourceUiLayout.PageGap;
        return content;
    }

    public void Dispose()
    {
        _presenter.StateChanged -= OnStateChanged;
        _presenter.Dispose();
    }
}
