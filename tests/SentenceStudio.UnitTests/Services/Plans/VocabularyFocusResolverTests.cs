using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;
using Xunit;

namespace SentenceStudio.UnitTests.Services.Plans;

/// <summary>
/// Real-SQLite tests for <see cref="VocabularyFocusResolver"/>: the grounded
/// replacement for opaque SkillEmphasis/GoalTag focus.
/// </summary>
public sealed class VocabularyFocusResolverTests : IDisposable
{
    private const string UserA = "focus-user-a";
    private const string UserB = "focus-user-b";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly MutableScope _scope = new(UserA);

    public VocabularyFocusResolverTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseSqlite(_connection)
               .ConfigureWarnings(w =>
                   w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        services.AddLogging();
        services.AddSingleton<IUserScopeProvider>(_scope);
        services.AddSingleton<IPlanDateContext>(new PlanDateContext(TimeZoneInfo.Utc));
        services.AddScoped<IVocabularyFocusResolver, VocabularyFocusResolver>();

        _provider = services.BuildServiceProvider();

        using var bootstrap = _provider.CreateScope();
        bootstrap.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    private IVocabularyFocusResolver NewResolver() =>
        _provider.CreateScope().ServiceProvider.GetRequiredService<IVocabularyFocusResolver>();

    private ApplicationDbContext NewDb() =>
        _provider.CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>();

    /// <summary>Seeds a word plus a progress row that makes it owned by <paramref name="userId"/>.</summary>
    private string SeedOwnedWord(
        string userId,
        VocabularyPartOfSpeech? partOfSpeech,
        string? tags = null,
        float mastery = 0.9f,
        int totalAttempts = 5,
        DateTime? nextReview = null,
        DateTime? lastPracticed = null,
        string? id = null)
    {
        id ??= Guid.NewGuid().ToString();
        using var db = NewDb();

        db.VocabularyWords.Add(new VocabularyWord
        {
            Id = id,
            TargetLanguageTerm = $"target_{id[..8]}",
            NativeLanguageTerm = $"native_{id[..8]}",
            Language = "Korean",
            PartOfSpeech = partOfSpeech,
            Tags = tags,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        db.VocabularyProgresses.Add(new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyWordId = id,
            UserId = userId,
            MasteryScore = mastery,
            TotalAttempts = totalAttempts,
            CorrectAttempts = totalAttempts,
            NextReviewDate = nextReview ?? DateTime.UtcNow.AddDays(30),
            LastPracticedAt = lastPracticed ?? DateTime.UtcNow.AddDays(-1),
            FirstSeenAt = DateTime.UtcNow.AddDays(-10),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        db.SaveChanges();
        return id;
    }

    /// <summary>Seeds a word owned only through a resource mapping (no progress row).</summary>
    private string SeedResourceOwnedWord(string userId, VocabularyPartOfSpeech? partOfSpeech)
    {
        var wordId = Guid.NewGuid().ToString();
        var resourceId = Guid.NewGuid().ToString();
        using var db = NewDb();

        db.VocabularyWords.Add(new VocabularyWord
        {
            Id = wordId,
            TargetLanguageTerm = $"target_{wordId[..8]}",
            NativeLanguageTerm = $"native_{wordId[..8]}",
            Language = "Korean",
            PartOfSpeech = partOfSpeech,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.LearningResources.Add(new LearningResource
        {
            Id = resourceId,
            Title = "Owned Resource",
            MediaType = "Podcast",
            Language = "Korean",
            UserProfileId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
        {
            Id = Guid.NewGuid().ToString(),
            ResourceId = resourceId,
            VocabularyWordId = wordId
        });

        db.SaveChanges();
        return wordId;
    }

    private void SeedVerbs(string userId, int count, VocabularyPartOfSpeech pos = VocabularyPartOfSpeech.Verb)
    {
        for (var i = 0; i < count; i++)
        {
            SeedOwnedWord(userId, pos);
        }
    }

    private static VocabularyFocusRequest ActiveVerbs(int count = VocabularyFocusRequest.DefaultCount) => new()
    {
        DisplayDescription = "I want to focus today on active verbs",
        PartOfSpeech = VocabularyPartOfSpeech.Verb,
        RequestedCount = count
    };

    // ------------------------------------------------------------ happy path

    [Fact]
    public async Task ActiveVerbs_ResolvesToOwnedVerbsOnly()
    {
        SeedVerbs(UserA, 8);
        SeedVerbs(UserA, 4, VocabularyPartOfSpeech.Adjective);
        SeedVerbs(UserA, 4, VocabularyPartOfSpeech.Noun);

        var result = await NewResolver().ResolveAsync(ActiveVerbs());

        result.Outcome.Should().Be(VocabularyFocusOutcome.Success);
        result.Items.Should().HaveCount(8, "only the owned verbs match");
        result.MatchedCount.Should().Be(8);
        result.OwnedCandidateCount.Should().Be(16);
        result.ClassifiedCandidateCount.Should().Be(16);
        result.DisplayDescription.Should().Be("I want to focus today on active verbs");

        using var db = NewDb();
        var selected = db.VocabularyWords
            .Where(w => result.SelectedVocabularyWordIds.Contains(w.Id))
            .ToList();
        selected.Should().OnlyContain(w => w.PartOfSpeech == VocabularyPartOfSpeech.Verb,
            "an adjective must never be handed to a learner who asked for active verbs");
    }

    [Fact]
    public async Task Focus_HonorsTheRequestedBound()
    {
        SeedVerbs(UserA, 18);

        var result = await NewResolver().ResolveAsync(ActiveVerbs(count: 6));

        result.Outcome.Should().Be(VocabularyFocusOutcome.Success);
        result.Items.Should().HaveCount(6);
        result.MatchedCount.Should().Be(18, "the bound caps the selection, not the match count");
    }

    [Fact]
    public async Task Focus_DefaultsToTenWords()
    {
        SeedVerbs(UserA, 15);

        var result = await NewResolver().ResolveAsync(new VocabularyFocusRequest
        {
            PartOfSpeech = VocabularyPartOfSpeech.Verb
        });

        result.Items.Should().HaveCount(VocabularyFocusRequest.DefaultCount);
    }

    [Fact]
    public async Task CategoryTagFocus_MatchesWholeTagsOnly()
    {
        SeedOwnedWord(UserA, VocabularyPartOfSpeech.Noun, tags: "food, money");
        SeedOwnedWord(UserA, VocabularyPartOfSpeech.Noun, tags: "health, fitness");
        SeedOwnedWord(UserA, VocabularyPartOfSpeech.Noun, tags: "seafood");
        SeedOwnedWord(UserA, VocabularyPartOfSpeech.Noun, tags: "confidence:low; food");
        SeedVerbs(UserA, 4, VocabularyPartOfSpeech.Adverb);

        var result = await NewResolver().ResolveAsync(new VocabularyFocusRequest
        {
            DisplayDescription = "food words",
            CategoryTags = ["Food"],
            RequestedCount = 5
        });

        result.Outcome.Should().Be(VocabularyFocusOutcome.InsufficientMatches,
            "only two whole-tag matches exist; 'seafood' must not match by substring");
        result.MatchedCount.Should().Be(2);
    }

    // ------------------------------------------------------------- ownership

    [Fact]
    public async Task Focus_IsOwnershipIsolated()
    {
        SeedVerbs(UserB, 12);
        SeedVerbs(UserA, 6);

        var result = await NewResolver().ResolveAsync(ActiveVerbs());

        result.OwnedCandidateCount.Should().Be(6, "another learner's vocabulary is invisible");
        result.MatchedCount.Should().Be(6);
        result.Outcome.Should().Be(VocabularyFocusOutcome.Success);
    }

    [Fact]
    public async Task Focus_CountsWordsOwnedThroughResourceMapping()
    {
        for (var i = 0; i < 6; i++)
        {
            SeedResourceOwnedWord(UserA, VocabularyPartOfSpeech.Verb);
        }

        var result = await NewResolver().ResolveAsync(ActiveVerbs());

        result.Outcome.Should().Be(VocabularyFocusOutcome.Success);
        result.Items.Should().HaveCount(6);
        result.Items.Should().OnlyContain(i => i.MatchReason == VocabularyFocusMatchReason.NeverPracticed);
    }

    [Fact]
    public async Task Focus_DedupesWordsOwnedThroughBothPaths()
    {
        var shared = SeedOwnedWord(UserA, VocabularyPartOfSpeech.Verb);
        using (var db = NewDb())
        {
            var resourceId = Guid.NewGuid().ToString();
            db.LearningResources.Add(new LearningResource
            {
                Id = resourceId,
                Title = "Also mapped",
                MediaType = "Podcast",
                Language = "Korean",
                UserProfileId = UserA,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = Guid.NewGuid().ToString(),
                ResourceId = resourceId,
                VocabularyWordId = shared
            });
            db.SaveChanges();
        }
        SeedVerbs(UserA, 5);

        var result = await NewResolver().ResolveAsync(ActiveVerbs());

        result.OwnedCandidateCount.Should().Be(6, "a doubly-owned word is counted once");
        result.SelectedVocabularyWordIds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task EmptyScope_ReturnsTypedNoDataAndNeverQueriesUnfiltered()
    {
        SeedVerbs(UserB, 12);
        _scope.SetUser(string.Empty);

        var result = await NewResolver().ResolveAsync(ActiveVerbs());

        result.Outcome.Should().Be(VocabularyFocusOutcome.NoMatches);
        result.Items.Should().BeEmpty();
        result.OwnedCandidateCount.Should().Be(0,
            "an empty scope must never see another learner's vocabulary");
    }

    // ------------------------------------------------------- metadata gates

    [Fact]
    public async Task NoClassifiedMetadata_ReportsMetadataUnavailableWithCounts()
    {
        // Mirrors the real sample database: plenty of owned vocabulary, almost
        // none of it carrying a part of speech.
        for (var i = 0; i < 20; i++)
        {
            SeedOwnedWord(UserA, partOfSpeech: null);
        }

        var result = await NewResolver().ResolveAsync(ActiveVerbs());

        result.Outcome.Should().Be(VocabularyFocusOutcome.MetadataUnavailable);
        result.OwnedCandidateCount.Should().Be(20);
        result.ClassifiedCandidateCount.Should().Be(0);
        result.MatchedCount.Should().Be(0);
        result.Items.Should().BeEmpty("there is no generic fallback");
    }

    [Fact]
    public async Task GoodCoverageWithNoVerbs_ReportsNoMatchesNotMetadataGap()
    {
        for (var i = 0; i < 20; i++)
        {
            SeedOwnedWord(UserA, VocabularyPartOfSpeech.Noun);
        }

        var result = await NewResolver().ResolveAsync(ActiveVerbs());

        result.Outcome.Should().Be(VocabularyFocusOutcome.NoMatches,
            "coverage is complete, so the absence of verbs is authoritative");
        result.ClassifiedCandidateCount.Should().Be(20);
    }

    [Fact]
    public async Task FewMatchesWithGoodCoverage_ReportsInsufficientMatches()
    {
        SeedVerbs(UserA, 3);
        for (var i = 0; i < 10; i++)
        {
            SeedOwnedWord(UserA, VocabularyPartOfSpeech.Noun);
        }

        var result = await NewResolver().ResolveAsync(ActiveVerbs());

        result.Outcome.Should().Be(VocabularyFocusOutcome.InsufficientMatches);
        result.MatchedCount.Should().Be(3);
        result.Items.Should().BeEmpty("an undersized set is never padded with unrelated words");
    }

    [Fact]
    public async Task LegacyNullPartOfSpeechRows_RemainValidAndQueryable()
    {
        // Characterization: null POS is a permanent, valid state for every row
        // written before this column existed.
        var legacy = SeedOwnedWord(UserA, partOfSpeech: null);
        SeedVerbs(UserA, 6);

        using var db = NewDb();
        var row = db.VocabularyWords.Single(w => w.Id == legacy);
        row.PartOfSpeech.Should().BeNull();

        var result = await NewResolver().ResolveAsync(ActiveVerbs());
        result.Outcome.Should().Be(VocabularyFocusOutcome.Success);
        result.SelectedVocabularyWordIds.Should().NotContain(legacy);
        result.OwnedCandidateCount.Should().Be(7);
        result.ClassifiedCandidateCount.Should().Be(6);
    }

    // ---------------------------------------------------------------- rank

    [Fact]
    public async Task Rank_PutsDueAndWeakWorkFirstThenUnpracticedThenLeastRecent()
    {
        var yesterday = DateTime.UtcNow.AddDays(-1);

        var due = SeedOwnedWord(UserA, VocabularyPartOfSpeech.Verb, mastery: 0.8f, nextReview: yesterday);
        var weak = SeedOwnedWord(UserA, VocabularyPartOfSpeech.Verb, mastery: 0.2f);
        var unpracticed = SeedOwnedWord(UserA, VocabularyPartOfSpeech.Verb, totalAttempts: 0);
        var stale = SeedOwnedWord(UserA, VocabularyPartOfSpeech.Verb, lastPracticed: DateTime.UtcNow.AddDays(-40));
        var fresh = SeedOwnedWord(UserA, VocabularyPartOfSpeech.Verb, lastPracticed: DateTime.UtcNow.AddHours(-1));

        var result = await NewResolver().ResolveAsync(ActiveVerbs());

        result.Outcome.Should().Be(VocabularyFocusOutcome.Success);
        result.SelectedVocabularyWordIds.Should().Equal(weak, due, unpracticed, stale, fresh);

        var reasons = result.Items.Select(i => i.MatchReason).ToList();
        reasons[0].Should().Be(VocabularyFocusMatchReason.WeakMastery);
        reasons[1].Should().Be(VocabularyFocusMatchReason.DueForReview);
        reasons[2].Should().Be(VocabularyFocusMatchReason.NeverPracticed);
        reasons[3].Should().Be(VocabularyFocusMatchReason.LeastRecentlyPracticed);
    }

    [Fact]
    public async Task Rank_IsDeterministicAcrossRuns()
    {
        for (var i = 0; i < 14; i++)
        {
            SeedOwnedWord(UserA, VocabularyPartOfSpeech.Verb, mastery: 0.5f);
        }

        var first = await NewResolver().ResolveAsync(ActiveVerbs());
        var second = await NewResolver().ResolveAsync(ActiveVerbs());

        second.SelectedVocabularyWordIds.Should().Equal(first.SelectedVocabularyWordIds,
            "identical inputs must produce an identical set, in identical order");
    }

    // ---------------------------------------------------------- validation

    [Theory]
    [InlineData(4)]
    [InlineData(21)]
    [InlineData(0)]
    public async Task OutOfBoundCounts_AreInvalidFocus(int count)
    {
        SeedVerbs(UserA, 12);

        var result = await NewResolver().ResolveAsync(ActiveVerbs(count));

        result.Outcome.Should().Be(VocabularyFocusOutcome.InvalidFocus);
        result.ValidationErrors.Should().ContainSingle();
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task FocusWithNoFilter_IsInvalid()
    {
        SeedVerbs(UserA, 12);

        var result = await NewResolver().ResolveAsync(new VocabularyFocusRequest
        {
            DisplayDescription = "something interesting"
        });

        result.Outcome.Should().Be(VocabularyFocusOutcome.InvalidFocus,
            "free-text description alone is never a filter");
    }

    [Theory]
    [InlineData(VocabularyPartOfSpeech.Unknown)]
    [InlineData(VocabularyPartOfSpeech.Other)]
    public async Task UnknownOrOtherPartOfSpeech_IsInvalidFocus(VocabularyPartOfSpeech pos)
    {
        SeedVerbs(UserA, 12);

        var result = await NewResolver().ResolveAsync(new VocabularyFocusRequest { PartOfSpeech = pos });

        result.Outcome.Should().Be(VocabularyFocusOutcome.InvalidFocus);
    }

    private sealed class MutableScope : IUserScopeProvider
    {
        private string _userId;
        public MutableScope(string userId) => _userId = userId;
        public string UserProfileId => _userId;
        public void SetUser(string userId) => _userId = userId;
        public bool TryGetUserProfileId(out string userProfileId)
        {
            userProfileId = _userId;
            return !string.IsNullOrEmpty(_userId);
        }
    }
}
