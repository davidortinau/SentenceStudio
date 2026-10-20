using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.ActivityLog;

internal static class ActivityLogView
{
    internal static SwiftView Page(
        ActivityLogState state, SourceUiStyle style, Func<string, string> text,
        Action<DateTime> selectDay, Action loadMore)
    {
        var content = new List<SwiftView>();
        if (state.Phase == ActivityLogPhase.Blocked)
            content.Add(style.Alert(
                    style.Text(Views.Text(text("NativeActivityLog_OwnerUnavailable")),
                        SourceTextRole.Body2), success: false)
                .WithAccessibilityIdentifier("native.activity-log.blocked"));
        else if (state.Phase == ActivityLogPhase.LoadError)
            content.Add(style.Alert(
                    style.Text(Views.Text(text("NativeActivityLog_LoadError")),
                        SourceTextRole.Body2), success: false)
                .WithAccessibilityIdentifier("native.activity-log.error"));
        else if (state.Phase == ActivityLogPhase.Loading && state.Weeks.Count == 0)
            content.Add(Views.IndeterminateProgress(text("Common_Loading"))
                .WithAccessibilityIdentifier("native.activity-log.loading"));
        else if (state.Weeks.Count == 0 && !state.HasMore)
        {
            var empty = Views.VStack(
                    Views.Image("calendar.badge.exclamationmark")
                        .WithForegroundColor(style.Appearance.SecondaryText.Light,
                            style.Appearance.SecondaryText.Dark),
                    style.Text(Views.Text(text("ActivityLog_EmptyState")),
                            SourceTextRole.Body1, SourceTextTone.Secondary))
                .WithPadding(48)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                .WithAccessibilityIdentifier("native.activity-log.empty");
            empty.Spacing = SourceUiLayout.FormGap;
            content.Add(empty);
        }
        else
        {
            content.Add(Legend(style, text));
            foreach (var week in state.Weeks)
                content.Add(Week(week, state.SelectedDay, style, text, selectDay));
        }

        if (state.HasMore
            && (state.Phase is (ActivityLogPhase.Ready or ActivityLogPhase.Empty)
                || state.Phase == ActivityLogPhase.Loading && state.Weeks.Count > 0))
        {
            var button = style.SecondaryButton(
                    text(state.Phase == ActivityLogPhase.Loading
                        ? "Common_Loading" : "Common_LoadMore"), loadMore)
                .WithAccessibilityIdentifier("native.activity-log.load-more");
            button.IsEnabled = state.Phase != ActivityLogPhase.Loading;
            var action = state.Phase == ActivityLogPhase.Loading
                ? Views.HStack(
                    Views.IndeterminateProgress(text("Common_Loading"))
                        .WithAccessibilityIdentifier("native.activity-log.more.loading"),
                    button)
                : Views.HStack(button);
            action.Spacing = SourceUiLayout.ItemGap;
            content.Add(action.WithFlexibleWidth(
                alignment: SwiftHorizontalAlignment.Center));
        }

        var inner = Views.VStack(content.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.activity-log.scroll.content");
        inner.Spacing = SourceUiLayout.PageGap;
        return style.Page(Views.Scroll(inner)
                .WithAccessibilityIdentifier("native.activity-log.scroll"))
            .WithAccessibilityIdentifier("native.activity-log.screen");
    }

    internal static SwiftView Refresh(
        SourceUiStyle style, Func<string, string> text, Action refresh)
    {
        var button = Views.Button(Views.Image("arrow.clockwise"), refresh)
            .WithAccessibilityLabel(text("Common_Refresh"))
            .WithTintColor(style.Appearance.Primary.Light,
                style.Appearance.Primary.Dark)
            .WithAccessibilityIdentifier("native.activity-log.refresh");
        return button;
    }

    internal static SwiftView Filters(
        ActivityLogState state, SourceUiStyle style, Func<string, string> text,
        Action<ActivityCategory?> select)
    {
        var options = new (string Id, string Label, ActivityCategory? Filter)[]
        {
            ("all", "ActivityLog_AllActivities", null),
            ("input", "ActivityLog_InputOnly", ActivityCategory.Input),
            ("output", "ActivityLog_OutputOnly", ActivityCategory.Output)
        };
        var buttons = options.Select(option =>
        {
            var label = text(option.Label);
            var check = Views.Image(option.Filter == state.Filter
                    ? "checkmark.circle.fill" : "circle")
                .WithAccessibilityIdentifier(
                    $"native.activity-log.filter.{option.Id}.check");
            var row = Views.HStack(check, Views.Text(label))
                .WithAccessibilityIdentifier(
                    $"native.activity-log.filter.{option.Id}.label");
            row.Spacing = SourceUiLayout.ItemGap;
            var button = Views.Button(row, () => select(option.Filter))
                .WithAccessibilityLabel(label)
                .WithAccessibilitySelected(option.Filter == state.Filter)
                .WithAccessibilityIdentifier(
                    $"native.activity-log.filter.{option.Id}");
            button.Text = label;
            return button;
        }).ToArray();
        var color = style.Appearance.SecondaryText;
        var icon = Views.Image("ellipsis")
            .WithFixedSymbolSize(20)
            .WithForegroundColor(color.Light, color.Dark)
            .WithAccessibilityIdentifier("native.activity-log.filters.icon");
        return Views.Menu(icon, buttons)
            .WithAccessibilityLabel(text("More"))
            .WithAccessibilityIdentifier("native.activity-log.filters");
    }

    internal static void UpdateToolbar(
        SwiftView toolbar, ActivityLogState state, SourceUiStyle style)
    {
        var refresh = Find(toolbar, "native.activity-log.refresh");
        if (refresh is not null)
            refresh.IsEnabled = state.Phase is not (
                ActivityLogPhase.Loading or ActivityLogPhase.Blocked);
        var color = style.Appearance.SecondaryText;
        Find(toolbar, "native.activity-log.filters.icon")
            ?.WithForegroundColor(color.Light, color.Dark);
        foreach (var (id, category) in new[]
        {
            ("all", (ActivityCategory?)null),
            ("input", (ActivityCategory?)ActivityCategory.Input),
            ("output", (ActivityCategory?)ActivityCategory.Output)
        })
        {
            Find(toolbar, $"native.activity-log.filter.{id}")
                ?.WithAccessibilitySelected(state.Filter == category);
            if (Find(toolbar, $"native.activity-log.filter.{id}.label")
                is { } row)
            {
                var previousIndicator = row.Children[0];
                var label = row.Children[1];
                row.Clear();
                previousIndicator.Dispose();
                row.Add(Views.Image(state.Filter == category
                        ? "checkmark.circle.fill" : "circle")
                    .WithAccessibilityIdentifier(
                        $"native.activity-log.filter.{id}.check"));
                row.Add(label);
            }
        }
        var menu = Find(toolbar, "native.activity-log.filters");
        if (menu is not null)
            menu.IsEnabled = state.Phase != ActivityLogPhase.Blocked;
    }

    private static SwiftView? Find(SwiftView view, string id)
    {
        if (view.AccessibilityIdentifier == id)
            return view;
        foreach (var child in view.Children)
            if (Find(child, id) is { } found)
                return found;
        return null;
    }

    private static SwiftView Legend(
        SourceUiStyle style, Func<string, string> text)
    {
        var input = style.Appearance.Accent;
        var output = style.Appearance.Warning;
        var row = Views.HStack(
            Views.Circle(12, input.Light, input.Dark),
            style.Text(Views.Text(text("ActivityLog_Input")),
                SourceTextRole.Caption1),
            Views.Circle(12, output.Light, output.Dark),
            style.Text(Views.Text(text("ActivityLog_Output")),
                SourceTextRole.Caption1))
            .WithAccessibilityIdentifier("native.activity-log.legend");
        row.Spacing = SourceUiLayout.ItemGap;
        return row;
    }

    private static SwiftView Week(
        ActivityLogWeek week, DateTime? selectedDay,
        SourceUiStyle style, Func<string, string> text,
        Action<DateTime> selectDay)
    {
        var culture = CultureInfo.CurrentUICulture;
        var range = ActivityLogWeekHeader.Format(week, culture);
        var minutes = string.Format(culture,
            text("ActivityLog_WeekMinutes"), week.TotalMinutes);
        var header = Views.HStack(
            style.Text(Views.Text(range), SourceTextRole.Body1Bold)
                .WithFlexibleWidth(),
            style.Text(Views.Text(minutes), SourceTextRole.Caption1,
                SourceTextTone.Secondary));
        header.Spacing = SourceUiLayout.ItemGap;
        var days = Views.HStack(week.Days.Select(day =>
            Day(day, selectedDay?.Date == day.Date.Date,
                style, text, selectDay)).ToArray())
            .WithFlexibleWidth();
        days.Spacing = 2;
        var sections = new List<SwiftView> { header, days };
        if (selectedDay is { } selected
            && week.Days.FirstOrDefault(day =>
                day.Date.Date == selected.Date) is { } detail)
            sections.Add(Detail(detail, style, text));
        var content = Views.VStack(sections.ToArray())
            .WithPadding(SourceUiLayout.ItemGap)
            .WithFlexibleWidth();
        content.Spacing = SourceUiLayout.ItemGap;
        return style.Card(content)
            .WithAccessibilityIdentifier(
                $"native.activity-log.week.{week.WeekStart:yyyyMMdd}");
    }

    private static SwiftView Day(
        ActivityLogDay day, bool selected, SourceUiStyle style,
        Func<string, string> text, Action<DateTime> selectDay)
    {
        var culture = CultureInfo.CurrentUICulture;
        var weekday = day.Date.ToString("ddd", culture);
        var abbreviation = style.ViewportWidth < 768
            ? weekday[..1] : weekday;
        var name = style.Text(Views.Text(abbreviation),
            SourceTextRole.Caption1, SourceTextTone.Secondary);
        SwiftView symbol;
        var hasRecords = day.Plans.Any(plan => plan.Items.Count > 0);
        if (!hasRecords)
        {
            var placeholder = style.ViewportWidth < 768
                ? Views.Text(" ") : style.Text(
                    Views.Text(text("ActivityLog_Rest")),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                    .WithOpacity(0.4);
            symbol = placeholder.WithMinimumHeight(
                SourceUiLayout.MinimumTouchHeight);
        }
        else
        {
            var count = day.Plans.Sum(plan => plan.CompletedCount);
            var total = day.Plans.Sum(plan => plan.TotalCount);
            SwiftView buttonLabel;
            if (day.TotalMinutes <= 0)
            {
                buttonLabel = style.Text(Views.Text($"{count}/{total}"),
                    SourceTextRole.Caption1, day.AllPlansCompleted
                        ? SourceTextTone.Success : SourceTextTone.Secondary);
            }
            else
            {
                var size = day.TotalMinutes < 10 ? 12 :
                    day.TotalMinutes < 25 ? 20 : 28;
                var input = style.Appearance.Accent;
                var output = style.Appearance.Warning;
                SwiftView dot;
                if (day.HasInput && day.HasOutput)
                {
                    dot = Views.HStack(
                        Views.Circle(size / 2, input.Light, input.Dark),
                        Views.Circle(size / 2, output.Light, output.Dark));
                    dot.Spacing = 0;
                    if (day.AllPlansCompleted)
                        dot = Views.HStack(dot,
                            Views.Image("checkmark.circle.fill")
                                .WithForegroundColor(style.Appearance.Success.Light,
                                    style.Appearance.Success.Dark)
                                .WithFixedSymbolSize(12));
                }
                else
                {
                    var color = day.HasInput ? input : output;
                    var diameter = size + (day.AllPlansCompleted ? 4 : 0);
                    dot = Views.Circle(diameter, color.Light, color.Dark);
                    if (day.AllPlansCompleted)
                        dot.WithRoundedSurface(color.Light, color.Dark,
                            diameter / 2, style.Appearance.Success.Light,
                            style.Appearance.Success.Dark, 2);
                }
                buttonLabel = dot;
            }
            var categories = day.HasInput && day.HasOutput
                ? $"{text("ActivityLog_Input")}, {text("ActivityLog_Output")}"
                : text(day.HasInput ? "ActivityLog_Input" : "ActivityLog_Output");
            var accessible = $"{day.Date.ToString("ddd, MMM d", culture)}, "
                + $"{day.TotalMinutes} {text("Common_MinutesShort")}, "
                + $"{categories}, {count}/{total} {text("PlanSummary_Completed")}";
            symbol = Views.Button(buttonLabel, () => selectDay(day.Date))
                .WithButtonStyle(SwiftButtonStyle.Plain)
                .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
                .WithFlexibleWidth()
                .WithAccessibilityLabel(accessible)
                .WithAccessibilitySelected(selected)
                .WithAccessibilityIdentifier(
                    $"native.activity-log.day.{day.Date:yyyyMMdd}");
        }
        var cell = Views.VStack(name, symbol).WithFlexibleWidth();
        cell.Spacing = 4;
        return cell;
    }

    private static SwiftView Detail(
        ActivityLogDay day, SourceUiStyle style, Func<string, string> text)
    {
        var culture = CultureInfo.CurrentUICulture;
        var count = day.Plans.Sum(plan => plan.CompletedCount);
        var total = day.Plans.Sum(plan => plan.TotalCount);
        var header = Views.HStack(
            style.Text(Views.Text(day.Date.ToString("ddd, MMM d", culture)),
                    SourceTextRole.Body1Bold)
                .WithFlexibleWidth(),
            style.Text(Views.Text(
                    $"{count}/{total} {text("PlanSummary_Completed")}"),
                SourceTextRole.Caption1, day.AllPlansCompleted
                    ? SourceTextTone.Success : SourceTextTone.Secondary));
        header.Spacing = SourceUiLayout.ItemGap;
        var sections = new List<SwiftView>
        {
            Views.Divider(), header
        };
        var planNumber = 1;
        var planOrdinal = 0;
        foreach (var plan in day.Plans)
        {
            var isAdHoc = plan.Items.Count > 0 && plan.Items.All(item =>
                item.PlanItemId.StartsWith("adhoc-", StringComparison.Ordinal));
            if (day.Plans.Count > 1 || isAdHoc)
            {
                var title = isAdHoc
                    ? text("PlanSummary_Freeform")
                    : $"{text("PlanSummary_Plan")} {planNumber}";
                sections.Add(style.Text(Views.Text(
                        $"{title} ({plan.GeneratedAt.ToLocalTime().ToString("h:mm tt", culture)})"),
                    SourceTextRole.Caption1, SourceTextTone.Secondary));
            }
            for (var entryOrdinal = 0; entryOrdinal < plan.Items.Count; entryOrdinal++)
                sections.Add(Entry(plan.Items[entryOrdinal], isAdHoc,
                    planOrdinal, entryOrdinal, day.Date, style, text));
            var totalRow = Views.HStack(
                style.Text(Views.Text(text("PlanSummary_TotalTime")),
                        SourceTextRole.Caption1, SourceTextTone.Secondary)
                    .WithFlexibleWidth(),
                style.Text(Views.Text(
                        $"{plan.TotalMinutesSpent} {text("Common_MinutesShort")}"),
                    SourceTextRole.Caption1));
            sections.Add(Views.Divider());
            sections.Add(totalRow);
            if (!isAdHoc)
                planNumber++;
            planOrdinal++;
        }
        var content = Views.VStack(sections.ToArray())
            .WithAccessibilityIdentifier(
                $"native.activity-log.detail.{day.Date:yyyyMMdd}");
        content.Spacing = SourceUiLayout.ItemGap;
        return content;
    }

    private static SwiftView Entry(
        ActivityLogEntry entry, bool isAdHoc,
        int planOrdinal, int entryOrdinal, DateTime date,
        SourceUiStyle style, Func<string, string> text)
    {
        var color = entry.Category == ActivityCategory.Input
            ? style.Appearance.Accent : style.Appearance.Warning;
        var caption = string.Join(" \u00b7 ",
            new[] { entry.ResourceTitle, entry.SkillName }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        var name = style.Text(Views.Text(text(ActivityNameKey(entry.ActivityType))),
            SourceTextRole.Body2, entry.IsCompleted
                ? SourceTextTone.Body : SourceTextTone.Secondary);
        var metadata = new List<SwiftView> { name };
        if (caption.Length > 0)
            metadata.Add(style.Text(Views.Text(caption),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithLineLimit(1));
        var labels = Views.VStack(metadata.ToArray()).WithFlexibleWidth();
        labels.Spacing = 2;
        var duration = isAdHoc
            ? $"{entry.MinutesSpent} {text("Common_MinutesShort")}"
            : $"{entry.MinutesSpent}/{entry.EstimatedMinutes} "
                + text("Common_MinutesShort");
        var row = Views.HStack(
            Views.Image(entry.IsCompleted
                ? "checkmark.circle.fill" : "circle")
                .WithForegroundColor(
                    entry.IsCompleted ? style.Appearance.Success.Light
                        : style.Appearance.SecondaryText.Light,
                    entry.IsCompleted ? style.Appearance.Success.Dark
                        : style.Appearance.SecondaryText.Dark),
            Views.Circle(12, color.Light, color.Dark),
            labels,
            style.Text(Views.Text(duration), SourceTextRole.Caption1,
                SourceTextTone.Secondary))
            .WithAccessibilityIdentifier(
                $"native.activity-log.entry.{date:yyyyMMdd}.{planOrdinal}.{entryOrdinal}");
        row.Spacing = SourceUiLayout.ItemGap;
        return row;
    }

    private static string ActivityNameKey(PlanActivityType activity) =>
        activity switch
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
