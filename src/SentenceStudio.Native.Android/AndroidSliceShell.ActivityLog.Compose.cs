using System.Globalization;
using AndroidX.Compose;
using SentenceStudio.Native.Features.ActivityLog;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Activity Log (source ActivityLog.razor, iOS ActivityLogView) on the shared
/// owner-scoped ActivityLogCoordinator: eight weeks, four more per Load More, the
/// All / Input / Output filter, and a day's plan detail. Weeks are lazy rows.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private const string ActivityOutputHex = "#FFC233";

    private sealed record ActivityLogRow(string Key, Func<ComposableNode> Content);

    private ComposableNode RenderActivityLog(AndroidVisualPalette palette)
    {
        var log = _activityLog;
        var state = log?.State ?? new ActivityLogState(ActivityLogPhase.Blocked, [], null, null, false, 8);
        var header = ScreenHeader(Localize("ActivityLog_Title"), palette, back: true,
            backAutomationId: "native.activity-log.back");
        if (header is TopAppBar bar)
            bar.Actions = ActivityLogActions(log, state, palette);
        var page = new Column
        {
            Modifier.FillMaxSize().TestTag("native.activity-log.screen"),
            header,
        };
        var rows = new List<ActivityLogRow>();
        if (state.Phase == ActivityLogPhase.Blocked)
            rows.Add(new("status", () => StatusAlert(Localize("NativeActivityLog_OwnerUnavailable"),
                palette, error: true, automationId: "native.activity-log.blocked")));
        else if (state.Phase == ActivityLogPhase.LoadError)
            rows.Add(new("status", () => StatusAlert(Localize("NativeActivityLog_LoadError"),
                palette, error: true, automationId: "native.activity-log.error")));
        else if (state.Phase is ActivityLogPhase.Loading or ActivityLogPhase.Closed
            && state.Weeks.Count == 0)
            rows.Add(new("status", () => new Row(Arrangement.SpacedBy(10), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().TestTag("native.activity-log.loading"),
                new CircularProgressIndicator { Modifier = Modifier.Size(24) },
                Body(Localize("Common_Loading"), palette, secondary: true),
            }));
        else if (state.Weeks.Count == 0 && !state.HasMore)
            rows.Add(new("status", () =>
            {
                var message = Body(Localize("ActivityLog_EmptyState"), palette, secondary: true,
                    modifier: Modifier.FillMaxWidth());
                message.Align = TextAlign.Center;
                return new Column(Arrangement.SpacedBy(12), Alignment.Horizontal.CenterHorizontally)
                {
                    Modifier.FillMaxWidth().Padding(48).TestTag("native.activity-log.empty"),
                    new Icon(Icons.Default.DateRange, Localize("ActivityLog_EmptyState"))
                    {
                        Modifier = Modifier.Size(32),
                        Tint = palette.SecondaryText,
                    },
                    message,
                };
            }));
        else
        {
            rows.Add(new("legend", () => ActivityLegend(palette)));
            foreach (var week in state.Weeks)
            {
                var item = week;
                rows.Add(new($"week.{item.WeekStart:yyyyMMdd}",
                    () => ActivityWeek(item, state.SelectedDay, palette)));
            }
        }
        if (state.HasMore && log is not null
            && (state.Phase is ActivityLogPhase.Ready or ActivityLogPhase.Empty
                || state.Phase == ActivityLogPhase.Loading && state.Weeks.Count > 0))
        {
            var loading = state.Phase == ActivityLogPhase.Loading;
            rows.Add(new("more", () =>
            {
                var row = new Row(Arrangement.Center,
                    Alignment.Vertical.CenterVertically)
                {
                    Modifier.FillMaxWidth().Padding(vertical: 8),
                };
                if (loading)
                    row.Add(new CircularProgressIndicator
                    {
                        Modifier = Modifier.Size(20).TestTag("native.activity-log.more.loading"),
                    });
                var button = new OutlinedButton(
                    () => RunMore(() => log.LoadMoreAsync(_lifetime.Token)), enabled: !loading)
                {
                    Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                        .TestTag("native.activity-log.load-more"),
                    Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
                };
                button.Add(new Text(Localize(loading ? "Common_Loading" : "Common_LoadMore")));
                row.Add(button);
                return row;
            }));
        }
        page.Add(new LazyColumn<ActivityLogRow>(rows, row => row.Content())
        {
            Key = row => row.Key,
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .Padding(horizontal: AndroidVisualTheme.PagePadding)
                .TestTag("native.activity-log.scroll"),
            VerticalArrangement = Arrangement.SpacedBy(12),
        });
        return page;
    }

    private ComposableNode ActivityLogActions(ActivityLogCoordinator? log, ActivityLogState state,
        AndroidVisualPalette palette)
    {
        var refresh = new IconButton(() =>
        {
            if (log is not null)
                RunMore(() => log.RefreshAsync(_lifetime.Token));
        }, enabled: log is not null
            && state.Phase is not (ActivityLogPhase.Loading or ActivityLogPhase.Blocked))
        {
            Modifier = Modifier.TestTag("native.activity-log.refresh")
                .Semantics(semantics => semantics.ContentDescription(Localize("Common_Refresh"))),
        };
        refresh.Add(new Icon(Icons.Default.Refresh, Localize("Common_Refresh"))
        {
            Modifier = Modifier.Size(24),
            Tint = palette.Text,
        });
        var filters = new IconButton(() => ToggleMoreMenu("activity-log.filters"),
            enabled: log is not null && state.Phase != ActivityLogPhase.Blocked)
        {
            Modifier = Modifier.TestTag("native.activity-log.filters")
                .Semantics(semantics => semantics.ContentDescription(Localize("More"))),
        };
        filters.Add(new Icon(Icons.Default.MoreVert, Localize("More"))
        {
            Modifier = Modifier.Size(24),
            Tint = palette.Text,
        });
        var menu = PopupMenu(expanded: _moreMenu == "activity-log.filters",
            onDismissRequest: CloseMoreMenu);
        foreach (var (id, key, category) in new (string, string, ActivityCategory?)[]
        {
            ("all", "ActivityLog_AllActivities", null),
            ("input", "ActivityLog_InputOnly", ActivityCategory.Input),
            ("output", "ActivityLog_OutputOnly", ActivityCategory.Output),
        })
        {
            var label = Localize(key);
            var selected = state.Filter == category;
            var filter = category;
            menu.Add(new DropdownMenuItem(text: new Text(label), onClick: () =>
            {
                CloseMoreMenu();
                if (log is not null)
                    RunMore(() => log.FilterAsync(filter, _lifetime.Token));
            })
            {
                Modifier = Modifier.TestTag($"native.activity-log.filter.{id}").Semantics(semantics =>
                {
                    semantics.ContentDescription(label);
                    semantics.Selected(selected);
                }),
                LeadingIcon = selected
                    ? new Icon(Icons.Default.Check, label) { Modifier = Modifier.Size(20), Tint = palette.Primary }
                    : new Box { Modifier.Size(20) },
            });
        }
        return new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
        {
            refresh,
            new Box { filters, menu },
        };
    }

    private static ComposableNode ActivityDot(int size, Color color, Color? ring = null)
    {
        var modifier = Modifier.Size(size).Background(color, Shape.Circle());
        if (ring is { } border)
            modifier = modifier.Border(2, border, Shape.Circle());
        return new Box { modifier };
    }

    private ComposableNode ActivityLegend(AndroidVisualPalette palette) =>
        new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.Padding(top: 8).TestTag("native.activity-log.legend"),
            ActivityDot(12, palette.Accent),
            Caption(Localize("ActivityLog_Input"), palette),
            ActivityDot(12, Color.FromHex(ActivityOutputHex)),
            Caption(Localize("ActivityLog_Output"), palette),
        };

    private ComposableNode ActivityWeek(ActivityLogWeek week, DateTime? selectedDay,
        AndroidVisualPalette palette)
    {
        var culture = CultureInfo.CurrentUICulture;
        var range = Body(ActivityLogWeekHeader.Format(week, culture), palette,
            modifier: Modifier.Weight(1f));
        range.FontWeight = FontWeight.Bold;
        var days = new Row(Arrangement.SpacedBy(2)) { Modifier.FillMaxWidth() };
        foreach (var day in week.Days)
            days.Add(ActivityDay(day, selectedDay?.Date == day.Date.Date, palette));
        var children = new List<ComposableNode>
        {
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                range,
                Caption(string.Format(culture, Localize("ActivityLog_WeekMinutes"),
                    week.TotalMinutes), palette),
            },
            days,
        };
        if (selectedDay is { } selected
            && week.Days.FirstOrDefault(day => day.Date.Date == selected.Date) is { } detail)
            children.Add(ActivityDayDetail(detail, palette));
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth()
                .TestTag($"native.activity-log.week.{week.WeekStart:yyyyMMdd}"),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        var column = new Column(Arrangement.SpacedBy(8))
        {
            Modifier = Modifier.FillMaxWidth().Padding(10),
        };
        foreach (var child in children)
            column.Add(child);
        surface.Add(column);
        return surface;
    }

    private ComposableNode ActivityDay(ActivityLogDay day, bool selected, AndroidVisualPalette palette)
    {
        var culture = CultureInfo.CurrentUICulture;
        var phone = WindowWidthDp < 768;
        var weekday = day.Date.ToString("ddd", culture);
        var name = Caption(phone ? weekday[..1] : weekday, palette);
        var cell = new Column(Arrangement.SpacedBy(4), Alignment.Horizontal.CenterHorizontally)
        {
            Modifier.Weight(1f),
            name,
        };
        if (!day.Plans.Any(plan => plan.Items.Count > 0))
        {
            var rest = new Column(Arrangement.Center, Alignment.Horizontal.CenterHorizontally)
            {
                Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight),
            };
            if (!phone)
                rest.Add(new Text(Localize("ActivityLog_Rest"))
                {
                    Modifier = Modifier.Alpha(0.4f),
                    Color = palette.SecondaryText,
                    FontSize = new Sp(AndroidVisualTheme.CaptionSize),
                });
            cell.Add(rest);
            return cell;
        }
        var count = day.Plans.Sum(plan => plan.CompletedCount);
        var total = day.Plans.Sum(plan => plan.TotalCount);
        var output = Color.FromHex(ActivityOutputHex);
        ComposableNode mark;
        if (day.TotalMinutes <= 0)
        {
            var text = Caption($"{count}/{total}", palette);
            if (day.AllPlansCompleted && text is Text label)
                label.Color = palette.Success;
            mark = text;
        }
        else
        {
            var size = day.TotalMinutes < 10 ? 12 : day.TotalMinutes < 25 ? 20 : 28;
            if (day.HasInput && day.HasOutput)
            {
                var pair = new Row(Arrangement.SpacedBy(0), Alignment.Vertical.CenterVertically)
                {
                    ActivityDot(size / 2, palette.Accent),
                    ActivityDot(size / 2, output),
                };
                if (day.AllPlansCompleted)
                    pair.Add(new Icon(Icons.Default.CheckCircle, Localize("PlanSummary_Completed"))
                    {
                        Modifier = Modifier.Size(12),
                        Tint = palette.Success,
                    });
                mark = pair;
            }
            else
            {
                var color = day.HasInput ? palette.Accent : output;
                mark = day.AllPlansCompleted
                    ? ActivityDot(size + 4, color, palette.Success)
                    : ActivityDot(size, color);
            }
        }
        var categories = day.HasInput && day.HasOutput
            ? $"{Localize("ActivityLog_Input")}, {Localize("ActivityLog_Output")}"
            : Localize(day.HasInput ? "ActivityLog_Input" : "ActivityLog_Output");
        var accessible = $"{day.Date.ToString("ddd, MMM d", culture)}, "
            + $"{day.TotalMinutes} {Localize("Common_MinutesShort")}, "
            + $"{categories}, {count}/{total} {Localize("PlanSummary_Completed")}";
        var date = day.Date;
        var button = new Column(Arrangement.Center, Alignment.Horizontal.CenterHorizontally)
        {
            Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                .Clip(Shape.RoundedCorners(AndroidVisualTheme.ControlRadius))
                .Background(selected ? palette.Elevated : palette.Card,
                    Shape.RoundedCorners(AndroidVisualTheme.ControlRadius))
                .Clickable(() => _activityLog?.ToggleDay(date))
                .TestTag($"native.activity-log.day.{day.Date:yyyyMMdd}")
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(accessible);
                    semantics.Selected(selected);
                    semantics.Role(SemanticsRole.Button);
                }),
            mark,
        };
        cell.Add(button);
        return cell;
    }

    private ComposableNode ActivityDayDetail(ActivityLogDay day, AndroidVisualPalette palette)
    {
        var culture = CultureInfo.CurrentUICulture;
        var count = day.Plans.Sum(plan => plan.CompletedCount);
        var total = day.Plans.Sum(plan => plan.TotalCount);
        var heading = Body(day.Date.ToString("ddd, MMM d", culture), palette,
            modifier: Modifier.Weight(1f));
        heading.FontWeight = FontWeight.Bold;
        var completed = Caption($"{count}/{total} {Localize("PlanSummary_Completed")}", palette);
        if (day.AllPlansCompleted && completed is Text done)
            done.Color = palette.Success;
        var detail = new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().TestTag($"native.activity-log.detail.{day.Date:yyyyMMdd}"),
            new HorizontalDivider { Color = palette.Border },
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                heading,
                completed,
            },
        };
        var planNumber = 1;
        for (var planOrdinal = 0; planOrdinal < day.Plans.Count; planOrdinal++)
        {
            var plan = day.Plans[planOrdinal];
            var isAdHoc = plan.Items.Count > 0 && plan.Items.All(item =>
                item.PlanItemId.StartsWith("adhoc-", StringComparison.Ordinal));
            if (day.Plans.Count > 1 || isAdHoc)
            {
                var title = isAdHoc ? Localize("PlanSummary_Freeform")
                    : $"{Localize("PlanSummary_Plan")} {planNumber}";
                detail.Add(Caption(
                    $"{title} ({plan.GeneratedAt.ToLocalTime().ToString("h:mm tt", culture)})", palette));
            }
            for (var entryOrdinal = 0; entryOrdinal < plan.Items.Count; entryOrdinal++)
                detail.Add(ActivityEntry(plan.Items[entryOrdinal], isAdHoc, planOrdinal,
                    entryOrdinal, day.Date, palette));
            detail.Add(new HorizontalDivider { Color = palette.Border });
            detail.Add(new Row(Arrangement.SpaceBetween, Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                Caption(Localize("PlanSummary_TotalTime"), palette),
                Caption($"{plan.TotalMinutesSpent} {Localize("Common_MinutesShort")}", palette),
            });
            if (!isAdHoc)
                planNumber++;
        }
        return detail;
    }

    private ComposableNode ActivityEntry(ActivityLogEntry entry, bool isAdHoc,
        int planOrdinal, int entryOrdinal, DateTime date, AndroidVisualPalette palette)
    {
        var color = entry.Category == ActivityCategory.Input
            ? palette.Accent : Color.FromHex(ActivityOutputHex);
        var caption = string.Join(" · ",
            new[] { entry.ResourceTitle, entry.SkillName }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        var labels = new Column(Arrangement.SpacedBy(2))
        {
            Modifier.Weight(1f),
            Body(Localize(ActivityNameKey(entry.ActivityType)), palette,
                secondary: !entry.IsCompleted),
        };
        if (caption.Length > 0)
            labels.Add(new Text(caption)
            {
                Color = palette.SecondaryText,
                FontSize = new Sp(AndroidVisualTheme.CaptionSize + 1),
                MaxLines = 1,
                Overflow = TextOverflow.Ellipsis,
            });
        var duration = isAdHoc
            ? $"{entry.MinutesSpent} {Localize("Common_MinutesShort")}"
            : $"{entry.MinutesSpent}/{entry.EstimatedMinutes} {Localize("Common_MinutesShort")}";
        var status = entry.IsCompleted
            ? (ComposableNode)new Icon(Icons.Default.CheckCircle, Localize("PlanSummary_Completed"))
            {
                Modifier = Modifier.Size(18),
                Tint = palette.Success,
            }
            : new Box
            {
                Modifier.Size(18).Border(2, palette.SecondaryText, Shape.Circle()),
            };
        return new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth()
                .TestTag($"native.activity-log.entry.{date:yyyyMMdd}.{planOrdinal}.{entryOrdinal}"),
            status,
            ActivityDot(12, color),
            labels,
            Caption(duration, palette),
        };
    }

    private static string ActivityNameKey(PlanActivityType activity) => activity switch
    {
        PlanActivityType.VocabularyReview => "Activity_VocabularyReview",
        PlanActivityType.Reading => "Activity_Reading",
        PlanActivityType.Listening => "Activity_Listening",
        PlanActivityType.Shadowing => "Activity_Shadowing",
        PlanActivityType.Cloze => "Activity_ClozeExercise",
        PlanActivityType.Translation => "Activity_Translate",
        PlanActivityType.Writing => "Activity_Writing",
        PlanActivityType.SceneDescription => "Activity_DescribeScene",
        PlanActivityType.Conversation => "Activity_Conversation",
        PlanActivityType.VocabularyGame => "Activity_VocabMatching",
        PlanActivityType.VideoWatching => "Activity_VideoWatching",
        PlanActivityType.NumberDrill => "Activity_NumberDrill",
        _ => "Activity_Generic"
    };
}
