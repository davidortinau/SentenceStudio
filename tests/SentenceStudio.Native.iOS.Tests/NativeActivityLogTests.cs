using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.iOS.Features.ActivityLog;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeActivityLogTests
{
    private const string OwnerA = "activity-owner-a";
    private const string OwnerB = "activity-owner-b";
    private static readonly DateTime Now =
        new(2026, 10, 8, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ExistingPlanAndAdHocHistoryReadsOwnerOnly_WithoutAnyDatabaseWrites()
    {
        using var fixture = new HistorySqlite();
        await fixture.SeedAsync();
        var beforeReads = await fixture.TotalChangesAsync();
        fixture.Scope.SetActiveUser(OwnerA);
        using var coordinator = fixture.Coordinator();

        Assert.True(await coordinator.OpenAsync(OwnerA));
        var state = coordinator.State;
        Assert.Equal(ActivityLogPhase.Ready, state.Phase);
        Assert.Equal(8, state.WeeksLoaded);
        Assert.True(state.HasMore);
        Assert.True(state.Weeks.Zip(state.Weeks.Skip(1))
            .All(pair => pair.First.WeekStart > pair.Second.WeekStart));
        var days = state.Weeks.SelectMany(week => week.Days)
            .Where(day => day.Plans.Count > 0).ToArray();
        Assert.Equal([new DateTime(2026, 10, 7), new DateTime(2026, 10, 8)],
            days.Select(day => day.Date.Date).Order().ToArray());
        var oct7 = days.Single(day => day.Date.Day == 7);
        Assert.Equal(10, oct7.TotalMinutes);
        Assert.Equal(2, oct7.Plans.Count);
        Assert.True(oct7.HasInput);
        Assert.True(oct7.HasOutput);
        Assert.False(oct7.AllPlansCompleted);
        Assert.Equal(2, oct7.Plans[0].Items.Count);
        Assert.Equal(1, oct7.Plans[0].CompletedCount);
        Assert.Equal(8, oct7.Plans[0].TotalMinutesSpent);
        var own = oct7.Plans[0].Items.Single(item =>
            item.PlanItemId == "owned-reading");
        Assert.Equal("Own resource", own.ResourceTitle);
        Assert.Equal("Own skill", own.SkillName);
        Assert.True(own.IsCompleted);
        var archived = Assert.Single(oct7.Plans[1].Items);
        Assert.Equal("archived-reading", archived.PlanItemId);
        Assert.Equal(2, archived.MinutesSpent);
        Assert.False(archived.IsCompleted);
        Assert.Null(archived.ResourceTitle);
        Assert.Null(archived.SkillName);
        var oct8 = days.Single(day => day.Date.Day == 8);
        Assert.True(oct8.AllPlansCompleted);
        Assert.StartsWith("adhoc-", Assert.Single(
            Assert.Single(oct8.Plans).Items).PlanItemId);
        Assert.Equal(20, days.Sum(day => day.TotalMinutes));
        Assert.DoesNotContain(state.Weeks.SelectMany(week => week.Days)
            .SelectMany(day => day.Plans).SelectMany(plan => plan.Items),
            entry => entry.MinutesSpent == 99);

        coordinator.ToggleDay(oct7.Date);
        Assert.Equal(oct7.Date, coordinator.State.SelectedDay);
        coordinator.ToggleDay(oct7.Date);
        Assert.Null(coordinator.State.SelectedDay);
        await coordinator.FilterAsync(ActivityCategory.Output);
        Assert.Equal(ActivityCategory.Output, coordinator.State.Filter);
        Assert.Equal(3, coordinator.State.Weeks.SelectMany(week => week.Days)
            .Sum(day => day.TotalMinutes));
        Assert.False(coordinator.State.HasMore);
        Assert.Null(coordinator.State.SelectedDay);
        await coordinator.FilterAsync(ActivityCategory.Input);
        Assert.Equal(17, coordinator.State.Weeks.SelectMany(week => week.Days)
            .Sum(day => day.TotalMinutes));
        await coordinator.FilterAsync(null);
        await coordinator.LoadMoreAsync();
        Assert.Equal(12, coordinator.State.WeeksLoaded);
        Assert.False(coordinator.State.HasMore);
        Assert.Contains(coordinator.State.Weeks.SelectMany(week => week.Days),
            day => day.Date.Month == 8 && day.Date.Day == 1
                && day.TotalMinutes == 6);
        await coordinator.RefreshAsync();
        Assert.Equal(8, coordinator.State.WeeksLoaded);
        Assert.True(coordinator.State.HasMore);
        coordinator.Close();
        Assert.Equal(ActivityLogPhase.Closed, coordinator.State.Phase);
        Assert.Empty(coordinator.State.Weeks);
        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.Equal(20, coordinator.State.Weeks.SelectMany(week => week.Days)
            .Sum(day => day.TotalMinutes));
        Assert.Equal(beforeReads, await fixture.TotalChangesAsync());
    }

    [Fact]
    public async Task ZeroMinuteOwnedHistoryRetainsRealRowsAndOpensBothDaysWithoutChangingTime()
    {
        using var fixture = new HistorySqlite();
        await fixture.SeedAsync(zeroMinutes: true);
        var beforeReads = await fixture.TotalChangesAsync();
        fixture.Scope.SetActiveUser(OwnerA);
        using var coordinator = fixture.Coordinator();

        Assert.True(await coordinator.OpenAsync(OwnerA));
        var state = coordinator.State;
        Assert.Equal(ActivityLogPhase.Ready, state.Phase);
        Assert.True(state.HasMore);
        Assert.All(state.Weeks, week => Assert.Equal(0, week.TotalMinutes));
        var recordDays = state.Weeks.SelectMany(week => week.Days)
            .Where(day => day.Plans.Any(plan => plan.Items.Count > 0))
            .OrderBy(day => day.Date).ToArray();
        Assert.Equal([new DateTime(2026, 10, 7), new DateTime(2026, 10, 8)],
            recordDays.Select(day => day.Date.Date).ToArray());
        var currentWeek = Assert.Single(state.Weeks, week =>
            week.Days.Any(day => day.Date.Date == new DateTime(2026, 10, 7)));
        Assert.Equal(new DateTime(2026, 10, 5), currentWeek.WeekStart.Date);
        Assert.Equal(7, currentWeek.Days.Length);
        Assert.Equal(4, currentWeek.ActivityCount);
        Assert.Equal(0, currentWeek.TotalMinutes);
        Assert.All(recordDays, day =>
        {
            Assert.Equal(0, day.TotalMinutes);
            Assert.Equal(2, day.Plans.Sum(plan => plan.TotalCount));
            Assert.All(day.Plans, plan => Assert.Equal(0, plan.TotalMinutesSpent));
        });

        var oct7 = recordDays[0];
        Assert.True(oct7.HasInput);
        Assert.True(oct7.HasOutput);
        Assert.False(oct7.AllPlansCompleted);
        Assert.Equal(1, Assert.Single(oct7.Plans).CompletedCount);
        var completed = oct7.Plans[0].Items.Single(entry =>
            entry.PlanItemId == "owned-vocabulary");
        Assert.Equal(PlanActivityType.VocabularyReview, completed.ActivityType);
        Assert.Equal(ActivityCategory.Input, completed.Category);
        Assert.True(completed.IsCompleted);
        Assert.Equal(0, completed.MinutesSpent);
        Assert.Equal(15, completed.EstimatedMinutes);
        Assert.Equal("Own resource", completed.ResourceTitle);
        Assert.Equal("Own skill", completed.SkillName);
        var partial = oct7.Plans[0].Items.Single(entry =>
            entry.PlanItemId == "partial-writing");
        Assert.Equal(ActivityCategory.Output, partial.Category);
        Assert.False(partial.IsCompleted);
        Assert.Equal(0, partial.MinutesSpent);
        Assert.Null(partial.ResourceTitle);
        Assert.Null(partial.SkillName);

        var oct8 = recordDays[1];
        Assert.Equal(2, oct8.Plans.Count);
        var archived = oct8.Plans.SelectMany(plan => plan.Items)
            .Single(entry => entry.PlanItemId == "archived-cloze");
        Assert.True((await fixture.CompletionAsync(archived.PlanItemId)).IsArchived);
        Assert.Equal(ActivityCategory.Input, archived.Category);
        Assert.False(archived.IsCompleted);
        Assert.Equal(0, archived.MinutesSpent);
        Assert.Equal("Own resource", archived.ResourceTitle);
        var adHoc = oct8.Plans.SelectMany(plan => plan.Items)
            .Single(entry => entry.PlanItemId == "adhoc-oct8");
        Assert.Equal(0, adHoc.MinutesSpent);
        Assert.True(adHoc.IsCompleted);
        Assert.DoesNotContain(recordDays.SelectMany(day => day.Plans)
            .SelectMany(plan => plan.Items), entry =>
                entry.PlanItemId.StartsWith("foreign-", StringComparison.Ordinal));

        var noRecordDay = state.Weeks.SelectMany(week => week.Days)
            .Single(day => day.Date.Date == new DateTime(2026, 10, 6));
        Assert.Empty(noRecordDay.Plans);
        coordinator.ToggleDay(noRecordDay.Date);
        Assert.Null(coordinator.State.SelectedDay);
        coordinator.ToggleDay(oct7.Date);
        Assert.Equal(oct7.Date, coordinator.State.SelectedDay);
        coordinator.ToggleDay(oct8.Date);
        Assert.Equal(oct8.Date, coordinator.State.SelectedDay);
        coordinator.ToggleDay(oct8.Date);
        Assert.Null(coordinator.State.SelectedDay);

        coordinator.Close();
        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.Equal(4, coordinator.State.Weeks.SelectMany(week => week.Days)
            .SelectMany(day => day.Plans).Sum(plan => plan.Items.Count));
        Assert.Equal(beforeReads, await fixture.TotalChangesAsync());
    }

    [Fact]
    public async Task ZeroMinuteFiltersAndLoadMoreUseOwnedCategoryRowsNotPositiveTotals()
    {
        using var fixture = new HistorySqlite();
        await fixture.SeedAsync(zeroMinutes: true);
        var beforeReads = await fixture.TotalChangesAsync();
        fixture.Scope.SetActiveUser(OwnerA);
        using var coordinator = fixture.Coordinator();
        Assert.True(await coordinator.OpenAsync(OwnerA));

        await coordinator.FilterAsync(ActivityCategory.Output);
        var output = coordinator.State;
        Assert.Equal(ActivityLogPhase.Ready, output.Phase);
        Assert.Equal(0, output.Weeks.Sum(week => week.TotalMinutes));
        Assert.False(output.HasMore);
        var outputRows = output.Weeks.SelectMany(week => week.Days)
            .SelectMany(day => day.Plans).SelectMany(plan => plan.Items).ToArray();
        Assert.Equal("partial-writing", Assert.Single(outputRows).PlanItemId);
        Assert.All(outputRows, row => Assert.Equal(ActivityCategory.Output, row.Category));
        coordinator.ToggleDay(new DateTime(2026, 10, 8));
        Assert.Null(coordinator.State.SelectedDay);
        coordinator.ToggleDay(new DateTime(2026, 10, 7));
        Assert.Equal(new DateTime(2026, 10, 7), coordinator.State.SelectedDay);

        await coordinator.FilterAsync(ActivityCategory.Input);
        var input = coordinator.State;
        Assert.Null(input.SelectedDay);
        Assert.Equal(ActivityLogPhase.Ready, input.Phase);
        Assert.Equal(0, input.Weeks.Sum(week => week.TotalMinutes));
        Assert.True(input.HasMore);
        var inputRows = input.Weeks.SelectMany(week => week.Days)
            .SelectMany(day => day.Plans).SelectMany(plan => plan.Items).ToArray();
        Assert.Equal(3, inputRows.Length);
        Assert.All(inputRows, row => Assert.Equal(ActivityCategory.Input, row.Category));
        Assert.DoesNotContain(inputRows, row =>
            row.PlanItemId.StartsWith("foreign-", StringComparison.Ordinal));
        coordinator.ToggleDay(new DateTime(2026, 10, 8));
        Assert.Equal(new DateTime(2026, 10, 8), coordinator.State.SelectedDay);
        await coordinator.LoadMoreAsync();
        Assert.Equal(12, coordinator.State.WeeksLoaded);
        Assert.False(coordinator.State.HasMore);
        Assert.Contains(coordinator.State.Weeks.SelectMany(week => week.Days),
            day => day.Date.Month == 8 && day.Date.Day == 1
                && day.TotalMinutes == 0 && day.Plans.Count == 1);
        await coordinator.RefreshAsync();
        Assert.Equal(ActivityCategory.Input, coordinator.State.Filter);
        Assert.Equal(8, coordinator.State.WeeksLoaded);
        Assert.True(coordinator.State.HasMore);
        Assert.Null(coordinator.State.SelectedDay);

        fixture.Scope.SetActiveUser(OwnerB);
        Assert.Equal(ActivityLogPhase.Blocked, coordinator.State.Phase);
        Assert.Empty(coordinator.State.Weeks);
        coordinator.Close();
        Assert.True(await coordinator.OpenAsync(OwnerB));
        var foreignRows = coordinator.State.Weeks.SelectMany(week => week.Days)
            .SelectMany(day => day.Plans).SelectMany(plan => plan.Items).ToArray();
        Assert.Equal(2, foreignRows.Length);
        Assert.All(foreignRows, row =>
        {
            Assert.StartsWith("foreign-", row.PlanItemId);
            Assert.Equal(0, row.MinutesSpent);
        });
        await coordinator.FilterAsync(ActivityCategory.Input);
        Assert.Equal("foreign-cloze", Assert.Single(coordinator.State.Weeks
            .SelectMany(week => week.Days).SelectMany(day => day.Plans)
            .SelectMany(plan => plan.Items)).PlanItemId);
        fixture.Scope.SetActiveUser(null);
        Assert.Equal(ActivityLogPhase.Blocked, coordinator.State.Phase);
        Assert.Empty(coordinator.State.Weeks);
        Assert.Equal(beforeReads, await fixture.TotalChangesAsync());
    }

    [Fact]
    public void ZeroMinuteDayViewChecksForRecordsBeforeRenderingRestOrStatus()
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS/Features/ActivityLog/ActivityLogView.cs"));
        var view = File.ReadAllText(path);
        Assert.Contains("day.Plans.Any(plan => plan.Items.Count > 0)", view);
        Assert.Contains("if (!hasRecords)", view);
        Assert.Contains("Views.Text($\"{count}/{total}\")", view);
        Assert.Contains("ActivityLog_Rest", view);
        Assert.Contains("native.activity-log.day.{day.Date:yyyyMMdd}", view);
    }

    [Fact]
    public async Task WeekHeadingUsesTheSameMondayToSundayDatesAsItsSevenSqliteCells()
    {
        using var fixture = new HistorySqlite();
        await fixture.SeedAsync(zeroMinutes: true);
        var weeks = (await fixture.Reader.ReadAsync(OwnerA, Now, 8, null)).Weeks;
        var english = CultureInfo.GetCultureInfo("en-US");
        var current = Assert.Single(weeks, week =>
            week.WeekStart.Date == new DateTime(2026, 10, 5));
        Assert.Equal(new DateTime(2026, 10, 5), current.Days[0].Date.Date);
        Assert.Equal(new DateTime(2026, 10, 11), current.Days[^1].Date.Date);
        Assert.Equal("Oct 5 \u2013 11",
            ActivityLogWeekHeader.Format(current, english));
        var prior = Assert.Single(weeks, week =>
            week.WeekStart.Date == new DateTime(2026, 9, 28));
        Assert.Equal("Sep 28 \u2013 Oct 4",
            ActivityLogWeekHeader.Format(prior, english));
        var korean = ActivityLogWeekHeader.Format(current,
            CultureInfo.GetCultureInfo("ko-KR"));
        Assert.DoesNotContain("2026", korean);
        Assert.EndsWith("11", korean);
    }

    [Fact]
    public async Task EmptyRecentCategoryStillLoadsOlderMatchingZeroMinuteHistory()
    {
        using var fixture = new HistorySqlite();
        await fixture.SeedAsync(zeroMinutes: true);
        await fixture.SeedCategoryGapAsync();
        var beforeReads = await fixture.TotalChangesAsync();
        fixture.Scope.SetActiveUser(OwnerA);
        using var coordinator = fixture.Coordinator();
        Assert.True(await coordinator.OpenAsync(OwnerA));

        await coordinator.FilterAsync(ActivityCategory.Output);
        var recent = coordinator.State;
        Assert.Equal(ActivityLogPhase.Ready, recent.Phase);
        Assert.NotEmpty(recent.Weeks);
        Assert.All(recent.Weeks, week =>
        {
            Assert.Equal(7, week.Days.Length);
            Assert.Equal(0, week.ActivityCount);
            Assert.All(week.Days, day => Assert.Empty(day.Plans));
        });
        Assert.True(recent.HasMore);
        coordinator.ToggleDay(new DateTime(2026, 10, 7));
        Assert.Null(coordinator.State.SelectedDay);
        await coordinator.LoadMoreAsync();
        Assert.Equal(ActivityLogPhase.Ready, coordinator.State.Phase);
        Assert.Equal(12, coordinator.State.WeeksLoaded);
        Assert.False(coordinator.State.HasMore);
        var older = Assert.Single(coordinator.State.Weeks
            .SelectMany(week => week.Days).SelectMany(day => day.Plans)
            .SelectMany(plan => plan.Items));
        Assert.Equal("older-writing", older.PlanItemId);
        Assert.Equal(ActivityCategory.Output, older.Category);
        Assert.Equal(0, older.MinutesSpent);
        coordinator.ToggleDay(new DateTime(2026, 8, 1));
        Assert.Equal(new DateTime(2026, 8, 1), coordinator.State.SelectedDay);
        await coordinator.RefreshAsync();
        Assert.Equal(ActivityCategory.Output, coordinator.State.Filter);
        Assert.Equal(ActivityLogPhase.Ready, coordinator.State.Phase);
        Assert.NotEmpty(coordinator.State.Weeks);
        Assert.All(coordinator.State.Weeks, week =>
            Assert.Equal(0, week.ActivityCount));
        Assert.Equal(8, coordinator.State.WeeksLoaded);
        Assert.True(coordinator.State.HasMore);
        Assert.Null(coordinator.State.SelectedDay);
        Assert.Equal(beforeReads, await fixture.TotalChangesAsync());
    }

    [Fact]
    public void NativeSourceCompositionKeepsOverflowSelectionAndUniformDayCells()
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));
        var view = File.ReadAllText(Path.Combine(root,
            "Features/ActivityLog/ActivityLogView.cs"));
        var scene = File.ReadAllText(Path.Combine(root,
            "App/ApplicationScene.ActivityLog.cs"));
        Assert.Contains("Views.Menu(icon, buttons)", view);
        Assert.Contains("Views.Image(\"ellipsis\")", view);
        Assert.DoesNotContain("Views.Image(\"ellipsis.vertical\")", view);
        Assert.Contains("\"checkmark.circle.fill\" : \"circle\"", view);
        Assert.Contains("Views.Image(state.Filter == category", view);
        Assert.Contains("row.Clear()", view);
        Assert.DoesNotContain("indicator.Text =", view);
        Assert.Contains("ActivityLogWeekHeader.Format(week, culture)", view);
        Assert.Contains("placeholder.WithMinimumHeight(", view);
        Assert.Contains("dot.WithRoundedSurface(", view);
        Assert.Contains("Views.HStack(button)", view);
        Assert.Contains("state.Weeks.Count == 0 && !state.HasMore", view);
        Assert.Contains("text(\"ActivityLog_EmptyState\")", view);
        Assert.DoesNotContain("NativeActivityLog_FilterEmpty", view);
        Assert.Contains("ActivityLogView.Filters(state, _style", scene);
    }

    [Theory]
    [InlineData(OwnerB)]
    [InlineData(null)]
    [InlineData(OwnerA)]
    public async Task RevocationImmediatelyClearsMountedHistory_AndRefusesOldOwner(
        string? nextOwner)
    {
        using var fixture = new HistorySqlite();
        await fixture.SeedAsync();
        fixture.Scope.SetActiveUser(OwnerA);
        using var coordinator = fixture.Coordinator();
        Assert.False(await coordinator.OpenAsync(null));
        Assert.False(await coordinator.OpenAsync(OwnerB));
        Assert.Empty((await fixture.Reader.ReadAsync(null, Now, 8, null)).Weeks);
        Assert.True(await coordinator.OpenAsync(OwnerA));
        Assert.Equal(ActivityLogPhase.Ready, coordinator.State.Phase);
        var oct7 = coordinator.State.Weeks
            .SelectMany(week => week.Days).Single(day =>
                day.Date.Month == 10 && day.Date.Day == 7);
        coordinator.ToggleDay(oct7.Date);

        var version = coordinator.ViewVersion;
        var notifications = new List<ActivityLogState>();
        coordinator.Changed += () => notifications.Add(coordinator.State);
        fixture.Scope.SetActiveUser(nextOwner);
        var blocked = Assert.Single(notifications);
        Assert.Equal(ActivityLogPhase.Blocked, blocked.Phase);
        Assert.Empty(blocked.Weeks);
        Assert.Null(blocked.SelectedDay);
        Assert.True(coordinator.ViewVersion > version);
        await coordinator.RefreshAsync();
        coordinator.ToggleDay(oct7.Date);
        Assert.Equal(ActivityLogPhase.Blocked, coordinator.State.Phase);
        Assert.Empty(coordinator.State.Weeks);
        coordinator.Close();
        notifications.Clear();
        fixture.Scope.SetActiveUser(OwnerB);
        Assert.Empty(notifications);
        Assert.True(await coordinator.OpenAsync(OwnerB));
        var onlyB = Assert.Single(coordinator.State.Weeks
            .SelectMany(week => week.Days)
            .SelectMany(day => day.Plans)
            .SelectMany(plan => plan.Items));
        Assert.Equal(99, onlyB.MinutesSpent);
        Assert.Equal("Foreign resource", onlyB.ResourceTitle);
        Assert.Equal("Foreign skill", onlyB.SkillName);
    }

    [Theory]
    [InlineData(OwnerB)]
    [InlineData(null)]
    public async Task InFlightSqliteReadCannotRestoreRevokedOwnersHistory(
        string? nextOwner)
    {
        var blocker = new HoldActivityRead();
        using var fixture = new HistorySqlite(blocker);
        await fixture.SeedAsync();
        fixture.Scope.SetActiveUser(OwnerA);
        using var coordinator = fixture.Coordinator();
        Assert.True(await coordinator.OpenAsync(OwnerA));
        blocker.Arm();
        var pending = coordinator.ReloadAsync();
        try
        {
            await blocker.Started.WaitAsync(TimeSpan.FromSeconds(5));
            var notifications = new List<ActivityLogState>();
            coordinator.Changed += () => notifications.Add(coordinator.State);
            fixture.Scope.SetActiveUser(nextOwner);
            Assert.Equal(ActivityLogPhase.Blocked,
                Assert.Single(notifications).Phase);
            Assert.Empty(coordinator.State.Weeks);
            blocker.Release();
            await pending;
            Assert.Single(notifications);
            Assert.Empty(coordinator.State.Weeks);
        }
        finally
        {
            blocker.Release();
        }
    }

    [Fact]
    public async Task FailedOwnedReadShowsAnError_AndRefreshRetriesWithoutHidingIt()
    {
        var blocker = new HoldActivityRead();
        using var fixture = new HistorySqlite(blocker);
        await fixture.SeedAsync();
        fixture.Scope.SetActiveUser(OwnerA);
        using var coordinator = fixture.Coordinator();
        Assert.True(await coordinator.OpenAsync(OwnerA));
        blocker.ThrowNext();
        await coordinator.RefreshAsync();
        Assert.Equal(ActivityLogPhase.LoadError, coordinator.State.Phase);
        Assert.Empty(coordinator.State.Weeks);
        await coordinator.RefreshAsync();
        Assert.Equal(ActivityLogPhase.Ready, coordinator.State.Phase);
    }

    [Fact]
    public async Task BackCancelsAnInFlightRead_AndReopeningReadsOnlyFreshOwnedRows()
    {
        var blocker = new HoldActivityRead();
        using var fixture = new HistorySqlite(blocker);
        await fixture.SeedAsync();
        fixture.Scope.SetActiveUser(OwnerA);
        using var coordinator = fixture.Coordinator();
        Assert.True(await coordinator.OpenAsync(OwnerA));
        blocker.Arm();
        var pending = coordinator.RefreshAsync();
        try
        {
            await blocker.Started.WaitAsync(TimeSpan.FromSeconds(5));
            coordinator.Close();
            Assert.False(coordinator.IsOpen);
            Assert.Equal(ActivityLogPhase.Closed, coordinator.State.Phase);
            Assert.Empty(coordinator.State.Weeks);
            blocker.Release();
            await pending;
            Assert.Equal(ActivityLogPhase.Closed, coordinator.State.Phase);
            Assert.True(await coordinator.OpenAsync(OwnerA));
            Assert.Equal(ActivityLogPhase.Ready, coordinator.State.Phase);
            Assert.Equal(20, coordinator.State.Weeks
                .SelectMany(week => week.Days).Sum(day => day.TotalMinutes));
        }
        finally
        {
            blocker.Release();
        }
    }

    [Fact]
    public async Task CategoryWithNoMatchingOwnedEntriesShowsBlankWeeksWithoutForeignRows()
    {
        using var fixture = new HistorySqlite();
        await fixture.SeedAsync();
        var beforeReads = await fixture.TotalChangesAsync();
        fixture.Scope.SetActiveUser(OwnerB);
        using var coordinator = fixture.Coordinator();
        Assert.True(await coordinator.OpenAsync(OwnerB));
        Assert.Equal(ActivityLogPhase.Ready, coordinator.State.Phase);
        await coordinator.FilterAsync(ActivityCategory.Input);
        var state = coordinator.State;
        Assert.Equal(ActivityLogPhase.Ready, state.Phase);
        Assert.NotEmpty(state.Weeks);
        Assert.False(state.HasMore);
        Assert.All(state.Weeks, week =>
        {
            Assert.Equal(DayOfWeek.Monday, week.WeekStart.DayOfWeek);
            Assert.Equal(7, week.Days.Length);
            Assert.Equal(0, week.ActivityCount);
            Assert.Equal(0, week.TotalMinutes);
            Assert.All(week.Days, day =>
            {
                Assert.Empty(day.Plans);
                Assert.Equal(0, day.TotalMinutes);
                Assert.False(day.HasInput);
                Assert.False(day.HasOutput);
            });
        });
        coordinator.ToggleDay(new DateTime(2026, 10, 7));
        Assert.Null(coordinator.State.SelectedDay);
        Assert.Equal(beforeReads, await fixture.TotalChangesAsync());
    }

    [Fact]
    public async Task NoOwnerOrNoOwnedCompletionsNeverCreatesBlankWeekCards()
    {
        using var fixture = new HistorySqlite();
        await fixture.SeedAsync(zeroMinutes: true);
        var beforeReads = await fixture.TotalChangesAsync();
        var missing = await fixture.Reader.ReadAsync(
            null, Now, 8, ActivityCategory.Input);
        Assert.Empty(missing.Weeks);
        Assert.False(missing.HasMore);

        const string ownerWithoutHistory = "activity-owner-without-history";
        fixture.Scope.SetActiveUser(ownerWithoutHistory);
        using var coordinator = fixture.Coordinator();
        Assert.True(await coordinator.OpenAsync(ownerWithoutHistory));
        await coordinator.FilterAsync(ActivityCategory.Input);
        Assert.Equal(ActivityLogPhase.Empty, coordinator.State.Phase);
        Assert.Empty(coordinator.State.Weeks);
        Assert.False(coordinator.State.HasMore);
        Assert.Equal(beforeReads, await fixture.TotalChangesAsync());
    }

    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void ActivityLogActionsAndErrorMessagesHaveLocalizedCopy(string resourceFile)
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Shared/Resources/Strings"));
        var keys = XDocument.Load(Path.Combine(root, resourceFile))
            .Descendants("data")
            .Where(node => !string.IsNullOrWhiteSpace(
                node.Element("value")?.Value))
            .Select(node => node.Attribute("name")?.Value)
            .ToHashSet();
        foreach (var key in new[]
        {
            "Nav_Activity", "ActivityLog_Title", "ActivityLog_AllActivities",
            "ActivityLog_InputOnly", "ActivityLog_OutputOnly",
            "ActivityLog_EmptyState", "ActivityLog_Input",
            "ActivityLog_Output", "ActivityLog_Rest",
            "ActivityLog_WeekMinutes", "Common_Refresh",
            "Common_LoadMore", "Common_MinutesShort",
            "PlanSummary_Completed", "PlanSummary_Plan",
            "PlanSummary_Freeform", "PlanSummary_TotalTime",
            "NativeActivityLog_OwnerUnavailable", "NativeActivityLog_LoadError",
            "NativeActivityLog_FilterEmpty"
        })
            Assert.Contains(key, keys);
    }

    private static string CurrentFile([CallerFilePath] string file = "") => file;

    private sealed class HistorySqlite : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly ServiceProvider _services;
        internal DeviceUserScopeProvider Scope { get; } = new();
        internal ActivityLogReader Reader { get; }

        internal HistorySqlite(DbCommandInterceptor? interceptor = null)
        {
            _connection.Open();
            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseSqlite(_connection);
                if (interceptor is not null)
                    options.AddInterceptors(interceptor);
            });
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .Database.EnsureCreated();
            Reader = new(_services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ActivityLogReader>.Instance);
        }

        internal ActivityLogCoordinator Coordinator() =>
            new(Reader, Scope, NullLogger<ActivityLogCoordinator>.Instance,
                () => Now);

        internal async Task SeedAsync(bool zeroMinutes = false)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.UserProfiles.AddRange(
                new UserProfile { Id = OwnerA, Name = "Owner A" },
                new UserProfile { Id = OwnerB, Name = "Owner B" });
            db.LearningResources.AddRange(
                new LearningResource {
                    Id = "resource-a", Title = "Own resource",
                    UserProfileId = OwnerA
                },
                new LearningResource {
                    Id = "resource-b", Title = "Foreign resource",
                    UserProfileId = OwnerB
                });
            db.SkillProfiles.AddRange(
                new SkillProfile {
                    Id = "skill-a", Title = "Own skill",
                    UserProfileId = OwnerA
                },
                new SkillProfile {
                    Id = "skill-b", Title = "Foreign skill",
                    UserProfileId = OwnerB
                });
            if (zeroMinutes)
                db.DailyPlanCompletions.AddRange(
                    Row("owned-vocabulary", OwnerA, 7, "VocabularyReview", 0, true,
                        "resource-a", "skill-a", 8, 0),
                    Row("partial-writing", OwnerA, 7, "Writing", 0, false,
                        "resource-b", "skill-b", 8, 0, seconds: 30),
                    Row("archived-cloze", OwnerA, 8, "Cloze", 0, false,
                        "resource-a", "skill-a", 9, 0, archived: true),
                    Row("adhoc-oct8", OwnerA, 8, "VocabularyReview", 0, true,
                        null, null, 10, 0),
                    Row("old-reading", OwnerA, 1, "Reading", 0, true,
                        null, null, 10, 0, month: 8),
                    Row("foreign-writing", OwnerB, 7, "Writing", 0, true,
                        "resource-b", "skill-b", 8, 0),
                    Row("foreign-cloze", OwnerB, 8, "Cloze", 0, false,
                        "resource-b", "skill-b", 10, 0));
            else
                db.DailyPlanCompletions.AddRange(
                    Row("owned-reading", OwnerA, 7, "Reading", 5, true,
                        "resource-a", "skill-a", 8, 0),
                    Row("partial-writing", OwnerA, 7, "Writing", 3, false,
                        null, null, 8, 0, seconds: 30),
                    Row("archived-reading", OwnerA, 7, "Reading", 2, false,
                        "resource-b", "skill-b", 11, 0, archived: true),
                    Row("adhoc-oct8", OwnerA, 8, "VocabularyReview", 10, true,
                        null, null, 9, 0),
                    Row("old-reading", OwnerA, 1, "Reading", 6, true,
                        null, null, 10, 0, month: 8),
                    Row("other-owned", OwnerB, 7, "Writing", 99, true,
                        "resource-b", "skill-b", 8, 0));
            await db.SaveChangesAsync();
        }

        internal async Task<DailyPlanCompletion> CompletionAsync(string id)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .DailyPlanCompletions.AsNoTracking().SingleAsync(row => row.Id == id);
        }

        internal async Task SeedCategoryGapAsync()
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var current = await db.DailyPlanCompletions.SingleAsync(row =>
                row.Id == "partial-writing" && row.UserProfileId == OwnerA);
            current.ActivityType = "Reading";
            db.DailyPlanCompletions.Add(Row("older-writing", OwnerA, 1, "Writing",
                0, false, null, null, 11, 0, month: 8));
            await db.SaveChangesAsync();
        }

        internal async Task<long> TotalChangesAsync()
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT total_changes();";
            return (long)(await command.ExecuteScalarAsync())!;
        }

        private static DailyPlanCompletion Row(
            string id, string owner, int day, string activity, int minutes,
            bool completed, string? resourceId, string? skillId,
            int hour, int minute, int seconds = 0,
            bool archived = false, int month = 10)
        {
            var date = new DateTime(2026, month, day, 0, 0, 0, DateTimeKind.Utc);
            return new DailyPlanCompletion
            {
                Id = id,
                UserProfileId = owner,
                Date = date,
                PlanItemId = id,
                ActivityType = activity,
                ResourceId = resourceId,
                SkillId = skillId,
                IsArchived = archived,
                IsCompleted = completed,
                MinutesSpent = minutes,
                EstimatedMinutes = 15,
                CompletedAt = completed ? date.AddHours(hour + 1) : null,
                CreatedAt = date.AddHours(hour).AddMinutes(minute)
                    .AddSeconds(seconds),
                UpdatedAt = date.AddHours(hour + 1),
                TitleKey = "unused-title-key"
            };
        }

        public void Dispose()
        {
            _services.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class HoldActivityRead : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _throwNext;

        internal Task Started => _started.Task;
        internal void Arm() => Volatile.Write(ref _armed, 1);
        internal void Release() => _released.TrySetResult();
        internal void ThrowNext() => Volatile.Write(ref _throwNext, 1);

        public override async ValueTask<InterceptionResult<DbDataReader>>
            ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
                InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("DailyPlanCompletion",
                    StringComparison.Ordinal))
                return result;
            if (Interlocked.Exchange(ref _throwNext, 0) == 1)
                throw new InvalidOperationException(
                    "Simulated read-only SQLite query failure.");
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _started.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
