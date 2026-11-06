using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native.Features.Import;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Import;

/// <summary>
/// The source Channel Detail page (src/SentenceStudio.UI/Pages/ChannelDetail.razor) in SwiftUI:
/// the channel form with Cancel and Save Channel, and on an existing channel the video discovery
/// card. Typing the link updates the coordinator without drawing the page again; Save's
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
        Func<string, string> text, Action cancel)
    {
        var state = channel.State;
        var items = new List<SwiftView>();
        if (state.Blocked)
            items.Add(style.Alert(Views.Text(text("Import_ProfileNotFound")), success: false)
                .WithAccessibilityIdentifier("native.import.youtube.channel.blocked"));
        else if (state.IsLoading)
            items.Add(Views.IndeterminateProgress(text("Common_Loading"))
                .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                .WithPadding(48)
                .WithAccessibilityIdentifier("native.import.youtube.channel.loading"));
        else
        {
            items.Add(Form(channel, state, style, text, cancel));
            if (state.ShowsDiscovery)
                items.Add(Discovery(channel, state, style, text));
        }
        var content = Views.VStack(items.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(ContentId);
        content.Spacing = SourceUiLayout.FormGap;
        return style.Page(Views.Scroll(content).WithAccessibilityIdentifier(ScrollId)
            .WithScrollDismissesKeyboard(SwiftScrollDismissesKeyboard.Interactively));
    }

    // ---- The form (source ChannelDetail.razor:23-92) ----

    private static SwiftView Form(YouTubeChannelCoordinator channel, YouTubeChannelState state,
        SourceUiStyle style, Func<string, string> text, Action cancel)
    {
        SwiftView? save = null;
        var url = style.TextInput(Views.TextField(text("ChannelDetail_URLPlaceholder"), state.ChannelUrl,
                    value =>
                    {
                        channel.SetChannelUrl(value);
                        if (save is not null)
                            YouTubeImportView.SetAvailability(save, style, channel.CanSave);
                    }))
            .WithFlexibleWidth()
            .WithAccessibilityLabel(text("ChannelDetail_ChannelURL"))
            .WithAccessibilityIdentifier("native.import.youtube.channel.url");
        var urlField = Field(style, $"{text("ChannelDetail_ChannelURL")} *", url,
            Caption(style, text("ChannelDetail_EnterFullURL")));

        // Source select: "-- Select Language --" (no language) and SentenceStudio.Common.Constants.Languages.
        // The bridge's picker values must be nonempty, so "no language" has a placeholder value; a
        // stored language outside the list is offered too, so it stays selected.
        const string NoLanguage = "native.no-language";
        var languages = new List<SwiftPickerOption> { new(NoLanguage, text("ChannelDetail_SelectLanguage")) };
        var names = SentenceStudio.Common.Constants.Languages.ToList();
        if (!string.IsNullOrWhiteSpace(state.Language) && !names.Contains(state.Language))
            names.Insert(0, state.Language);
        languages.AddRange(names.Select(language => new SwiftPickerOption(language, language)));
        var language = Menu(style, languages,
            string.IsNullOrWhiteSpace(state.Language) ? NoLanguage : state.Language,
            value => channel.SetLanguage(value == NoLanguage ? string.Empty : value),
            text("ChannelDetail_Language"), "native.import.youtube.channel.language");
        var intervals = new SwiftPickerOption[]
        {
            new("6", text("ChannelDetail_Every6Hours")),
            new("12", text("ChannelDetail_Every12Hours")),
            new("24", text("ChannelDetail_Every24Hours")),
            new("48", text("ChannelDetail_Every48Hours")),
        };
        var interval = Menu(style, intervals, state.CheckIntervalHours.ToString(CultureInfo.InvariantCulture),
            value => channel.SetCheckIntervalHours(int.Parse(value, CultureInfo.InvariantCulture)),
            text("ChannelDetail_CheckInterval"), "native.import.youtube.channel.interval");
        var monitor = style.Text(Views.Toggle(text("ChannelDetail_MonitorChannel"), state.IsActive,
                    channel.SetIsActive)
                .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark), SourceTextRole.Body2)
            .WithAccessibilityIdentifier("native.import.youtube.channel.active");

        var children = new List<SwiftView>
        {
            urlField,
            Field(style, $"{text("ChannelDetail_Language")} *", language),
            Field(style, text("ChannelDetail_CheckInterval"), interval),
            monitor,
        };
        if (state.Error is { } error)
            children.Add(style.Alert(IconLabel("exclamationmark.circle", error.Format(text)), success: false)
                .WithAccessibilityIdentifier("native.import.youtube.channel.error"));
        if (state.IsResolving)
        {
            var resolving = Views.HStack(Views.IndeterminateProgress(text("ChannelDetail_ResolvingChannel")),
                Views.Text(text("ChannelDetail_ResolvingChannel")));
            resolving.Spacing = SourceUiLayout.ItemGap;
            children.Add(style.InfoAlert(resolving)
                .WithAccessibilityIdentifier("native.import.youtube.channel.resolving"));
        }

        var cancelButton = style.SecondaryButton(text("Common_Cancel"), cancel)
            .WithAccessibilityIdentifier("native.import.youtube.channel.cancel");
        SourceUiStyle.SetCustomButtonAvailability(cancelButton, style.Appearance,
            SourceButtonRole.FilledSecondary, !state.IsSaving);
        var saveLabel = Views.HStack();
        if (state.IsSaving)
            saveLabel.Add(Views.IndeterminateProgress(text("Import_Saving")).WithTintColor("#FFFFFF", "#FFFFFF"));
        saveLabel.Add(Views.Image("square.and.arrow.down"));
        saveLabel.Add(Views.Text(text("ChannelDetail_SaveChannel")));
        saveLabel.Spacing = 4;
        save = style.LabelButton(saveLabel, () => _ = channel.SaveAsync(), SourceButtonRole.Primary,
                fullWidth: true)
            .WithAccessibilityLabel(text("ChannelDetail_SaveChannel"))
            .WithAccessibilityIdentifier("native.import.youtube.channel.save");
        YouTubeImportView.SetAvailability(save, style, state.CanSave);
        var buttons = Views.HStack(cancelButton, save).WithFlexibleWidth();
        buttons.Spacing = SourceUiLayout.ItemGap;
        children.Add(buttons);

        var column = Views.VStack(children.ToArray()).WithFlexibleWidth();
        column.Spacing = SourceUiLayout.FormGap;
        return style.Card(column.WithPadding(SourceUiLayout.CardInset))
            .WithAccessibilityIdentifier("native.import.youtube.channel.form");
    }

    // ---- Video discovery (source ChannelDetail.razor:95-211) ----

    private static SwiftView Discovery(YouTubeChannelCoordinator channel, YouTubeChannelState state,
        SourceUiStyle style, Func<string, string> text)
    {
        var children = new List<SwiftView>
        {
            style.Text(IconLabel("magnifyingglass", text("ChannelDetail_DiscoverVideos")),
                SourceTextRole.Title3, sourceHeading: true),
        };

        var checkLabel = state.IsCheckingVideos
            ? Views.HStack(Views.IndeterminateProgress(text("ChannelDetail_Checking")),
                Views.Text(text("ChannelDetail_Checking")))
            : Views.HStack(Views.Image("arrow.triangle.2.circlepath"),
                Views.Text(text("ChannelDetail_CheckForNewVideos")));
        checkLabel.Spacing = 4;
        var check = style.LabelButton(checkLabel, () => _ = channel.CheckForNewVideosAsync(),
                SourceButtonRole.FilledSecondary)
            .WithAccessibilityLabel(text("ChannelDetail_CheckForNewVideos"))
            .WithAccessibilityIdentifier("native.import.youtube.channel.check");
        SourceUiStyle.SetCustomButtonAvailability(check, style.Appearance, SourceButtonRole.FilledSecondary,
            !state.IsCheckingVideos && state.HasYouTubeChannelId);
        var actions = new List<SwiftView> { check };
        if (!state.Videos.IsDefaultOrEmpty)
        {
            var importLabel = state.IsImportingVideos
                ? Views.HStack(Views.IndeterminateProgress(text("ChannelDetail_Importing"))
                        .WithTintColor("#FFFFFF", "#FFFFFF"),
                    Views.Text(text("ChannelDetail_Importing")))
                : Views.HStack(Views.Image("arrow.down.circle"),
                    Views.Text(Format(text("ChannelDetail_ImportSelected"), state.SelectedVideoIds.Count)));
            importLabel.Spacing = 4;
            var import = style.LabelButton(importLabel, () => _ = channel.ImportSelectedAsync(),
                    SourceButtonRole.Primary)
                .WithAccessibilityLabel(Format(text("ChannelDetail_ImportSelected"), state.SelectedVideoIds.Count))
                .WithAccessibilityIdentifier("native.import.youtube.channel.import");
            YouTubeImportView.SetAvailability(import, style,
                !state.IsImportingVideos && state.SelectedVideoIds.Count > 0);
            actions.Add(import);
        }
        var actionRow = Views.HStack(actions.ToArray()).WithAdaptiveStack(420);
        actionRow.Spacing = SourceUiLayout.ItemGap;
        children.Add(actionRow);

        if (!state.HasYouTubeChannelId)
        {
            var warning = new List<SwiftView>
            {
                IconLabel("exclamationmark.triangle", text("ChannelDetail_ChannelIDNotResolved")),
            };
            if (!string.IsNullOrEmpty(state.ResolutionError))
                warning.Add(style.Text(Views.Text(Format(text("YouTubeChannel_ErrorLabel"), state.ResolutionError)),
                    SourceTextRole.Caption1, SourceTextTone.Secondary));
            var warningColumn = Views.VStack(warning.ToArray()).WithFlexibleWidth();
            warningColumn.Spacing = 4;
            children.Add(warningColumn.WithForegroundColor(WarningText.Light, WarningText.Dark)
                .WithPadding(16)
                .WithFlexibleWidth()
                .WithRoundedSurface("#FFF3CD", "#332701", style.Appearance.BootstrapRadius,
                    "#FFE69C", "#997404", 1)
                .WithAccessibilityIdentifier("native.import.youtube.channel.unresolved"));
            var retryLabel = state.IsRetryingResolve
                ? Views.HStack(Views.IndeterminateProgress(text("ChannelDetail_Resolving")),
                    Views.Text(text("ChannelDetail_Resolving")))
                : Views.HStack(Views.Image("arrow.triangle.2.circlepath"),
                    Views.Text(text("ChannelDetail_RetryResolve")));
            retryLabel.Spacing = 4;
            var retry = style.LabelButton(retryLabel, () => _ = channel.RetryResolveAsync(),
                    SourceButtonRole.FilledSecondary)
                .WithAccessibilityLabel(text("ChannelDetail_RetryResolve"))
                .WithAccessibilityIdentifier("native.import.youtube.channel.retry-resolve");
            SourceUiStyle.SetCustomButtonAvailability(retry, style.Appearance, SourceButtonRole.FilledSecondary,
                !state.IsRetryingResolve);
            children.Add(retry);
        }
        else if (!state.Videos.IsDefaultOrEmpty)
        {
            var toggleAll = style.LabelButton(Views.Text(text(state.AllNewSelected
                        ? "ChannelDetail_SelectNone" : "ChannelDetail_SelectAllNew")),
                    channel.ToggleSelectAll, SourceButtonRole.OutlineSecondary, horizontalPadding: 8)
                .WithFixedSize()
                .WithAccessibilityIdentifier("native.import.youtube.channel.select-all");
            var countRow = Views.HStack(Caption(style, Format(text("ChannelDetail_VideosFound"),
                    state.Videos.Length)), toggleAll);
            countRow.Spacing = SourceUiLayout.ItemGap;
            children.Add(countRow);
            // A lazy grid draws only the rows on screen (source: a virtualized list).
            var rows = Views.LazyVGrid(1, state.Videos.Select((video, index) =>
                    VideoRow(channel, state, video, index, style, text)).ToArray())
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier("native.import.youtube.channel.videos");
            rows.Spacing = 0;
            children.Add(style.BorderedCard(rows, radius: 8));
        }
        else if (state.HasCheckedVideos && !state.IsCheckingVideos)
            children.Add(style.Text(Views.Text(text("ChannelDetail_NoVideosFound")), SourceTextRole.Body2,
                    SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.import.youtube.channel.no-videos"));

        if (state.DiscoveryError is { } error)
            children.Add(style.Alert(IconLabel("exclamationmark.circle", error.Format(text)), success: false)
                .WithAccessibilityIdentifier("native.import.youtube.channel.discovery-error"));

        var column = Views.VStack(children.ToArray()).WithFlexibleWidth();
        column.Spacing = SourceUiLayout.ItemGap + 4;
        return style.Card(column.WithPadding(SourceUiLayout.CardInset))
            .WithAccessibilityIdentifier("native.import.youtube.channel.discovery");
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
        var padded = row.WithPadding(8, 16, 8, 16).WithFlexibleWidth();
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

    private static SwiftView Menu(SourceUiStyle style, IReadOnlyList<SwiftPickerOption> options, string selected,
        Action<string> changed, string label, string id) =>
        style.Input(Views.Picker(string.Empty, options, selected, changed)
                .WithPickerStyle(SwiftPickerStyle.Menu)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Leading))
            .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier(id);

    private static SwiftView Field(SourceUiStyle style, string label, SwiftView input, SwiftView? help = null)
    {
        var children = new List<SwiftView>
        {
            style.Text(Views.Text(label), SourceTextRole.Body2, SourceTextTone.Secondary), input
        };
        if (help is not null)
            children.Add(help);
        var field = Views.VStack(children.ToArray()).WithFlexibleWidth();
        field.Spacing = SourceUiLayout.ItemGap;
        return field;
    }

    private static SwiftView IconLabel(string symbol, string title)
    {
        var label = Views.HStack(Views.Image(symbol), Views.Text(title));
        label.Spacing = 4;
        return label;
    }

    private static SwiftView Caption(SourceUiStyle style, string value) =>
        style.Text(Views.Text(value), SourceTextRole.Caption1, SourceTextTone.Secondary);

    private static string Format(string template, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, template, values);
}
