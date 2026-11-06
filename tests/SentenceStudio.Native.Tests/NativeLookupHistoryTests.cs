using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Models;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Native.Features.Lookups;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Lookup history on native (Captain 2026-11-04, decision 4 B), against real SQLite and the
/// shared repositories: Writing and Reading record each successful lookup with its translation
/// for the launch owner; a failed lookup records nothing; Recent lookups (one shared coordinator
/// for iOS and Android) lists only the active learner's lookups and adds one to vocabulary.
/// </summary>
public sealed class NativeLookupHistoryTests
{
    [Fact]
    public async Task Writing_SuccessfulTranslation_IsRecordedForTheLaunchOwner_AndQueuedForSync()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 4);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        await MakeWordsAsync(fixture, NativeSliceFixture.UserA);
        using var writing = ActivatorUtilities.CreateInstance<NativeWritingPresenter>(
            fixture.Provider, new FakeWritingAi());
        await writing.OpenAsync(Launch(fixture, NativeActivityCatalog.Writing, PlanActivityType.Writing));
        var pending = fixture.Provider.GetRequiredService<NativePendingWrites>();

        writing.SetDraft("새 문장");
        var state = await writing.TranslateAsync();

        Assert.Equal("translated: 새 문장", Assert.Single(state.Notices).Argument);
        var lookup = Assert.Single(await LookupsAsync(fixture));
        Assert.Equal(NativeSliceFixture.UserA, lookup.UserProfileId);
        Assert.Equal("새 문장", lookup.Text);
        Assert.Equal("translated: 새 문장", lookup.Translation);
        Assert.Equal(VocabularyLookupActivity.Writing, lookup.Activity);
        Assert.Equal(VocabularyLookupDirection.ToNative, lookup.Direction);
        Assert.Equal(fixture.ResourceId(NativeSliceFixture.UserA), lookup.ResourceId);
        Assert.Equal("Korean", lookup.Language);
        Assert.True(pending.IsPending(NativeSliceFixture.UserA));

        // The learner's own word by its native term: a lookup into the target language.
        writing.SetDraft($"native-{NativeSliceFixture.UserA}-0");
        await writing.TranslateAsync();
        var reverse = Assert.Single(await LookupsAsync(fixture),
            row => row.Direction == VocabularyLookupDirection.ToTarget);
        Assert.Equal($"target-{NativeSliceFixture.UserA}-0", reverse.TargetTerm);
    }

    [Fact]
    public async Task Writing_EnglishTranslationForAKoreanSpeaker_IsShownButNotRecorded()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 4);
        await SetLanguagesAsync(fixture, NativeSliceFixture.UserA, native: "Korean", target: "English");
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        await MakeWordsAsync(fixture, NativeSliceFixture.UserA);
        using var writing = ActivatorUtilities.CreateInstance<NativeWritingPresenter>(
            fixture.Provider, new FakeWritingAi { Reply = "an apple" });
        await writing.OpenAsync(Launch(fixture, NativeActivityCatalog.Writing, PlanActivityType.Writing));

        writing.SetDraft("사과");
        var state = await writing.TranslateAsync();

        Assert.Equal("an apple", Assert.Single(state.Notices).Argument);
        Assert.Empty(await LookupsAsync(fixture));

        // The learner's own word still has known sides.
        writing.SetDraft($"target-{NativeSliceFixture.UserA}-1");
        await writing.TranslateAsync();
        var owned = Assert.Single(await LookupsAsync(fixture));
        Assert.Equal(VocabularyLookupDirection.ToNative, owned.Direction);
        Assert.Equal($"target-{NativeSliceFixture.UserA}-1", owned.TargetTerm);
    }

    [Fact]
    public async Task Reading_RememberWord_AlreadyInTheResource_IsShownAsInVocabulary_NotAnError()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        await SetTranscriptAsync(fixture, NativeSliceFixture.UserA, "바다가 좋아요.");
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var reading = ActivatorUtilities.CreateInstance<NativeReadingPresenter>(fixture.Provider,
            new NoAudio(), new NoPlayers(), new Translator("sea"));
        var state = await reading.OpenAsync(Launch(fixture, NativeActivityCatalog.Reading, PlanActivityType.Reading));
        var word = state.Sentences.SelectMany(sentence => sentence.Tokens).First(token => token.Text == "바다가");
        state = await reading.TapWordAsync(word.Id);
        Assert.True(state.WordPanel!.CanSave);
        // The word reaches the resource after the page loaded (another device, the web).
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = "synced-word", TargetLanguageTerm = "바다가", NativeLanguageTerm = "sea",
                Language = "Korean", UserProfileId = NativeSliceFixture.UserA
            });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = "synced-mapping", ResourceId = fixture.ResourceId(NativeSliceFixture.UserA),
                VocabularyWordId = "synced-word"
            });
            await db.SaveChangesAsync();
        }

        state = await reading.SaveWordAsync();

        Assert.Equal(NativeReadingText.AlreadyInResource, state.Notice?.Key);
        Assert.Equal(NativeMessageKind.Info, state.Notice?.Kind);
        Assert.Equal("Synthetic vocabulary", state.Notice?.Argument);
        Assert.True(state.WordPanel!.IsInVocabulary);
        Assert.False(state.WordPanel.IsSaving);
        Assert.Contains(state.Sentences.SelectMany(sentence => sentence.Tokens),
            token => token.Text == "바다가" && token.IsVocabulary);
        using var check = fixture.Scope();
        var verify = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await verify.VocabularyWords.CountAsync(row => row.TargetLanguageTerm == "바다가"));
        Assert.False(await verify.UserActivities.AnyAsync(row => row.Input == "Added vocabulary: 바다가"));
    }

    [Fact]
    public async Task RecentLookups_AnUnexpectedFailureDuringALoad_OffersRetryNotTheSpinner()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        await fixture.Provider.GetRequiredService<VocabularyLookupRepository>().RecordAsync(NativeSliceFixture.UserA,
            new("바다", "sea", VocabularyLookupOrigin.OwnedTargetTerm, VocabularyLookupActivity.Reading));
        using var lookups = Coordinator(fixture);
        Assert.True(await lookups.OpenAsync(NativeSliceFixture.UserA));

        // The head's render throws as the reload shows its spinner.
        var failRender = true;
        lookups.Changed += () =>
        {
            if (!failRender)
                return;
            failRender = false;
            throw new InvalidOperationException("render");
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => lookups.ReloadAsync());
        Assert.Equal(RecentLookupsPhase.Loading, lookups.State.Phase);

        lookups.ReportUnexpectedFailure();
        var state = lookups.State;
        Assert.Equal(RecentLookupsPhase.LoadError, state.Phase);
        Assert.Equal(NativeText.UnexpectedError, state.Notice?.Key);
        await lookups.ReloadAsync();
        Assert.Equal(RecentLookupsPhase.Ready, lookups.State.Phase);
    }

    [Fact]
    public async Task RecentLookups_ACancelledLoad_NeverLeavesTheSpinner()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        await fixture.Provider.GetRequiredService<VocabularyLookupRepository>().RecordAsync(NativeSliceFixture.UserA,
            new("바다", "sea", VocabularyLookupOrigin.OwnedTargetTerm, VocabularyLookupActivity.Reading));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // The first load cancelled: Retry is offered.
        using (var first = Coordinator(fixture))
        {
            await first.OpenAsync(NativeSliceFixture.UserA, cancelled.Token);
            Assert.Equal(RecentLookupsPhase.LoadError, first.State.Phase);
            await first.ReloadAsync();
            Assert.Equal(RecentLookupsPhase.Ready, first.State.Phase);
        }

        // A later reload cancelled: the list shown before stays.
        using var lookups = Coordinator(fixture);
        Assert.True(await lookups.OpenAsync(NativeSliceFixture.UserA));
        await lookups.ReloadAsync(cancelled.Token);
        var state = lookups.State;
        Assert.Equal(RecentLookupsPhase.Ready, state.Phase);
        Assert.Equal("바다", Assert.Single(state.Rows).Text);
    }

    [Fact]
    public async Task Writing_FailedTranslation_RecordsNothing()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 4);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        await MakeWordsAsync(fixture, NativeSliceFixture.UserA);
        var ai = new FakeWritingAi { Fail = true };
        using var writing = ActivatorUtilities.CreateInstance<NativeWritingPresenter>(fixture.Provider, ai);
        await writing.OpenAsync(Launch(fixture, NativeActivityCatalog.Writing, PlanActivityType.Writing));

        writing.SetDraft("새 문장");
        Assert.Equal(NativeWritingText.TranslateFailed, Assert.Single((await writing.TranslateAsync()).Notices).Key);
        ai.Fail = false;
        ai.Reply = "   ";
        await writing.TranslateAsync();

        Assert.Empty(await LookupsAsync(fixture));
    }

    [Fact]
    public async Task Reading_SuccessfulLookup_RecordsTheActivityRowAndTheLookup_AFailedOneNothing()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        await SetTranscriptAsync(fixture, NativeSliceFixture.UserA, "바다가 좋아요.");
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var translator = new Translator("sea");
        using var reading = ActivatorUtilities.CreateInstance<NativeReadingPresenter>(fixture.Provider,
            new NoAudio(), new NoPlayers(), translator);
        var state = await reading.OpenAsync(Launch(fixture, NativeActivityCatalog.Reading, PlanActivityType.Reading));
        var word = state.Sentences.SelectMany(sentence => sentence.Tokens).First(token => token.Text == "바다가");

        state = await reading.TapWordAsync(word.Id);

        Assert.Equal("sea", state.WordPanel!.Definition);
        var lookup = Assert.Single(await LookupsAsync(fixture));
        Assert.Equal("바다가", lookup.Text);
        Assert.Equal("sea", lookup.Translation);
        Assert.Equal(VocabularyLookupActivity.Reading, lookup.Activity);
        Assert.Equal(fixture.ResourceId(NativeSliceFixture.UserA), lookup.ResourceId);
        using (var scope = fixture.Scope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .UserActivities.AnyAsync(row => row.Input == "Dictionary lookup: 바다가"));

        translator.Throws = true;
        var other = state.Sentences.SelectMany(sentence => sentence.Tokens).First(token => token.Text == "좋아요");
        state = await reading.TapWordAsync(other.Id);
        Assert.Equal(NativeReadingPresenter.TranslationUnavailableMarker, state.WordPanel!.Definition);
        Assert.Single(await LookupsAsync(fixture));
    }

    [Fact]
    public async Task RecentLookups_ShowOnlyTheActiveLearnersLookups_AndAddOneToVocabulary()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 0);
        fixture.Activate(NativeSliceFixture.UserB);
        var repository = fixture.Provider.GetRequiredService<VocabularyLookupRepository>();
        Assert.Equal(VocabularyLookupRecordStatus.Recorded, await repository.RecordAsync(NativeSliceFixture.UserB,
            new("강", "B's river", VocabularyLookupOrigin.TranslatedToEnglish, VocabularyLookupActivity.Writing)));
        fixture.Activate(NativeSliceFixture.UserA);
        Assert.Equal(VocabularyLookupRecordStatus.Recorded, await repository.RecordAsync(NativeSliceFixture.UserA,
            new("바다", "sea", VocabularyLookupOrigin.TranslatedToEnglish, VocabularyLookupActivity.Reading,
                fixture.ResourceId(NativeSliceFixture.UserA))));

        using var lookups = Coordinator(fixture);
        Assert.True(await lookups.OpenAsync(NativeSliceFixture.UserA));
        var state = lookups.State;
        Assert.Equal(RecentLookupsPhase.Ready, state.Phase);
        var row = Assert.Single(state.Rows);
        Assert.Equal("바다", row.Text);
        Assert.Equal("Synthetic vocabulary", row.ResourceTitle);
        var destination = Assert.Single(row.Destinations);
        Assert.Equal("native.lookups.add." + row.AutomationKey + "." + RecentLookupsAccess.Key(destination.ResourceId),
            RecentLookupsAccess.Destination(row, destination));

        await lookups.AddAsync(row.Id, destination.ResourceId);
        state = lookups.State;
        Assert.Null(state.AddingId);
        Assert.Equal("Lookups_Added", state.Notice?.Key);
        Assert.Equal("Synthetic vocabulary", state.Notice?.Argument);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var created = await db.VocabularyWords.AsNoTracking().SingleAsync(word => word.TargetLanguageTerm == "바다");
            Assert.Equal("sea", created.NativeLanguageTerm);
            Assert.Equal(NativeSliceFixture.UserA, created.UserProfileId);
            Assert.True(await db.ResourceVocabularyMappings.AnyAsync(mapping =>
                mapping.VocabularyWordId == created.Id
                && mapping.ResourceId == fixture.ResourceId(NativeSliceFixture.UserA)));
        }

        await lookups.AddAsync(row.Id, destination.ResourceId);
        Assert.Equal("Lookups_AlreadyPresent", lookups.State.Notice?.Key);
        // A resource that is not one of the row's destinations is never written to.
        await lookups.AddAsync(row.Id, fixture.ResourceId(NativeSliceFixture.UserB));
        using (var scope = fixture.Scope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .VocabularyWords.CountAsync());

        // Another learner becomes active: the open list is blocked and emptied.
        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Equal(RecentLookupsPhase.Blocked, lookups.State.Phase);
        Assert.Empty(lookups.State.Rows);
    }

    [Fact]
    public async Task RecentLookups_RefuseAnAbsentOrUntrustedOwner()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 0);
        using (var nobody = Coordinator(fixture))
            Assert.False(await nobody.OpenAsync(NativeSliceFixture.UserA));

        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var foreign = Coordinator(fixture);
        Assert.False(await foreign.OpenAsync(NativeSliceFixture.UserB));
        Assert.False(foreign.IsOpen);
        Assert.Empty(foreign.State.Rows);
    }

    [Fact]
    public void SourceLine_ShowsActivityResourceAndLocalTime()
    {
        var row = new RecentLookupRow("lk:1", "lk-1", "바다", "sea", VocabularyLookupActivity.Reading,
            "Story", new DateTime(2026, 11, 4, 15, 30, 0, DateTimeKind.Utc), []);
        var line = RecentLookupsCoordinator.SourceLine(row, key => key == "Activity_Reading" ? "Reading" : key,
            System.Globalization.CultureInfo.InvariantCulture, TimeZoneInfo.Utc);
        Assert.Equal("Reading · Story · 11/04/2026 15:30", line);
    }

    private static RecentLookupsCoordinator Coordinator(NativeSliceFixture fixture) =>
        new(fixture.Provider.GetRequiredService<VocabularyLookupRepository>(), fixture.UserScope,
            fixture.Provider.GetRequiredService<NativePendingWrites>(),
            NullLogger<RecentLookupsCoordinator>.Instance);

    private static NativeActivityLaunch Launch(NativeSliceFixture fixture, string route, PlanActivityType type) =>
        new(route, NativeSliceFixture.UserA, new DateOnly(2026, 11, 4), null,
            [fixture.ResourceId(NativeSliceFixture.UserA)], null, [], false,
            fixture.ResourceId(NativeSliceFixture.UserA), type);

    private static async Task<List<VocabularyLookup>> LookupsAsync(NativeSliceFixture fixture)
    {
        using var scope = fixture.Scope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyLookups.AsNoTracking().ToListAsync();
    }

    /// <summary>Writing uses only Word entries.</summary>
    private static async Task MakeWordsAsync(NativeSliceFixture fixture, string owner)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        foreach (var word in await db.VocabularyWords.ToListAsync())
        {
            word.LexicalUnitType = LexicalUnitType.Word;
            word.UserProfileId = owner;
        }
        await db.SaveChangesAsync();
    }

    private static async Task SetLanguagesAsync(NativeSliceFixture fixture, string owner,
        string native, string target)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.UserProfiles.SingleAsync(row => row.Id == owner);
        profile.NativeLanguage = native;
        profile.TargetLanguage = target;
        await db.SaveChangesAsync();
    }

    private static async Task SetTranscriptAsync(NativeSliceFixture fixture, string owner, string transcript)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var resource = await db.LearningResources.SingleAsync(item => item.Id == fixture.ResourceId(owner));
        resource.MediaType = "Text";
        resource.Transcript = transcript;
        await db.SaveChangesAsync();
    }

    private static async Task SignInAsync(NativeSliceFixture fixture, string email) =>
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(email, "fixture-password")).Phase);

    private sealed class FakeWritingAi : INativeWritingAi
    {
        public bool Fail { get; set; }
        public string? Reply { get; set; }

        public Task<GradeResponse?> GradeAsync(string owner, string sentence,
            string targetLanguage, string nativeLanguage, CancellationToken cancellationToken) =>
            Task.FromResult<GradeResponse?>(null);

        public Task<string?> TranslateAsync(string owner, string text, CancellationToken cancellationToken) =>
            Fail ? throw new HttpRequestException("Synthetic translator offline.")
                : Task.FromResult<string?>(Reply ?? $"translated: {text}");
    }

    private sealed class Translator(string reply) : IOwnerBoundNativeAiGateway
    {
        public bool Throws { get; set; }

        public Task<T?> SendPromptAsync<T>(string owner, string prompt, CancellationToken cancellationToken) =>
            Throws ? throw new HttpRequestException("Synthetic translator offline.")
                : Task.FromResult((T?)(object)reply);
    }

    private sealed class NoAudio : INativeReadingAudioSource
    {
        public Task<TimestampedAudio?> GetAsync(string owner, LearningResource resource, CancellationToken cancellationToken) =>
            Task.FromResult<TimestampedAudio?>(null);
    }

    private sealed class NoPlayers : IAudioPlayerFactory
    {
        public bool CanPlayLocally => false;
        public IUiAudioPlayer? CreatePlayer(Stream audioStream) => null;
    }
}
