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
/// The source Media Import page (src/SentenceStudio.UI/Pages/MediaImport.razor) in SwiftUI, as
/// one grouped Form (Captain 2026-11-13): Refresh and Add Channel in the toolbar, the tabs as a
/// segmented control, and the My Channels, Single Video and Import History Sections. Typing the link or the transcript updates the
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
        // Captain 2026-11-13 (the native form pattern): one grouped Form, the tabs as the
        // system segmented control in its first Section.
        var items = new List<SwiftView> { Views.Section(string.Empty, Tabs(import, state, style, text)) };
        if (state.Blocked)
            items.Add(Views.Section(string.Empty, NativeForm.Notice(style, text("Import_ProfileNotFound"),
                "native.import.youtube.blocked", error: true)));
        else
            items.AddRange(state.Tab switch
            {
                YouTubeImportTab.Channels => Channels(import, state, style, text, actions),
                YouTubeImportTab.SingleVideo => SingleVideo(import, state, style, text, actions),
                _ => History(import, state, style, text, actions),
            });
        var page = NativeForm.Page(style, items, ContentId, ScrollId);
        return Views.Toolbar(page, Toolbar(import, state, style, text, actions));
    }

    /// <summary>
    /// Source PageHeader: Add Channel and Refresh, as titled toolbar icons at every width (the
    /// browse pages' Add, departure 69).
    /// </summary>
    private static SwiftView[] Toolbar(YouTubeImportCoordinator import, YouTubeImportState state,
        SourceUiStyle style, Func<string, string> text, YouTubeImportActions actions)
    {
        var enabled = !state.Blocked;
        var items = new List<SwiftView>();
        var refresh = Views.Button(Views.Label(text("Import_Refresh"), "arrow.triangle.2.circlepath"),
                () => _ = import.RefreshImportsAsync())
            .WithAccessibilityIdentifier("native.import.youtube.menu.refresh");
        refresh.IsEnabled = enabled;
        items.Add(refresh);
        if (actions.AddChannel is { } addChannel)
        {
            var add = Views.Button(Views.Label(text("Import_AddChannel"), "plus"), addChannel)
                .WithAccessibilityIdentifier("native.import.youtube.add-channel");
            add.IsEnabled = enabled;
            items.Add(add);
        }
        return items.ToArray();
    }

    /// <summary>Source: the tab group (selected filled); the system segmented control.</summary>
    private static SwiftView Tabs(YouTubeImportCoordinator import, YouTubeImportState state,
        SourceUiStyle style, Func<string, string> text) =>
        Views.Picker(text("Import_MyChannels"),
                [
                    new SwiftPickerOption(nameof(YouTubeImportTab.Channels), text("Import_MyChannels")),
                    new SwiftPickerOption(nameof(YouTubeImportTab.SingleVideo), text("Import_SingleVideo")),
                    new SwiftPickerOption(nameof(YouTubeImportTab.History), text("Import_History")),
                ],
                state.Tab.ToString(),
                value => import.SwitchTab(Enum.Parse<YouTubeImportTab>(value)))
            .WithPickerStyle(SwiftPickerStyle.Segmented)
            .WithAccessibilityIdentifier("native.import.youtube.tabs");

    // ---- My Channels (source MediaImport.razor:40-100) ----

    private static IEnumerable<SwiftView> Channels(YouTubeImportCoordinator import,
        YouTubeImportState state, SourceUiStyle style, Func<string, string> text,
        YouTubeImportActions actions)
    {
        if (state.IsLoadingChannels)
        {
            yield return Views.Section(string.Empty, Spinner(style, text, "native.import.youtube.channels.loading"));
            yield break;
        }
        if (state.Channels.IsDefaultOrEmpty)
        {
            var rows = new List<SwiftView> { Empty(style, "play.rectangle", text("Import_NoChannelsYet"),
                "native.import.youtube.channels.empty") };
            if (actions.AddChannel is { } addChannel)
                rows.Add(NativeForm.Action(style, text("Import_AddFirstChannel"), "plus.circle",
                    addChannel, "native.import.youtube.channels.add-first"));
            yield return Views.Section(string.Empty, rows.ToArray());
            yield break;
        }
        yield return Views.Section(text("Import_MyChannels"),
            state.Channels.Select(channel => ChannelRow(import, channel, style, text, actions)).ToArray());
    }

    private static SwiftView ChannelRow(YouTubeImportCoordinator import, YouTubeChannelRow channel,
        SourceUiStyle style, Func<string, string> text, YouTubeImportActions actions)
    {
        var id = $"native.import.youtube.channel.{channel.Id}";
        var title = channel.ChannelName ?? channel.ChannelHandle ?? text("YouTubeImport_UnnamedChannel");
        var details = new List<string>();
        if (!string.IsNullOrEmpty(channel.ChannelHandle))
            details.Add(channel.ChannelHandle);
        if (!string.IsNullOrEmpty(channel.Language))
            details.Add(channel.Language);
        var checkedAt = channel.LastCheckedAt is { } last
            ? last.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : text("Import_Never");
        details.Add($"{text("Import_LastChecked")} {checkedAt}");
        var column = Views.VStack(
                style.Text(Views.Text(title), SourceTextRole.Body1Strong).WithLineLimit(1),
                Caption(style, string.Join(" • ", details)))
            .WithFlexibleWidth();
        column.Spacing = 2;
        var icon = Views.Image("play.rectangle.fill").WithFixedSymbolSize(24)
            .WithForegroundColor(YouTubeRed.Light, YouTubeRed.Dark);
        var label = Views.HStack(icon, column).WithFlexibleWidth();
        label.Spacing = SourceUiLayout.ItemGap + 4;
        // Plain style: the channel opens from its text, the switch stays its own target.
        var open = Views.Button(label, () => actions.EditChannel?.Invoke(channel.Id))
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
        row.Spacing = SourceUiLayout.ItemGap;
        return row.WithAccessibilityIdentifier(id);
    }

    // ---- Single Video (source MediaImport.razor:103-206) ----

    private static IEnumerable<SwiftView> SingleVideo(YouTubeImportCoordinator import,
        YouTubeImportState state, SourceUiStyle style, Func<string, string> text,
        YouTubeImportActions actions)
    {
        // Step 1: the link and Fetch.
        SwiftView? fetch = null;
        var url = (Views.TextField("https://www.youtube.com/watch?v=...", state.VideoUrl,
                    value =>
                    {
                        import.SetVideoUrl(value);
                        if (fetch is not null)
                            SetAvailability(fetch, style, import.CanFetch);
                    })
                .OnSubmit(() => _ = import.FetchTranscriptsAsync()))
            .WithAccessibilityLabel(text("Import_YouTubeURL"))
            .WithAccessibilityIdentifier("native.import.youtube.url");
        var fetchRow = new List<SwiftView> { url };
        fetch = NativeForm.Action(style, text("Import_Fetch"), "arrow.down.circle",
            () => _ = import.FetchTranscriptsAsync(), "native.import.youtube.fetch");
        SetAvailability(fetch, style, state.CanFetch);
        fetchRow.Add(fetch);
        if (state.IsLoadingTranscripts)
            fetchRow.Add(Views.IndeterminateProgress(text("Import_Fetching")));
        yield return Views.Section(text("Import_YouTubeURL"), fetchRow.ToArray());

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
                children.Add(NativeForm.Choice(style, text("Import_TranscriptLanguage"), options,
                    Math.Max(state.SelectedTrack, 0).ToString(CultureInfo.InvariantCulture),
                    "native.import.youtube.track", value =>
                    {
                        _ = import.SelectTrackAsync(int.Parse(value, CultureInfo.InvariantCulture));
                        return true;
                    }));
            }
            else
            {
                var only = state.Tracks[0];
                children.Add(NativeForm.Value(text("YouTubeImport_LanguageLabel").TrimEnd(':', ' '),
                    only.IsAutoGenerated
                        ? $"{only.LanguageName} {text("YouTubeImport_AutoGenerated")}"
                        : only.LanguageName,
                    "native.import.youtube.track.single"));
            }

            if (state.HasTranscript)
            {
                children.Add(NativeForm.Editor(style, text("Import_Transcript"), state.TranscriptText,
                    "native.import.youtube.transcript", import.SetTranscriptText, rows: 12));
                var polishLabel = state.IsPolishing
                    ? Views.HStack(Views.IndeterminateProgress(text("Common_Loading")),
                        Views.Text(text("Import_PolishWithAI")))
                    : Views.Label(text("Import_PolishWithAI"), "sparkles");
                if (state.IsPolishing)
                    polishLabel.Spacing = SourceUiLayout.ItemGap;
                var polish = NativeForm.Action(style, Views.Button(polishLabel, () => _ = import.PolishAsync())
                    .WithAccessibilityLabel(text("Import_PolishWithAI"))
                    .WithAccessibilityIdentifier("native.import.youtube.polish"));
                polish.IsEnabled = !state.IsPolishing && !state.IsSaving;
                children.Add(polish);
                var saveLabel = state.IsSaving
                    ? Views.HStack(Views.IndeterminateProgress(text("Import_Saving")),
                        Views.Text(text("Import_SaveAsResource")))
                    : Views.Label(text("Import_SaveAsResource"), "square.and.arrow.down");
                if (state.IsSaving)
                    saveLabel.Spacing = SourceUiLayout.ItemGap;
                var save = NativeForm.Action(style, Views.Button(saveLabel, () => _ = import.SaveAsResourceAsync())
                    .WithAccessibilityLabel(text("Import_SaveAsResource"))
                    .WithAccessibilityIdentifier("native.import.youtube.save"));
                SetAvailability(save, style, !state.IsSaving);
                children.Add(save);
            }
            else if (state.IsLoadingTranscript)
                children.Add(Views.IndeterminateProgress(text("Import_DownloadingTranscript"))
                    .WithAccessibilityIdentifier("native.import.youtube.downloading"));
            yield return Views.Section(text("Import_Transcript"), children.ToArray());
        }

        // Success result.
        if (state.SavedResourceId is { } saved)
            yield return Views.Section(string.Empty,
                Views.Label(text("Import_ResourceSaved"), "checkmark.circle.fill")
                    .WithForegroundColor(style.Appearance.Success.Light, style.Appearance.Success.Dark)
                    .WithAccessibilityIdentifier("native.import.youtube.saved"),
                NativeForm.Action(style, text("Import_ViewResource"), "book",
                    () => actions.ViewResource(saved), "native.import.youtube.saved.view"),
                NativeForm.Action(style, text("Import_ImportAnother"), "arrow.clockwise",
                    import.Reset, "native.import.youtube.saved.another"));

        // Error.
        if (state.Error is { } error)
            yield return Views.Section(string.Empty, NativeForm.Notice(style, error.Format(text),
                "native.import.youtube.error", error: true));
    }

    // ---- Import History (source MediaImport.razor:209-272) ----

    private static IEnumerable<SwiftView> History(YouTubeImportCoordinator import,
        YouTubeImportState state, SourceUiStyle style, Func<string, string> text,
        YouTubeImportActions actions)
    {
        if (state.IsLoadingImports)
        {
            yield return Views.Section(string.Empty, Spinner(style, text, "native.import.youtube.history.loading"));
            yield break;
        }
        if (state.ImportCount == 0)
        {
            yield return Views.Section(string.Empty, Empty(style, "clock.arrow.circlepath",
                text("Import_NoHistoryYet"), "native.import.youtube.history.empty"));
            yield break;
        }
        // Each import is a Form row (the Form draws only the rows on screen).
        var rows = new List<SwiftView>
        {
            Caption(style, Format(text("Import_ShowingCount"),
                    state.DisplayedImports.Length, state.ImportCount))
                .WithAccessibilityIdentifier("native.import.youtube.history.count")
        };
        rows.AddRange(state.DisplayedImports.Select(row => ImportRow(import, state, row, style, text, actions)));
        if (state.HasMoreImports)
            rows.Add(NativeForm.Action(style, text("Import_ShowMore"), "chevron.down",
                import.ShowMoreImports, "native.import.youtube.history.more"));
        yield return Views.Section(text("Import_History"), rows.ToArray());
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
        return line.WithAccessibilityIdentifier(id);
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

    /// <summary>A Form action row's availability (system rows dim by themselves).</summary>
    internal static void SetAvailability(SwiftView button, SourceUiStyle style, bool enabled)
    {
        if (button.IsEnabled != enabled)
            button.IsEnabled = enabled;
    }

    private static SwiftView Spinner(SourceUiStyle style, Func<string, string> text, string id) =>
        Views.IndeterminateProgress(text("Common_Loading"))
            .WithAccessibilityIdentifier(id);

    /// <summary>An empty tab: its symbol and message, centered in the row.</summary>
    private static SwiftView Empty(SourceUiStyle style, string symbol, string message, string id)
    {
        var color = style.Appearance.SecondaryText;
        var column = Views.VStack(
                Views.Image(symbol).WithFixedSymbolSize(40).WithForegroundColor(color.Light, color.Dark)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center),
                style.Text(Views.Text(message), SourceTextRole.Body1, SourceTextTone.Secondary)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center))
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithPadding(24);
        column.Spacing = 12;
        return column.WithAccessibilityIdentifier(id);
    }

    private static SwiftView Caption(SourceUiStyle style, string value) =>
        style.Text(Views.Text(value), SourceTextRole.Caption1, SourceTextTone.Secondary);

    private static string Format(string template, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, template, values);
}
