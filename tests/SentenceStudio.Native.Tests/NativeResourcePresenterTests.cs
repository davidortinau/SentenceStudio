using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

public sealed class NativeResourcePresenterTests
{
    [Fact]
    public async Task List_UsesSourceSmartFirstOrderingAndOnlyActualOwnedFilterValues()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        await AddAsync(fixture, NativeSliceFixture.UserA, "smart-z", "Z review",
            smart: true);
        await AddAsync(fixture, NativeSliceFixture.UserA, "smart-a", "A review",
            smart: true);
        await AddAsync(fixture, NativeSliceFixture.UserA, "video-new", "Coast lesson",
            type: "Video", language: "Spanish", description: "A shore", tags: "beach");
        await AddAsync(fixture, NativeSliceFixture.UserB, "foreign-film", "Private",
            type: "Film", language: "German");
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;

        var list = await presenter.LoadListAsync();
        Assert.Equal(NativeResourceListPhase.Populated, list.Phase);
        Assert.Equal(["smart-a", "smart-z", "video-new",
            fixture.ResourceId(NativeSliceFixture.UserA)],
            list.Items.Select(item => item.Id));
        Assert.Equal(["Video", "Vocabulary List"], list.Types.ToArray());
        Assert.Equal(["Korean", "Spanish"], list.Languages.ToArray());
        Assert.DoesNotContain(list.Items, item => item.Id == "foreign-film");
        Assert.Equal(NativeResourceListPhase.Populated, presenter.Search("shore").Phase);
        Assert.Equal("video-new", Assert.Single(presenter.List.Items).Id);
        Assert.Equal("video-new", Assert.Single(presenter.Search("BEACH").Items).Id);
        Assert.Equal("video-new", Assert.Single(presenter.Search("spanish").Items).Id);
        Assert.Equal("video-new", Assert.Single(presenter.Search("Coast").Items).Id);
        Assert.Equal(NativeResourceListPhase.NoMatches,
            presenter.Search("not present").Phase);
        presenter.Search(null);
        Assert.Equal("video-new",
            Assert.Single(presenter.Filter("Video", "Spanish").Items).Id);
        Assert.Equal(NativeResourceListPhase.NoMatches,
            presenter.Filter("Video", "Korean").Phase);
        Assert.Throws<ArgumentException>(() => presenter.Filter("Film", null));
        Assert.Equal(NativeResourceListPhase.Populated,
            presenter.Filter(null, null).Phase);
    }

    [Fact]
    public async Task Filter_CaseVariantsShareOptionsAndStaySelectedThroughSearchAndRefresh()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore {
            ListGate = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        store.ListGate.SetResult([
            ResourceItem("upper") with { Title = "North shore" },
            ResourceItem("lower") with {
                Title = "South shore", MediaType = "video", Language = "korean" },
            ResourceItem("type-only") with { Language = null },
            ResourceItem("language-only") with { MediaType = "", Language = "KOREAN" },
            ResourceItem("none") with { MediaType = null, Language = "" },
            ResourceItem("spaces") with { MediaType = " ", Language = " " }
        ]);
        var presenter = Create(fixture, store);

        var list = await presenter.LoadListAsync();
        Assert.Equal(["Video"], list.Types.ToArray());
        Assert.Equal(["Korean"], list.Languages.ToArray());
        Assert.Equal(6, list.Items.Length);
        Assert.Equal(["upper", "lower", "type-only"],
            presenter.Filter("video", null).Items.Select(item => item.Id));
        Assert.Equal(["upper", "lower", "language-only"],
            presenter.Filter(null, "KOREAN").Items.Select(item => item.Id));
        var selected = presenter.Filter("vIDeo", "koREan");
        Assert.Equal("Video", selected.MediaType);
        Assert.Equal("Korean", selected.Language);
        Assert.Equal(["upper", "lower"], selected.Items.Select(item => item.Id));
        Assert.Throws<ArgumentException>(() => presenter.Filter("", null));
        Assert.Throws<ArgumentException>(() => presenter.Filter(null, " "));
        Assert.Equal("Video", presenter.List.MediaType);
        Assert.Equal("Korean", presenter.List.Language);

        Assert.Equal("lower", Assert.Single(presenter.Search("SOUTH").Items).Id);
        Assert.Equal(NativeResourceListPhase.NoMatches,
            presenter.Search("not present").Phase);
        Assert.Equal(["upper", "lower"],
            presenter.Search(null).Items.Select(item => item.Id));

        store.ListGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.ListGate.SetResult([
            ResourceItem("refreshed") with {
                Title = "South shore again", MediaType = "video", Language = "korean" },
            ResourceItem("unfiltered") with { MediaType = null, Language = null }
        ]);
        presenter.Search("shore");
        var refreshed = await presenter.LoadListAsync();
        Assert.Equal(["video"], refreshed.Types.ToArray());
        Assert.Equal(["korean"], refreshed.Languages.ToArray());
        Assert.Equal("video", refreshed.MediaType);
        Assert.Equal("korean", refreshed.Language);
        Assert.Equal("shore", refreshed.SearchText);
        Assert.Equal("refreshed", Assert.Single(refreshed.Items).Id);
        Assert.Single(presenter.Filter(null, null).Items);
        Assert.Equal(2, presenter.Search(null).Items.Length);
    }

    [Fact]
    public async Task SqliteResourceTimestamps_RoundTripUtcClockWithUnspecifiedKind()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await AddAsync(fixture, NativeSliceFixture.UserA, "dated", "Dated");
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;

        var item = Assert.Single((await presenter.LoadListAsync()).Items);
        var detail = (await presenter.OpenAsync(item.Id)).Resource!;
        Assert.Equal(new DateTime(2026, 9, 28, 10, 0, 0), item.CreatedAt);
        Assert.Equal(DateTimeKind.Unspecified, item.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Unspecified, item.UpdatedAt.Kind);
        Assert.Equal(item.CreatedAt, detail.CreatedAt);
        Assert.Equal(item.UpdatedAt, detail.UpdatedAt);
        Assert.Equal(DateTimeKind.Unspecified, detail.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Unspecified, detail.UpdatedAt.Kind);
    }

    [Fact]
    public async Task List_DistinguishesTrueEmptyNoMatchesLoadErrorAndOwnerLoss()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        Assert.Equal(NativeResourceListPhase.Empty,
            (await fixture.Resources.LoadListAsync()).Phase);

        await AddAsync(fixture, NativeSliceFixture.UserA, "only", "Word list");
        var presenter = fixture.Resources;
        Assert.Equal(NativeResourceListPhase.Populated,
            (await presenter.LoadListAsync()).Phase);
        Assert.Equal(NativeResourceListPhase.NoMatches,
            presenter.Search("absent").Phase);
        fixture.LoseOwner();
        Assert.Equal(NativeResourceListPhase.Blocked, presenter.List.Phase);
        Assert.Empty(presenter.List.Items.IsDefault ? [] : presenter.List.Items);
        Assert.Equal(NativeResourceListPhase.Blocked,
            (await presenter.LoadListAsync()).Phase);

        fixture.Activate(NativeSliceFixture.UserA);
        var brokenStore = new FakeStore { FailList = true };
        var broken = Create(fixture, brokenStore);
        Assert.Equal(NativeResourceListPhase.LoadError,
            (await broken.LoadListAsync()).Phase);
        Assert.Equal("NativeResources_LoadError", broken.List.ErrorKey);
        Assert.Equal(NativeResourceListPhase.LoadError,
            broken.Search("Original").Phase);
        Assert.Equal(NativeResourceListPhase.LoadError,
            broken.Filter(null, null).Phase);
        Assert.Empty(broken.List.Items);
        brokenStore.FailList = false;
        Assert.Equal(NativeResourceListPhase.Populated,
            (await broken.LoadListAsync()).Phase);
        brokenStore.FailList = true;
        Assert.Equal(NativeResourceListPhase.LoadError,
            (await broken.LoadListAsync()).Phase);
        Assert.Equal(NativeResourceListPhase.LoadError,
            broken.Search("Original").Phase);
        Assert.Equal(NativeResourceListPhase.LoadError,
            broken.Filter("Video", "Korean").Phase);
        Assert.Empty(broken.List.Items);
    }

    [Fact]
    public async Task Loading_DoesNotExposeStaleResultsAndAppliesQueuedSearchAfterRead()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore
        {
            ListGate = new TaskCompletionSource<IReadOnlyList<NativeResourceItem>>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var presenter = Create(fixture, store);
        var loading = presenter.LoadListAsync();
        Assert.Equal(NativeResourceListPhase.Loading, presenter.List.Phase);
        Assert.Equal(NativeResourceListPhase.Loading,
            presenter.Search("Original").Phase);
        Assert.Equal(NativeResourceListPhase.Loading,
            presenter.Filter(null, null).Phase);
        Assert.Empty(presenter.List.Items);

        store.ListGate.SetResult([
            new NativeResourceItem("owner-resource", "Original", null, null,
                "Video", "Korean", false, DateTime.UtcNow, DateTime.UtcNow)
        ]);
        Assert.Equal(NativeResourceListPhase.Populated, (await loading).Phase);
        Assert.Equal("Original", Assert.Single(presenter.List.Items).Title);
    }

    [Fact]
    public async Task Detail_IsOwnerScopedAndProjectsBoundedContentAndMappingCount()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var resource = await db.LearningResources.SingleAsync(row =>
                row.Id == fixture.ResourceId(NativeSliceFixture.UserA));
            resource.Transcript = new string('T', 20_000);
            resource.Translation = new string('K', 18_000);
            resource.MediaUrl = "https://example.invalid/watch/long-path";
            await db.SaveChangesAsync();
        }
        var presenter = fixture.Resources;
        await presenter.LoadListAsync();
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var listSql = NativeResourceStore.ListQuery(
                db, NativeSliceFixture.UserA).ToQueryString();
            Assert.DoesNotContain("\"Transcript\"", listSql);
            Assert.DoesNotContain("\"Translation\"", listSql);
            Assert.DoesNotContain("\"MediaUrl\"", listSql);
            var detailSql = NativeResourceStore.DetailQuery(db,
                NativeSliceFixture.UserA,
                fixture.ResourceId(NativeSliceFixture.UserA)).ToQueryString();
            Assert.Contains("substr(", detailSql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("240", detailSql);
            Assert.Contains("CAST(", detailSql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("UpdatedAt", detailSql);
        }
        var detail = await presenter.OpenAsync(fixture.ResourceId(NativeSliceFixture.UserA));
        Assert.Equal(NativeResourceDetailPhase.Ready, detail.Phase);
        Assert.Equal(240, detail.Resource!.TranscriptPreview!.Length);
        Assert.Equal(240, detail.Resource.TranslationPreview!.Length);
        Assert.True(detail.Resource.HasTranscript);
        Assert.True(detail.Resource.HasTranslation);
        Assert.Equal(4, detail.Resource.VocabularyCount);
        Assert.Equal("Korean", detail.Resource.Language);
        Assert.StartsWith("https://example.invalid/", detail.Resource.MediaUrl);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var storedAt = await db.LearningResources.AsNoTracking()
                .Where(row => row.Id == fixture.ResourceId(NativeSliceFixture.UserA))
                .Select(row => row.UpdatedAt.ToString()).SingleAsync();
            Assert.Equal(storedAt, detail.Resource.OriginalMetadata.SqliteUpdatedAtText);
            Assert.Equal(detail.Resource.UpdatedAt, detail.Resource.OriginalMetadata.UpdatedAt);
        }
        Assert.Equal(NativeResourceDetailPhase.NotFound,
            (await presenter.OpenAsync(fixture.ResourceId(NativeSliceFixture.UserB))).Phase);
        Assert.Null(presenter.Detail.Resource);
        Assert.False(presenter.BeginEdit());
        Assert.Equal(NativeResourceDetailPhase.NotFound,
            (await presenter.OpenAsync("missing-id")).Phase);
    }

    [Fact]
    public async Task OpeningBWhileAReadIsDelayed_KeepsBVisibleAndSavesOnlyB()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore();
        store.AddResource("resource-b", "Resource B");
        var delayed = new TaskCompletionSource<NativeResourceDetail?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        store.DelayedGets.Add("owner-resource", delayed);
        var presenter = Create(fixture, store);

        var openA = presenter.OpenAsync("owner-resource");
        Assert.Equal(NativeResourceDetailPhase.Loading, presenter.Detail.Phase);
        presenter.InvalidateDetail();
        var b = await presenter.OpenAsync("resource-b");
        Assert.Equal("resource-b", b.Resource!.Id);
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Edited B");
        var editing = presenter.Detail;

        delayed.SetResult(store.StoredDetail("owner-resource"));
        Assert.Same(editing, await openA);
        Assert.Same(editing, presenter.Detail);
        Assert.Equal("Edited B", presenter.Detail.Draft!.Title);
        Assert.True(await presenter.SaveAsync());
        Assert.Equal("resource-b", store.LastSaveResourceId);
        Assert.Equal("Edited B", presenter.Detail.Resource!.Title);
        Assert.Equal("Original", store.StoredDetail("owner-resource").Title);
    }

    [Fact]
    public async Task SupersededReadFailure_DoesNotReplaceBWithAnError()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore();
        store.AddResource("resource-b", "Resource B");
        var delayed = new TaskCompletionSource<NativeResourceDetail?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        store.DelayedGets.Add("owner-resource", delayed);
        var presenter = Create(fixture, store);

        var openA = presenter.OpenAsync("owner-resource");
        var b = await presenter.OpenAsync("resource-b");
        delayed.SetException(new InvalidOperationException("Stale read failed."));

        Assert.Same(b, await openA);
        Assert.Null(presenter.Detail.ErrorKey);
        Assert.Equal("resource-b", presenter.Detail.Resource!.Id);
        Assert.True(presenter.BeginEdit());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PoppingBeforeReadCompletes_DoesNotRestoreDetailOrError(bool fails)
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore();
        var delayed = new TaskCompletionSource<NativeResourceDetail?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        store.DelayedGets.Add("owner-resource", delayed);
        var presenter = Create(fixture, store);

        var open = presenter.OpenAsync("owner-resource");
        presenter.InvalidateDetail();
        Assert.Equal(NativeResourceDetailPhase.Idle, presenter.Detail.Phase);
        if (fails)
            delayed.SetException(new InvalidOperationException("Read finished after pop."));
        else
            delayed.SetResult(store.StoredDetail("owner-resource"));

        Assert.Equal(NativeResourceDetailPhase.Idle, (await open).Phase);
        Assert.Null(presenter.Detail.Resource);
        Assert.Null(presenter.Detail.ErrorKey);
        Assert.False(presenter.BeginEdit());
    }

    [Fact]
    public async Task SigningOutAndReturningToSameOwner_InvalidatesAnEarlierRead()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore();
        var delayed = new TaskCompletionSource<NativeResourceDetail?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        store.DelayedGets.Add("owner-resource", delayed);
        var presenter = Create(fixture, store);

        var open = presenter.OpenAsync("owner-resource");
        fixture.LoseOwner();
        fixture.Activate(NativeSliceFixture.UserA);
        delayed.SetResult(store.StoredDetail("owner-resource"));

        Assert.Equal(NativeResourceDetailPhase.Blocked, (await open).Phase);
        Assert.Null(presenter.Detail.Resource);
        Assert.False(presenter.BeginEdit());
        Assert.Equal("owner-resource",
            (await presenter.OpenAsync("owner-resource")).Resource!.Id);
    }

    [Fact]
    public async Task OwnerSwitchWhileAReadIsDelayed_DoesNotClearBOwnerDetail()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore();
        store.AddResource("resource-b", "Other owner's B");
        var delayed = new TaskCompletionSource<NativeResourceDetail?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        store.DelayedGets.Add("owner-resource", delayed);
        var presenter = Create(fixture, store);

        var openA = presenter.OpenAsync("owner-resource");
        fixture.Activate(NativeSliceFixture.UserB);
        var b = await presenter.OpenAsync("resource-b");
        delayed.SetResult(store.StoredDetail("owner-resource"));

        Assert.Same(b, await openA);
        Assert.Equal("resource-b", presenter.Detail.Resource!.Id);
        Assert.True(presenter.BeginEdit());
    }

    [Fact]
    public async Task Edit_CancelAndSmartResourceNeverWrite()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await AddAsync(fixture, NativeSliceFixture.UserA, "smart", "Review",
            smart: true);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        await presenter.LoadListAsync();
        await presenter.OpenAsync(fixture.ResourceId(NativeSliceFixture.UserA));
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Uncommitted");
        presenter.ChangeDraft(NativeResourceField.Description, "Draft only");
        presenter.ChangeDraft(NativeResourceField.Tags, "unsaved");
        Assert.Equal("Uncommitted", presenter.Detail.Draft!.Title);
        presenter.CancelEdit();
        Assert.Null(presenter.Detail.Draft);
        Assert.Equal("Synthetic vocabulary", presenter.Detail.Resource!.Title);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.LearningResources.AsNoTracking()
                .SingleAsync(resource => resource.Id ==
                    fixture.ResourceId(NativeSliceFixture.UserA));
            Assert.Equal("Synthetic vocabulary", row.Title);
            Assert.Null(row.Tags);
        }
        await presenter.OpenAsync("smart");
        Assert.False(presenter.BeginEdit());
        Assert.Equal(NativeResourceDetailPhase.ReadOnly, presenter.Detail.Phase);
        Assert.Null(presenter.Detail.Draft);
        Assert.False(await presenter.SaveAsync());
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        Assert.Equal(ResourceMetadataUpdateResult.SmartReadOnly,
            await store.SaveMetadataAsync(NativeSliceFixture.UserA, "smart",
                presenter.Detail.Resource!.OriginalMetadata, "Bad edit", null, null, default));
    }

    [Fact]
    public async Task Save_WritesOnlyThreeOwnedFieldsReloadsAndSurvivesRestart()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: true);
        await fixture.SeedAsync(NativeSliceFixture.UserB, known: true);
        fixture.Activate(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var resource = await db.LearningResources.SingleAsync(row =>
                row.Id == fixture.ResourceId(NativeSliceFixture.UserA));
            resource.MediaUrl = "https://example.invalid/unchanged";
            resource.Transcript = "unchanged transcript";
            resource.Translation = "unchanged translation";
            await db.SaveChangesAsync();
        }
        var presenter = fixture.Resources;
        await presenter.LoadListAsync();
        await presenter.OpenAsync(fixture.ResourceId(NativeSliceFixture.UserA));
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Native edit");
        presenter.ChangeDraft(NativeResourceField.Description, "Updated description");
        presenter.ChangeDraft(NativeResourceField.Tags, "travel, korean");
        Assert.True(await presenter.SaveAsync());
        Assert.Equal("Native edit", presenter.Detail.Resource!.Title);
        Assert.Null(presenter.Detail.Draft);
        Assert.Equal("Native edit", Assert.Single(presenter.List.Items).Title);
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.Detail.Sync);
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.List.Sync);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var owned = await db.LearningResources.AsNoTracking()
                .SingleAsync(row => row.Id == fixture.ResourceId(NativeSliceFixture.UserA));
            var foreign = await db.LearningResources.AsNoTracking()
                .SingleAsync(row => row.Id == fixture.ResourceId(NativeSliceFixture.UserB));
            Assert.Equal("Native edit", owned.Title);
            Assert.Equal("Updated description", owned.Description);
            Assert.Equal("travel, korean", owned.Tags);
            Assert.Equal(NativeSliceFixture.UserA, owned.UserProfileId);
            Assert.Equal("Vocabulary List", owned.MediaType);
            Assert.Equal("Korean", owned.Language);
            Assert.Equal("https://example.invalid/unchanged", owned.MediaUrl);
            Assert.Equal("unchanged transcript", owned.Transcript);
            Assert.Equal("unchanged translation", owned.Translation);
            Assert.True(owned.UpdatedAt > owned.CreatedAt);
            Assert.Equal("Synthetic vocabulary", foreign.Title);
            Assert.Equal(4, await db.ResourceVocabularyMappings
                .CountAsync(mapping => mapping.ResourceId == owned.Id));
            Assert.Equal(4, await db.ResourceVocabularyMappings
                .CountAsync(mapping => mapping.ResourceId == foreign.Id));
            Assert.Equal(4, await db.VocabularyProgresses
                .CountAsync(progress => progress.UserId == NativeSliceFixture.UserB));
        }
        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        var restored = fixture.Resources;
        Assert.Equal(NativeResourceSyncPhase.Pending,
            (await restored.LoadListAsync()).Sync);
        Assert.Equal("Native edit",
            (await restored.OpenAsync(fixture.ResourceId(NativeSliceFixture.UserA)))
                .Resource!.Title);
        Assert.True(restored.BeginEdit());
        restored.ChangeDraft(NativeResourceField.Title, "Synthetic vocabulary");
        restored.ChangeDraft(NativeResourceField.Description, string.Empty);
        restored.ChangeDraft(NativeResourceField.Tags, string.Empty);
        Assert.True(await restored.SaveAsync());
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.LearningResources.AsNoTracking().SingleAsync(resource =>
                resource.Id == fixture.ResourceId(NativeSliceFixture.UserA));
            Assert.Equal("Synthetic vocabulary", row.Title);
            Assert.Equal(string.Empty, row.Description);
            Assert.Equal(string.Empty, row.Tags);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  keep this spacing  ", "  spaced, tags  ")]
    public async Task TitleOnlySave_PreservesUntouchedNullableAndEmptyMetadata(
        string? description, string? tags)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await AddAsync(fixture, NativeSliceFixture.UserA, "title-only", "Original",
            description: description, tags: tags);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        await presenter.LoadListAsync();
        var opened = await presenter.OpenAsync("title-only");
        Assert.Equal(description, opened.Resource!.OriginalMetadata.Description);
        Assert.Equal(tags, opened.Resource.OriginalMetadata.Tags);
        Assert.True(presenter.BeginEdit());
        Assert.False(presenter.Detail.Draft!.DescriptionDirty);
        Assert.False(presenter.Detail.Draft.TagsDirty);

        presenter.ChangeDraft(NativeResourceField.Title, "  Edited title  ");
        Assert.True(presenter.Detail.Draft!.TitleDirty);
        Assert.True(await presenter.SaveAsync());

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.LearningResources.AsNoTracking()
            .SingleAsync(item => item.Id == "title-only");
        Assert.Equal("  Edited title  ", row.Title);
        Assert.Equal(description, row.Description);
        Assert.Equal(tags, row.Tags);
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.Detail.Sync);
    }

    [Fact]
    public async Task ClearingDescriptionAndTags_StoresEmptyTextWithoutTrimmingOtherEdits()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await AddAsync(fixture, NativeSliceFixture.UserA, "clear-fields", "Original",
            description: "description", tags: "tags");
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        await presenter.LoadListAsync();
        await presenter.OpenAsync("clear-fields");
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Description, string.Empty);
        presenter.ChangeDraft(NativeResourceField.Tags, null);
        Assert.True(presenter.Detail.Draft!.DescriptionDirty);
        Assert.True(presenter.Detail.Draft.TagsDirty);
        Assert.True(await presenter.SaveAsync());

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.LearningResources.AsNoTracking()
            .SingleAsync(item => item.Id == "clear-fields");
        Assert.Equal("Original", row.Title);
        Assert.Equal(string.Empty, row.Description);
        Assert.Equal(string.Empty, row.Tags);
    }

    [Fact]
    public async Task DetailSnapshot_UsesRawNullableTitleNotTheDisplayFallback()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.LearningResources.SingleAsync(row =>
                row.Id == fixture.ResourceId(NativeSliceFixture.UserA))).Title = null;
            await db.SaveChangesAsync();
        }
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        await presenter.LoadListAsync();
        var detail = await presenter.OpenAsync(fixture.ResourceId(NativeSliceFixture.UserA));
        Assert.Equal(string.Empty, detail.Resource!.Title);
        Assert.Null(detail.Resource.OriginalMetadata.Title);
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Restored title");
        Assert.True(await presenter.SaveAsync());
    }

    [Fact]
    public async Task StaleCoreSyncFields_ConflictPreservesDraftAndReloadResetsSnapshot()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        await presenter.LoadListAsync();
        var id = fixture.ResourceId(NativeSliceFixture.UserA);
        var opened = await presenter.OpenAsync(id);
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Unsent local title");

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.LearningResources.SingleAsync(resource => resource.Id == id);
            row.Description = "Downloaded while editor open";
            row.Tags = "new,  spaced";
            await db.SaveChangesAsync();
        }

        await presenter.LoadListAsync();
        Assert.Same(opened.Resource, (await presenter.OpenAsync(id)).Resource);
        Assert.Equal("Unsent local title", presenter.Detail.Draft!.Title);
        Assert.False(await presenter.SaveAsync());
        Assert.Equal(NativeResourceDetailPhase.Conflict, presenter.Detail.Phase);
        Assert.Equal(ResourceMetadataUpdateResult.Conflict, presenter.Detail.LastSaveResult);
        Assert.Equal("NativeResources_Conflict", presenter.Detail.ErrorKey);
        Assert.Equal("Unsent local title", presenter.Detail.Draft!.Title);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed, presenter.Detail.Sync);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed, presenter.List.Sync);
        presenter.ChangeDraft(NativeResourceField.Tags, "copyable draft tags");
        Assert.Equal(NativeResourceDetailPhase.Conflict, presenter.Detail.Phase);
        Assert.False(await presenter.SaveAsync());

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.LearningResources.AsNoTracking()
                .SingleAsync(resource => resource.Id == id);
            Assert.Equal("Synthetic vocabulary", row.Title);
            Assert.Equal("Downloaded while editor open", row.Description);
            Assert.Equal("new,  spaced", row.Tags);
            Assert.Equal(opened.Resource!.OriginalMetadata.UpdatedAt, row.UpdatedAt);
        }

        Assert.True(await presenter.ReloadDraftAsync());
        Assert.Equal(NativeResourceDetailPhase.Ready, presenter.Detail.Phase);
        Assert.Null(presenter.Detail.LastSaveResult);
        Assert.Equal("Synthetic vocabulary", presenter.Detail.Draft!.Title);
        Assert.Equal("Downloaded while editor open", presenter.Detail.Draft.Description);
        Assert.Equal("new,  spaced", presenter.Detail.Draft.Tags);
        Assert.False(presenter.Detail.Draft.TitleDirty);
        Assert.False(presenter.Detail.Draft.DescriptionDirty);
        Assert.False(presenter.Detail.Draft.TagsDirty);
        Assert.Equal("Downloaded while editor open",
            presenter.Detail.Resource!.OriginalMetadata.Description);
    }

    [Theory]
    [InlineData(ResourceMetadataUpdateResult.NotFoundOrForbidden,
        NativeResourceDetailPhase.NotFound, "ResourceEdit_NotFound")]
    [InlineData(ResourceMetadataUpdateResult.SmartReadOnly,
        NativeResourceDetailPhase.ReadOnly, "ResourceEdit_AutoUpdated")]
    [InlineData(ResourceMetadataUpdateResult.ValidationFailed,
        NativeResourceDetailPhase.SaveError, "NativeResources_SaveInvalid")]
    [InlineData(ResourceMetadataUpdateResult.Conflict,
        NativeResourceDetailPhase.Conflict, "NativeResources_Conflict")]
    public async Task TypedRefusals_RetainDraftWithoutMarkingPendingOrRetryingStaleSnapshot(
        ResourceMetadataUpdateResult outcome, NativeResourceDetailPhase phase,
        string errorKey)
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore { SaveOutcome = outcome };
        var presenter = Create(fixture, store);
        await presenter.LoadListAsync();
        await presenter.OpenAsync("owner-resource");
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Unsaved change");

        Assert.False(await presenter.SaveAsync());
        Assert.Equal(phase, presenter.Detail.Phase);
        Assert.Equal(outcome, presenter.Detail.LastSaveResult);
        Assert.Equal(errorKey, presenter.Detail.ErrorKey);
        Assert.Equal("Unsaved change", presenter.Detail.Draft!.Title);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed, presenter.Detail.Sync);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed, presenter.List.Sync);
        presenter.ChangeDraft(NativeResourceField.Description, "Copyable draft");
        Assert.Equal(phase, presenter.Detail.Phase);
        Assert.False(await presenter.SaveAsync());
        Assert.Equal(1, store.SaveCalls);

        store.SaveOutcome = ResourceMetadataUpdateResult.Success;
        Assert.True(await presenter.ReloadDraftAsync());
        Assert.Equal(NativeResourceDetailPhase.Ready, presenter.Detail.Phase);
        Assert.Equal("Original", presenter.Detail.Draft!.Title);
        Assert.Null(presenter.Detail.LastSaveResult);
        presenter.CancelEdit();
        Assert.Equal(1, store.SaveCalls);
    }

    [Fact]
    public async Task FailedConflictReload_RetainsDraftAndAllowsAnotherReload()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore { SaveOutcome = ResourceMetadataUpdateResult.Conflict };
        var presenter = Create(fixture, store);
        await presenter.OpenAsync("owner-resource");
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Keep this draft");
        Assert.False(await presenter.SaveAsync());
        store.FailGet = true;

        Assert.False(await presenter.ReloadDraftAsync());
        Assert.Equal(NativeResourceDetailPhase.Conflict, presenter.Detail.Phase);
        Assert.Equal("NativeResources_ReloadError", presenter.Detail.ErrorKey);
        Assert.Equal("Keep this draft", presenter.Detail.Draft!.Title);
        Assert.False(presenter.Detail.Reloading);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed, presenter.Detail.Sync);

        store.FailGet = false;
        Assert.True(await presenter.ReloadDraftAsync());
        Assert.Equal("Original", presenter.Detail.Draft!.Title);
    }

    [Fact]
    public async Task CancelDuringSaveGate_DoesNotCommitAndKeepsTheDraft()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore {
            SaveGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var presenter = Create(fixture, store);
        await presenter.OpenAsync("owner-resource");
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Canceled pending write");
        using var cancellation = new CancellationTokenSource();
        var save = presenter.SaveAsync(cancellation.Token);
        Assert.Equal(NativeResourceDetailPhase.Saving, presenter.Detail.Phase);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        Assert.Equal(NativeResourceDetailPhase.Ready, presenter.Detail.Phase);
        Assert.Equal("Canceled pending write", presenter.Detail.Draft!.Title);
        Assert.Equal("Original", presenter.Detail.Resource!.Title);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed, presenter.Detail.Sync);
        Assert.Equal(1, store.SaveCalls);
        presenter.CancelEdit();
        Assert.Null(presenter.Detail.Draft);
    }

    [Fact]
    public async Task OwnerSwitchAfterCommittedWrite_KeepsTheOriginalOwnerPending()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore {
            SaveGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var presenter = Create(fixture, store);
        await presenter.OpenAsync("owner-resource");
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Committed before owner switch");
        var save = presenter.SaveAsync();
        fixture.Activate(NativeSliceFixture.UserB);
        store.SaveGate.SetResult(true);

        Assert.False(await save);
        Assert.Equal(NativeResourceDetailPhase.Blocked, presenter.Detail.Phase);
        Assert.Equal(1, store.SaveCalls);
        fixture.Activate(NativeSliceFixture.UserA);
        Assert.Equal(NativeResourceSyncPhase.Pending,
            (await presenter.LoadListAsync()).Sync);
        Assert.Equal("Committed before owner switch",
            (await presenter.OpenAsync("owner-resource")).Resource!.Title);
    }

    [Fact]
    public async Task CancellationAfterCommit_CannotRetryAndStaysPendingForOwner()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore {
            ReloadGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var presenter = Create(fixture, store);
        await presenter.OpenAsync("owner-resource");
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Committed before cancellation");
        using var cancellation = new CancellationTokenSource();
        var save = presenter.SaveAsync(cancellation.Token);
        Assert.Equal(NativeResourceDetailPhase.Saving, presenter.Detail.Phase);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        Assert.Equal(NativeResourceDetailPhase.LoadError, presenter.Detail.Phase);
        Assert.Equal("NativeResources_SavedReloadError", presenter.Detail.ErrorKey);
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.Detail.Sync);
        Assert.Null(presenter.Detail.Draft);
        Assert.False(await presenter.SaveAsync());
        Assert.Equal(1, store.SaveCalls);
    }

    [Fact]
    public async Task CancelWhileConflictReloadWaits_CannotResurrectTheDiscardedDraft()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore {
            SaveOutcome = ResourceMetadataUpdateResult.Conflict,
            ReloadGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var presenter = Create(fixture, store);
        await presenter.OpenAsync("owner-resource");
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Canceled during reload");
        Assert.False(await presenter.SaveAsync());
        var reload = presenter.ReloadDraftAsync();
        Assert.True(presenter.Detail.Reloading);

        presenter.CancelEdit();
        store.ReloadGate.SetResult(true);
        Assert.False(await reload);
        Assert.Null(presenter.Detail.Draft);
        Assert.False(presenter.Detail.Reloading);
        Assert.Equal(NativeResourceDetailPhase.Ready, presenter.Detail.Phase);
        Assert.Equal(1, store.SaveCalls);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed, presenter.Detail.Sync);
    }

    [Fact]
    public async Task Save_RefusalValidationExceptionAndDuplicateActionAreExplicit()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore();
        var presenter = Create(fixture, store);
        await presenter.LoadListAsync();
        await presenter.OpenAsync("owner-resource");
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, " ");
        Assert.False(await presenter.SaveAsync());
        Assert.Equal("ResourceEdit_TitleRequired", presenter.Detail.ErrorKey);
        Assert.Equal(0, store.SaveCalls);

        presenter.ChangeDraft(NativeResourceField.Title, "New title");
        store.RefuseSave = true;
        Assert.False(await presenter.SaveAsync());
        Assert.Equal(NativeResourceDetailPhase.NotFound, presenter.Detail.Phase);
        Assert.Equal("ResourceEdit_NotFound", presenter.Detail.ErrorKey);
        Assert.Equal(NativeSliceFixture.UserA, store.LastSaveOwner);
        Assert.Equal("Original", presenter.Detail.Resource!.Title);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed, presenter.Detail.Sync);

        store.RefuseSave = false;
        Assert.True(await presenter.ReloadDraftAsync());
        presenter.ChangeDraft(NativeResourceField.Title, "New title");
        store.FailSave = true;
        Assert.False(await presenter.SaveAsync());
        Assert.Equal("NativeResources_SaveError", presenter.Detail.ErrorKey);
        store.FailSave = false;
        store.SaveGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = presenter.SaveAsync();
        Assert.Equal(NativeResourceDetailPhase.Saving, presenter.Detail.Phase);
        Assert.False(await presenter.SaveAsync());
        Assert.Equal(3, store.SaveCalls);
        store.SaveGate.SetResult(true);
        Assert.True(await first);
        Assert.Equal("New title", presenter.Detail.Resource!.Title);
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.Detail.Sync);
    }

    [Fact]
    public async Task Sync_OnlyConfirmsOnOwnerReceiptAndRevisionStillMatches()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        await presenter.LoadListAsync();
        await presenter.OpenAsync(fixture.ResourceId(NativeSliceFixture.UserA));
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Synced candidate");
        Assert.True(await presenter.SaveAsync());
        fixture.Sync.Outcome = NativeSyncOutcome.Offline;
        Assert.Equal(NativeResourceSyncPhase.Pending,
            await presenter.SynchronizeAsync());
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.List.Sync);
        Assert.Equal("NativeResources_SyncUnconfirmed", presenter.List.SyncErrorKey);
        Assert.Equal("NativeResources_SyncUnconfirmed", presenter.Detail.SyncErrorKey);
        fixture.Sync.Outcome = NativeSyncOutcome.Confirmed;
        fixture.Sync.DuringSynchronization = _ =>
            fixture.MarkPendingForTest(NativeSliceFixture.UserA);
        Assert.Equal(NativeResourceSyncPhase.Pending,
            await presenter.SynchronizeAsync());
        fixture.Sync.DuringSynchronization = null;
        Assert.Equal(NativeResourceSyncPhase.Confirmed,
            await presenter.SynchronizeAsync());
        Assert.Equal(NativeResourceSyncPhase.Confirmed, presenter.Detail.Sync);
        Assert.Equal(3, fixture.Sync.RequestedOwners.Count);
        Assert.All(fixture.Sync.RequestedOwners,
            owner => Assert.Equal(NativeSliceFixture.UserA, owner));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed,
            (await fixture.Resources.LoadListAsync()).Sync);
    }

    [Fact]
    public async Task SyncAndListRefresh_KeepAnOpenIsolatedDraftUntilUserLeavesEditor()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        await presenter.LoadListAsync();
        await presenter.OpenAsync(fixture.ResourceId(NativeSliceFixture.UserA));
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Unsaved while syncing");

        fixture.Sync.Outcome = NativeSyncOutcome.Confirmed;
        Assert.Equal(NativeResourceSyncPhase.Confirmed,
            await presenter.SynchronizeAsync());
        await presenter.LoadListAsync();
        Assert.Equal("Unsaved while syncing", presenter.Detail.Draft!.Title);
        presenter.CancelEdit();
        Assert.Equal("Synthetic vocabulary", presenter.Detail.Resource!.Title);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal("Synthetic vocabulary",
            (await db.LearningResources.AsNoTracking().SingleAsync(resource =>
                resource.Id == fixture.ResourceId(NativeSliceFixture.UserA))).Title);
    }

    [Fact]
    public async Task SimultaneousSyncCallers_ShareOneReceiptAndFinalState()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var readiness = new GatedSyncReadiness();
        var presenter = Create(fixture, new FakeStore(), readiness);
        fixture.MarkPendingForTest(NativeSliceFixture.UserA);
        await presenter.LoadListAsync();
        await presenter.OpenAsync("owner-resource");

        var first = presenter.SynchronizeAsync();
        var second = presenter.SynchronizeAsync();
        Assert.Same(first, second);
        Assert.Equal(1, readiness.Calls);
        Assert.False(first.IsCompleted);

        readiness.Complete(0, NativeSyncOutcome.Confirmed);
        var results = await Task.WhenAll(first, second);
        Assert.Equal(NativeResourceSyncPhase.Confirmed, results[0]);
        Assert.Equal(results[0], results[1]);
        Assert.Equal(NativeResourceSyncPhase.Confirmed, presenter.List.Sync);
        Assert.Equal(NativeResourceSyncPhase.Confirmed, presenter.Detail.Sync);
        Assert.Null(presenter.List.SyncErrorKey);
        Assert.False(fixture.Provider.GetRequiredService<NativePendingWrites>()
            .IsPending(NativeSliceFixture.UserA));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersededSyncResult_CannotDowngradeAConfirmedRetry(bool fails)
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var readiness = new GatedSyncReadiness();
        var store = new FakeStore();
        var presenter = Create(fixture, store, readiness);
        fixture.MarkPendingForTest(NativeSliceFixture.UserA);
        await presenter.LoadListAsync();
        await presenter.OpenAsync("owner-resource");

        var stale = presenter.SynchronizeAsync();
        presenter.InvalidateSync();
        var latest = presenter.SynchronizeAsync();
        Assert.NotSame(stale, latest);
        Assert.Equal(2, readiness.Calls);
        readiness.Complete(1, NativeSyncOutcome.Confirmed);
        Assert.Equal(NativeResourceSyncPhase.Confirmed, await latest);

        if (fails)
            readiness.Fail(0);
        else
            readiness.Complete(0, NativeSyncOutcome.Offline);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed, await stale);
        Assert.False(await presenter.RefreshAfterSyncAsync(stale));
        Assert.Equal(1, store.ListCalls);
        Assert.Equal(NativeResourceSyncPhase.Confirmed, presenter.List.Sync);
        Assert.Equal(NativeResourceSyncPhase.Confirmed, presenter.Detail.Sync);
        Assert.Null(presenter.List.SyncErrorKey);
        Assert.Null(presenter.Detail.SyncErrorKey);
        Assert.False(fixture.Provider.GetRequiredService<NativePendingWrites>()
            .IsPending(NativeSliceFixture.UserA));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedSync_AllowsASecondAttemptToConfirm(bool fails)
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var readiness = new GatedSyncReadiness();
        var presenter = Create(fixture, new FakeStore(), readiness);
        await presenter.LoadListAsync();

        var first = presenter.SynchronizeAsync();
        if (fails)
            readiness.Fail(0);
        else
            readiness.Complete(0, NativeSyncOutcome.Offline);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed, await first);
        Assert.Equal("NativeResources_SyncUnconfirmed", presenter.List.SyncErrorKey);

        var retry = presenter.SynchronizeAsync();
        Assert.NotSame(first, retry);
        Assert.Equal(2, readiness.Calls);
        readiness.Complete(1, NativeSyncOutcome.Confirmed);
        Assert.Equal(NativeResourceSyncPhase.Confirmed, await retry);
        Assert.Null(presenter.List.SyncErrorKey);
    }

    [Fact]
    public async Task OwnerChange_RejectsFormerOwnersLateConfirmationAndPreservesPending()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        var readiness = new GatedSyncReadiness();
        var presenter = Create(fixture,
            fixture.Provider.GetRequiredService<INativeResourceStore>(), readiness);
        fixture.MarkPendingForTest(NativeSliceFixture.UserA);
        await presenter.LoadListAsync();
        var formerOwner = presenter.SynchronizeAsync();

        fixture.Activate(NativeSliceFixture.UserB);
        await presenter.LoadListAsync();
        var currentOwner = presenter.SynchronizeAsync();
        readiness.Complete(1, NativeSyncOutcome.Confirmed);
        Assert.Equal(NativeResourceSyncPhase.Confirmed, await currentOwner);
        readiness.Complete(0, NativeSyncOutcome.Confirmed);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed, await formerOwner);
        Assert.Equal(NativeResourceSyncPhase.Confirmed, presenter.List.Sync);
        Assert.Null(presenter.List.SyncErrorKey);
        Assert.Equal(fixture.ResourceId(NativeSliceFixture.UserB),
            Assert.Single(presenter.List.Items).Id);
        Assert.True(fixture.Provider.GetRequiredService<NativePendingWrites>()
            .IsPending(NativeSliceFixture.UserA));
        Assert.False(await presenter.RefreshAfterSyncAsync(formerOwner));
    }

    [Fact]
    public async Task ReturningToSameOwner_DoesNotJoinAnEarlierSessionOrApplyItsResult()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var readiness = new GatedSyncReadiness();
        var presenter = Create(fixture, new FakeStore(), readiness);
        var previousSession = presenter.SynchronizeAsync();

        fixture.LoseOwner();
        fixture.Activate(NativeSliceFixture.UserA);
        var currentSession = presenter.SynchronizeAsync();
        Assert.NotSame(previousSession, currentSession);
        Assert.Equal(2, readiness.Calls);
        readiness.Complete(1, NativeSyncOutcome.Confirmed);
        Assert.Equal(NativeResourceSyncPhase.Confirmed, await currentSession);
        readiness.Complete(0, NativeSyncOutcome.Offline);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed, await previousSession);
        Assert.Equal(NativeResourceSyncPhase.Confirmed, presenter.List.Sync);
        Assert.Null(presenter.List.SyncErrorKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrDisposal_InvalidatesALateSyncReceipt(bool dispose)
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var readiness = new GatedSyncReadiness();
        var presenter = Create(fixture, new FakeStore(), readiness);
        fixture.MarkPendingForTest(NativeSliceFixture.UserA);
        await presenter.LoadListAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = presenter.SynchronizeAsync(cancellation.Token);

        if (dispose)
            presenter.InvalidateSync();
        cancellation.Cancel();
        readiness.Complete(0, NativeSyncOutcome.Confirmed);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.List.Sync);
        Assert.Null(presenter.List.SyncErrorKey);
        Assert.True(fixture.Provider.GetRequiredService<NativePendingWrites>()
            .IsPending(NativeSliceFixture.UserA));
        Assert.False(presenter.IsLatestSynchronization(pending));
    }

    [Fact]
    public async Task SyncRefresh_LateListCannotReplaceNewerConfirmedResources()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var readiness = new GatedSyncReadiness();
        var store = new FakeStore();
        var presenter = Create(fixture, store, readiness);
        await presenter.LoadListAsync();
        var oldRows = new TaskCompletionSource<IReadOnlyList<NativeResourceItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newRows = new TaskCompletionSource<IReadOnlyList<NativeResourceItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        store.ListGates.Enqueue(oldRows);
        store.ListGates.Enqueue(newRows);

        var stale = presenter.SynchronizeAsync();
        readiness.Complete(0, NativeSyncOutcome.Offline);
        Assert.Equal(NativeResourceSyncPhase.Unconfirmed, await stale);
        var oldRefresh = presenter.RefreshAfterSyncAsync(stale);
        var latest = presenter.SynchronizeAsync();
        readiness.Complete(1, NativeSyncOutcome.Confirmed);
        Assert.Equal(NativeResourceSyncPhase.Confirmed, await latest);
        var newRefresh = presenter.RefreshAfterSyncAsync(latest);
        newRows.SetResult([ResourceItem("latest")]);
        Assert.True(await newRefresh);
        oldRows.SetResult([ResourceItem("stale")]);
        Assert.False(await oldRefresh);
        Assert.Equal("latest", Assert.Single(presenter.List.Items).Id);
        Assert.Equal(NativeResourceSyncPhase.Confirmed, presenter.List.Sync);
        Assert.Null(presenter.List.SyncErrorKey);
    }

    [Fact]
    public async Task OwnerStampAndListGeneration_RejectAStaleListCompletion()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore();
        var presenter = Create(fixture, store);
        var oldRows = new TaskCompletionSource<IReadOnlyList<NativeResourceItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newRows = new TaskCompletionSource<IReadOnlyList<NativeResourceItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        store.ListGates.Enqueue(oldRows);
        store.ListGates.Enqueue(newRows);

        var stale = presenter.LoadListAsync();
        fixture.LoseOwner();
        fixture.Activate(NativeSliceFixture.UserA);
        var latest = presenter.LoadListAsync();
        newRows.SetResult([ResourceItem("latest")]);
        Assert.Equal("latest", Assert.Single((await latest).Items).Id);
        oldRows.SetResult([ResourceItem("stale")]);
        Assert.Equal("latest", Assert.Single((await stale).Items).Id);
        Assert.Equal("latest", Assert.Single(presenter.List.Items).Id);
    }

    [Fact]
    public async Task CanceledListRefresh_RestoresThePreviousRowsAndCanRetry()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore();
        var presenter = Create(fixture, store);
        await presenter.LoadListAsync();
        store.ListGate = new TaskCompletionSource<IReadOnlyList<NativeResourceItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var canceled = presenter.LoadListAsync(cancellation.Token);
        Assert.Equal(NativeResourceListPhase.Loading, presenter.List.Phase);
        cancellation.Cancel();
        store.ListGate.SetResult([ResourceItem("ignored")]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal(NativeResourceListPhase.Populated, presenter.List.Phase);
        Assert.Equal("owner-resource", Assert.Single(presenter.List.Items).Id);

        store.ListGate = null;
        Assert.Equal(NativeResourceListPhase.Populated,
            (await presenter.LoadListAsync()).Phase);
        Assert.Equal(3, store.ListCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncUiAction_SharedFlightAllowsRetryAfterCompletion(bool fails)
    {
        var action = new NativeResourceSyncAction();
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var first = action.RunAsync(() =>
        {
            Interlocked.Increment(ref calls);
            return release.Task;
        });
        var duplicate = action.RunAsync(() =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        Assert.Same(first, duplicate);
        Assert.Equal(1, calls);

        if (fails)
        {
            release.SetException(new InvalidOperationException("Sync failed."));
            await Assert.ThrowsAsync<InvalidOperationException>(() => first);
            await Assert.ThrowsAsync<InvalidOperationException>(() => duplicate);
        }
        else
        {
            release.SetResult();
            await Task.WhenAll(first, duplicate);
        }

        await action.RunAsync(() =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CommittedWriteWithFailedReload_StaysPendingWithoutStaleDetailOrSecondSave()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore { FailGetAfterSave = true };
        var presenter = Create(fixture, store);
        await presenter.LoadListAsync();
        await presenter.OpenAsync("owner-resource");
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Committed locally");

        Assert.False(await presenter.SaveAsync());
        Assert.Equal(1, store.SaveCalls);
        Assert.Equal(NativeResourceDetailPhase.LoadError, presenter.Detail.Phase);
        Assert.Equal("NativeResources_SavedReloadError", presenter.Detail.ErrorKey);
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.Detail.Sync);
        Assert.Null(presenter.Detail.Resource);
        Assert.Null(presenter.Detail.Draft);
        presenter.CancelEdit();
        Assert.Equal(NativeResourceDetailPhase.LoadError, presenter.Detail.Phase);
        Assert.False(await presenter.SaveAsync());
        Assert.Equal(1, store.SaveCalls);

        store.FailGetAfterSave = false;
        Assert.Equal("Committed locally",
            (await presenter.OpenAsync("owner-resource")).Resource!.Title);
    }

    [Fact]
    public async Task DetailAndSave_FailClosedWhenOwnerSwitchesOrReadFails()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var store = new FakeStore { FailGet = true };
        var presenter = Create(fixture, store);
        await presenter.LoadListAsync();
        Assert.Equal(NativeResourceDetailPhase.LoadError,
            (await presenter.OpenAsync("owner-resource")).Phase);
        store.FailGet = false;
        await presenter.OpenAsync("owner-resource");
        Assert.True(presenter.BeginEdit());
        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Equal(NativeResourceDetailPhase.Blocked, presenter.Detail.Phase);
        Assert.False(await presenter.SaveAsync());
        Assert.Equal(0, store.SaveCalls);
        Assert.Empty(presenter.List.Items.IsDefault ? [] : presenter.List.Items);
    }

    private static NativeResourcePresenter Create(
        NativeSliceFixture fixture, INativeResourceStore store,
        INativeSyncReadiness? readiness = null) => new(
            fixture.UserScope, fixture.Preferences, store, readiness ?? fixture.Sync,
            fixture.Provider.GetRequiredService<NativePendingWrites>(),
            NullLogger<NativeResourcePresenter>.Instance);

    private static NativeResourceItem ResourceItem(string id) =>
        new(id, id, null, null, "Video", "Korean", false,
            new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc));

    private sealed class GatedSyncReadiness : INativeSyncReadiness
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<NativeSyncOutcome>> _operations = [];

        public int Calls
        {
            get { lock (_gate) return _operations.Count; }
        }

        public Task<NativeSyncOutcome> SynchronizeAsync(
            string userId, CancellationToken cancellationToken)
        {
            // Deliberately ignore cancellation to exercise late adapter completions.
            var operation = new TaskCompletionSource<NativeSyncOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
                _operations.Add(operation);
            return operation.Task;
        }

        public void Complete(int index, NativeSyncOutcome outcome) =>
            Operation(index).SetResult(outcome);

        public void Fail(int index) =>
            Operation(index).SetException(new InvalidOperationException("Sync failed."));

        private TaskCompletionSource<NativeSyncOutcome> Operation(int index)
        {
            lock (_gate)
                return _operations[index];
        }
    }

    private static async Task AddAsync(NativeSliceFixture fixture,
        string owner, string id, string title,
        bool smart = false, string type = "Vocabulary List",
        string language = "Korean", string? description = null, string? tags = null)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.LearningResources.Add(new LearningResource
        {
            Id = id,
            UserProfileId = owner,
            Title = title,
            Description = description,
            Tags = tags,
            MediaType = type,
            Language = language,
            IsSmartResource = smart,
            CreatedAt = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();
    }

    private sealed class FakeStore : INativeResourceStore
    {
        private static readonly DateTime InitialTimestamp =
            new(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);
        private readonly Dictionary<string, NativeResourceDetail> _additionalResources = new();
        private NativeResourceDetail _resource = new(
            "owner-resource", "Original", null, null, "Video", "Korean",
            null, false, InitialTimestamp, InitialTimestamp,
            false, null, false, null, 0,
            new ResourceMetadataSnapshot(InitialTimestamp, "Original", null, null,
                "2026-09-28 10:00:00"));

        public bool FailList { get; set; }
        public bool FailGet { get; set; }
        public bool FailGetAfterSave { get; set; }
        public bool FailSave { get; set; }
        public bool RefuseSave { get; set; }
        public ResourceMetadataUpdateResult SaveOutcome { get; set; } =
            ResourceMetadataUpdateResult.Success;
        public int SaveCalls { get; private set; }
        public string? LastSaveOwner { get; private set; }
        public string? LastSaveResourceId { get; private set; }
        public TaskCompletionSource<bool>? SaveGate { get; set; }
        public TaskCompletionSource<bool>? ReloadGate { get; set; }
        public TaskCompletionSource<IReadOnlyList<NativeResourceItem>>? ListGate { get; set; }
        public Queue<TaskCompletionSource<IReadOnlyList<NativeResourceItem>>> ListGates { get; } =
            new();
        public int ListCalls { get; private set; }
        public Dictionary<string, TaskCompletionSource<NativeResourceDetail?>> DelayedGets { get; } =
            new();

        public void AddResource(string id, string title) =>
            _additionalResources.Add(id, _resource with {
                Id = id, Title = title,
                OriginalMetadata = _resource.OriginalMetadata with { Title = title }
            });

        public NativeResourceDetail StoredDetail(string id) =>
            id == _resource.Id ? _resource : _additionalResources[id];

        public Task<IReadOnlyList<NativeResourceItem>> ListAsync(
            string owner, CancellationToken cancellationToken)
        {
            ListCalls++;
            if (FailList)
                throw new InvalidOperationException("Scratch list unavailable.");
            if (ListGates.TryDequeue(out var next))
                return next.Task;
            if (ListGate is not null)
                return ListGate.Task;
            return Task.FromResult<IReadOnlyList<NativeResourceItem>>(
                new[] { _resource }.Concat(_additionalResources.Values)
                    .Select(resource => new NativeResourceItem(
                        resource.Id, resource.Title, resource.Description,
                        resource.Tags, resource.MediaType, resource.Language,
                        resource.IsSmartResource, resource.CreatedAt, resource.UpdatedAt))
                    .ToArray());
        }

        public async Task<NativeResourceDetail?> GetAsync(
            string owner, string resourceId, CancellationToken cancellationToken)
        {
            if (FailGet || FailGetAfterSave && SaveCalls > 0)
                throw new InvalidOperationException("Scratch detail unavailable.");
            if (DelayedGets.TryGetValue(resourceId, out var delayed))
                return await delayed.Task;
            if (ReloadGate is not null && SaveCalls > 0)
                await ReloadGate.Task.WaitAsync(cancellationToken);
            return resourceId == _resource.Id ? _resource
                : _additionalResources.GetValueOrDefault(resourceId);
        }

        public async Task<ResourceMetadataUpdateResult> SaveMetadataAsync(
            string owner, string resourceId, ResourceMetadataSnapshot original,
            string title, string? description, string? tags,
            CancellationToken cancellationToken)
        {
            SaveCalls++;
            LastSaveOwner = owner;
            LastSaveResourceId = resourceId;
            if (FailSave)
                throw new InvalidOperationException("Scratch write unavailable.");
            if (RefuseSave)
                return ResourceMetadataUpdateResult.NotFoundOrForbidden;
            if (SaveGate is not null)
                await SaveGate.Task.WaitAsync(cancellationToken);
            if (SaveOutcome != ResourceMetadataUpdateResult.Success)
                return SaveOutcome;
            var resource = StoredDetail(resourceId);
            var updatedAt = resource.UpdatedAt.AddTicks(10);
            var updated = resource with {
                Title = title, Description = description, Tags = tags,
                UpdatedAt = updatedAt,
                OriginalMetadata = new ResourceMetadataSnapshot(updatedAt, title,
                    description, tags, updatedAt.ToString("O"))
            };
            if (resourceId == _resource.Id)
                _resource = updated;
            else
                _additionalResources[resourceId] = updated;
            return ResourceMetadataUpdateResult.Success;
        }
    }
}
