using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Data;

/// <summary>
/// Lookup history (Captain 2026-11-04, decision 4 B) on real SQLite with two learners: one row
/// per learner, language, direction and normalized text; every read and write is the active
/// learner's only; "Add to vocabulary" creates or links the learner's word in their resource.
/// </summary>
public sealed class VocabularyLookupRepositoryTests : IDisposable
{
    private const string LearnerA = "learner-a";
    private const string LearnerB = "learner-b";
    /// <summary>A Korean speaker learning English: the Translate prompt's English is their target language.</summary>
    private const string LearnerKo = "learner-ko";
    private static readonly DateTime Start = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ActiveLearner _learner = new() { Id = LearnerA };
    private readonly SteppingClock _clock = new(Start);
    private readonly ServiceProvider _provider;

    public VocabularyLookupRepositoryTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddSingleton<IUserScopeProvider>(_learner);
        services.AddSingleton<IFileSystemService>(new NoFiles());
        services.AddSingleton<LearningResourceRepository>();
        _provider = services.BuildServiceProvider();
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
        db.UserProfiles.AddRange(
            new UserProfile { Id = LearnerA, Name = "A", TargetLanguage = "Korean", NativeLanguage = "English" },
            new UserProfile { Id = LearnerB, Name = "B", TargetLanguage = "Japanese", NativeLanguage = "English" },
            new UserProfile { Id = LearnerKo, Name = "K", TargetLanguage = "English", NativeLanguage = "Korean" });
        db.LearningResources.AddRange(
            Resource("res-a", LearnerA, "A's story", "Korean"),
            Resource("res-a-smart", LearnerA, "Daily words", "Korean", smart: true),
            Resource("res-a-ja", LearnerA, "A's Japanese", "Japanese"),
            Resource("res-b", LearnerB, "B's story", "Korean"));
        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private VocabularyLookupRepository Repository() =>
        new(_provider, NullLogger<VocabularyLookupRepository>.Instance, clock: _clock);

    private static VocabularyLookupEntry Reading(string text, string translation, string? resource = "res-a") =>
        new(text, translation, VocabularyLookupOrigin.TranslatedToEnglish, VocabularyLookupActivity.Reading, resource);

    [Fact]
    public async Task RepeatLookup_UpdatesItsOneRow_WithTheLatestTranslationAndTime_KeepingTheFirst()
    {
        (await Repository().RecordAsync(LearnerA, Reading("물", "water"))).Should().Be(VocabularyLookupRecordStatus.Recorded);
        _clock.Advance(TimeSpan.FromMinutes(5));
        (await Repository().RecordAsync(LearnerA, new VocabularyLookupEntry(" 물 ", "water (drink)",
            VocabularyLookupOrigin.OwnedTargetTerm, VocabularyLookupActivity.Writing))).Should().Be(VocabularyLookupRecordStatus.Recorded);

        var row = (await RowsAsync()).Should().ContainSingle().Subject;
        row.Id.Should().Be(SyncIds.VocabularyLookup(LearnerA, "Korean", VocabularyLookupDirection.ToNative, "물"));
        row.UserProfileId.Should().Be(LearnerA);
        row.Text.Should().Be("물");
        row.Translation.Should().Be("water (drink)");
        row.Activity.Should().Be(VocabularyLookupActivity.Writing);
        row.ResourceId.Should().BeNull("the last lookup came from no resource");
        row.Language.Should().Be("Korean", "with no resource the learner's target language is used");
        row.FirstLookedUpAt.Should().Be(Start);
        row.LastLookedUpAt.Should().Be(Start.AddMinutes(5));
    }

    [Fact]
    public void LookupIds_AreTheSameForTheSameNormalizedText_AndDifferPerLearnerLanguageAndDirection()
    {
        var id = SyncIds.VocabularyLookup(LearnerA, "Korean", VocabularyLookupDirection.ToNative, "Hello   World");
        id.Should().StartWith("lk:").And.HaveLength(35);
        SyncIds.VocabularyLookup(LearnerA, " korean ", VocabularyLookupDirection.ToNative, "  hello world ").Should().Be(id);
        SyncIds.VocabularyLookup(LearnerB, "Korean", VocabularyLookupDirection.ToNative, "hello world").Should().NotBe(id);
        SyncIds.VocabularyLookup(LearnerA, "Japanese", VocabularyLookupDirection.ToNative, "hello world").Should().NotBe(id);
        SyncIds.VocabularyLookup(LearnerA, "Korean", VocabularyLookupDirection.ToTarget, "hello world").Should().NotBe(id);
        // Composed and decomposed Hangul are one lookup.
        SyncIds.NormalizeLookupText("물").Should().Be("물");
        // Fixed values other clients (the Flutter port) check against (docs/sync-all-tables-plan.md).
        SyncIds.VocabularyLookup("owner-1", "Korean", VocabularyLookupDirection.ToNative, "물")
            .Should().Be("lk:f47f2616cfde663868fd1963db3d1457");
        SyncIds.VocabularyLookup("owner-1", "Korean", VocabularyLookupDirection.ToTarget, "Hello   World")
            .Should().Be("lk:d763ec34f12a61897324811212e68380");
    }

    [Fact]
    public async Task AbsentOrOtherLearner_RecordsNothing_AndReadsNothing()
    {
        _learner.Id = null;
        (await Repository().RecordAsync(LearnerA, Reading("물", "water"))).Should().Be(VocabularyLookupRecordStatus.Refused);
        (await Repository().GetRecentAsync(LearnerA)).Items.Should().BeEmpty();

        _learner.Id = LearnerA;
        (await Repository().RecordAsync(string.Empty, Reading("물", "water"))).Should().Be(VocabularyLookupRecordStatus.Refused);
        (await Repository().RecordAsync(LearnerB, Reading("물", "water"))).Should().Be(VocabularyLookupRecordStatus.Refused,
            "a caller acting for B while A is active is refused");
        (await Repository().GetRecentAsync(LearnerB)).Items.Should().BeEmpty();
        (await RowsAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(VocabularyLookupOrigin.OwnedTargetTerm, "Korean", VocabularyLookupDirection.ToNative)]
    [InlineData(VocabularyLookupOrigin.OwnedNativeTerm, "Korean", VocabularyLookupDirection.ToTarget)]
    [InlineData(VocabularyLookupOrigin.OwnedTargetTerm, "English", VocabularyLookupDirection.ToNative)]
    [InlineData(VocabularyLookupOrigin.OwnedNativeTerm, "English", VocabularyLookupDirection.ToTarget)]
    [InlineData(VocabularyLookupOrigin.TranslatedToEnglish, "English", VocabularyLookupDirection.ToNative)]
    [InlineData(VocabularyLookupOrigin.TranslatedToEnglish, " english ", VocabularyLookupDirection.ToNative)]
    public void Direction_IsKnownForOwnedMatchesAndForEnglishFromAnEnglishSpeaker(
        VocabularyLookupOrigin origin, string nativeLanguage, VocabularyLookupDirection expected) =>
        VocabularyLookupRepository.DirectionFor(origin, nativeLanguage, "물", "water").Should().Be(expected);

    [Theory]
    [InlineData("Korean")]
    [InlineData("Japanese")]
    [InlineData("")]
    [InlineData(null)]
    public void Direction_OfAnEnglishTranslation_IsUnknownForAnyOtherNativeLanguage(string? nativeLanguage) =>
        VocabularyLookupRepository.DirectionFor(VocabularyLookupOrigin.TranslatedToEnglish,
            nativeLanguage, "water", "물").Should().BeNull(
            "the prompt translates into English, which is this learner's target language or neither side");

    [Fact]
    public void Direction_OfAnEnglishTranslationOfEnglishText_IsUnknown() =>
        VocabularyLookupRepository.DirectionFor(VocabularyLookupOrigin.TranslatedToEnglish,
            "English", " Hello  world ", "hello world").Should().BeNull("English typed in gives English back");

    [Fact]
    public async Task KoreanSpeaker_AnEnglishTranslationIsNotRecorded_ButTheirOwnWordMatchesAre()
    {
        _learner.Id = LearnerKo;
        (await Repository().RecordAsync(LearnerKo, new VocabularyLookupEntry("apple", "사과",
                VocabularyLookupOrigin.TranslatedToEnglish, VocabularyLookupActivity.Writing)))
            .Should().Be(VocabularyLookupRecordStatus.DirectionUnknown);
        (await RowsAsync()).Should().BeEmpty("a word with unknown sides could be added to vocabulary swapped");

        (await Repository().RecordAsync(LearnerKo, new VocabularyLookupEntry("apple", "사과",
                VocabularyLookupOrigin.OwnedTargetTerm, VocabularyLookupActivity.Reading)))
            .Should().Be(VocabularyLookupRecordStatus.Recorded);
        (await Repository().RecordAsync(LearnerKo, new VocabularyLookupEntry("사과", "apple",
                VocabularyLookupOrigin.OwnedNativeTerm, VocabularyLookupActivity.Writing)))
            .Should().Be(VocabularyLookupRecordStatus.Recorded);
        var rows = await RowsAsync();
        rows.Should().HaveCount(2).And.OnlyContain(row => row.TargetTerm == "apple" && row.NativeTerm == "사과");
        rows.Should().OnlyContain(row => row.Language == "English");
    }

    [Fact]
    public async Task EmptyTextOrTranslation_IsNotALookup()
    {
        (await Repository().RecordAsync(LearnerA, Reading("물", "  "))).Should().Be(VocabularyLookupRecordStatus.Invalid);
        (await Repository().RecordAsync(LearnerA, Reading("   ", "water"))).Should().Be(VocabularyLookupRecordStatus.Invalid);
        (await RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AnotherLearnersResource_IsNotStored_AndTheResourceLanguageIsUsed()
    {
        (await Repository().RecordAsync(LearnerA, Reading("물", "water", resource: "res-b"))).Should().Be(VocabularyLookupRecordStatus.Recorded);
        (await Repository().RecordAsync(LearnerA, Reading("水", "water", resource: "res-a-ja"))).Should().Be(VocabularyLookupRecordStatus.Recorded);

        var rows = await RowsAsync();
        rows.Single(row => row.Text == "물").ResourceId.Should().BeNull();
        var japanese = rows.Single(row => row.Text == "水");
        japanese.ResourceId.Should().Be("res-a-ja");
        japanese.Language.Should().Be("Japanese");
    }

    [Fact]
    public async Task TwoLearners_EachSeeOnlyTheirOwnLookups_NewestFirst_AndTheirOwnResources()
    {
        await Repository().RecordAsync(LearnerA, Reading("물", "water"));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Repository().RecordAsync(LearnerA, Reading("불", "fire", resource: null));
        _learner.Id = LearnerB;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Repository().RecordAsync(LearnerB, Reading("물", "water (B)", resource: "res-b"));

        var forB = await Repository().GetRecentAsync(LearnerB);
        forB.Items.Should().ContainSingle().Which.Lookup.Translation.Should().Be("water (B)");
        forB.Items[0].ResourceTitle.Should().Be("B's story");
        forB.Destinations.Select(d => d.ResourceId).Should().Equal("res-b");

        _learner.Id = LearnerA;
        var forA = await Repository().GetRecentAsync(LearnerA);
        forA.Items.Select(item => item.Lookup.Text).Should().Equal("불", "물");
        forA.Items.Should().OnlyContain(item => item.Lookup.UserProfileId == LearnerA);
        forA.Items[1].ResourceTitle.Should().Be("A's story");
        forA.Destinations.Select(d => d.ResourceId).Should().BeEquivalentTo(["res-a", "res-a-ja"],
            "smart resources and another learner's resources are not destinations");
        forA.DestinationsFor(forA.Items[1].Lookup).Select(d => d.ResourceId).Should().Equal("res-a");
        (await Repository().GetRecentAsync(LearnerA, limit: 1)).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task AddToVocabulary_CreatesTheWordInTheResource_ThenReportsItIsAlreadyThere()
    {
        await Repository().RecordAsync(LearnerA, Reading("물", "water"));
        var lookup = (await RowsAsync()).Single();

        (await Repository().AddToVocabularyAsync(LearnerA, lookup.Id, "res-a")).Should().Be(ResourceVocabularyWriteStatus.Success);
        (await Repository().AddToVocabularyAsync(LearnerA, lookup.Id, "res-a")).Should().Be(ResourceVocabularyWriteStatus.AlreadyPresent);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var word = await db.VocabularyWords.AsNoTracking().SingleAsync();
        word.TargetLanguageTerm.Should().Be("물");
        word.NativeLanguageTerm.Should().Be("water");
        word.UserProfileId.Should().Be(LearnerA);
        word.Language.Should().Be("Korean");
        (await db.ResourceVocabularyMappings.AsNoTracking().SingleAsync()).ResourceId.Should().Be("res-a");
        (await db.VocabularyLookups.CountAsync()).Should().Be(1, "the lookup is kept");
    }

    [Fact]
    public async Task AddToVocabulary_LinksTheLearnersExistingWord_WithoutDuplicating_AndMapsANativeLookup()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.LearningResources.Add(Resource("res-a2", LearnerA, "Second", "Korean"));
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = "word-water", TargetLanguageTerm = "물", NativeLanguageTerm = "water",
                Language = "Korean", UserProfileId = LearnerA
            });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = "map-1", ResourceId = "res-a2", VocabularyWordId = "word-water"
            });
            await db.SaveChangesAsync();
        }
        // Writing looked up the English "water": the translation is the target term.
        await Repository().RecordAsync(LearnerA, new VocabularyLookupEntry("water", "물",
            VocabularyLookupOrigin.OwnedNativeTerm, VocabularyLookupActivity.Writing));
        var lookup = (await RowsAsync()).Single();

        (await Repository().AddToVocabularyAsync(LearnerA, lookup.Id, "res-a")).Should().Be(ResourceVocabularyWriteStatus.Success);

        await using var check = _provider.CreateAsyncScope();
        var verify = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await verify.VocabularyWords.CountAsync()).Should().Be(1, "the learner's existing word is linked, not duplicated");
        (await verify.ResourceVocabularyMappings.AsNoTracking()
            .Where(mapping => mapping.VocabularyWordId == "word-water")
            .Select(mapping => mapping.ResourceId).ToListAsync())
            .Should().BeEquivalentTo(["res-a2", "res-a"]);
    }

    [Fact]
    public async Task AddToVocabulary_RefusesAnotherLearnersLookupOrResource_AndAnotherLanguage()
    {
        await Repository().RecordAsync(LearnerA, Reading("물", "water"));
        var lookup = (await RowsAsync()).Single();

        (await Repository().AddToVocabularyAsync(LearnerA, lookup.Id, "res-b")).Should().Be(ResourceVocabularyWriteStatus.NotFoundOrForbidden);
        (await Repository().AddToVocabularyAsync(LearnerA, lookup.Id, "res-a-ja")).Should().Be(ResourceVocabularyWriteStatus.Invalid);
        (await Repository().AddToVocabularyAsync(LearnerA, lookup.Id, "res-a-smart")).Should().Be(ResourceVocabularyWriteStatus.SmartReadOnly);

        _learner.Id = LearnerB;
        (await Repository().AddToVocabularyAsync(LearnerB, lookup.Id, "res-b")).Should().Be(ResourceVocabularyWriteStatus.NotFoundOrForbidden);
        (await Repository().AddToVocabularyAsync(LearnerA, lookup.Id, "res-a")).Should().Be(ResourceVocabularyWriteStatus.NotFoundOrForbidden,
            "the caller's learner is not the active one");

        await using var scope = _provider.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().VocabularyWords.CountAsync()).Should().Be(0);
    }

    private async Task<List<VocabularyLookup>> RowsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyLookups.AsNoTracking().OrderBy(row => row.Text).ToListAsync();
    }

    private static LearningResource Resource(string id, string owner, string title, string language,
        bool smart = false) => new()
    {
        Id = id, UserProfileId = owner, Title = title, Language = language, IsSmartResource = smart,
        CreatedAt = Start, UpdatedAt = Start
    };

    private sealed class ActiveLearner : IUserScopeProvider
    {
        public string? Id { get; set; }
        public string UserProfileId => Id ?? throw new UnauthorizedAccessException();
        public bool TryGetUserProfileId(out string userProfileId)
        {
            userProfileId = Id ?? string.Empty;
            return !string.IsNullOrEmpty(Id);
        }
    }

    private sealed class SteppingClock(DateTime start) : TimeProvider
    {
        private DateTimeOffset _now = new(start);
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class NoFiles : IFileSystemService
    {
        public string AppDataDirectory => Path.GetTempPath();
        public Task<Stream> OpenAppPackageFileAsync(string filename) =>
            throw new FileNotFoundException(filename);
    }
}
