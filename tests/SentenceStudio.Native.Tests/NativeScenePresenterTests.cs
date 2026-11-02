using Stopwatch = System.Diagnostics.Stopwatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Describe a Scene source contract (activities.md 1.1-1.4 and 7) against
/// real SQLite and shared services, with fake AI and image-byte ports.
/// </summary>
public sealed class NativeScenePresenterTests
{
    private const string Scene = NativeActivityCatalog.Scene;
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13];
    private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xe0, 0, 16];

    [Fact]
    public void Catalog_RegistersSceneForTheSceneDescriptionPlanType()
    {
        Assert.True(NativeActivityCatalog.IsImplemented(Scene));
        Assert.True(NativeActivityCatalog.IsImplemented(PlanActivityType.SceneDescription));
        var definition = NativeActivityCatalog.ForRoute(Scene)!;
        Assert.Equal(PlanActivityType.SceneDescription, definition.TimerType);
        // Source Scene passes its skill to ActivityTimer.StartSession.
        Assert.True(definition.TimerRecordsSkill);
        Assert.False(NativeActivityCatalog.IsVocabularyDriven(PlanActivityType.SceneDescription));
        Assert.Contains(NativeDashboardPresenter.ChooseOwnActivities, tile => tile.Route == Scene);
    }

    [Fact]
    public async Task Journey_GradesNewestFirstWritesActivityAndScoresOwnedVocabulary()
    {
        using var fixture = new NativeSliceFixture();
        var owner = NativeSliceFixture.UserA;
        await fixture.SeedAsync(owner, wordCount: 4);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var store = new FakeImageStore();
        store.Images.Add(new SceneImage { Id = 7, Url = "https://example.invalid/park.jpg", Description = "A park" });
        var ai = new FakeSceneAi();
        using var scene = Presenter(fixture, store, ai);

        var state = await scene.OpenAsync(ChooseOwnLaunch(fixture, owner));

        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(7, state.Image!.Id);
        Assert.Equal(NativeSceneImageLoad.Loaded, state.Image.Load);
        Assert.Equal(Png, state.Image.Bytes);
        Assert.Empty(state.Results);
        Assert.False(state.CanGrade);
        Assert.Equal([owner], store.LoadOwners.Distinct().ToArray());

        var target = WordTarget(fixture, owner, 0);
        ai.Analysis = [new VocabularyAnalysis { DictionaryForm = target, UsedForm = target, UsageCorrect = true }];
        scene.SetDraft("  공원에 사람이 있어요  ");
        Assert.True(scene.State.CanGrade);
        state = await scene.GradeAsync();

        var first = Assert.Single(state.Results);
        Assert.Equal("공원에 사람이 있어요", first.Answer);
        Assert.Equal(82, first.Accuracy);
        Assert.Equal("Recommended", first.RecommendedSentence);
        Assert.Equal("Grammar note", first.GrammarNotes);
        Assert.Equal(string.Empty, state.Draft);
        Assert.Contains(state.Notices, notice => notice.Key == NativeSceneText.GradeExcellent
            && notice.Argument == "82");
        // Source GradeDescription(input, stored Description, profile languages).
        Assert.Equal((owner, "공원에 사람이 있어요", "A park", "Korean", "English"), Assert.Single(ai.Graded));

        ai.Accuracy = 55;
        ai.Analysis = null;
        scene.SetDraft("둘");
        state = await scene.GradeAsync();
        Assert.Equal(["둘", "공원에 사람이 있어요"], state.Results.Select(result => result.Answer).ToArray());
        Assert.Contains(state.Notices, notice => notice.Key == NativeSceneText.GradeGood);
        ai.Accuracy = 20;
        scene.SetDraft("셋");
        state = await scene.GradeAsync();
        Assert.Contains(state.Notices, notice => notice.Key == NativeSceneText.GradeKeepPracticing);

        // Source ToggleExpand: one card open at a time.
        state = await scene.ToggleDetailsAsync(first.Index);
        Assert.Equal(first.Index, state.ExpandedIndex);
        state = await scene.ToggleDetailsAsync(first.Index);
        Assert.Equal(-1, state.ExpandedIndex);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var rows = await db.UserActivities.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.Equal(nameof(Activity.SceneDescription), row.Activity);
                Assert.Equal(owner, row.UserProfileId);
                // Stored as a UTC instant (streaks read UTC).
                Assert.True(Math.Abs((row.CreatedAt - DateTime.UtcNow).TotalMinutes) < 5);
            });
            Assert.Contains(rows, row => row.Input == "공원에 사람이 있어요" && row.Accuracy == 82);
            var attempt = await db.VocabularyLearningContexts.AsNoTracking()
                .Where(context => context.Activity == NativeScenePresenter.ScoringActivity)
                .Join(db.VocabularyProgresses, context => context.VocabularyProgressId,
                    progress => progress.Id, (context, progress) => new { context, progress })
                .SingleAsync();
            Assert.Equal(owner, attempt.progress.UserId);
            Assert.Equal(fixture.WordId(owner, 0), attempt.progress.VocabularyWordId);
            Assert.Equal("Text", attempt.context.InputMode);
            Assert.True(attempt.context.WasCorrect);
        }

        // New scene keeps the graded list (source LoadNewScene does not clear it).
        store.Images.Add(new SceneImage { Id = 8, Url = "https://example.invalid/b.jpg" });
        store.RandomPick = images => images.Single(image => image.Id == 8);
        state = await scene.NewSceneAsync();
        Assert.Equal(8, state.Image!.Id);
        Assert.Equal(3, state.Results.Length);

        var exit = await scene.ExitAsync();
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.Null(exit.ErrorKey);
        Assert.True(exit.HasPendingSync);
        scene.SetDraft("late");
        Assert.Equal(NativeDestination.TodaysPlan, (await scene.GradeAsync()).Destination);
        Assert.Equal(3, ai.Graded.Count);

        using var check = fixture.Scope();
        var ctx = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var adHoc = await ctx.DailyPlanCompletions.AsNoTracking()
            .SingleAsync(item => item.PlanItemId.StartsWith("adhoc-"));
        Assert.Equal(owner, adHoc.UserProfileId);
        Assert.Equal(nameof(PlanActivityType.SceneDescription), adHoc.ActivityType);
    }

    [Fact]
    public async Task EmptyGallery_ShowsTheEmptyStateAndStillSavesTheTimer()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var scene = Presenter(fixture, new FakeImageStore(), new FakeSceneAi());

        var state = await scene.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));

        Assert.Equal(NativePhase.Empty, state.Phase);
        Assert.Null(state.Image);
        Assert.Contains(state.Notices, notice => notice.Key == NativeSceneText.NoImagesFound
            && notice.Kind == NativeSceneNoticeKind.Warning);
        Assert.False(state.CanGrade);
        // Browse Gallery works from the empty state.
        state = await scene.OpenGalleryAsync();
        Assert.NotNull(state.Gallery);
        Assert.Empty(state.Gallery!.Items);
        Assert.Equal(NativeDestination.TodaysPlan, (await scene.ExitAsync()).Destination);
        using var scope = fixture.Scope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .DailyPlanCompletions.AnyAsync(row => row.UserProfileId == NativeSliceFixture.UserA
                && row.ActivityType == nameof(PlanActivityType.SceneDescription)));
    }

    [Fact]
    public async Task GradeFailure_ShowsAnErrorAndRecordsNothing()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeSceneAi { Fail = true };
        using var scene = Presenter(fixture, FakeImageStore.WithOne(), ai);
        await scene.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));

        scene.SetDraft("문장");
        var state = await scene.GradeAsync();
        Assert.Empty(state.Results);
        Assert.Contains(state.Notices, notice => notice.Key == NativeSceneText.GradeFailed);
        Assert.False(state.IsGrading);

        // The source turned a failed grade into a 0% card and row; native records nothing.
        ai.Fail = false;
        ai.ReturnNull = true;
        scene.SetDraft("문장");
        state = await scene.GradeAsync();
        Assert.Empty(state.Results);
        Assert.Contains(state.Notices, notice => notice.Key == NativeSceneText.GradeFailed);
        Assert.Equal(0, await CountUserActivitiesAsync(fixture, NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task HungGrade_ExitReturnsPromptlyStopsTheTimerAndRecordsNothing()
    {
        using var fixture = new NativeSliceFixture();
        var owner = NativeSliceFixture.UserA;
        await fixture.SeedAsync(owner);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeSceneAi { Hang = true };
        using var scene = Presenter(fixture, FakeImageStore.WithOne(), ai);
        await scene.OpenAsync(ChooseOwnLaunch(fixture, owner));
        scene.SetDraft("문장");
        var pending = scene.GradeAsync();
        Assert.True(scene.State.IsGrading);
        // Rule 4: changing the scene waits for the grade.
        Assert.False(scene.State.CanChangeScene);
        Assert.Equal(1, (await scene.NewSceneAsync()).Image!.Id);

        var watch = Stopwatch.StartNew();
        var exit = await scene.ExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.Null(exit.ErrorKey);
        // Exit cancels the hung grade; its late completion writes nothing.
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(NativeDestination.TodaysPlan, scene.State.Destination);
        Assert.Equal(0, await CountUserActivitiesAsync(fixture, owner));
        using var scope = fixture.Scope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .DailyPlanCompletions.AnyAsync(row => row.UserProfileId == owner
                && row.ActivityType == nameof(PlanActivityType.SceneDescription)));
    }

    [Fact]
    public async Task Back_ReturnsItsDestinationToTheCancelledGrade_EvenWhenItResumesFirst()
    {
        using var fixture = new NativeSliceFixture();
        var owner = NativeSliceFixture.UserA;
        await fixture.SeedAsync(owner);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeSceneAi { Hang = true };
        using var scene = Presenter(fixture, FakeImageStore.WithOne(), ai);
        await scene.OpenAsync(ChooseOwnLaunch(fixture, owner));
        var published = new List<NativeSceneState>();
        scene.StateChanged += published.Add;
        scene.SetDraft("문장");
        var pending = scene.GradeAsync();
        Assert.True(scene.State.IsGrading);

        ExitRace.HoldExitUntil(scene.ExitFence, () => pending);
        var exit = await scene.ExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var late = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.Equal(NativeDestination.TodaysPlan, late.Destination);
        Assert.Equal(NativeDestination.TodaysPlan, published[^1].Destination);
        Assert.Equal(0, await CountUserActivitiesAsync(fixture, owner));
    }

    [Fact]
    public async Task HungImageDownload_DoesNotBlockExit()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var store = FakeImageStore.WithOne();
        store.HangLoads = true;
        using var scene = Presenter(fixture, store, new FakeSceneAi());
        var open = scene.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));
        await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(NativeSceneImageLoad.Loading, scene.State.Image!.Load);
        // The input is usable while the image downloads.
        scene.SetDraft("문장");
        Assert.True(scene.State.CanGrade);

        var exit = await scene.ExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        await open.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ImageFailure_ShowsTheFailedImageButKeepsTheInput()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var store = FakeImageStore.WithOne();
        store.Bytes = null;
        using var scene = Presenter(fixture, store, new FakeSceneAi());
        var state = await scene.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));
        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(NativeSceneImageLoad.Failed, state.Image!.Load);
        scene.SetDraft("문장");
        Assert.True(scene.State.CanGrade);
    }

    [Fact]
    public async Task Gallery_SelectsAddsAndDeletesLikeTheSource()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var store = new FakeImageStore();
        store.Images.Add(new SceneImage { Id = 1, Url = "https://example.invalid/1.jpg" });
        store.Images.Add(new SceneImage { Id = 2, Url = "https://example.invalid/2.jpg", Description = "Two" });
        store.RandomPick = images => images[0];
        var ai = new FakeSceneAi();
        using var scene = Presenter(fixture, store, ai);
        await scene.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));
        scene.SetDraft("하나");
        await scene.GradeAsync();

        var state = await scene.OpenGalleryAsync();
        Assert.NotNull(state.Gallery);
        Assert.False(state.Gallery!.IsLoading);
        Assert.Equal([1, 2], state.Gallery.Items.Select(item => item.Id).ToArray());
        Assert.True(state.Gallery.Items[0].IsCurrent);
        Assert.All(state.Gallery.Items, item => Assert.Equal(NativeSceneImageLoad.Loaded, item.Load));

        // Tap switches the scene, clears results and closes the gallery.
        state = await scene.TapGalleryItemAsync(2);
        Assert.Null(state.Gallery);
        Assert.Equal(2, state.Image!.Id);
        Assert.Empty(state.Results);
        scene.SetDraft("둘");
        await scene.GradeAsync();
        Assert.Equal("Two", ai.Graded[^1].SceneDescription);

        // Add by URL: invalid is refused, https is saved and the list refreshed.
        await scene.OpenGalleryAsync();
        scene.SetNewImageUrl("not a url");
        Assert.True(scene.State.Gallery!.CanAddUrl);
        state = await scene.AddImageUrlAsync();
        Assert.Contains(state.Notices, notice => notice.Key == NativeSceneText.InvalidUrl);
        Assert.Equal(string.Empty, state.Gallery!.NewImageUrl);
        scene.SetNewImageUrl(" https://example.invalid/3.jpg ");
        state = await scene.AddImageUrlAsync();
        Assert.Contains(state.Notices, notice => notice.Key == NativeSceneText.ImageAdded);
        Assert.Equal("https://example.invalid/3.jpg", store.Images[^1].Url);
        Assert.Equal(3, state.Gallery!.Items.Length);

        // A picked photo is saved through the store.
        state = await scene.AddPhotoAsync(new NativeScenePickedPhoto(Jpeg, "image/jpeg"));
        Assert.Contains(state.Notices, notice => notice.Key == NativeSceneText.ImageAdded);
        Assert.Equal(4, state.Gallery!.Items.Length);
        Assert.Equal(Jpeg, state.Gallery.Items[^1].Bytes);
        state = await scene.PhotoPickFailedAsync("tooLarge");
        Assert.Contains(state.Notices, notice => notice.Key == NativeSceneText.PhotoTooLarge);

        // Select mode toggles checks; delete removes them and empties a deleted current scene.
        state = await scene.ToggleSelectModeAsync();
        Assert.True(state.Gallery!.IsSelecting);
        Assert.False(state.Gallery.CanDelete);
        await scene.TapGalleryItemAsync(1);
        await scene.TapGalleryItemAsync(2);
        state = await scene.TapGalleryItemAsync(1);
        Assert.Equal(1, state.Gallery!.SelectedCount);
        Assert.Equal(2, state.Image!.Id);
        state = await scene.DeleteSelectedAsync();
        Assert.Contains(state.Notices, notice => notice.Key == NativeSceneText.Deleted && notice.Argument == "1");
        Assert.False(state.Gallery!.IsSelecting);
        Assert.DoesNotContain(state.Gallery.Items, item => item.Id == 2);
        Assert.Equal(NativePhase.Empty, state.Phase);
        Assert.Null(state.Image);

        // Close clears select mode and the URL field.
        scene.SetNewImageUrl("https://example.invalid/x.jpg");
        state = await scene.CloseGalleryAsync();
        Assert.Null(state.Gallery);
        state = await scene.OpenGalleryAsync();
        Assert.Equal(string.Empty, state.Gallery!.NewImageUrl);
    }

    [Fact]
    public async Task AbsentOrChangedOwner_FailsClosedWithoutRows()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeSceneAi();
        var store = FakeImageStore.WithOne();

        using (var foreign = Presenter(fixture, store, ai))
            Assert.Equal(NativePhase.Blocked,
                (await foreign.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserB))).Phase);
        Assert.DoesNotContain(NativeSliceFixture.UserB, store.LoadOwners);

        using var scene = Presenter(fixture, store, ai);
        await scene.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));
        scene.SetDraft("문장");
        fixture.LoseOwner();
        Assert.Equal(NativePhase.Blocked, (await scene.GradeAsync()).Phase);
        Assert.Equal(NativePhase.Blocked, (await scene.OpenGalleryAsync()).Phase);
        Assert.Empty(ai.Graded);

        fixture.Activate(NativeSliceFixture.UserA);
        using var second = Presenter(fixture, store, ai);
        await second.OpenAsync(ChooseOwnLaunch(fixture, NativeSliceFixture.UserA));
        second.SetDraft("문장");
        ai.OnGrade = fixture.LoseOwner;
        Assert.Equal(NativePhase.Blocked, (await second.GradeAsync()).Phase);

        Assert.Equal(0, await CountUserActivitiesAsync(fixture, NativeSliceFixture.UserA));
        Assert.Equal(0, await CountUserActivitiesAsync(fixture, NativeSliceFixture.UserB));
        using var scope = fixture.Scope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .DailyPlanCompletions.AnyAsync(row => row.UserProfileId == NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task ImageStore_KeepsPhotosInTheOwnersFolderAndRefusesOtherOwners()
    {
        var ownerA = Guid.NewGuid().ToString();
        var ownerB = Guid.NewGuid().ToString();
        using var fixture = new NativeSliceFixture();
        fixture.RegisterUser("scene-a@example.invalid", ownerA);
        fixture.RegisterUser("scene-b@example.invalid", ownerB);
        await SignInAsync(fixture, "scene-a@example.invalid");
        var files = fixture.Provider.GetRequiredService<IFileSystemService>();
        using var store = new NativeSceneImageStore(
            fixture.Provider.GetRequiredService<IServiceScopeFactory>(),
            fixture.Provider.GetRequiredService<NativeActivityOwner>(), files,
            NullLogger<NativeSceneImageStore>.Instance);
        var none = CancellationToken.None;

        // Source AddImage takes a URL; only absolute HTTPS URLs can be shown natively.
        Assert.Null(await store.AddUrlAsync(ownerA, "http://example.invalid/a.jpg", none));
        Assert.Null(await store.AddUrlAsync(ownerA, "javascript:alert(1)", none));
        var url = await store.AddUrlAsync(ownerA, " https://example.invalid/a.jpg ", none);
        Assert.Equal("https://example.invalid/a.jpg", url!.Url);
        Assert.Null(await store.AddPhotoAsync(ownerA, new NativeScenePickedPhoto([1, 2, 3], "image/heic"), none));
        var photo = await store.AddPhotoAsync(ownerA, new NativeScenePickedPhoto(Jpeg, "image/jpeg"), none);
        Assert.NotNull(photo);
        Assert.StartsWith(NativeSceneImageStore.LocalScheme, photo.Url);
        Assert.Null(photo.Description);
        var path = store.LocalPath(ownerA, photo.Url)!;
        Assert.StartsWith(Path.Combine(files.AppDataDirectory, "SceneImages",
            Guid.Parse(ownerA).ToString("N")), path);
        Assert.True(File.Exists(path));
        Assert.Equal(Jpeg, await store.LoadBytesAsync(ownerA, photo, none));
        // A crafted local URL cannot leave the owner's folder.
        Assert.Null(store.LocalPath(ownerA, NativeSceneImageStore.LocalScheme + "../../x.jpg"));
        Assert.Equal(2, (await store.ListAsync(ownerA, none)).Count);

        // Another owner (not active) gets nothing and writes nothing.
        Assert.Empty(await store.ListAsync(ownerB, none));
        Assert.Null(await store.GetRandomAsync(ownerB, none));
        Assert.Null(await store.AddUrlAsync(ownerB, "https://example.invalid/b.jpg", none));
        Assert.Null(await store.LoadBytesAsync(ownerB, photo, none));
        Assert.Equal(0, await store.DeleteAsync(ownerB, [photo.Id], none));
        Assert.Empty(await store.ListAsync(string.Empty, none));

        Assert.Equal(1, await store.DeleteAsync(ownerA, [photo.Id], none));
        Assert.False(File.Exists(path));
        Assert.Equal(url.Id, Assert.Single(await store.ListAsync(ownerA, none)).Id);
        // The fixture removes only its own top-level files; remove the test's photo folder.
        Directory.Delete(Path.Combine(files.AppDataDirectory, "SceneImages"), recursive: true);
    }

    [Fact]
    public async Task SceneAi_RendersTheSourceGradeMyDescriptionPrompt()
    {
        using var fixture = new NativeSliceFixture(sentenceGradingEnabled: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = fixture.Provider.GetRequiredService<INativeSceneAi>();

        var grade = await ai.GradeAsync(NativeSliceFixture.UserA, "공원이에요", "A sunny park",
            "Korean", "English", CancellationToken.None);

        Assert.NotNull(grade);
        var prompt = Assert.Single(fixture.SentenceGateway.Prompts);
        Assert.Contains("I'm learning Korean and have written this to describe", prompt);
        Assert.Contains("\"공원이에요\"", prompt);
        Assert.Contains("generated the following description: \"A sunny park\"", prompt);
        Assert.Contains("English meaning", prompt);
    }

    private static NativeScenePresenter Presenter(NativeSliceFixture fixture,
        INativeSceneImageStore store, INativeSceneAi ai) =>
        ActivatorUtilities.CreateInstance<NativeScenePresenter>(fixture.Provider, store, ai);

    private static NativeActivityLaunch ChooseOwnLaunch(NativeSliceFixture fixture, string owner) =>
        new(Scene, owner, new DateOnly(2026, 9, 28), null,
            [fixture.ResourceId(owner)], null, [], false, fixture.ResourceId(owner),
            PlanActivityType.SceneDescription);

    private static string WordTarget(NativeSliceFixture fixture, string owner, int index)
    {
        using var scope = fixture.Scope();
        return scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyWords.AsNoTracking().Single(word => word.Id == fixture.WordId(owner, index))
            .TargetLanguageTerm!;
    }

    private static async Task<int> CountUserActivitiesAsync(NativeSliceFixture fixture, string owner)
    {
        using var scope = fixture.Scope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .UserActivities.CountAsync(row => row.UserProfileId == owner);
    }

    private static async Task SignInAsync(NativeSliceFixture fixture, string email) =>
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(email, "fixture-password")).Phase);

    private sealed class FakeImageStore : INativeSceneImageStore
    {
        public List<SceneImage> Images { get; } = [];
        public List<string> LoadOwners { get; } = [];
        public Func<List<SceneImage>, SceneImage>? RandomPick { get; set; }
        public byte[]? Bytes { get; set; } = Png;
        public bool HangLoads { get; set; }
        public TaskCompletionSource LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static FakeImageStore WithOne()
        {
            var store = new FakeImageStore();
            store.Images.Add(new SceneImage { Id = 1, Url = "https://example.invalid/1.jpg" });
            return store;
        }

        public Task<IReadOnlyList<SceneImage>> ListAsync(string owner, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SceneImage>>([.. Images]);

        public Task<SceneImage?> GetRandomAsync(string owner, CancellationToken cancellationToken) =>
            Task.FromResult(Images.Count == 0 ? null
                : RandomPick?.Invoke(Images) ?? Images[0]);

        public Task<SceneImage?> AddUrlAsync(string owner, string url, CancellationToken cancellationToken)
        {
            var image = new SceneImage { Id = Images.Max(item => item.Id) + 1, Url = url.Trim() };
            Images.Add(image);
            return Task.FromResult<SceneImage?>(image);
        }

        public Task<SceneImage?> AddPhotoAsync(string owner, NativeScenePickedPhoto photo,
            CancellationToken cancellationToken)
        {
            var image = new SceneImage { Id = Images.Max(item => item.Id) + 1, Url = "native-scene-image:x.jpg" };
            Images.Add(image);
            return Task.FromResult<SceneImage?>(image);
        }

        public Task<int> DeleteAsync(string owner, IReadOnlyCollection<int> ids, CancellationToken cancellationToken) =>
            Task.FromResult(Images.RemoveAll(image => ids.Contains(image.Id)));

        public async Task<byte[]?> LoadBytesAsync(string owner, SceneImage image,
            CancellationToken cancellationToken)
        {
            LoadOwners.Add(owner);
            LoadStarted.TrySetResult();
            if (HangLoads)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            return Bytes;
        }
    }

    private sealed class FakeSceneAi : INativeSceneAi
    {
        public List<(string Owner, string Description, string SceneDescription, string Target, string Native)> Graded { get; } = [];
        public bool Fail { get; set; }
        public bool ReturnNull { get; set; }
        public bool Hang { get; set; }
        public double Accuracy { get; set; } = 82;
        public List<VocabularyAnalysis>? Analysis { get; set; }
        public Action? OnGrade { get; set; }

        public async Task<GradeResponse?> GradeAsync(string owner, string description,
            string sceneDescription, string targetLanguage, string nativeLanguage,
            CancellationToken cancellationToken)
        {
            if (Fail)
                throw new HttpRequestException("Synthetic grader offline.");
            Graded.Add((owner, description, sceneDescription, targetLanguage, nativeLanguage));
            OnGrade?.Invoke();
            if (Hang)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            return ReturnNull ? null : new GradeResponse
            {
                Accuracy = Accuracy,
                Fluency = 64,
                AccuracyExplanation = "Accuracy note",
                FluencyExplanation = "Fluency note",
                GrammarNotes = new GrammarNotes
                {
                    RecommendedTranslation = "Recommended",
                    Explanation = "Grammar note"
                },
                VocabularyAnalysis = Analysis
            };
        }
    }
}
