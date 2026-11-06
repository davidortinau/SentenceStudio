using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Repositories;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Repositories;

/// <summary>
/// Backport B1: the web/MAUI Minimal Pairs pages read and wrote with a hard-coded user "1",
/// so a real learner's pairs never showed. The repositories now take the owner on every call,
/// refuse an empty owner, and never read, change or delete another owner's rows.
/// </summary>
public sealed class MinimalPairRepositoryOwnerScopeTests : IDisposable
{
    private const string UserA = "mp-user-a";
    private const string UserB = "mp-user-b";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly ApplicationDbContext _db;
    private readonly MinimalPairRepository _pairs;
    private readonly MinimalPairSessionRepository _sessions;

    private readonly VocabularyWord _a1;
    private readonly VocabularyWord _a2;
    private readonly VocabularyWord _a3;
    private readonly VocabularyWord _b1;
    private readonly VocabularyWord _b2;

    public MinimalPairRepositoryOwnerScopeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders());
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        _db.Database.EnsureCreated();

        _db.UserProfiles.AddRange(
            new UserProfile { Id = UserA, Name = UserA, CreatedAt = DateTime.UtcNow },
            new UserProfile { Id = UserB, Name = UserB, CreatedAt = DateTime.UtcNow });
        var resourceA = Resource(UserA);
        var resourceB = Resource(UserB);
        _a1 = Word("바다", "sea", UserA);
        _a2 = Word("파다", "dig", UserA);
        _a3 = Word("빠다", "butter", UserA);
        _b1 = Word("달", "moon", UserB);
        _b2 = Word("탈", "mask", UserB);
        _db.LearningResources.AddRange(resourceA, resourceB);
        _db.VocabularyWords.AddRange(_a1, _a2, _a3, _b1, _b2);
        foreach (var word in new[] { _a1, _a2, _a3 })
            _db.ResourceVocabularyMappings.Add(Map(resourceA, word));
        foreach (var word in new[] { _b1, _b2 })
            _db.ResourceVocabularyMappings.Add(Map(resourceB, word));
        _db.SaveChanges();

        var loggers = _scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        _pairs = new MinimalPairRepository(_db, loggers.CreateLogger<MinimalPairRepository>());
        _sessions = new MinimalPairSessionRepository(_db, loggers.CreateLogger<MinimalPairSessionRepository>());
    }

    [Fact]
    public async Task CreatePair_ForOwnWords_IsOwnedAndListedOnlyForThatOwner()
    {
        var pair = await _pairs.CreatePairAsync(UserA, _a2.Id, _a1.Id, "ㅂ vs ㅍ");
        var foreign = await _pairs.CreatePairAsync(UserB, _b1.Id, _b2.Id);

        pair.Should().NotBeNull();
        pair!.UserId.Should().Be(UserA);
        string.CompareOrdinal(pair.VocabularyWordAId, pair.VocabularyWordBId).Should().BeNegative();
        foreign.Should().NotBeNull();

        (await _pairs.GetUserPairsAsync(UserA)).Select(p => p.Id).Should().Equal(pair.Id);
        (await _pairs.GetUserPairsAsync(UserB)).Select(p => p.Id).Should().Equal(foreign!.Id);
        (await _pairs.GetUserPairsAsync("1")).Should().BeEmpty("the old hard-coded user owns nothing");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyOwner_ReadsNothingAndWritesNothing(string owner)
    {
        await _pairs.CreatePairAsync(UserA, _a1.Id, _a2.Id);
        // A legacy row with an empty owner must not become visible to an absent owner.
        _db.MinimalPairs.Add(new MinimalPair
        {
            Id = "mp-legacy-empty", UserId = string.Empty,
            VocabularyWordAId = _b1.Id, VocabularyWordBId = _b2.Id,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        var before = await CountsAsync();

        (await _pairs.CreatePairAsync(owner, _a1.Id, _a3.Id)).Should().BeNull();
        (await _pairs.GetUserPairsAsync(owner)).Should().BeEmpty();
        (await _pairs.GetPairByIdAsync(owner, "mp-legacy-empty")).Should().BeNull();
        (await _pairs.DeletePairAsync(owner, "mp-legacy-empty")).Should().BeFalse();
        (await _sessions.StartSessionAsync(owner, "Focus", 10)).Should().BeNull();

        (await CountsAsync()).Should().Be(before);
    }

    [Fact]
    public async Task CreatePair_WithAnotherOwnersWordOrTheSameWord_IsRefused()
    {
        (await _pairs.CreatePairAsync(UserA, _a1.Id, _b1.Id)).Should().BeNull();
        (await _pairs.CreatePairAsync(UserA, _a1.Id, _a1.Id)).Should().BeNull();
        (await _db.MinimalPairs.CountAsync()).Should().Be(0);
    }

    // The one ownership rule (Captain 2026-11-06): a pair follows its words. A's legacy pair whose
    // word is recorded to another learner, or to no one, is not listed or opened (its row stays);
    // a pair of A's own words is.
    [Fact]
    public async Task Pairs_AreShownOnlyWhenBothWordsAreTheOwners()
    {
        var own = await _pairs.CreatePairAsync(UserA, _a1.Id, _a2.Id);
        own.Should().NotBeNull();
        _db.MinimalPairs.AddRange(
            new MinimalPair
            {
                Id = "mp-a-with-b-word", UserId = UserA,
                VocabularyWordAId = _a1.Id, VocabularyWordBId = _b1.Id,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            },
            new MinimalPair
            {
                Id = "mp-a-with-legacy-word", UserId = UserA,
                VocabularyWordAId = _a2.Id, VocabularyWordBId = _a3.Id,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
        await _db.SaveChangesAsync();
        await _db.VocabularyWords.Where(word => word.Id == _a3.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(word => word.UserProfileId, (string?)null));
        _db.ChangeTracker.Clear();

        (await _pairs.GetUserPairsAsync(UserA)).Select(p => p.Id).Should().Equal(own!.Id);
        (await _pairs.GetPairByIdAsync(UserA, "mp-a-with-b-word")).Should().BeNull();
        (await _pairs.GetPairByIdAsync(UserA, "mp-a-with-legacy-word")).Should().BeNull();
        (await _pairs.GetPairsByWordIdAsync(UserA, _a2.Id)).Select(p => p.Id).Should().Equal(own.Id);
        (await _pairs.CreatePairAsync(UserA, _a1.Id, _a3.Id)).Should().BeNull(
            "a word recorded to no one is not the learner's to pair");
        (await _db.MinimalPairs.CountAsync()).Should().Be(3, "hidden pairs stay on disk");
    }

    [Fact]
    public async Task CreatePair_Duplicate_ReturnsNull()
    {
        (await _pairs.CreatePairAsync(UserA, _a1.Id, _a2.Id)).Should().NotBeNull();
        (await _pairs.CreatePairAsync(UserA, _a2.Id, _a1.Id)).Should().BeNull();
        (await _db.MinimalPairs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AnotherOwnersPair_CannotBeReadChangedOrDeleted()
    {
        var foreign = (await _pairs.CreatePairAsync(UserB, _b1.Id, _b2.Id, "ㄷ vs ㅌ"))!;

        (await _pairs.GetPairByIdAsync(UserA, foreign.Id)).Should().BeNull();
        (await _pairs.UpdateContrastLabelAsync(UserA, foreign.Id, "changed")).Should().BeFalse();
        (await _pairs.DeletePairAsync(UserA, foreign.Id)).Should().BeFalse();

        _db.ChangeTracker.Clear();
        var stored = await _db.MinimalPairs.SingleAsync(p => p.Id == foreign.Id);
        stored.ContrastLabel.Should().Be("ㄷ vs ㅌ");
        (await _pairs.GetPairByIdAsync(UserB, foreign.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task OwnPair_DeleteRemovesItsAttempts()
    {
        var pair = (await _pairs.CreatePairAsync(UserA, _a1.Id, _a2.Id))!;
        var session = (await _sessions.StartSessionAsync(UserA, "Focus", 10))!;
        (await _sessions.RecordAttemptAsync(UserA, session.Id, pair.Id, _a1.Id, _a1.Id, true)).Should().NotBeNull();

        (await _pairs.DeletePairAsync(UserA, pair.Id)).Should().BeTrue();

        _db.ChangeTracker.Clear();
        (await _db.MinimalPairs.CountAsync()).Should().Be(0);
        (await _db.MinimalPairAttempts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Session_OwnedByTheLearner_RecordsSequencedAttemptsAndEnds()
    {
        var pair = (await _pairs.CreatePairAsync(UserA, _a1.Id, _a2.Id))!;
        var session = (await _sessions.StartSessionAsync(UserA, "Focus", 10))!;
        session.UserId.Should().Be(UserA);

        var first = await _sessions.RecordAttemptAsync(UserA, session.Id, pair.Id, _a1.Id, _a2.Id, false);
        var second = await _sessions.RecordAttemptAsync(UserA, session.Id, pair.Id, _a2.Id, _a2.Id, true);

        first!.SequenceNumber.Should().Be(1);
        second!.SequenceNumber.Should().Be(2);
        (await _sessions.GetSessionAccuracyAsync(UserA, session.Id)).Should().Be((2, 1, 50d));
        (await _sessions.GetSessionAccuracyAsync(UserB, session.Id)).Should().Be((0, 0, 0d));
        (await _sessions.GetSessionAttemptsAsync(UserB, session.Id)).Should().BeEmpty();
        (await _sessions.EndSessionAsync(UserA, session.Id)).Should().BeTrue();

        _db.ChangeTracker.Clear();
        (await _db.MinimalPairSessions.SingleAsync()).EndedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RecordAttempt_ForeignSessionForeignPairOrWordOutsidePair_IsRefused()
    {
        var ownPair = (await _pairs.CreatePairAsync(UserA, _a1.Id, _a2.Id))!;
        var foreignPair = (await _pairs.CreatePairAsync(UserB, _b1.Id, _b2.Id))!;
        var ownSession = (await _sessions.StartSessionAsync(UserA, "Mixed", 20))!;
        var foreignSession = (await _sessions.StartSessionAsync(UserB, "Mixed", 20))!;

        (await _sessions.RecordAttemptAsync(UserA, foreignSession.Id, ownPair.Id, _a1.Id, _a1.Id, true))
            .Should().BeNull("the session is another learner's");
        (await _sessions.RecordAttemptAsync(UserA, ownSession.Id, foreignPair.Id, _b1.Id, _b1.Id, true))
            .Should().BeNull("the pair is another learner's");
        (await _sessions.RecordAttemptAsync(UserA, ownSession.Id, ownPair.Id, _a3.Id, _a1.Id, false))
            .Should().BeNull("the prompt word is not in the pair");
        (await _sessions.RecordAttemptAsync(string.Empty, ownSession.Id, ownPair.Id, _a1.Id, _a1.Id, true))
            .Should().BeNull("no owner, no write");
        (await _sessions.EndSessionAsync(UserA, foreignSession.Id)).Should().BeFalse();

        (await _db.MinimalPairAttempts.CountAsync()).Should().Be(0);
        _db.ChangeTracker.Clear();
        (await _db.MinimalPairSessions.SingleAsync(s => s.Id == foreignSession.Id)).EndedAt.Should().BeNull();
    }

    private async Task<(int Pairs, int Sessions, int Attempts)> CountsAsync() =>
        (await _db.MinimalPairs.CountAsync(), await _db.MinimalPairSessions.CountAsync(),
            await _db.MinimalPairAttempts.CountAsync());

    private static LearningResource Resource(string owner) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Title = $"Resource {owner}",
        MediaType = "Vocabulary List",
        Language = "Korean",
        UserProfileId = owner,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static VocabularyWord Word(string target, string native, string owner) => new()
    {
        Id = Guid.NewGuid().ToString(),
        UserProfileId = owner,
        TargetLanguageTerm = target,
        NativeLanguageTerm = native,
        Language = "Korean",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static ResourceVocabularyMapping Map(LearningResource resource, VocabularyWord word) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ResourceId = resource.Id,
        VocabularyWordId = word.Id,
    };

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
        _connection.Dispose();
    }
}
