using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native.Features.Import;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Shared.Models;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Import;

/// <summary>
/// What the page opens: the saved or imported resource (source /resources/edit/{id}) and the
/// channel page (source /media-import/channel and /media-import/channel/{id}).
/// </summary>
internal sealed record YouTubeImportActions(
    Action<string> ViewResource, Action? AddChannel, Action<string>? EditChannel);

/// <summary>
/// The source Media Import page (src/SentenceStudio.UI/Pages/MediaImport.razor) in SwiftUI: the
/// page menu (Add Channel, Refresh; the Add Channel button on wide pages), the tab group, and the
/// My Channels, Single Video and Import History tabs. Typing the link or the transcript updates the
/// coordinator without drawing the page again; the Fetch button's availability follows the link
/// in place.
/// </summary>
internal static class YouTubeImportView
{
    internal const string ScrollId = "native.import.youtube.scroll";
    internal const string ContentId = "native.import.youtube.scroll.content";

    private static readonly SourceColorPair InfoBadge = new("#0DCAF0", "#0DCAF0");
    private static readonly SourceColorPair DarkBadgeText = new("#000000", "#000000");
    private static readonly SourceColorPair LightBadgeText = new("#FFFFFF", "#FFFFFF");
    private static readonly SourceColorPair YouTubeRed = new("#DC3545", "#EA868F");
    private static readonly SourceColorPair WarningOutline = new("#997404", "#FFDA6A");

    internal static SwiftView Page(YouTubeImportCoordinator import, SourceUiStyle style,
        Func<string, string> text, YouTubeImportActions actions)
    {
        var state = import.State;
        var items = new List<SwiftView> { Tabs(import, state, style, text) };
        if (state.Blocked)
            items.Add(style.Alert(Views.Text(text("Import_ProfileNotFound")), success: false)
                .WithAccessibilityIdentifier("native.import.youtube.blocked"));
        else
            items.AddRange(state.Tab switch
            {
                YouTubeImportTab.Channels => Channels(import, state, style, text, actions),
                YouTubeImportTab.SingleVideo => SingleVideo(import, state, style, text, actions),
                _ => History(import, state, style, text, actions),
            });
        var content = Views.VStack(items.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(ContentId);
        content.Spacing = SourceUiLayout.FormGap;
        var page = style.Page(Views.Scroll(content).WithAccessibilityIdentifier(ScrollId)
            .WithScrollDismissesKeyboard(SwiftScrollDismissesKeyboard.Interactively));
        return Views.Toolbar(page, Toolbar(import, state, style, text, actions));
    }

    /// <summary>
    /// Source PageHeader: Add Channel shows as a button on wide pages (PrimaryActions are
    /// d-none d-md-flex); the overflow menu (SecondaryActions) holds Add Channel and Refresh.
    /// </summary>
    private static SwiftView[] Toolbar(YouTubeImportCoordinator import, YouTubeImportState state,
        SourceUiStyle style, Func<string, string> text, YouTubeImportActions actions)
    {
        var enabled = !state.Blocked;
        var items = new List<SwiftView>();
        var menuItems = new List<SwiftView>();
        if (actions.AddChannel is { } addChannel)
        {
            if (style.ViewportWidth >= 768)
            {
                var add = style.LabelButton(IconLabel("plus", text("Import_AddChannel")), addChannel,
                        SourceButtonRole.Primary)
                    .WithAccessibilityIdentifier("native.import.youtube.add-channel");
                add.IsEnabled = enabled;
                items.Add(add);
            }
            var menuAdd = Views.Button(IconLabel("plus", text("Import_AddChannel")), addChannel)
                .WithAccessibilityIdentifier("native.import.youtube.menu.add-channel");
            menuAdd.Text = text("Import_AddChannel");
            menuItems.Add(menuAdd);
        }
        var refresh = Views.Button(IconLabel("arrow.triangle.2.circlepath", text("Import_Refresh")),
                () => _ = import.RefreshImportsAsync())
            .WithAccessibilityIdentifier("native.import.youtube.menu.refresh");
        refresh.Text = text("Import_Refresh");
        menuItems.Add(refresh);
        var color = style.Appearance.SecondaryText;
        var menu = Views.Menu(Views.Image("ellipsis.circle").WithFixedSymbolSize(20)
                    .WithForegroundColor(color.Light, color.Dark),
                menuItems.ToArray())
            .WithAccessibilityLabel(text("More"))
            .WithAccessibilityIdentifier("native.import.youtube.menu");
        menu.IsEnabled = enabled;
        items.Add(menu);
        return items.ToArray();
    }

    /// <summary>
    /// Source: the full-width button group (btn-group w-100; selected primary, the others
    /// outline). Each button takes a third of the width and its label wraps inside it, as the
    /// source's flex buttons do on a phone (the bridge's button group keeps every member at its
    /// one-line width, which overflows a phone).
    /// </summary>
    private static SwiftView Tabs(YouTubeImportCoordinator import, YouTubeImportState state,
        SourceUiStyle style, Func<string, string> text)
    {
        SwiftView Tab(YouTubeImportTab tab, string symbol, string key, string id,
            SourceButtonGroupPosition position)
        {
            var selected = state.Tab == tab;
            var label = Views.HStack(Views.Image(symbol), Views.Text(text(key)));
            label.Spacing = 4;
            var button = style.LabelButton(label, () => import.SwitchTab(tab),
                    selected ? SourceButtonRole.Primary : SourceButtonRole.OutlineSecondary,
                    horizontalPadding: 8, fullWidth: true, groupPosition: position)
                .WithAccessibilityLabel(text(key))
                .WithAccessibilitySelected(selected)
                .WithAccessibilityIdentifier($"native.import.youtube.tab.{id}");
            button.Text = text(key);
            return button;
        }
        var tabs = Views.HStack(
                Tab(YouTubeImportTab.Channels, "play.rectangle", "Import_MyChannels", "channels",
                    SourceButtonGroupPosition.First),
                Tab(YouTubeImportTab.SingleVideo, "link", "Import_SingleVideo", "single",
                    SourceButtonGroupPosition.Middle),
                Tab(YouTubeImportTab.History, "clock.arrow.circlepath", "Import_History", "history",
                    SourceButtonGroupPosition.Last))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.import.youtube.tabs");
        // The bridge's stacks take no negative spacing: neighbouring borders meet, not overlap.
        tabs.Spacing = 0;
        return tabs;
    }

    // ---- My Channels (source MediaImport.razor:40-100) ----

    private static IEnumerable<SwiftView> Channels(YouTubeImportCoordinator import,
        YouTubeImportState state, SourceUiStyle style, Func<string, string> text,
        YouTubeImportActions actions)
    {
        if (state.IsLoadingChannels)
        {
            yield return Spinner(style, text, "native.import.youtube.channels.loading");
            yield break;
        }
        if (state.Channels.IsDefaultOrEmpty)
        {
            var add = actions.AddChannel is { } addChannel
                ? style.LabelButton(IconLabel("plus.circle", text("Import_AddFirstChannel")),
                        addChannel, SourceButtonRole.Primary)
                    .WithAccessibilityIdentifier("native.import.youtube.channels.add-first")
                : null;
            yield return Empty(style, "play.rectangle", text("Import_NoChannelsYet"), add,
                "native.import.youtube.channels.empty");
            yield break;
        }
        foreach (var channel in state.Channels)
            yield return ChannelCard(import, channel, style, text, actions);
    }

    private static SwiftView ChannelCard(YouTubeImportCoordinator import, YouTubeChannelRow channel,
        SourceUiStyle style, Func<string, string> text, YouTubeImportActions actions)
    {
        var id = $"native.import.youtube.channel.{channel.Id}";
        var title = channel.ChannelName ?? channel.ChannelHandle ?? text("YouTubeImport_UnnamedChannel");
        var details = new List<string>();
        if (!string.IsNullOrEmpty(channel.ChannelHandle))
            details.Add(channel.ChannelHandle);
        if (!string.IsNullOrEmpty(channel.Language))
            details.Add(channel.Language);
        var lines = new List<SwiftView>
        {
            style.Text(Views.Text(title), SourceTextRole.Title3, sourceHeading: true)
                .WithLineLimit(1)
        };
        var meta = new List<SwiftView>();
        if (details.Count > 0)
            meta.Add(Caption(style, string.Join(" • ", details)));
        var checkedAt = channel.LastCheckedAt is { } last
            ? last.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : text("Import_Never");
        meta.Add(Caption(style, $"{text("Import_LastChecked")} {checkedAt}"));
        var metaRow = Views.HStack(meta.ToArray()).WithAdaptiveStack(420);
        metaRow.Spacing = SourceUiLayout.ItemGap;
        lines.Add(metaRow);
        var column = Views.VStack(lines.ToArray()).WithFlexibleWidth();
        column.Spacing = 4;
        var icon = Views.Image("play.rectangle.fill").WithFixedSymbolSize(24)
            .WithForegroundColor(YouTubeRed.Light, YouTubeRed.Dark);
        var open = Views.Button(Views.HStack(icon, column), () => actions.EditChannel?.Invoke(channel.Id))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth()
            .WithAccessibilityLabel(title)
            .WithAccessibilityIdentifier($"{id}.open");
        // Source: a bare switch whose title says what it does next (its accessible name here).
        var monitor = Views.Toggle(text(channel.IsActive
                    ? "Import_PauseMonitoring" : "Import_ResumeMonitoring"), channel.IsActive,
                isOn => _ = import.ToggleChannelActiveAsync(channel.Id))
            .WithLabelsHidden()
            .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithAccessibilityIdentifier($"{id}.active");
        // The switch waits while its change is saved.
        monitor.IsEnabled = !channel.IsSaving;
        var row = Views.HStack(open, monitor).WithFlexibleWidth();
        row.Spacing = SourceUiLayout.ItemGap + 4;
        return style.Card(row.WithPadding(SourceUiLayout.CardInset))
            .WithAccessibilityIdentifier(id);
    }

    // ---- Single Video (source MediaImport.razor:103-206) ----

    private static IEnumerable<SwiftView> SingleVideo(YouTubeImportCoordinator import,
        YouTubeImportState state, SourceUiStyle style, Func<string, string> text,
        YouTubeImportActions actions)
    {
        // Step 1: the link and Fetch.
        SwiftView? fetch = null;
        var url = style.TextInput(Views.TextField("https://www.youtube.com/watch?v=...", state.VideoUrl,
                    value =>
                    {
                        import.SetVideoUrl(value);
                        if (fetch is not null)
                            SetAvailability(fetch, style, import.CanFetch);
                    })
                .OnSubmit(() => _ = import.FetchTranscriptsAsync()))
            .WithFlexibleWidth()
            .WithAccessibilityLabel(text("Import_YouTubeURL"))
            .WithAccessibilityIdentifier("native.import.youtube.url");
        var fetchLabel = state.IsLoadingTranscripts
            ? Views.HStack(Views.IndeterminateProgress(text("Import_Fetching")).WithTintColor("#FFFFFF", "#FFFFFF"),
                Views.Text(text("Import_Fetch")))
            : Views.HStack(Views.Text(text("Import_Fetch")));
        fetchLabel.Spacing = 4;
        fetch = style.LabelButton(fetchLabel, () => _ = import.FetchTranscriptsAsync(),
                SourceButtonRole.Primary, horizontalPadding: 24)
            .WithAccessibilityLabel(text("Import_Fetch"))
            .WithAccessibilityIdentifier("native.import.youtube.fetch");
        SetAvailability(fetch, style, state.CanFetch);
        var link = Views.HStack(url, fetch).WithFlexibleWidth();
        link.Spacing = SourceUiLayout.ItemGap;
        yield return Card(style, "native.import.youtube.link", Label(style, text("Import_YouTubeURL")), link);

        // Step 2: the transcript.
        if (state.HasTracks)
        {
            var children = new List<SwiftView>();
            if (state.Tracks.Length > 1)
            {
                var options = state.Tracks.Select(track => new SwiftPickerOption(
                    track.Index.ToString(CultureInfo.InvariantCulture),
                    track.IsAutoGenerated
                        ? $"{track.LanguageName} {text("YouTubeImport_AutoShort")}"
                        : track.LanguageName)).ToArray();
                var picker = style.Input(Views.Picker(string.Empty, options,
                            Math.Max(state.SelectedTrack, 0).ToString(CultureInfo.InvariantCulture),
                            value => _ = import.SelectTrackAsync(
                                int.Parse(value, CultureInfo.InvariantCulture)))
                        .WithPickerStyle(SwiftPickerStyle.Menu)
                        .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Leading))
                    .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
                    .WithAccessibilityLabel(text("Import_TranscriptLanguage"))
                    .WithAccessibilityIdentifier("native.import.youtube.track");
                var field = Views.VStack(Label(style, text("Import_TranscriptLanguage")), picker)
                    .WithFlexibleWidth();
                field.Spacing = SourceUiLayout.ItemGap;
                children.Add(field);
            }
            else
            {
                var only = state.Tracks[0];
                var line = Views.HStack(
                    style.Text(Views.Text(text("YouTubeImport_LanguageLabel")), SourceTextRole.Body2,
                        SourceTextTone.Secondary),
                    style.Text(Views.Text(only.LanguageName), SourceTextRole.Body2Strong,
                        SourceTextTone.Secondary, strongElement: true));
                line.Spacing = 4;
                if (only.IsAutoGenerated)
                    line.Add(style.Text(Views.Text(text("YouTubeImport_AutoGenerated")),
                        SourceTextRole.Body2, SourceTextTone.Secondary));
                children.Add(line.WithAccessibilityIdentifier("native.import.youtube.track.single"));
            }

            if (state.HasTranscript)
            {
                var editor = style.Input(Views.TextEditor(state.TranscriptText, import.SetTranscriptText)
                        .WithTextEditorScrollBackgroundHidden())
                    .WithMinimumHeight(Rows(style, 12))
                    .WithAccessibilityLabel(text("Import_Transcript"))
                    .WithAccessibilityIdentifier("native.import.youtube.transcript");
                var field = Views.VStack(Label(style, text("Import_Transcript")), editor).WithFlexibleWidth();
                field.Spacing = SourceUiLayout.ItemGap;
                children.Add(field);

                var polishLabel = Views.HStack();
                if (state.IsPolishing)
                    polishLabel.Add(Views.IndeterminateProgress(text("Common_Loading")));
                polishLabel.Add(Views.Image("sparkles"));
                polishLabel.Add(Views.Text(text("Import_PolishWithAI")));
                polishLabel.Spacing = 4;
                var polish = style.LabelButton(polishLabel, () => _ = import.PolishAsync(),
                        SourceButtonRole.FilledSecondary)
                    .WithAccessibilityLabel(text("Import_PolishWithAI"))
                    .WithAccessibilityIdentifier("native.import.youtube.polish");
                SourceUiStyle.SetCustomButtonAvailability(polish, style.Appearance,
                    SourceButtonRole.FilledSecondary, !state.IsPolishing && !state.IsSaving);
                var saveLabel = Views.HStack();
                if (state.IsSaving)
                    saveLabel.Add(Views.IndeterminateProgress(text("Import_Saving")).WithTintColor("#FFFFFF", "#FFFFFF"));
                saveLabel.Add(Views.Image("square.and.arrow.down"));
                saveLabel.Add(Views.Text(text("Import_SaveAsResource")));
                saveLabel.Spacing = 4;
                var save = style.LabelButton(saveLabel, () => _ = import.SaveAsResourceAsync(),
                        SourceButtonRole.Primary, fullWidth: true)
                    .WithAccessibilityLabel(text("Import_SaveAsResource"))
                    .WithAccessibilityIdentifier("native.import.youtube.save");
                SetAvailability(save, style, !state.IsSaving);
                var buttons = Views.HStack(polish, save).WithFlexibleWidth();
                buttons.Spacing = SourceUiLayout.ItemGap;
                children.Add(buttons);
            }
            else if (state.IsLoadingTranscript)
            {
                var downloading = Views.HStack(Views.IndeterminateProgress(text("Import_DownloadingTranscript")),
                    style.Text(Views.Text(text("Import_DownloadingTranscript")), SourceTextRole.Body2,
                        SourceTextTone.Secondary));
                downloading.Spacing = SourceUiLayout.ItemGap;
                children.Add(downloading.WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithPadding(16)
                    .WithAccessibilityIdentifier("native.import.youtube.downloading"));
            }
            yield return Card(style, "native.import.youtube.transcript.card", children.ToArray());
        }

        // Success result.
        if (state.SavedResourceId is { } saved)
        {
            var view = style.SuccessOutlineSmallButton(Views.Text(text("Import_ViewResource")),
                    () => actions.ViewResource(saved))
                .WithAccessibilityIdentifier("native.import.youtube.saved.view");
            var another = style.LabelButton(Views.Text(text("Import_ImportAnother")), import.Reset,
                    SourceButtonRole.OutlineSecondary, horizontalPadding: 8)
                .WithAccessibilityIdentifier("native.import.youtube.saved.another");
            var message = Views.HStack(Views.Image("checkmark.circle.fill"),
                Views.Text(text("Import_ResourceSaved")));
            message.Spacing = 4;
            var row = Views.HStack(message.WithFlexibleWidth(), view, another).WithAdaptiveStack(420);
            row.Spacing = SourceUiLayout.ItemGap;
            yield return style.Alert(row, success: true)
                .WithAccessibilityIdentifier("native.import.youtube.saved");
        }

        // Error.
        if (state.Error is { } error)
            yield return style.Alert(Views.Text(error.Format(text)), success: false)
                .WithAccessibilityIdentifier("native.import.youtube.error");
    }

    // ---- Import History (source MediaImport.razor:209-272) ----

    private static IEnumerable<SwiftView> History(YouTubeImportCoordinator import,
        YouTubeImportState state, SourceUiStyle style, Func<string, string> text,
        YouTubeImportActions actions)
    {
        if (state.IsLoadingImports)
        {
            yield return Spinner(style, text, "native.import.youtube.history.loading");
            yield break;
        }
        if (state.ImportCount == 0)
        {
            yield return Empty(style, "clock.arrow.circlepath", text("Import_NoHistoryYet"), null,
                "native.import.youtube.history.empty");
            yield break;
        }
        yield return Caption(style, Format(text("Import_ShowingCount"),
                state.DisplayedImports.Length, state.ImportCount))
            .WithAccessibilityIdentifier("native.import.youtube.history.count");
        var rows = state.DisplayedImports.Select(row => ImportRow(import, state, row, style, text, actions))
            .ToArray();
        var list = Views.LazyVGrid(1, rows).WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.import.youtube.history.rows");
        list.Spacing = 0;
        yield return style.BorderedCard(list, radius: 8);
        if (state.HasMoreImports)
            yield return style.LabelButton(IconLabel("chevron.down", text("Import_ShowMore")),
                    import.ShowMoreImports, SourceButtonRole.FilledSecondary)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                .WithAccessibilityIdentifier("native.import.youtube.history.more");
    }

    private static SwiftView ImportRow(YouTubeImportCoordinator import, YouTubeImportState state,
        YouTubeImportRow row, SourceUiStyle style, Func<string, string> text,
        YouTubeImportActions actions)
    {
        var id = $"native.import.youtube.import.{row.Id}";
        var title = row.VideoTitle ?? text("YouTubeImport_Untitled");
        var titleText = style.Text(Views.Text(title), SourceTextRole.Body1Strong).WithLineLimit(1);
        var open = Views.Button(titleText, () =>
            {
                if (import.OpenImport(row.Id) is { } resource)
                    actions.ViewResource(resource);
            })
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth()
            .WithAccessibilityLabel(title)
            .WithAccessibilityIdentifier($"{id}.open");
        // Source title attribute: the failure's message, or the stuck hint.
        if (row.IsFailed && !string.IsNullOrEmpty(row.ErrorMessage))
            open.WithAccessibilityLabel($"{title}. {row.ErrorMessage}");
        else if (row.IsStuck)
            open.WithAccessibilityLabel($"{title}. {text("YouTubeImport_StuckHint")}");

        var cells = new List<SwiftView> { StatusBadge(row.Status, style, text, $"{id}.status"), open };
        // Source: the channel column shows from the md breakpoint up (d-none d-md-inline).
        if (style.ViewportWidth >= 768)
            cells.Add(Caption(style, row.IsManual ? text("Import_ManualImport")
                    : row.ChannelName ?? text("YouTubeImport_ChannelFallback"))
                .WithLineLimit(1)
                .WithAccessibilityIdentifier($"{id}.channel"));
        if (row.CreatedAt is { } created)
            cells.Add(Caption(style, created.ToLocalTime().ToString("d", CultureInfo.CurrentCulture))
                .WithAccessibilityIdentifier($"{id}.date"));
        if (row.CanRetry)
        {
            var retrying = state.RetryingImportId == row.Id;
            var label = Views.HStack();
            if (retrying)
                label.Add(Views.IndeterminateProgress(text("Common_Loading")));
            label.Add(Views.Image("arrow.clockwise"));
            label.Add(Views.Text(text("Import_Retry")));
            label.Spacing = 4;
            var retry = OutlineWarningButton(label, () => _ = import.RetryImportAsync(row.Id), style)
                .WithAccessibilityLabel(text("Import_Retry"))
                .WithAccessibilityIdentifier($"{id}.retry");
            retry.IsEnabled = state.RetryingImportId is null;
            cells.Add(retry);
        }
        var line = Views.HStack(cells.ToArray()).WithFlexibleWidth();
        line.Spacing = SourceUiLayout.ItemGap + 4;
        return line.WithPadding(8, 16, 8, 16)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(id);
    }

    /// <summary>
    /// Source btn btn-sm btn-outline-warning: no fill, a warning border and text. The fill stays
    /// unset (the bridge's surface colors are opaque #RRGGBB only).
    /// </summary>
    internal static SwiftView OutlineWarningButton(SwiftView label, Action action, SourceUiStyle style)
    {
        label.WithForegroundColor(WarningOutline.Light, WarningOutline.Dark)
            .WithPadding(4, 8, 4, 8);
        return Views.Button(label, action)
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithButtonAppearance(null, null,
                style.Appearance.Warning.Light, style.Appearance.Warning.Dark, 6)
            .WithFixedSize();
    }

    /// <summary>Source RenderStatusBadge: the status's color, icon and text, with a spinner while it runs.</summary>
    private static SwiftView StatusBadge(VideoImportStatus status, SourceUiStyle style,
        Func<string, string> text, string id)
    {
        var (fill, foreground, symbol, label) = status switch
        {
            VideoImportStatus.Pending => (style.Appearance.Warning, DarkBadgeText, "hourglass", text("Import_Pending")),
            VideoImportStatus.FetchingTranscript => (InfoBadge, DarkBadgeText, "arrow.down.circle", text("Import_Fetching")),
            VideoImportStatus.CleaningTranscript => (InfoBadge, DarkBadgeText, "sparkles", text("Import_Cleaning")),
            VideoImportStatus.GeneratingVocabulary => (InfoBadge, DarkBadgeText, "doc.text", text("Import_Generating")),
            VideoImportStatus.SavingResource => (InfoBadge, DarkBadgeText, "square.and.arrow.down", text("Import_Saving")),
            VideoImportStatus.Completed => (style.Appearance.Success, LightBadgeText, "checkmark.circle", text("Import_Completed")),
            VideoImportStatus.Failed => (style.Appearance.Danger, LightBadgeText, "xmark.circle", text("Import_Failed")),
            _ => (style.Appearance.BootstrapSecondary, LightBadgeText, "questionmark.circle", text("Unknown"))
        };
        var content = Views.HStack(Views.Image(symbol), style.Text(Views.Text(label), SourceTextRole.BadgeSmall));
        content.Spacing = 3;
        if (status >= VideoImportStatus.FetchingTranscript && status < VideoImportStatus.Completed)
            content.Add(Views.IndeterminateProgress(label).WithTintColor(foreground.Light, foreground.Dark));
        // Source .badge is white-space: nowrap: the badge keeps its width and the title truncates.
        return content
            .WithPadding(4, 8, 4, 8)
            .WithRoundedSurface(fill.Light, fill.Dark, 6)
            .WithForegroundColor(foreground.Light, foreground.Dark)
            .WithFixedSize()
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier(id);
    }

    // ---- Messages ----

    /// <summary>
    /// The page's messages, pinned over its top (source toasts are fixed over the page): errors
    /// stay until dismissed or the next action, success and info toasts expire.
    /// </summary>
    internal static SwiftView? Messages(ImmutableArray<NativeMessage> messages, NativeMessage? toast,
        SourceUiStyle style, Func<string, string> text, Action dismissBanners, Action dismissToast)
    {
        var items = new List<SwiftView>();
        if (style.MessageBanners(messages, text,
                index => $"native.import.youtube.message.{index}", dismissBanners,
                "native.import.youtube.message.dismiss") is { } banners)
            items.Add(style.Card(banners.WithPadding(8), elevated: true, radius: 8));
        if (toast is not null)
            items.Add(style.MessageToast(toast, text, dismissToast));
        if (items.Count == 0)
            return null;
        var stack = Views.VStack(items.ToArray()).WithFlexibleWidth();
        stack.Spacing = SourceUiLayout.ItemGap;
        return stack;
    }

    // ---- Helpers ----

    internal static void SetAvailability(SwiftView button, SourceUiStyle style, bool enabled) =>
        SourceUiStyle.SetCustomButtonAvailability(button, style.Appearance, SourceButtonRole.Primary, enabled);

    private static SwiftView Spinner(SourceUiStyle style, Func<string, string> text, string id) =>
        Views.IndeterminateProgress(text("Common_Loading"))
            .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithPadding(48)
            .WithAccessibilityIdentifier(id);

    private static SwiftView Empty(SourceUiStyle style, string symbol, string message, SwiftView? action,
        string id)
    {
        // Source text-center: each line is centered (the bridge's stacks align their children leading).
        var color = style.Appearance.SecondaryText;
        var children = new List<SwiftView>
        {
            Views.Image(symbol).WithFixedSymbolSize(40).WithForegroundColor(color.Light, color.Dark)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center),
            style.Text(Views.Text(message), SourceTextRole.Body1, SourceTextTone.Secondary)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center),
        };
        if (action is not null)
            children.Add(action.WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center));
        var column = Views.VStack(children.ToArray())
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithPadding(48);
        column.Spacing = 16;
        return column.WithAccessibilityIdentifier(id);
    }

    private static SwiftView Card(SourceUiStyle style, string id, params SwiftView[] children)
    {
        var column = Views.VStack(children).WithFlexibleWidth();
        column.Spacing = SourceUiLayout.ItemGap + 4;
        return style.Card(column.WithPadding(SourceUiLayout.CardInset))
            .WithAccessibilityIdentifier(id);
    }

    private static SwiftView IconLabel(string symbol, string title)
    {
        var label = Views.HStack(Views.Image(symbol), Views.Text(title));
        label.Spacing = 4;
        return label;
    }

    private static SwiftView Label(SourceUiStyle style, string title) =>
        style.Text(Views.Text(title), SourceTextRole.Body2, SourceTextTone.Secondary);

    private static SwiftView Caption(SourceUiStyle style, string value) =>
        style.Text(Views.Text(value), SourceTextRole.Caption1, SourceTextTone.Secondary);

    private static string Format(string template, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, template, values);

    private static double Rows(SourceUiStyle style, int rows)
    {
        var font = SourceUiTypography.For(SourceTextRole.Input, style.ViewportWidth,
            style.Appearance.Selection.FontScale, style.Appearance.Selection.ThemeId);
        return rows * font.Size * font.LineHeight + 2 * SourceUiLayout.ItemGap;
    }
}
