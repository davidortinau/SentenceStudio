using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.Tests;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeResourceDateFormatterTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");
    private static readonly TimeZoneInfo West = TimeZoneInfo.CreateCustomTimeZone(
        "resource-west", TimeSpan.FromHours(-7), "West", "West");
    private static readonly TimeZoneInfo East = TimeZoneInfo.CreateCustomTimeZone(
        "resource-east", TimeSpan.FromHours(9), "East", "East");

    [Theory]
    [InlineData("en-US")]
    [InlineData("ko-KR")]
    public void LegacySmartUnspecified_KeepsLocalWallClockInListAndDetail(string locale)
    {
        var culture = CultureInfo.GetCultureInfo(locale);
        var early = new DateTime(2026, 10, 1, 1, 30, 0, DateTimeKind.Unspecified);
        var late = new DateTime(2026, 10, 1, 23, 30, 0, DateTimeKind.Unspecified);

        AssertCreatedDate(early, early, true, West, culture);
        AssertCreatedDate(late, late, true, East, culture);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ko-KR")]
    public void NormalOrSyncedUnspecified_CrossesDeviceLocalDateInBothViews(string locale)
    {
        var culture = CultureInfo.GetCultureInfo(locale);
        var early = new DateTime(2026, 10, 1, 1, 30, 0, DateTimeKind.Unspecified);
        var late = new DateTime(2026, 10, 1, 23, 30, 0, DateTimeKind.Unspecified);

        AssertCreatedDate(early, new DateTime(2026, 9, 30, 18, 30, 0),
            false, West, culture);
        AssertCreatedDate(late, new DateTime(2026, 10, 2, 8, 30, 0),
            false, East, culture);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ko-KR")]
    public void SmartUpdatedAtUnspecified_IsUtcEvenThoughCreatedAtIsLocal(string locale)
    {
        var culture = CultureInfo.GetCultureInfo(locale);
        var stored = new DateTime(2026, 10, 1, 1, 30, 0, DateTimeKind.Unspecified);

        Assert.Equal(stored.ToString("g", culture),
            NativeResourceDateFormatter.DetailCreatedDate(stored, true, West, culture));
        Assert.Equal(new DateTime(2026, 9, 30, 18, 30, 0).ToString("g", culture),
            NativeResourceDateFormatter.DetailUpdatedDate(stored, West, culture));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UtcKind_ConvertsOnceRegardlessOfSmartProvenance(bool isSmartResource)
    {
        var utc = new DateTime(2026, 10, 1, 1, 30, 0, DateTimeKind.Utc);
        var expected = new DateTime(2026, 9, 30, 18, 30, 0);

        AssertCreatedDate(utc, expected, isSmartResource, West, English);
        Assert.Equal(expected.ToString("g", English),
            NativeResourceDateFormatter.DetailUpdatedDate(utc, West, English));
    }

    [Fact]
    public void EnglishAndKoreanUseDistinctCultureFormats()
    {
        var stored = new DateTime(2026, 10, 1, 23, 30, 0, DateTimeKind.Unspecified);

        Assert.NotEqual(NativeResourceDateFormatter.ListDate(stored, false, East, English),
            NativeResourceDateFormatter.ListDate(stored, false, East, Korean));
        Assert.NotEqual(NativeResourceDateFormatter.DetailCreatedDate(stored, false, East, English),
            NativeResourceDateFormatter.DetailCreatedDate(stored, false, East, Korean));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ko-KR")]
    public void LocalKind_IsNotConvertedAgainForAnyResourceField(string locale)
    {
        var culture = CultureInfo.GetCultureInfo(locale);
        var local = new DateTime(2026, 10, 1, 1, 30, 0, DateTimeKind.Local);

        AssertCreatedDate(local, local, false, West, culture);
        AssertCreatedDate(local, local, true, West, culture);
        Assert.Equal(local.ToString("g", culture),
            NativeResourceDateFormatter.DetailUpdatedDate(local, West, culture));
    }

    [Fact]
    public async Task SqliteSmartRow_PreservesWallClockAndProvenanceInListAndDetail()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        var createdAt = new DateTime(2026, 10, 1, 1, 30, 0, DateTimeKind.Local);
        var updatedAt = new DateTime(2026, 10, 1, 1, 30, 0, DateTimeKind.Utc);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.LearningResources.Add(new LearningResource
            {
                Id = "legacy-smart",
                UserProfileId = NativeSliceFixture.UserA,
                Title = "Review",
                IsSmartResource = true,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt
            });
            await db.SaveChangesAsync();
        }

        fixture.Activate(NativeSliceFixture.UserA);
        var item = Assert.Single((await fixture.Resources.LoadListAsync()).Items);
        var detail = Assert.IsType<NativeResourceDetail>(
            (await fixture.Resources.OpenAsync(item.Id)).Resource);
        Assert.True(item.IsSmartResource);
        Assert.True(detail.IsSmartResource);
        Assert.Equal(DateTimeKind.Unspecified, item.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Unspecified, detail.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Unspecified, detail.UpdatedAt.Kind);
        Assert.Equal(createdAt.Ticks, item.CreatedAt.Ticks);
        Assert.Equal(item.CreatedAt, detail.CreatedAt);
        Assert.Equal(updatedAt.Ticks, detail.UpdatedAt.Ticks);

        foreach (var culture in new[] { English, Korean })
        {
            Assert.Equal(createdAt.ToString("d", culture),
                NativeResourceDateFormatter.ListDate(
                    item.CreatedAt, item.IsSmartResource, West, culture));
            Assert.Equal(createdAt.ToString("g", culture),
                NativeResourceDateFormatter.DetailCreatedDate(
                    detail.CreatedAt, detail.IsSmartResource, West, culture));
            Assert.Equal(new DateTime(2026, 9, 30, 18, 30, 0).ToString("g", culture),
                NativeResourceDateFormatter.DetailUpdatedDate(
                    detail.UpdatedAt, West, culture));
        }
    }

    private static void AssertCreatedDate(DateTime stored, DateTime expected,
        bool isSmartResource, TimeZoneInfo deviceTimeZone, CultureInfo culture)
    {
        Assert.Equal(expected.ToString("d", culture),
            NativeResourceDateFormatter.ListDate(
                stored, isSmartResource, deviceTimeZone, culture));
        Assert.Equal(expected.ToString("g", culture),
            NativeResourceDateFormatter.DetailCreatedDate(
                stored, isSmartResource, deviceTimeZone, culture));
    }
}
