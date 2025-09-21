using MauiReactor;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Pages.Dashboard;

public partial class PracticeStreakCard : Component
{
    [Prop]
    private List<PracticeHeatPoint> _heatData;

    [Prop]
    private bool _isVisible;

    public override VisualNode Render()
    {
        if (!_isVisible || _heatData?.Any() != true) return ContentView();

        var allDays = _heatData.ToList();
        var currentStreak = CalculateCurrentStreak(allDays);
        var maxStreak = CalculateMaxStreak(allDays);
        var totalPractice = allDays.Sum(d => d.Count);

        // Generate GitHub-style calendar for last year (52 weeks)
        var calendarData = GenerateCalendarData(allDays, 52);

        return
            VStack(
                Label($"{totalPractice} practices in the last year")
                    .ThemeKey(MyTheme.H2),

                HScrollView(
                    // GitHub-style contribution calendar
                    VStack(
                        // Month labels
                        HStack(
                            Label("").WidthRequest(15), // Space for day labels
                            HStack(
                                GetMonthLabels(calendarData).Select(month =>
                                    Label(month.Label)
                                        .FontSize(10)
                                        .TextColor(Colors.Gray)
                                        .WidthRequest(month.Width * 13) // 11px squares + 2px spacing
                                        .HorizontalTextAlignment(TextAlignment.Start)
                                ).ToArray()
                            ).Spacing(0)
                        ).Margin(0, 0, 0, 4),

                        // Main calendar grid with day labels
                        HStack(
                            // Day of week labels
                            VStack(
                                Label("").HeightRequest(11), // Sun (hidden)
                                Label("Mon").FontSize(9).TextColor(Colors.Gray).HeightRequest(11),
                                Label("").HeightRequest(11), // Tue (hidden)
                                Label("Wed").FontSize(9).TextColor(Colors.Gray).HeightRequest(11),
                                Label("").HeightRequest(11), // Thu (hidden)
                                Label("Fri").FontSize(9).TextColor(Colors.Gray).HeightRequest(11),
                                Label("").HeightRequest(11)  // Sat (hidden)
                            ).Spacing(2).WidthRequest(15),

                            // Calendar squares grid
                            HStack(
                                calendarData.Select(week =>
                                    VStack(
                                        week.Select(day =>
                                            Border()
                                                .WidthRequest(11)
                                                .HeightRequest(11)
                                                .Padding(0)
                                                .StrokeShape(Rectangle())
                                                .Background(GetGitHubColor(day?.Count ?? 0))
                                                .StrokeThickness(1)
                                                .Stroke(Color.FromRgba(0, 0, 0, 30)) // Very light border
                                                .Set(ToolTipProperties.TextProperty, day.Date.ToShortDateString())
                                        ).ToArray()
                                    ).Spacing(2)
                                ).ToArray()
                            ).Spacing(2)
                        ).Spacing(0)
                    )
                ),

                HStack(
                    Label("Less").FontSize(10).TextColor(Colors.Gray).VCenter(),
                    HStack(
                        Border().WidthRequest(11).HeightRequest(11).StrokeShape(Rectangle()).Padding(0)
                            .Background(GetGitHubColor(0)).StrokeThickness(1).Stroke(Color.FromRgba(0, 0, 0, 30)),
                        Border().WidthRequest(11).HeightRequest(11).StrokeShape(Rectangle()).Padding(0)
                            .Background(GetGitHubColor(1)).StrokeThickness(1).Stroke(Color.FromRgba(0, 0, 0, 30)),
                        Border().WidthRequest(11).HeightRequest(11).StrokeShape(Rectangle()).Padding(0)
                            .Background(GetGitHubColor(2)).StrokeThickness(1).Stroke(Color.FromRgba(0, 0, 0, 30)),
                        Border().WidthRequest(11).HeightRequest(11).StrokeShape(Rectangle()).Padding(0)
                            .Background(GetGitHubColor(4)).StrokeThickness(1).Stroke(Color.FromRgba(0, 0, 0, 30)),
                        Border().WidthRequest(11).HeightRequest(11).StrokeShape(Rectangle()).Padding(0)
                            .Background(GetGitHubColor(5)).StrokeThickness(1).Stroke(Color.FromRgba(0, 0, 0, 30))
                    ).Spacing(2).VCenter(),
                    Label("More").FontSize(10).TextColor(Colors.Gray).VCenter()
                ).Spacing(4).HEnd()
            ).Spacing(15).Padding(16);
    }

    private int CalculateCurrentStreak(List<PracticeHeatPoint> allData)
    {
        var today = DateTime.Now.Date;
        int streak = 0;

        for (int i = 0; i < 365; i++) // Check last 365 days
        {
            var checkDate = today.AddDays(-i);
            var dayData = allData.FirstOrDefault(d => d.Date.Date == checkDate);

            if (dayData?.Count > 0)
                streak++;
            else if (i > 0) // Don't break on today if no activity
                break;
        }
        return streak;
    }

    private int CalculateMaxStreak(List<PracticeHeatPoint> allData)
    {
        int maxStreak = 0;
        int currentStreak = 0;
        var dataDict = allData.ToDictionary(d => d.Date.Date, d => d.Count);

        // Check last 365 days
        for (int i = 364; i >= 0; i--)
        {
            var checkDate = DateTime.Now.Date.AddDays(-i);
            if (dataDict.GetValueOrDefault(checkDate, 0) > 0)
            {
                currentStreak++;
                maxStreak = Math.Max(maxStreak, currentStreak);
            }
            else
            {
                currentStreak = 0;
            }
        }

        return maxStreak;
    }

    private List<List<PracticeHeatPoint?>> GenerateCalendarData(List<PracticeHeatPoint> allData, int weeks)
    {
        var endDate = DateTime.Now.Date;
        // Compute the first day such that:
        // - We show exactly `weeks` columns
        // - The last column ends on `endDate` (today) and is partial (Sun..today)
        // - Columns start on Sundays (GitHub style)
        // Example: if today is Wed (3), subtract 51 weeks + 3 days to land on a Sunday
        int daysFromSunday = (int)endDate.DayOfWeek; // Sunday=0, Monday=1, ...
        var startDate = endDate.AddDays(-(((weeks - 1) * 7) + daysFromSunday));

        var calendar = new List<List<PracticeHeatPoint?>>();
        var dataDict = allData.ToDictionary(d => d.Date.Date, d => d);

        // Add some sample data for demonstration if no real data exists
        if (!allData.Any())
        {
            // Add some sample practice days to show color variation
            var sampleDates = new[]
            {
                endDate.AddDays(-7),  // 1 practice
                endDate.AddDays(-14), // 2 practices
                endDate.AddDays(-21), // 3 practices
                endDate.AddDays(-28), // 4 practices
                endDate.AddDays(-35)  // 5 practices
            };

            for (int i = 0; i < sampleDates.Length; i++)
            {
                dataDict[sampleDates[i]] = new PracticeHeatPoint(sampleDates[i], i + 1);
            }
        }

        // Generate weeks
        for (int week = 0; week < weeks; week++)
        {
            var weekData = new List<PracticeHeatPoint?>();

            // Generate days for this week (Sunday to Saturday)
            for (int day = 0; day < 7; day++)
            {
                var currentDate = startDate.AddDays(week * 7 + day);

                // Do not render days after today in the current (rightmost) column
                if (currentDate > endDate)
                {
                    break;
                }
                dataDict.TryGetValue(currentDate, out var dayData);
                weekData.Add(dayData);
            }

            calendar.Add(weekData);
        }

        return calendar;
    }

    private List<(string Label, int Width)> GetMonthLabels(List<List<PracticeHeatPoint?>> calendarData)
    {
        var labels = new List<(string Label, int Width)>();
        string? lastMonth = null;
        int weeksSinceLastLabel = 0;

        for (int week = 0; week < calendarData.Count; week++)
        {
            var firstDay = calendarData[week].FirstOrDefault(d => d != null);
            if (firstDay == null && week < calendarData.Count - 1)
            {
                // If no data for this week, use the date we would have generated
                var endDate = DateTime.Now.Date;
                int daysFromSunday = (int)endDate.DayOfWeek;
                int weeks = calendarData.Count; // reflect current grid
                var startDate = endDate.AddDays(-(((weeks - 1) * 7) + daysFromSunday));
                firstDay = new PracticeHeatPoint(startDate.AddDays(week * 7), 0);
            }

            if (firstDay != null)
            {
                var month = firstDay.Date.ToString("MMM");
                if (month != lastMonth && (weeksSinceLastLabel >= 3 || lastMonth == null)) // Show if different month and enough space
                {
                    labels.Add((month, weeksSinceLastLabel + 1));
                    lastMonth = month;
                    weeksSinceLastLabel = 0;
                }
                else
                {
                    weeksSinceLastLabel++;
                }
            }
        }

        return labels;
    }

    private Color GetGitHubColor(int count)
    {
        // GitHub contribution graph colors (proper progression)
        return count switch
        {
            0 => Color.FromRgba(235, 237, 240, 255), // #ebedf0 - No contributions (light gray)
            1 => Color.FromRgba(155, 233, 168, 255), // #9be9a8 - Low contributions (light green)
            2 => Color.FromRgba(64, 196, 99, 255),   // #40c463 - Medium-low contributions (medium green)
            3 => Color.FromRgba(48, 161, 78, 255),   // #30a14e - Medium contributions (darker green)
            4 => Color.FromRgba(33, 110, 57, 255),   // #216e39 - High contributions (dark green)
            _ => Color.FromRgba(22, 77, 40, 255)     // #164d28 - Very high contributions (darkest green)
        };
    }
}