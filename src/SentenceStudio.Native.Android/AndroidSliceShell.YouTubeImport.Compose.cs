using System.Globalization;
using AndroidX.Compose;
using SentenceStudio.Native.Features.Import;
using SentenceStudio.Shared.Models;
using Color = AndroidX.Compose.Color;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose YouTube import (source MediaImport.razor and ChannelDetail.razor; iOS
/// YouTubeImportView and YouTubeChannelView): the header menu (Add Channel, Refresh), the three
/// tabs, My Channels, Single Video, Import History, and the channel page with its video
/// discovery. Each page is one lazy list; errors stay pinned above it, success is the shell's
/// snackbar.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private const string YouTubeMenu = "native.import.youtube.menu";

    private ComposableNode RenderYouTubeImport(AndroidVisualPalette palette)
    {
        if (IsYouTubeChannelOpen && _youTubeChannel is { } channel)
            return RenderYouTubeChannel(channel, palette);

        var import = _youTubeImport;
        var header = (TopAppBar)ScreenHeader(Localize("Import_YouTubeImport"), palette, back: true,
            backAutomationId: "native.import.youtube.back");
        var page = new Column { Modifier.FillMaxSize().TestTag("native.import.youtube.screen") };
        if (import is not { IsOpen: true })
        {
            page.Add(header);
            page.Add(new Column(Arrangement.SpacedBy(12), Alignment.Horizontal.CenterHorizontally)
            {
                Modifier.FillMaxWidth().Padding(AndroidVisualTheme.PagePadding),
                import is null
                    ? StatusAlert(Localize("Import_ProfileNotFound"), palette, error: true,
                        automationId: "native.import.youtube.blocked")
                    : new CircularProgressIndicator
                    {
                        Modifier = Modifier.Size(28).TestTag("native.import.youtube.loading"),
                    },
            });
            return page;
        }

        var state = import.State;
        header.Actions = YouTubeHeaderMenu(state, palette);
        page.Add(header);
        if (MessageBanners(state.Messages, palette, index => $"native.import.youtube.message.{index}",
                () => _youTubeImport?.DismissMessages(), "native.import.youtube.message.dismiss",
                Localize("Common_Close")) is { } banners)
            page.Add(new Box
            {
                Modifier.FillMaxWidth().Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 8),
                banners,
            });

        var items = new List<ImportItem> { new("tabs", () => YouTubeTabs(state)) };
        if (state.Blocked)
            items.Add(new("blocked", () => StatusAlert(Localize("Import_ProfileNotFound"), palette, error: true,
                automationId: "native.import.youtube.blocked")));
        else
            switch (state.Tab)
            {
                case YouTubeImportTab.Channels:
                    YouTubeChannels(items, state, palette);
                    break;
                case YouTubeImportTab.SingleVideo:
                    YouTubeSingleVideo(items, import, state, palette);
                    break;
                default:
                    YouTubeHistory(items, state, palette);
                    break;
            }
        page.Add(ImportLazyList(items, "native.import.youtube.scroll"));
        return page;
    }

    private static ComposableNode ImportLazyList(List<ImportItem> items, string tag) =>
        new LazyColumn<ImportItem>(items, item => item.Content())
        {
            Key = item => item.Key,
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .Padding(horizontal: AndroidVisualTheme.PagePadding)
                .TestTag(tag),
            VerticalArrangement = Arrangement.SpacedBy(12),
        };

    /// <summary>Source PageHeader SecondaryActions: Add Channel and Refresh in the overflow menu.</summary>
    private ComposableNode YouTubeHeaderMenu(YouTubeImportState state, AndroidVisualPalette palette)
    {
        var label = Localize("More");
        var overflow = new IconButton(() => ToggleImportMenu(YouTubeMenu), enabled: !state.Blocked)
        {
            Modifier = Modifier.TestTag(YouTubeMenu).Semantics(semantics => semantics.ContentDescription(label)),
        };
        overflow.Add(new Icon(Icons.Default.MoreVert, label) { Modifier = Modifier.Size(24), Tint = palette.Text });
        var menu = PopupMenu(expanded: !state.Blocked && _importMenu == YouTubeMenu, onDismissRequest: CloseImportMenu);
        menu.Add(new DropdownMenuItem(text: new Text(Localize("Import_AddChannel")), onClick: () =>
        {
            CloseImportMenu();
            Post(() => OpenYouTubeChannel(null));
        })
        {
            Modifier = Modifier.TestTag("native.import.youtube.menu.add-channel"),
            LeadingIcon = new Icon(Icons.Default.Add, null) { Modifier = Modifier.Size(18), Tint = palette.Text },
        });
        menu.Add(new DropdownMenuItem(text: new Text(Localize("Import_Refresh")), onClick: () =>
        {
            CloseImportMenu();
            YouTubeCommand(coordinator => coordinator.RefreshImportsAsync());
        })
        {
            Modifier = Modifier.TestTag("native.import.youtube.menu.refresh"),
            LeadingIcon = new Icon(Icons.Default.Refresh, null) { Modifier = Modifier.Size(18), Tint = palette.Text },
        });
        return new Box { overflow, menu };
    }

    /// <summary>Source btn-group w-100: three equal buttons, the selected one filled.</summary>
    private ComposableNode YouTubeTabs(YouTubeImportState state)
    {
        ComposableNode Tab(YouTubeImportTab tab, string key, string id)
        {
            var selected = state.Tab == tab;
            var label = Localize(key);
            var modifier = Modifier.Weight(1f).HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag($"native.import.youtube.tab.{id}")
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(label);
                    semantics.Role(SemanticsRole.Tab);
                    semantics.Selected(selected);
                });
            void Select() => YouTubeEdit(coordinator => coordinator.SwitchTab(tab));
            if (selected)
            {
                var filled = new AndroidX.Compose.Button(Select, enabled: true)
                {
                    Modifier = modifier,
                    Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
                };
                filled.Add(new Text(label) { Align = TextAlign.Center });
                return filled;
            }
            var outlined = new OutlinedButton(Select, enabled: true)
            {
                Modifier = modifier,
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            outlined.Add(new Text(label) { Align = TextAlign.Center });
            return outlined;
        }
        return new Row(Arrangement.SpacedBy(6), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().Padding(top: 8).TestTag("native.import.youtube.tabs"),
            Tab(YouTubeImportTab.Channels, "Import_MyChannels", "channels"),
            Tab(YouTubeImportTab.SingleVideo, "Import_SingleVideo", "single"),
            Tab(YouTubeImportTab.History, "Import_History", "history"),
        };
    }

    // ------------------------------------------------------------ My Channels

    private void YouTubeChannels(List<ImportItem> items, YouTubeImportState state, AndroidVisualPalette palette)
    {
        if (state.IsLoadingChannels)
        {
            items.Add(new("channels.loading", () => YouTubeSpinner("native.import.youtube.channels.loading")));
            return;
        }
        if (state.Channels.IsDefaultOrEmpty)
        {
            items.Add(new("channels.empty", () => YouTubeEmpty(Localize("Import_NoChannelsYet"),
                Action(Localize("Import_AddFirstChannel"), "native.import.youtube.channels.add-first",
                    () => OpenYouTubeChannel(null), true),
                "native.import.youtube.channels.empty", palette)));
            return;
        }
        foreach (var channel in state.Channels)
            items.Add(new($"channel.{channel.Id}", () => YouTubeChannelCard(channel, palette)));
    }

    private ComposableNode YouTubeChannelCard(YouTubeChannelRow channel, AndroidVisualPalette palette)
    {
        var id = $"native.import.youtube.channel.{channel.Id}";
        var title = channel.ChannelName ?? channel.ChannelHandle ?? Localize("YouTubeImport_UnnamedChannel");
        var details = new List<string>();
        if (!string.IsNullOrEmpty(channel.ChannelHandle))
            details.Add(channel.ChannelHandle);
        if (!string.IsNullOrEmpty(channel.Language))
            details.Add(channel.Language);
        var checkedAt = channel.LastCheckedAt is { } last
            ? last.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : Localize("Import_Never");
        var lines = new Column(Arrangement.SpacedBy(2))
        {
            Modifier.Weight(1f),
            new Text(title)
            {
                Color = palette.Text,
                FontSize = new Sp(17),
                FontWeight = FontWeight.SemiBold,
                MaxLines = 1,
                Overflow = TextOverflow.Ellipsis,
            },
        };
        if (details.Count > 0)
            lines.Add(ImportCaption(string.Join(" • ", details), palette));
        lines.Add(ImportCaption($"{Localize("Import_LastChecked")} {checkedAt}", palette));
        var open = new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
        {
            Modifier.Weight(1f).Clickable(() => OpenYouTubeChannel(channel.Id))
                .TestTag($"{id}.open")
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(title);
                    semantics.Role(SemanticsRole.Button);
                }),
            new Icon(Icons.Default.PlayArrow, null) { Modifier = Modifier.Size(28), Tint = palette.Danger },
            lines,
        };
        // Source: a bare switch whose title says what it does next (its accessible name here).
        var monitorLabel = Localize(channel.IsActive ? "Import_PauseMonitoring" : "Import_ResumeMonitoring");
        var monitor = new AndroidX.Compose.Switch(channel.IsActive,
            _ => YouTubeCommand(coordinator => coordinator.ToggleChannelActiveAsync(channel.Id)),
            enabled: !channel.IsSaving)
        {
            Modifier = Modifier.TestTag($"{id}.active")
                .Semantics(semantics => semantics.ContentDescription(monitorLabel)),
        };
        return Card(palette, id, new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            open,
            monitor,
        });
    }

    // ------------------------------------------------------------ Single Video

    private void YouTubeSingleVideo(List<ImportItem> items, YouTubeImportCoordinator import,
        YouTubeImportState state, AndroidVisualPalette palette)
    {
        items.Add(new("link", () =>
        {
            var urlLabel = Localize("Import_YouTubeURL");
            var keyboard = KeyboardOptionsCompanion.Default;
            var url = new OutlinedTextField(state.VideoUrl,
                value => YouTubeEdit(coordinator => coordinator.SetVideoUrl(value)), singleLine: true)
            {
                Modifier = Modifier.FillMaxWidth().TestTag("native.import.youtube.url")
                    .Semantics(semantics => semantics.ContentDescription(urlLabel)),
                Label = new Text(urlLabel),
                Placeholder = new Text("https://www.youtube.com/watch?v=..."),
                KeyboardOptions = keyboard.Copy(
                    keyboard.Capitalization, Java.Lang.Boolean.False,
                    AndroidX.Compose.KeyboardType.Uri,
                    AndroidX.Compose.ImeAction.Go,
                    keyboard.PlatformImeOptions, keyboard.ShowKeyboardOnFocus,
                    keyboard.HintLocales),
                KeyboardActions = KeyboardActionsHelper.Create(onGo: () =>
                    YouTubeCommand(coordinator => coordinator.FetchTranscriptsAsync())),
            };
            return Card(palette, "native.import.youtube.link",
                url,
                YouTubeBusyAction(Localize("Import_Fetch"), "native.import.youtube.fetch",
                    () => YouTubeCommand(coordinator => coordinator.FetchTranscriptsAsync()),
                    state.CanFetch, state.IsLoadingTranscripts, primary: true));
        }));

        if (state.HasTracks)
            items.Add(new("transcript", () =>
            {
                var children = new List<ComposableNode>();
                if (state.Tracks.Length > 1)
                {
                    var options = state.Tracks.Select(track => (track.Index.ToString(CultureInfo.InvariantCulture),
                        track.IsAutoGenerated
                            ? $"{track.LanguageName} {Localize("YouTubeImport_AutoShort")}"
                            : track.LanguageName)).ToArray();
                    children.Add(ImportChoice(Localize("Import_TranscriptLanguage"), options,
                        Math.Max(state.SelectedTrack, 0).ToString(CultureInfo.InvariantCulture),
                        "native.import.youtube.track",
                        value => YouTubeCommand(coordinator =>
                            coordinator.SelectTrackAsync(int.Parse(value, CultureInfo.InvariantCulture))),
                        !state.IsLoadingTranscript && !state.IsSaving, palette));
                }
                else
                {
                    var only = state.Tracks[0];
                    var line = $"{Localize("YouTubeImport_LanguageLabel")} {only.LanguageName}";
                    if (only.IsAutoGenerated)
                        line += $" {Localize("YouTubeImport_AutoGenerated")}";
                    children.Add(Body(line, palette, secondary: true,
                        modifier: Modifier.TestTag("native.import.youtube.track.single")));
                }
                if (state.HasTranscript)
                {
                    var transcriptLabel = Localize("Import_Transcript");
                    children.Add(new OutlinedTextField(state.TranscriptText,
                        value => YouTubeEdit(coordinator => coordinator.SetTranscriptText(value)),
                        enabled: !state.IsSaving, singleLine: false, minLines: 12)
                    {
                        Modifier = Modifier.FillMaxWidth().TestTag("native.import.youtube.transcript")
                            .Semantics(semantics => semantics.ContentDescription(transcriptLabel)),
                        Label = new Text(transcriptLabel),
                    });
                    children.Add(YouTubeBusyAction(Localize("Import_PolishWithAI"), "native.import.youtube.polish",
                        () => YouTubeCommand(coordinator => coordinator.PolishAsync()),
                        !state.IsPolishing && !state.IsSaving, state.IsPolishing, primary: false));
                    children.Add(YouTubeBusyAction(Localize("Import_SaveAsResource"), "native.import.youtube.save",
                        () => YouTubeCommand(coordinator => coordinator.SaveAsResourceAsync()),
                        !state.IsSaving && !state.IsPolishing, state.IsSaving, primary: true));
                }
                else if (state.IsLoadingTranscript)
                    children.Add(new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
                    {
                        Modifier.FillMaxWidth().Padding(16).TestTag("native.import.youtube.downloading"),
                        new CircularProgressIndicator { Modifier = Modifier.Size(24) },
                        Body(Localize("Import_DownloadingTranscript"), palette, secondary: true),
                    });
                return Card(palette, "native.import.youtube.transcript.card", children.ToArray());
            }));

        if (state.SavedResourceId is { } saved)
            items.Add(new("saved", () => Card(palette, "native.import.youtube.saved",
                new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
                {
                    new Icon(Icons.Default.CheckCircle, null) { Modifier = Modifier.Size(22), Tint = palette.Success },
                    Body(Localize("Import_ResourceSaved"), palette, modifier: Modifier.Weight(1f)),
                },
                Action(Localize("Import_ViewResource"), "native.import.youtube.saved.view",
                    () => ViewYouTubeResource(saved), true),
                Action(Localize("Import_ImportAnother"), "native.import.youtube.saved.another",
                    () => YouTubeEdit(coordinator => coordinator.Reset()), true, primary: false))));

        if (state.Error is { } error)
            items.Add(new("error", () => StatusAlert(error.Format(Localize), palette, error: true,
                automationId: "native.import.youtube.error")));
    }

    /// <summary>A full-width action with a spinner while its work runs.</summary>
    private static ComposableNode YouTubeBusyAction(string label, string tag, Action command, bool enabled,
        bool busy, bool primary)
    {
        var content = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically);
        if (busy)
            content.Add(new CircularProgressIndicator { Modifier = Modifier.Size(18) });
        content.Add(new Text(label));
        var modifier = Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight).TestTag(tag)
            .Semantics(semantics => semantics.ContentDescription(label));
        if (primary)
        {
            var button = new AndroidX.Compose.Button(command, enabled)
            {
                Modifier = modifier,
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            button.Add(content);
            return button;
        }
        var outlined = new OutlinedButton(command, enabled)
        {
            Modifier = modifier,
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        outlined.Add(content);
        return outlined;
    }

    // ------------------------------------------------------------ Import History

    private void YouTubeHistory(List<ImportItem> items, YouTubeImportState state, AndroidVisualPalette palette)
    {
        if (state.IsLoadingImports)
        {
            items.Add(new("history.loading", () => YouTubeSpinner("native.import.youtube.history.loading")));
            return;
        }
        if (state.ImportCount == 0)
        {
            items.Add(new("history.empty", () => YouTubeEmpty(Localize("Import_NoHistoryYet"), null,
                "native.import.youtube.history.empty", palette)));
            return;
        }
        items.Add(new("history.count", () => new Text(ImportFormat(Localize("Import_ShowingCount"),
            state.DisplayedImports.Length, state.ImportCount))
        {
            Modifier = Modifier.TestTag("native.import.youtube.history.count"),
            Color = palette.SecondaryText,
            FontSize = new Sp(13),
        }));
        foreach (var row in state.DisplayedImports)
            items.Add(new($"import.{row.Id}", () => YouTubeImportRowCard(state, row, palette)));
        if (state.HasMoreImports)
            items.Add(new("history.more", () => Action(Localize("Import_ShowMore"),
                "native.import.youtube.history.more", () => YouTubeEdit(coordinator => coordinator.ShowMoreImports()),
                true, primary: false)));
    }

    private ComposableNode YouTubeImportRowCard(YouTubeImportState state, YouTubeImportRow row,
        AndroidVisualPalette palette)
    {
        var id = $"native.import.youtube.import.{row.Id}";
        var title = row.VideoTitle ?? Localize("YouTubeImport_Untitled");
        // Source title attribute: the failure's message, or the stuck hint.
        var description = row.IsFailed && !string.IsNullOrEmpty(row.ErrorMessage)
            ? $"{title}. {row.ErrorMessage}"
            : row.IsStuck ? $"{title}. {Localize("YouTubeImport_StuckHint")}" : title;
        var titleModifier = Modifier.FillMaxWidth().TestTag($"{id}.open");
        if (row.IsClickable)
            titleModifier = titleModifier.Clickable(() =>
            {
                if (_youTubeImport?.OpenImport(row.Id) is { } resource)
                    ViewYouTubeResource(resource);
            });
        var titleText = new Text(title)
        {
            Modifier = titleModifier.Semantics(semantics =>
            {
                semantics.ContentDescription(description);
                if (row.IsClickable)
                    semantics.Role(SemanticsRole.Button);
            }),
            Color = row.IsClickable ? palette.Primary : palette.Text,
            FontSize = new Sp(16),
            FontWeight = FontWeight.SemiBold,
            MaxLines = 2,
            Overflow = TextOverflow.Ellipsis,
        };
        var meta = new FlowRow { Modifier.FillMaxWidth() };
        meta.Add(YouTubeStatusBadge(row.Status, palette, $"{id}.status"));
        meta.Add(new Text(row.IsManual ? Localize("Import_ManualImport")
            : row.ChannelName ?? Localize("YouTubeImport_ChannelFallback"))
        {
            Modifier = Modifier.Padding(end: 8, bottom: 4).TestTag($"{id}.channel"),
            Color = palette.SecondaryText,
            FontSize = new Sp(13),
        });
        if (row.CreatedAt is { } created)
            meta.Add(new Text(created.ToLocalTime().ToString("d", CultureInfo.CurrentCulture))
            {
                Modifier = Modifier.Padding(end: 8, bottom: 4).TestTag($"{id}.date"),
                Color = palette.SecondaryText,
                FontSize = new Sp(13),
            });
        var column = new Column(Arrangement.SpacedBy(6))
        {
            Modifier.FillMaxWidth().Padding(horizontal: 16, vertical: 12),
            titleText,
            meta,
        };
        if (row.CanRetry)
        {
            var retrying = state.RetryingImportId == row.Id;
            column.Add(YouTubeRetryButton(retrying, state.RetryingImportId is null,
                () => YouTubeCommand(coordinator => coordinator.RetryImportAsync(row.Id)), $"{id}.retry", palette));
        }
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth().TestTag(id),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = palette.Card,
        };
        surface.Add(column);
        return surface;
    }

    /// <summary>Source btn-outline-warning Retry, with a spinner while it runs.</summary>
    private static ComposableNode YouTubeRetryButton(bool busy, bool enabled, Action retry, string tag,
        AndroidVisualPalette palette)
    {
        var label = Localize("Import_Retry");
        var button = new OutlinedButton(retry, enabled)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight).TestTag(tag)
                .Border(1, palette.Warning, AndroidVisualTheme.ControlRadius)
                .Semantics(semantics => semantics.ContentDescription(label)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        if (busy)
            button.Add(new CircularProgressIndicator { Modifier = Modifier.Size(16).Padding(end: 6) });
        else
            button.Add(new Icon(Icons.Default.Refresh, null) { Modifier = Modifier.Size(18), Tint = palette.WarningText });
        button.Add(new Text(label) { Color = palette.WarningText });
        return button;
    }

    /// <summary>Source RenderStatusBadge: the status's color and text (a spinner while it runs).</summary>
    private ComposableNode YouTubeStatusBadge(VideoImportStatus status, AndroidVisualPalette palette, string tag)
    {
        var info = Color.FromHex("#0DCAF0");
        var dark = Color.FromHex("#000000");
        var light = Color.FromHex("#FFFFFF");
        var (fill, text, key) = status switch
        {
            VideoImportStatus.Pending => (palette.Warning, dark, "Import_Pending"),
            VideoImportStatus.FetchingTranscript => (info, dark, "Import_Fetching"),
            VideoImportStatus.CleaningTranscript => (info, dark, "Import_Cleaning"),
            VideoImportStatus.GeneratingVocabulary => (info, dark, "Import_Generating"),
            VideoImportStatus.SavingResource => (info, dark, "Import_Saving"),
            VideoImportStatus.Completed => (palette.Success, light, "Import_Completed"),
            VideoImportStatus.Failed => (palette.Danger, light, "Import_Failed"),
            _ => (palette.SecondaryText, light, "Unknown"),
        };
        var label = Localize(key);
        if (status is >= VideoImportStatus.FetchingTranscript and < VideoImportStatus.Completed)
            label += "…";
        return ImportBadge(label, fill, text, tag, Localize(key));
    }

    private static ComposableNode YouTubeSpinner(string tag) =>
        new Row(Arrangement.Center, Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().Padding(48).TestTag(tag)
                .Semantics(semantics => semantics.ContentDescription(Localize("Common_Loading"))),
            new CircularProgressIndicator { Modifier = Modifier.Size(32) },
        };

    private static ComposableNode YouTubeEmpty(string message, ComposableNode? action, string tag,
        AndroidVisualPalette palette)
    {
        var column = new Column(Arrangement.SpacedBy(16), Alignment.Horizontal.CenterHorizontally)
        {
            Modifier.FillMaxWidth().Padding(32).TestTag(tag),
            Body(message, palette, secondary: true),
        };
        if (action is not null)
            column.Add(action);
        return column;
    }

    // ------------------------------------------------------------ The channel page

    private ComposableNode RenderYouTubeChannel(YouTubeChannelCoordinator channel, AndroidVisualPalette palette)
    {
        var state = channel.State;
        var header = ScreenHeader(Localize(state.IsNew ? "ChannelDetail_AddChannel" : "ChannelDetail_EditChannel"),
            palette, back: true, backAutomationId: "native.import.youtube.channel.back");
        var page = new Column { Modifier.FillMaxSize().TestTag("native.import.youtube.channel.screen") };
        page.Add(header);
        if (MessageBanners(state.Messages, palette, index => $"native.import.youtube.channel.message.{index}",
                () => _youTubeChannel?.DismissMessages(), "native.import.youtube.channel.message.dismiss",
                Localize("Common_Close")) is { } banners)
            page.Add(new Box
            {
                Modifier.FillMaxWidth().Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 8),
                banners,
            });
        var items = new List<ImportItem>();
        if (state.Blocked)
            items.Add(new("blocked", () => StatusAlert(Localize("Import_ProfileNotFound"), palette, error: true,
                automationId: "native.import.youtube.channel.blocked")));
        else if (!state.Open || state.IsLoading)
            items.Add(new("loading", () => YouTubeSpinner("native.import.youtube.channel.loading")));
        else
        {
            items.Add(new("form", () => YouTubeChannelForm(state, palette)));
            if (state.ShowsDiscovery)
                YouTubeDiscovery(items, state, palette);
        }
        page.Add(ImportLazyList(items, "native.import.youtube.channel.scroll"));
        return page;
    }

    private ComposableNode YouTubeChannelForm(YouTubeChannelState state, AndroidVisualPalette palette)
    {
        var urlLabel = $"{Localize("ChannelDetail_ChannelURL")} *";
        var url = new OutlinedTextField(state.ChannelUrl,
            value => ChannelEdit(coordinator => coordinator.SetChannelUrl(value)),
            enabled: !state.IsSaving, singleLine: true)
        {
            Modifier = Modifier.FillMaxWidth().TestTag("native.import.youtube.channel.url")
                .Semantics(semantics => semantics.ContentDescription(Localize("ChannelDetail_ChannelURL"))),
            Label = new Text(urlLabel),
            Placeholder = new Text(Localize("ChannelDetail_URLPlaceholder")),
        };
        // Source select: "-- Select Language --" and Constants.Languages; a stored language outside
        // the list is offered too, so it stays selected.
        const string noLanguage = "native.no-language";
        var names = SentenceStudio.Common.Constants.Languages.ToList();
        if (!string.IsNullOrWhiteSpace(state.Language) && !names.Contains(state.Language))
            names.Insert(0, state.Language);
        var languages = new List<(string, string)> { (noLanguage, Localize("ChannelDetail_SelectLanguage")) };
        languages.AddRange(names.Select(name => (name, name)));
        var intervals = YouTubeChannelCoordinator.CheckIntervals.Select(hours =>
            (hours.ToString(CultureInfo.InvariantCulture), Localize($"ChannelDetail_Every{hours}Hours"))).ToArray();
        var children = new List<ComposableNode>
        {
            url,
            ImportCaption(Localize("ChannelDetail_EnterFullURL"), palette),
            ImportChoice($"{Localize("ChannelDetail_Language")} *", languages,
                string.IsNullOrWhiteSpace(state.Language) ? noLanguage : state.Language,
                "native.import.youtube.channel.language",
                value => ChannelEdit(coordinator => coordinator.SetLanguage(value == noLanguage ? string.Empty : value)),
                !state.IsSaving, palette),
            ImportChoice(Localize("ChannelDetail_CheckInterval"), intervals,
                state.CheckIntervalHours.ToString(CultureInfo.InvariantCulture),
                "native.import.youtube.channel.interval",
                value => ChannelEdit(coordinator =>
                    coordinator.SetCheckIntervalHours(int.Parse(value, CultureInfo.InvariantCulture))),
                !state.IsSaving, palette),
            SettingSwitch(Localize("ChannelDetail_MonitorChannel"), "native.import.youtube.channel.active",
                state.IsActive, value => ChannelEdit(coordinator => coordinator.SetIsActive(value)),
                !state.IsSaving, palette),
        };
        if (state.Error is { } error)
            children.Add(StatusAlert(error.Format(Localize), palette, error: true,
                automationId: "native.import.youtube.channel.error"));
        if (state.IsResolving)
            children.Add(new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().TestTag("native.import.youtube.channel.resolving"),
                new CircularProgressIndicator { Modifier = Modifier.Size(20) },
                Body(Localize("ChannelDetail_ResolvingChannel"), palette, secondary: true),
            });
        children.Add(YouTubeBusyAction(Localize("ChannelDetail_SaveChannel"), "native.import.youtube.channel.save",
            () => ChannelCommand(coordinator => coordinator.SaveAsync()), state.CanSave, state.IsSaving,
            primary: true));
        children.Add(Action(Localize("Common_Cancel"), "native.import.youtube.channel.cancel",
            CancelYouTubeChannel, !state.IsSaving, primary: false));
        return Card(palette, "native.import.youtube.channel.form", children.ToArray());
    }

    private void YouTubeDiscovery(List<ImportItem> items, YouTubeChannelState state, AndroidVisualPalette palette)
    {
        items.Add(new("discovery", () =>
        {
            var children = new List<ComposableNode>
            {
                ImportHeading(Localize("ChannelDetail_DiscoverVideos"), palette),
                YouTubeBusyAction(Localize(state.IsCheckingVideos ? "ChannelDetail_Checking" : "ChannelDetail_CheckForNewVideos"),
                    "native.import.youtube.channel.check",
                    () => ChannelCommand(coordinator => coordinator.CheckForNewVideosAsync()),
                    !state.IsCheckingVideos && state.HasYouTubeChannelId, state.IsCheckingVideos, primary: false),
            };
            if (!state.Videos.IsDefaultOrEmpty)
            {
                var importLabel = state.IsImportingVideos
                    ? Localize("ChannelDetail_Importing")
                    : ImportFormat(Localize("ChannelDetail_ImportSelected"), state.SelectedVideoIds.Count);
                children.Add(YouTubeBusyAction(importLabel, "native.import.youtube.channel.import",
                    () => ChannelCommand(coordinator => coordinator.ImportSelectedAsync()),
                    !state.IsImportingVideos && state.SelectedVideoIds.Count > 0, state.IsImportingVideos,
                    primary: true));
            }
            if (!state.HasYouTubeChannelId)
            {
                var warning = new List<ComposableNode>
                {
                    Body(Localize("ChannelDetail_ChannelIDNotResolved"), palette),
                };
                if (!string.IsNullOrEmpty(state.ResolutionError))
                    warning.Add(ImportCaption(ImportFormat(Localize("YouTubeChannel_ErrorLabel"),
                        state.ResolutionError), palette));
                children.Add(ImportAlert(palette, warning: true, "native.import.youtube.channel.unresolved",
                    warning.ToArray()));
                children.Add(YouTubeBusyAction(Localize(state.IsRetryingResolve
                        ? "ChannelDetail_Resolving" : "ChannelDetail_RetryResolve"),
                    "native.import.youtube.channel.retry-resolve",
                    () => ChannelCommand(coordinator => coordinator.RetryResolveAsync()),
                    !state.IsRetryingResolve, state.IsRetryingResolve, primary: false));
            }
            else if (!state.Videos.IsDefaultOrEmpty)
            {
                var toggleLabel = Localize(state.AllNewSelected ? "ChannelDetail_SelectNone" : "ChannelDetail_SelectAllNew");
                var toggle = new TextButton(() => ChannelEdit(coordinator => coordinator.ToggleSelectAll()),
                    !state.IsImportingVideos)
                {
                    Modifier = Modifier.TestTag("native.import.youtube.channel.select-all")
                        .Semantics(semantics => semantics.ContentDescription(toggleLabel)),
                };
                toggle.Add(new Text(toggleLabel));
                children.Add(new Row(Arrangement.SpaceBetween, Alignment.Vertical.CenterVertically)
                {
                    Modifier.FillMaxWidth(),
                    ImportCaption(ImportFormat(Localize("ChannelDetail_VideosFound"), state.Videos.Length), palette),
                    toggle,
                });
            }
            else if (state.HasCheckedVideos && !state.IsCheckingVideos)
                children.Add(Body(Localize("ChannelDetail_NoVideosFound"), palette, secondary: true,
                    modifier: Modifier.TestTag("native.import.youtube.channel.no-videos")));
            if (state.DiscoveryError is { } error)
                children.Add(StatusAlert(error.Format(Localize), palette, error: true,
                    automationId: "native.import.youtube.channel.discovery-error"));
            return Card(palette, "native.import.youtube.channel.discovery", children.ToArray());
        }));
        if (!state.HasYouTubeChannelId || state.Videos.IsDefaultOrEmpty)
            return;
        foreach (var video in state.Videos)
            items.Add(new($"video.{video.VideoId}", () => YouTubeVideoRow(state, video, palette)));
    }

    private ComposableNode YouTubeVideoRow(YouTubeChannelState state, YouTubeDiscoveredVideo video,
        AndroidVisualPalette palette)
    {
        // Keyed by the video, so an id keeps naming the same video when the list changes.
        var id = $"native.import.youtube.channel.video.{video.VideoId}";
        var selected = state.SelectedVideoIds.Contains(video.VideoId);
        var enabled = video.CanSelect && !state.IsImportingVideos;
        var row = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().Padding(horizontal: 8, vertical: 6),
            new Checkbox(@checked: selected,
                onCheckedChange: _ => ChannelEdit(coordinator => coordinator.ToggleVideo(video.VideoId)),
                enabled: enabled)
            {
                Modifier = Modifier.TestTag($"{id}.select")
                    .Semantics(semantics => semantics.ContentDescription(video.Title)),
            },
            new Text(video.Title)
            {
                Modifier = Modifier.Weight(1f),
                Color = palette.Text,
                FontSize = new Sp(15),
                MaxLines = 2,
                Overflow = TextOverflow.Ellipsis,
            },
        };
        var dark = Color.FromHex("#000000");
        var light = Color.FromHex("#FFFFFF");
        if (video.IsCompleted)
            row.Add(ImportBadge(Localize("ChannelDetail_Imported"), palette.Success, light, $"{id}.imported",
                Localize("ChannelDetail_Imported")));
        else if (video.IsInProgress)
            row.Add(ImportBadge(Localize("ChannelDetail_Importing"), Color.FromHex("#0DCAF0"), dark,
                $"{id}.importing", Localize("ChannelDetail_Importing")));
        var column = new Column(Arrangement.SpacedBy(4)) { Modifier.FillMaxWidth(), row };
        if (video.IsFailed)
            column.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().Padding(start: 56, bottom: 8),
                ImportBadge(Localize("Import_Failed"), palette.Danger, light, $"{id}.failed", Localize("Import_Failed")),
                YouTubeRetryButton(state.RetryingVideoId == video.VideoId, !state.IsRetryingImport,
                    () => ChannelCommand(coordinator => coordinator.RetryFailedVideoAsync(video.VideoId)),
                    $"{id}.retry", palette),
            });
        var surface = new Surface
        {
            // Source: an imported video's row is dimmed (opacity-50).
            Modifier = Modifier.FillMaxWidth().TestTag(id).Alpha(video.IsCompleted ? 0.5f : 1f),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = palette.Card,
        };
        surface.Add(column);
        return surface;
    }
}
