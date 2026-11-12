using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// The Resources list's sort, filter column and phone filter sheet (Captain 2026-11-10: the
/// Resources page follows the Vocabulary list's layout and filter and sort options).
/// </summary>
public sealed class NativeResourceListOptionsTests
{
    private const string A = NativeSliceFixture.UserA;
    /// <summary>The fixture's seeded vocabulary list (added 2026-09-27, never updated).</summary>
    private static readonly string Seed = $"resource-{A}";

    [Fact]
    public async Task Sort_KeepsSmartFirst_DefaultsToUpdatedNewest_AndIsRemembered()
    {
        using var fixture = await SeededAsync();
        var presenter = fixture.Resources;
        var list = await presenter.LoadListAsync();
        // The source order: smart resources (by title), then the most recently updated.
        Assert.Equal((NativeResourceSort.Updated, true), (list.SortField, list.SortDescending));
        Assert.Equal(["smart", "new-edit", "old-new", Seed], Ids(list));

        list = presenter.SetSort(NativeResourceSort.Added);
        Assert.Equal((NativeResourceSort.Added, true), (list.SortField, list.SortDescending));
        Assert.Equal(["smart", "old-new", "new-edit", Seed], Ids(list));
        // The same field again flips the direction; smart resources stay first.
        list = presenter.SetSort(NativeResourceSort.Added);
        Assert.False(list.SortDescending);
        Assert.Equal(["smart", Seed, "new-edit", "old-new"], Ids(list));
        Assert.Equal("added:asc", fixture.Preferences.Get("resources-sort", string.Empty));

        // A search keeps the sort.
        Assert.Equal(["new-edit", "old-new"], Ids(presenter.Search("lesson")));
    }

    [Fact]
    public async Task FilterPanel_HidesAndShows_AndIsRemembered()
    {
        using var fixture = await SeededAsync();
        var presenter = fixture.Resources;
        await presenter.LoadListAsync();
        Assert.False(presenter.List.FilterPanelHidden);
        Assert.True(presenter.ToggleFilterPanel().FilterPanelHidden);
        Assert.Equal("hidden", fixture.Preferences.Get("resources-filter-panel", string.Empty));
        Assert.False(presenter.ToggleFilterPanel().FilterPanelHidden);
    }

    [Fact]
    public async Task Sheet_AppliesTypeLanguageAndSortTogether_CancelKeepsTheList()
    {
        using var fixture = await SeededAsync();
        var presenter = fixture.Resources;
        await presenter.LoadListAsync();

        // Closed sheet: its choices do nothing.
        Assert.Null(presenter.ChangeSheetFilter("Video", null).Sheet);

        var list = presenter.OpenFilterSheet();
        Assert.Equal(new NativeResourceFilterDraft(null, null, NativeResourceSort.Updated, true), list.Sheet);
        list = presenter.ChangeSheetFilter("Video", "Spanish");
        list = presenter.SetSheetSort(NativeResourceSort.Added);
        // The list waits for Apply.
        Assert.Null(list.MediaType);
        Assert.Equal(4, list.Items.Length);
        Assert.Equal(new NativeResourceFilterDraft("Video", "Spanish", NativeResourceSort.Added, true), list.Sheet);

        list = presenter.CancelFilterSheet();
        Assert.Null(list.Sheet);
        Assert.Equal(4, list.Items.Length);
        Assert.Equal(NativeResourceSort.Updated, list.SortField);

        presenter.OpenFilterSheet();
        presenter.ChangeSheetFilter("Video", null);
        presenter.SetSheetSort(NativeResourceSort.Added);
        list = presenter.ApplyFilterSheet();
        Assert.Null(list.Sheet);
        Assert.Equal(("Video", NativeResourceSort.Added), (list.MediaType, list.SortField));
        Assert.True(list.HasActiveFilters);
        Assert.Equal(["old-new", "new-edit"], Ids(list));
        Assert.Throws<ArgumentException>(() =>
        {
            presenter.OpenFilterSheet();
            presenter.ChangeSheetFilter("Film", null);
        });
    }

    [Fact]
    public async Task ClearFilters_ClearsTypeAndLanguage_KeepsSortAndSearch()
    {
        using var fixture = await SeededAsync();
        var presenter = fixture.Resources;
        await presenter.LoadListAsync();
        presenter.SetSort(NativeResourceSort.Added);
        presenter.Search("lesson");
        presenter.Filter("Video", "Spanish");
        presenter.OpenFilterSheet();

        var list = presenter.ClearFilters();
        Assert.False(list.HasActiveFilters);
        Assert.Equal((null, null), (list.Sheet!.MediaType, list.Sheet.Language));
        Assert.Equal(("lesson", NativeResourceSort.Added), (list.SearchText, list.SortField));
        Assert.Equal(["old-new", "new-edit"], Ids(list));
    }

    [Fact]
    public async Task ClearSearchAndFilters_ClearsBoth_KeepsSort()
    {
        using var fixture = await SeededAsync();
        var presenter = fixture.Resources;
        await presenter.LoadListAsync();
        presenter.SetSort(NativeResourceSort.Added);
        presenter.Search("lesson");
        presenter.Filter("Video", null);

        var list = presenter.ClearSearchAndFilters();
        Assert.Equal((string.Empty, false, NativeResourceSort.Added),
            (list.SearchText, list.HasActiveFilters, list.SortField));
        Assert.Equal(4, list.Items.Length);
    }

    private static string[] Ids(NativeResourceListState list) =>
        list.Items.Select(item => item.Id).ToArray();

    /// <summary>
    /// A's resources: the seeded vocabulary list (oldest), a smart one, one added early but
    /// edited last, one added last but not edited since.
    /// </summary>
    private static async Task<NativeSliceFixture> SeededAsync()
    {
        var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.LearningResources.AddRange(
                Resource("smart", "Daily review", true, Day(2), Day(2)),
                Resource("new-edit", "Edited lesson", false, Day(3), Day(9), "Video", "Spanish"),
                Resource("old-new", "New lesson", false, Day(8), Day(8), "Video", "Korean"));
            await db.SaveChangesAsync();
        }
        fixture.Activate(A);
        return fixture;
    }

    private static DateTime Day(int day) => new(2026, 10, day, 9, 0, 0, DateTimeKind.Utc);

    private static LearningResource Resource(string id, string title, bool smart,
        DateTime created, DateTime updated, string type = "Vocabulary List",
        string language = "Korean") => new()
    {
        Id = id, UserProfileId = A, Title = title, IsSmartResource = smart,
        MediaType = type, Language = language, CreatedAt = created, UpdatedAt = updated
    };
}
