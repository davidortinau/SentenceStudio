using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native.Features.Import;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Import;

/// <summary>
/// The source Channel Detail page (src/SentenceStudio.UI/Pages/ChannelDetail.razor) in SwiftUI,
/// as one grouped Form (Captain 2026-11-13): the channel Section with Save Channel in the toolbar
/// (Back leaves without saving), and on an existing channel the video discovery Section. Typing the link updates the coordinator without drawing the page again; Save's
/// availability follows it in place.
/// </summary>
internal static class YouTubeChannelView
{
    internal const string ScrollId = "native.import.youtube.channel.scroll";
    internal const string ContentId = "native.import.youtube.channel.scroll.content";

    private static readonly SourceColorPair InfoBadge = new("#0DCAF0", "#0DCAF0");
    private static readonly SourceColorPair DarkBadgeText = new("#000000", "#000000");
    private static readonly SourceColorPair LightBadgeText = new("#FFFFFF", "#FFFFFF");
    private static readonly SourceColorPair WarningText = new("#664D03", "#FFDA6A");

    internal static SwiftView Page(YouTubeChannelCoordinator channel, SourceUiStyle style,
        Func<string, string> text)
    {
        var state = channel.State;
        var sections = new List<SwiftView>();
        SwiftView? save = null;
        if (state.Blocked)
            sections.Add(Views.Section(string.Empty, NativeForm.Notice(style, text("Import_ProfileNotFound"),
                "native.import.youtube.channel.blocked", error: true)));
        else if (state.IsLoading)
            sections.Add(Views.Section(string.Empty, Views.IndeterminateProgress(text("Common_Loading"))
                .WithAccessibilityIdentifier("native.import.youtube.channel.loading")));
        else
        {
            sections.Add(ChannelForm(channel, state, style, text, () => save));
            if (state.ShowsDiscovery)
                sections.Add(Discovery(channel, state, style, text));
        }
        // Captain 2026-11-13 (the native form pattern): one grouped Form; Save Channel is the
        // toolbar's action and Back leaves without saving (the source's Cancel).
        var page = NativeForm.Page(style, sections, ContentId, ScrollId);
        if (state.Blocked || state.IsLoading)
            return page;
        save = Views.Button(state.IsSaving ? text("Import_Saving") : text("ChannelDetail_SaveChannel"),
                () => _ = channel.SaveAsync())
            .WithAccessibilityIdentifier("native.import.youtube.channel.save");
        YouTubeImportView.SetAvailability(save, style, state.CanSave);
        return Views.Toolbar(page, save);
    }

    // ---- The form (source ChannelDetail.razor:23-92) ----

    private static SwiftView ChannelForm(YouTubeChannelCoordinator channel, YouTubeChannelState state,
        SourceUiStyle style, Func<string, string> text, Func<SwiftView?> save)
    {
        var url = NativeForm.Field($"{text("ChannelDetail_ChannelURL")} *", state.ChannelUrl,
            "native.import.youtube.channel.url", value =>
            {
                channel.SetChannelUrl(value);
                if (save() is { } button)
                    YouTubeImportView.SetAvailability(button, style, channel.CanSave);
            }, text("ChannelDetail_URLPlaceholder"));

        // Source select: "-- Select Language --" (no language) and SentenceStudio.Common.Constants.Languages.
        // The bridge's picker values must be nonempty, so "no language" has a placeholder value; a
        // stored language outside the list is offered too, so it stays selected.
        const string NoLanguage = "native.no-language";
        var languages = new List<SwiftPickerOption> { new(NoLanguage, text("ChannelDetail_SelectLanguage")) };
        var names = SentenceStudio.Common.Constants.Languages.ToList();
        if (!string.IsNullOrWhiteSpace(state.Language) && !names.Contains(state.Language))
            names.Insert(0, state.Language);
        languages.AddRange(names.Select(language => new SwiftPickerOption(language, language)));
        var language = Choice(style, languages,
            string.IsNullOrWhiteSpace(state.Language) ? NoLanguage : state.Language,
            value => channel.SetLanguage(value == NoLanguage ? string.Empty : value),
            $"{text("ChannelDetail_Language")} *", "native.import.youtube.channel.language");
        var intervals = new SwiftPickerOption[]
        {
            new("6", text("ChannelDetail_Every6Hours")),
            new("12", text("ChannelDetail_Every12Hours")),
            new("24", text("ChannelDetail_Every24Hours")),
            new("48", text("ChannelDetail_Every48Hours")),
        };
        var interval = Choice(style, intervals, state.CheckIntervalHours.ToString(CultureInfo.InvariantCulture),
            value => channel.SetCheckIntervalHours(int.Parse(value, CultureInfo.InvariantCulture)),
            text("ChannelDetail_CheckInterval"), "native.import.youtube.channel.interval");
        var monitor = NativeForm.Switch(style, text("ChannelDetail_MonitorChannel"), state.IsActive,
            channel.SetIsActive, "native.import.youtube.channel.active");

        var rows = new List<SwiftView>
        {
            url,
            Caption(style, text("ChannelDetail_EnterFullURL")),
            language,
            interval,
            monitor,
        };
        if (state.Error is { } error)
            rows.Add(NativeForm.Notice(style, error.Format(text), "native.import.youtube.channel.error",
                error: true));
        if (state.IsResolving)
            rows.Add(Views.IndeterminateProgress(text("ChannelDetail_ResolvingChannel"))
                .WithAccessibilityIdentifier("native.import.youtube.channel.resolving"));
        if (state.IsSaving)
            rows.Add(Views.IndeterminateProgress(text("Import_Saving"))
                .WithAccessibilityIdentifier("native.import.youtube.channel.saving"));
        return Views.Section(string.Empty, rows.ToArray());
    }

    // ---- Video discovery (source ChannelDetail.razor:95-211) ----

    private static SwiftView Discovery(YouTubeChannelCoordinator channel, YouTubeChannelState state,
        SourceUiStyle style, Func<string, string> text)
    {
        var rows = new List<SwiftView>();
        var checkLabel = state.IsCheckingVideos
            ? Views.HStack(Views.IndeterminateProgress(text("ChannelDetail_Checking")),
                Views.Text(text("ChannelDetail_Checking")))
            : Views.Label(text("ChannelDetail_CheckForNewVideos"), "arrow.triangle.2.circlepath");
        if (state.IsCheckingVideos)
            checkLabel.Spacing = SourceUiLayout.ItemGap;
        var check = NativeForm.Action(style, Views.Button(checkLabel, () => _ = channel.CheckForNewVideosAsync())
            .WithAccessibilityLabel(text("ChannelDetail_CheckForNewVideos"))
            .WithAccessibilityIdentifier("native.import.youtube.channel.check"));
        check.IsEnabled = !state.IsCheckingVideos && state.HasYouTubeChannelId;
        rows.Add(check);
        if (!state.Videos.IsDefaultOrEmpty)
        {
            var title = Format(text("ChannelDetail_ImportSelected"), state.SelectedVideoIds.Count);
            var importLabel = state.IsImportingVideos
                ? Views.HStack(Views.IndeterminateProgress(text("ChannelDetail_Importing")),
                    Views.Text(text("ChannelDetail_Importing")))
                : Views.Label(title, "arrow.down.circle");
            if (state.IsImportingVideos)
                importLabel.Spacing = SourceUiLayout.ItemGap;
            var import = NativeForm.Action(style, Views.Button(importLabel, () => _ = channel.ImportSelectedAsync())
                .WithAccessibilityLabel(title)
                .WithAccessibilityIdentifier("native.import.youtube.channel.import"));
            YouTubeImportView.SetAvailability(import, style,
                !state.IsImportingVideos && state.SelectedVideoIds.Count > 0);
            rows.Add(import);
        }

        if (!state.HasYouTubeChannelId)
        {
            var warning = new List<SwiftView>
            {
                Views.Label(text("ChannelDetail_ChannelIDNotResolved"), "exclamationmark.triangle"),
            };
            if (!string.IsNullOrEmpty(state.ResolutionError))
                warning.Add(style.Text(Views.Text(Format(text("YouTubeChannel_ErrorLabel"), state.ResolutionError)),
                    SourceTextRole.Caption1, SourceTextTone.Secondary));
            var warningColumn = Views.VStack(warning.ToArray()).WithFlexibleWidth();
            warningColumn.Spacing = 4;
            rows.Add(warningColumn.WithForegroundColor(WarningText.Light, WarningText.Dark)
                .WithAccessibilityIdentifier("native.import.youtube.channel.unresolved"));
            var retryLabel = state.IsRetryingResolve
                ? Views.HStack(Views.IndeterminateProgress(text("ChannelDetail_Resolving")),
                    Views.Text(text("ChannelDetail_Resolving")))
                : Views.Label(text("ChannelDetail_RetryResolve"), "arrow.triangle.2.circlepath");
            if (state.IsRetryingResolve)
                retryLabel.Spacing = SourceUiLayout.ItemGap;
            var retry = NativeForm.Action(style, Views.Button(retryLabel, () => _ = channel.RetryResolveAsync())
                .WithAccessibilityLabel(text("ChannelDetail_RetryResolve"))
                .WithAccessibilityIdentifier("native.import.youtube.channel.retry-resolve"));
            retry.IsEnabled = !state.IsRetryingResolve;
            rows.Add(retry);
        }
        else if (!state.Videos.IsDefaultOrEmpty)
        {
            var toggleAll = NativeForm.Bordered(style, text(state.AllNewSelected
                    ? "ChannelDetail_SelectNone" : "ChannelDetail_SelectAllNew"),
                channel.ToggleSelectAll, "native.import.youtube.channel.select-all")
                .WithFixedSize();
            var countRow = Views.HStack(Caption(style, Format(text("ChannelDetail_VideosFound"),
                    state.Videos.Length)).WithFlexibleWidth(), toggleAll);
            countRow.Spacing = SourceUiLayout.ItemGap;
            rows.Add(countRow);
            // Each video is a Form row (the Form draws only the rows on screen).
            rows.AddRange(state.Videos.Select((video, index) =>
                VideoRow(channel, state, video, index, style, text)));
        }
        else if (state.HasCheckedVideos && !state.IsCheckingVideos)
            rows.Add(style.Text(Views.Text(text("ChannelDetail_NoVideosFound")), SourceTextRole.Body2,
                    SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.import.youtube.channel.no-videos"));

        if (state.DiscoveryError is { } error)
            rows.Add(NativeForm.Notice(style, error.Format(text), "native.import.youtube.channel.discovery-error",
                error: true));
        return Views.Section(text("ChannelDetail_DiscoverVideos"), rows.ToArray());
    }

    private static SwiftView VideoRow(YouTubeChannelCoordinator channel, YouTubeChannelState state,
        YouTubeDiscoveredVideo video, int index, SourceUiStyle style, Func<string, string> text)
    {
        var id = $"native.import.youtube.channel.video.{index}";
        var select = Views.Toggle(video.Title, state.SelectedVideoIds.Contains(video.VideoId),
                _ => channel.ToggleVideo(video.VideoId))
            .WithLabelsHidden()
            .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithAccessibilityIdentifier($"{id}.select");
        select.IsEnabled = video.CanSelect && !state.IsImportingVideos;
        var cells = new List<SwiftView>
        {
            select,
            style.Text(Views.Text(video.Title), SourceTextRole.Body2).WithLineLimit(1).WithFlexibleWidth(),
        };
        if (video.IsCompleted)
            cells.Add(Badge(style, "checkmark.circle", text("ChannelDetail_Imported"), style.Appearance.Success,
                LightBadgeText, $"{id}.imported"));
        else if (video.IsFailed)
        {
            cells.Add(Badge(style, "xmark.circle", text("Import_Failed"), style.Appearance.Danger,
                LightBadgeText, $"{id}.failed"));
            var label = Views.HStack();
            if (state.RetryingVideoId == video.VideoId)
                label.Add(Views.IndeterminateProgress(text("Common_Loading")));
            label.Add(Views.Image("arrow.clockwise"));
            label.Add(Views.Text(text("Import_Retry")));
            label.Spacing = 4;
            var retry = YouTubeImportView.OutlineWarningButton(label,
                    () => _ = channel.RetryFailedVideoAsync(video.VideoId), style)
                .WithAccessibilityLabel(text("Import_Retry"))
                .WithAccessibilityIdentifier($"{id}.retry");
            retry.IsEnabled = !state.IsRetryingImport;
            cells.Add(retry);
        }
        else if (video.IsInProgress)
            cells.Add(Badge(style, "hourglass", text("ChannelDetail_Importing"), InfoBadge, DarkBadgeText,
                $"{id}.importing"));
        var row = Views.HStack(cells.ToArray()).WithFlexibleWidth();
        row.Spacing = SourceUiLayout.ItemGap + 4;
        var padded = row;
        // Source: an imported video's row is dimmed (opacity-50).
        if (video.IsCompleted)
            padded.WithOpacity(0.5);
        return padded.WithAccessibilityIdentifier(id);
    }

    private static SwiftView Badge(SourceUiStyle style, string symbol, string label, SourceColorPair fill,
        SourceColorPair foreground, string id)
    {
        var content = Views.HStack(Views.Image(symbol), style.Text(Views.Text(label), SourceTextRole.BadgeSmall));
        content.Spacing = 3;
        // Source .badge is white-space: nowrap: the badge keeps its width and the title truncates.
        return content.WithPadding(4, 8, 4, 8)
            .WithRoundedSurface(fill.Light, fill.Dark, 6)
            .WithForegroundColor(foreground.Light, foreground.Dark)
            .WithFixedSize()
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier(id);
    }

    /// <summary>A choice row (NativeForm.Choice): the label beside the current value.</summary>
    private static SwiftView Choice(SourceUiStyle style, IReadOnlyList<SwiftPickerOption> options,
        string selected, Action<string> changed, string label, string id) =>
        NativeForm.Choice(style, label, options, selected, id, value =>
        {
            changed(value);
            return true;
        });

    private static SwiftView Caption(SourceUiStyle style, string value) =>
        style.Text(Views.Text(value), SourceTextRole.Caption1, SourceTextTone.Secondary);

    private static string Format(string template, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, template, values);
}
