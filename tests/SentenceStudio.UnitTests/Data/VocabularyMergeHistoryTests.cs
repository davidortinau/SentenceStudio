using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FluentAssertions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.UnitTests.PlanGeneration;

namespace SentenceStudio.UnitTests.Data;

/// <summary>
/// Regression tests for the duplicate-merge learning-history preservation fix.
///
/// Before the fix, <see cref="LearningResourceRepository.MergeVocabularyWordsAsync"/> only reassigned
/// resource mappings and then deleted the duplicate <see cref="VocabularyWord"/> rows — the database's
/// ON DELETE CASCADE then destroyed every duplicate's <see cref="VocabularyProgress"/> (mastery, streak,
/// spaced-repetition schedule) and <see cref="VocabularyLearningContext"/> (per-attempt history). A user
/// who had practiced a word to mastery would see it reset to Unknown after a merge.
///
/// These tests run against the in-memory SQLite fixture, which builds the schema WITH the real cascade
/// relationships and enforces foreign keys, so they genuinely reproduce the original data-loss bug and
/// prove the fix preserves and recalculates history "as if it had always been one word."
/// </summary>
public class VocabularyMergeHistoryTests : IClassFixture<PlanGenerationTestFixture>
{
    private readonly PlanGenerationTestFixture _fixture;

    private const string UserA = PlanGenerationTestFixture.TestUserId;
    private const string UserB = "test-user-2";

    public VocabularyMergeHistoryTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
        _fixture.SeedUserProfile();
    }

    // A compact description of a learning context to seed.
    private sealed record Ctx(
        DateTime At,
        bool Correct,
        string InputMode = "Text",
        bool Passive = false,
        string Activity = "VocabularyQuiz");

    [Fact]
    public async Task Merge_MovesAndRecalculatesProgress_AsIfAlwaysOneWord()
    {
        // Arrange — a user practiced the same concept under two duplicate word records.
        var t = DateTime.UtcNow.AddDays(-10);
        var keeper = SeedWord("사과", "apple");
        var dup = SeedWord("사과 ", "apple (dup)");

        var keeperProg = SeedProgress(keeper, UserA, new[]
        {
            new Ctx(t.AddMinutes(1), Correct: true, InputMode: "MultipleChoice"),
            new Ctx(t.AddMinutes(3), Correct: true, InputMode: "Text"),
        });
        var dupProg = SeedProgress(dup, UserA, new[]
        {
            new Ctx(t.AddMinutes(2), Correct: true, InputMode: "Text"),
            new Ctx(t.AddMinutes(4), Correct: false, InputMode: "Text"),
        });

        var oracle = BuildOracle(keeper, UserA, keeperProg, dupProg);

        // Act
        var deleted = await MergeAsync(keeper, dup);

        // Assert — duplicate word is gone, keeper folded in all four attempts.
        deleted.Should().Be(1);
        WordExists(dup).Should().BeFalse("the duplicate word should be deleted");
        WordExists(keeper).Should().BeTrue("the keeper word must remain");
        GetProgress(dup, UserA).Should().BeNull("the duplicate progress row must not survive");

        var merged = GetProgress(keeper, UserA);
        merged.Should().NotBeNull();
        merged!.TotalAttempts.Should().Be(4, "all four attempts must be preserved");
        merged.CorrectAttempts.Should().Be(3);
        merged.MasteryScore.Should().BeGreaterThan(0f, "mastery must NOT be reset to Unknown");

        // The keeper must equal a full chronological replay of every context (the "one word" oracle).
        merged.MasteryScore.Should().BeApproximately(oracle.MasteryScore, 0.0001f);
        merged.CurrentStreak.Should().BeApproximately(oracle.CurrentStreak, 0.0001f);
        merged.ProductionInStreak.Should().Be(oracle.ProductionInStreak);
        merged.TotalAttempts.Should().Be(oracle.TotalAttempts);
        merged.CorrectAttempts.Should().Be(oracle.CorrectAttempts);
    }

    [Fact]
    public async Task Merge_ReParentsContexts_DoesNotCascadeDelete()
    {
        // Arrange — duplicate carries both active attempts and a passive exposure.
        var t = DateTime.UtcNow.AddDays(-5);
        var keeper = SeedWord("물", "water");
        var dup = SeedWord("물 ", "water (dup)");

        var keeperProg = SeedProgress(keeper, UserA, new[]
        {
            new Ctx(t.AddMinutes(1), Correct: true),
            new Ctx(t.AddMinutes(2), Correct: true),
        });
        SeedProgress(dup, UserA, new[]
        {
            new Ctx(t.AddMinutes(3), Correct: true),
            new Ctx(t.AddMinutes(4), Correct: false),
            new Ctx(t.AddMinutes(5), Correct: false, Passive: true, Activity: "Reading"),
        });

        var keeperProgId = keeperProg.Id;
        var totalContextsBefore = CountAllContexts();
        totalContextsBefore.Should().Be(5);

        // Act
        await MergeAsync(keeper, dup);

        // Assert — every context survives and is re-parented onto the keeper (none cascade-deleted).
        CountAllContexts().Should().Be(5, "no learning context may be destroyed by the merge");
        CountContextsFor(keeperProgId).Should().Be(5, "all contexts must now point at the keeper progress");

        var merged = GetProgress(keeper, UserA)!;
        merged.Id.Should().Be(keeperProgId, "the existing keeper progress row is retained");
        merged.TotalAttempts.Should().Be(4, "the passive exposure is not an attempt");
        merged.ExposureCount.Should().Be(1, "the passive exposure must be preserved as an exposure");
    }

    [Fact]
    public async Task Merge_ReparentsDependentVocabularyArtifacts_DoesNotCascadeDelete()
    {
        // Arrange — duplicate carries downstream artifacts that EF would otherwise cascade-delete.
        var keeper = SeedWord("불", "fire");
        var dup = SeedWord(" 불 ", "fire duplicate");
        var contrastWord = SeedWord("물", "water");
        EnsureWordsMappedToUser(UserA, keeper, dup, contrastWord);

        var exampleId = SeedExampleSentence(dup, targetSentence: "불이 뜨겁다.", learningResourceId: GetResourceIdForUser(UserA));
        var pairId = SeedMinimalPair(UserA, dup, contrastWord);
        var attemptId = SeedMinimalPairAttempt(UserA, pairId, promptWordId: dup, selectedWordId: contrastWord);

        // Act
        await MergeAsync(keeper, dup);

        // Assert — every artifact survives and now references the keeper instead of the deleted duplicate.
        WordExists(dup).Should().BeFalse();
        GetExampleSentence(exampleId)!.VocabularyWordId.Should().Be(keeper);

        var pair = GetMinimalPair(pairId)!;
        new[] { pair.VocabularyWordAId, pair.VocabularyWordBId }.Should().BeEquivalentTo(new[] { keeper, contrastWord });

        var attempt = GetMinimalPairAttempt(attemptId)!;
        attempt.PairId.Should().Be(pairId);
        attempt.PromptWordId.Should().Be(keeper);
        attempt.SelectedWordId.Should().Be(contrastWord);
    }

    [Fact]
    public async Task Merge_CopiesMissingKeeperEncodingFieldsFromDuplicates()
    {
        // Arrange — keeper wins conflicts, but missing keeper value should not cause duplicate-authored aids to be lost.
        var keeper = SeedWord("문", "door");
        var dup = SeedWord(" 문 ", "door duplicate");
        UpdateWord(dup, word =>
        {
            word.MnemonicText = "Picture a door shaped like the character.";
            word.MnemonicImageUri = "https://example.test/door.png";
            word.AudioPronunciationUri = "https://example.test/door.mp3";
            word.Tags = "house";
        });

        // Act
        await MergeAsync(keeper, dup);

        // Assert
        var merged = GetWord(keeper)!;
        merged.MnemonicText.Should().Be("Picture a door shaped like the character.");
        merged.MnemonicImageUri.Should().Be("https://example.test/door.png");
        merged.AudioPronunciationUri.Should().Be("https://example.test/door.mp3");
        merged.Tags.Should().Be("house");
    }

    [Fact]
    public async Task Merge_DeduplicatesDependentArtifacts_WhenKeeperAlreadyHasEquivalentLinks()
    {
        // Arrange — duplicate and keeper both point at equivalent minimal-pair and phrase links.
        var keeper = SeedWord("밤", "night");
        var dup = SeedWord(" 밤 ", "night duplicate");
        var contrastWord = SeedWord("밥", "rice");
        EnsureWordsMappedToUser(UserA, keeper, dup, contrastWord);

        var existingPairId = SeedMinimalPair(UserA, keeper, contrastWord);
        var duplicatePairId = SeedMinimalPair(UserA, dup, contrastWord);
        var attemptId = SeedMinimalPairAttempt(UserA, duplicatePairId, promptWordId: dup, selectedWordId: contrastWord);

        // Act
        await MergeAsync(keeper, dup);

        // Assert — duplicate links collapse into existing keeper links, preserving attempts.
        WordExists(dup).Should().BeFalse();
        MinimalPairExists(duplicatePairId).Should().BeFalse("the duplicate pair should collapse into the existing keeper pair");
        CountMinimalPairs(UserA, keeper, contrastWord).Should().Be(1);

        var attempt = GetMinimalPairAttempt(attemptId)!;
        attempt.PairId.Should().Be(existingPairId);
        attempt.PromptWordId.Should().Be(keeper);
        attempt.SelectedWordId.Should().Be(contrastWord);
    }

    [Fact]
    public async Task Merge_DoesNotReparentUnownedPhraseConstituents_WhenDuplicateIsShared()
    {
        // Arrange — phrase constituents have no owner column, so shared duplicate words must leave them untouched.
        SeedUserProfile(UserB);
        var keeper = SeedWord("눈", "snow");
        var dup = SeedWord(" 눈 ", "snow duplicate");
        var contrastWord = SeedWord("눈물", "tears");
        EnsureWordsMappedToUser(UserA, keeper, dup, contrastWord);
        EnsureWordsMappedToUser(UserB, dup);
        var exampleId = SeedExampleSentence(dup, targetSentence: "눈이 온다.");
        var linkId = SeedPhraseConstituent(dup, contrastWord);

        // Act
        await MergeAsync(keeper, dup);

        // Assert — active-user mappings/progress may merge, but the shared unowned phrase graph is unchanged.
        WordExists(dup).Should().BeTrue("another user still maps to the duplicate, so it cannot be deleted");
        GetExampleSentence(exampleId)!.VocabularyWordId.Should().Be(dup);
        var link = GetPhraseConstituent(linkId)!;
        link.PhraseWordId.Should().Be(dup);
        link.ConstituentWordId.Should().Be(contrastWord);
    }

    [Fact]
    public async Task Merge_RefusesWhenKeeperIsSharedOutsideActiveUser()
    {
        // Arrange — moving artifacts onto a keeper shared by another user would leak ownerless examples/links.
        SeedUserProfile(UserB);
        var keeper = SeedWord("길", "road");
        var dup = SeedWord(" 길 ", "road duplicate");
        EnsureWordsMappedToUser(UserA, keeper, dup);
        EnsureWordsMappedToUser(UserB, keeper);
        var exampleId = SeedExampleSentence(dup, targetSentence: "길이 멀다.");

        // Act
        var deleted = await MergeAsync(keeper, dup);

        // Assert
        deleted.Should().Be(0);
        WordExists(dup).Should().BeTrue();
        GetExampleSentence(exampleId)!.VocabularyWordId.Should().Be(dup);
    }

    [Fact]
    public async Task Merge_DeduplicatesArtifactLinksAcrossMultipleDuplicates()
    {
        // Arrange — two duplicate rows both have equivalent links that collapse to keeper+contrast.
        var keeper = SeedWord("달", "moon");
        var dup1 = SeedWord(" 달 ", "moon duplicate 1");
        var dup2 = SeedWord("달  ", "moon duplicate 2");
        var contrastWord = SeedWord("딸", "daughter");
        EnsureWordsMappedToUser(UserA, keeper, dup1, dup2, contrastWord);
        SeedMinimalPair(UserA, dup1, contrastWord);
        SeedMinimalPair(UserA, dup2, contrastWord);

        // Act
        var deleted = await MergeAsync(keeper, dup1, dup2);

        // Assert
        deleted.Should().Be(2);
        CountMinimalPairs(UserA, keeper, contrastWord).Should().Be(1);
    }

    [Fact]
    public async Task Merge_RemovesMinimalPairsThatCollapseToKeeperSelfPair()
    {
        // Arrange — a keeper-vs-duplicate pair becomes keeper-vs-keeper after merge and is no longer valid.
        var keeper = SeedWord("말", "horse");
        var dup = SeedWord(" 말 ", "horse duplicate");
        EnsureWordsMappedToUser(UserA, keeper, dup);
        var pairId = SeedMinimalPair(UserA, keeper, dup);
        SeedMinimalPairAttempt(UserA, pairId, promptWordId: dup, selectedWordId: keeper);

        // Act
        await MergeAsync(keeper, dup);

        // Assert
        MinimalPairExists(pairId).Should().BeFalse();
    }

    [Fact]
    public async Task Merge_IsolatesProgressPerUser()
    {
        // Arrange — two users practiced the duplicate; only user A also practiced the keeper.
        SeedUserProfile(UserB);
        var t = DateTime.UtcNow.AddDays(-8);
        var keeper = SeedWord("책", "book");
        var dup = SeedWord("책 ", "book (dup)");

        var keeperProgA = SeedProgress(keeper, UserA, new[]
        {
            new Ctx(t.AddMinutes(1), Correct: true),
        });
        var dupProgA = SeedProgress(dup, UserA, new[]
        {
            new Ctx(t.AddMinutes(2), Correct: true),
            new Ctx(t.AddMinutes(3), Correct: true),
        });
        var dupProgB = SeedProgress(dup, UserB, new[]
        {
            new Ctx(t.AddMinutes(1), Correct: true),
            new Ctx(t.AddMinutes(2), Correct: false),
        });

        var oracleA = BuildOracle(keeper, UserA, keeperProgA, dupProgA);
        EnsureWordsMappedToUser(UserB, dup);

        // Act
        await MergeAsync(keeper, dup);

        // Assert — active-user merge changes only UserA; UserB's duplicate progress stays untouched.
        GetProgress(dup, UserA).Should().BeNull();
        GetProgress(dup, UserB).Should().NotBeNull();
        CountProgressFor(keeper).Should().Be(1, "only active-user progress is folded into the keeper");
        WordExists(dup).Should().BeTrue("another user still maps to this word, so it must not be globally deleted");

        var mergedA = GetProgress(keeper, UserA)!;
        mergedA.TotalAttempts.Should().Be(3);
        mergedA.MasteryScore.Should().BeApproximately(oracleA.MasteryScore, 0.0001f);
        GetProgress(keeper, UserB).Should().BeNull("active-user cleanup must not retag another user's practice history");
    }

    [Fact]
    public async Task FindDuplicateVocabularyGroups_ScopesToActiveUserResources()
    {
        // Arrange — same target exists for two users, but the active user owns only one copy.
        SeedUserProfile(UserB);
        var userAWord = SeedWord("사과", "apple");
        var userBWord = SeedWord(" 사과 ", "apple");
        EnsureWordsMappedToUser(UserA, userAWord);
        EnsureWordsMappedToUser(UserB, userBWord);

        using var scope = _fixture.ServiceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<LearningResourceRepository>();

        // Act
        var result = await repo.FindDuplicateVocabularyGroupsAsync();

        // Assert
        result.TotalGroupCount.Should().Be(0, "duplicate cleanup must not compare active-user words with another tenant's vocabulary");
    }

    [Fact]
    public async Task FindDuplicateVocabularyGroups_AllowsNativeMeaningConflictAndKeepsRecommendedNativeMeaning()
    {
        // Arrange — normalized target matches, but native meanings differ. The form-level duplicate rule
        // prevents editing one native meaning to match another, so merge must allow this and keep the keeper.
        var basicCar = SeedWord("자동차", "car");
        var automobile = SeedWord(" 자동차 ", "automobile");
        AddEncodingAids(automobile);
        EnsureWordsMappedToUser(UserA, basicCar, automobile);

        using var scope = _fixture.ServiceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<LearningResourceRepository>();

        // Act
        var result = await repo.FindDuplicateVocabularyGroupsAsync("자동차");

        // Assert
        result.TotalGroupCount.Should().Be(1);
        var group = result.Groups.Single();
        group.NormalizedTerm.Should().Be(VocabularyDuplicatePolicy.NormalizeTargetTerm("자동차"));
        group.CanMergeAutomatically.Should().BeTrue("same target with different native meanings is a safe merge; the recommended keeper's native meaning wins");
        group.MergeBlockedReason.Should().BeNull();
        group.RecommendedKeeperId.Should().Be(automobile);

        var deleted = await repo.MergeVocabularyWordsAsync(group.RecommendedKeeperId, group.Words.Select(w => w.Word.Id).Where(id => id != group.RecommendedKeeperId).ToList());

        deleted.Should().Be(1);
        GetWord(automobile)!.NativeLanguageTerm.Should().Be("automobile");
        WordExists(basicCar).Should().BeFalse();
    }

    [Fact]
    public async Task FindDuplicateVocabularyGroups_BlocksLanguageAndLexicalTypeConflicts()
    {
        // Arrange — target matches, but language/type differences still mean these may be different lexical entries.
        var koreanWord = SeedWord("자동차", "car");
        var japaneseWord = SeedWord("자동차 ", "car");
        UpdateWord(japaneseWord, word =>
        {
            word.Language = "Japanese";
            word.LexicalUnitType = LexicalUnitType.Phrase;
        });
        EnsureWordsMappedToUser(UserA, koreanWord, japaneseWord);

        using var scope = _fixture.ServiceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<LearningResourceRepository>();

        // Act
        var result = await repo.FindDuplicateVocabularyGroupsAsync("자동차");

        // Assert
        var group = result.Groups.Single();
        group.CanMergeAutomatically.Should().BeFalse("language and lexical type conflicts still require editing before merge");
        group.MergeBlockedReason.Should().Contain("language");
        group.MergeBlockedReason.Should().Contain("lexical type");
        group.MergeBlockedReason.Should().NotContain("native meaning");
    }

    [Fact]
    public async Task FindDuplicateVocabularyGroups_RecommendsBestEncodingOverResourceCount()
    {
        // Arrange — resource links are combined by merge, so they must not decide the keeper.
        var basicManyResources = SeedWord("열쇠", "key");
        var strongOneResource = SeedWord("열쇠 ", "key");
        AddEncodingAids(strongOneResource);
        EnsureWordsMappedToUser(UserA, basicManyResources, strongOneResource);
        EnsureAdditionalResourceMapping(UserA, basicManyResources);
        EnsureAdditionalResourceMapping(UserA, basicManyResources);

        using var scope = _fixture.ServiceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<LearningResourceRepository>();

        // Act
        var result = await repo.FindDuplicateVocabularyGroupsAsync("열쇠");

        // Assert
        var group = result.Groups.Single();
        group.RecommendedKeeperId.Should().Be(strongOneResource, "keeper selection should favor the record with stronger encoding and memory aids");
        group.Words.First().Word.Id.Should().Be(strongOneResource, "the review UI should show the recommended keeper first");
    }

    [Fact]
    public async Task Merge_AncientAggregateHistory_WithoutContexts_NeverRegresses()
    {
        // Arrange — the duplicate holds high mastery from before per-attempt context tracking existed,
        // so there are no contexts to replay. The merge must not zero that mastery out.
        var t = DateTime.UtcNow.AddDays(-30);
        var keeper = SeedWord("학교", "school");
        var dup = SeedWord("학교 ", "school (dup)");

        SeedProgress(keeper, UserA, new[]
        {
            new Ctx(t.AddMinutes(1), Correct: true),
        });
        SeedAggregateOnlyProgress(dup, UserA,
            masteryScore: 0.9f, totalAttempts: 20, correctAttempts: 18,
            currentStreak: 10f, productionInStreak: 6);

        // Act
        await MergeAsync(keeper, dup);

        // Assert — counts are monotonic and mastery/streak are protected from regression.
        var merged = GetProgress(keeper, UserA)!;
        merged.TotalAttempts.Should().Be(21, "1 context-backed + 20 ancient attempts");
        merged.CorrectAttempts.Should().Be(19);
        merged.MasteryScore.Should().BeGreaterThanOrEqualTo(0.9f, "ancient mastery must not be lost");
        merged.CurrentStreak.Should().BeGreaterThanOrEqualTo(10f);
        merged.ProductionInStreak.Should().BeGreaterThanOrEqualTo(6);
    }

    [Fact]
    public async Task Merge_LegacyAggregateOnly_PreservesReviewSchedule()
    {
        // Arrange — both rows predate per-attempt context tracking, so there are no contexts to replay.
        // The duplicate carries a real spaced-repetition schedule (due date, interval, ease). Replay would
        // reset these to defaults (NextReviewDate=null), making the merged word never due for review again,
        // so the merge must carry the schedule forward.
        var keeper = SeedWord("바다", "sea");
        var dup = SeedWord("바다 ", "sea (dup)");
        var dueDate = DateTime.UtcNow.AddDays(9);

        SeedAggregateOnlyProgress(keeper, UserA,
            masteryScore: 0.5f, totalAttempts: 5, correctAttempts: 4,
            currentStreak: 2f, productionInStreak: 1);
        SeedAggregateOnlyProgress(dup, UserA,
            masteryScore: 0.88f, totalAttempts: 30, correctAttempts: 27,
            currentStreak: 12f, productionInStreak: 8,
            nextReviewDate: dueDate, reviewInterval: 14, easeFactor: 2.6f);

        // Act
        await MergeAsync(keeper, dup);

        // Assert — the merged keeper carries the duplicate's real review cadence forward.
        var merged = GetProgress(keeper, UserA)!;
        merged.NextReviewDate.Should().NotBeNull("a merged word must keep a review date so it resurfaces");
        merged.NextReviewDate!.Value.Should().BeCloseTo(dueDate, TimeSpan.FromSeconds(1));
        merged.ReviewInterval.Should().Be(14);
        merged.EaseFactor.Should().BeApproximately(2.6f, 0.0001f);
        merged.IsDueForReview.Should().BeFalse("the carried-over due date is in the future");
    }

    [Fact]
    public async Task Merge_DeclaredFamiliarDuplicate_NeverQuizzed_PreservesScheduleAndDeclaration()
    {
        // Arrange — the user marked the DUPLICATE "Familiar" (a high-value "I know this" signal) but never
        // quizzed it, so it carries a 14-day grace-period schedule with ZERO attempts. The keeper was never
        // touched. summedTotal == 0 here, so the schedule restore must gate on the schedule's existence
        // rather than on attempt count, otherwise the grace cadence is lost and the word goes dormant
        // (NextReviewDate=null => never due) after the grace window — stuck Familiar with no verification probe.
        var keeper = SeedWord("우유", "milk");
        var dup = SeedWord("우유 ", "milk (dup)");
        var dueDate = DateTime.UtcNow.AddDays(14);

        SeedDeclaredFamiliarProgress(dup, UserA, dueDate);
        GetProgress(keeper, UserA).Should().BeNull("keeper was never practiced or declared");

        // Act
        await MergeAsync(keeper, dup);

        // Assert — the keeper carries BOTH the declaration and the review cadence forward.
        GetProgress(dup, UserA).Should().BeNull();
        var merged = GetProgress(keeper, UserA)!;
        merged.IsUserDeclared.Should().BeTrue("a user-declared Familiar status is high-value signal");
        merged.IsFamiliar.Should().BeTrue("declaration + Pending verification must survive the merge");
        merged.NextReviewDate.Should().NotBeNull("the grace-period review date must survive the merge");
        merged.NextReviewDate!.Value.Should().BeCloseTo(dueDate, TimeSpan.FromSeconds(1));
        merged.ReviewInterval.Should().Be(30);
    }

    [Fact]
    public async Task Merge_WhenOnlyDuplicatePracticed_CreatesKeeperProgress()
    {
        // Arrange — the keeper word was never practiced by this user; only the duplicate was.
        var t = DateTime.UtcNow.AddDays(-3);
        var keeper = SeedWord("의자", "chair");
        var dup = SeedWord("의자 ", "chair (dup)");

        var dupProg = SeedProgress(dup, UserA, new[]
        {
            new Ctx(t.AddMinutes(1), Correct: true),
            new Ctx(t.AddMinutes(2), Correct: true),
        });
        GetProgress(keeper, UserA).Should().BeNull("keeper has no progress yet");

        var oracle = BuildOracle(keeper, UserA, dupProg);

        // Act
        await MergeAsync(keeper, dup);

        // Assert — a keeper progress record is created carrying the duplicate's history.
        GetProgress(dup, UserA).Should().BeNull();
        var merged = GetProgress(keeper, UserA);
        merged.Should().NotBeNull();
        merged!.TotalAttempts.Should().Be(2);
        merged.MasteryScore.Should().BeGreaterThan(0f);
        merged.MasteryScore.Should().BeApproximately(oracle.MasteryScore, 0.0001f);
    }

    [Fact]
    public async Task LivePath_And_Replay_ProduceIdenticalAggregate()
    {
        // This guards the "single source of truth" guarantee: the merge recalculates by replaying
        // contexts through VocabularyMasteryCalculator, so a replay of the live path's own contexts
        // must reproduce the live path's aggregate exactly. If the two ever diverge, merge math is wrong.
        using var scope = _fixture.ServiceProvider.CreateScope();
        var progressRepo = scope.ServiceProvider.GetRequiredService<VocabularyProgressRepository>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var contextRepo = new VocabularyLearningContextRepository(
            _fixture.ServiceProvider,
            loggerFactory.CreateLogger<VocabularyLearningContextRepository>());
        var service = new VocabularyProgressService(
            progressRepo,
            contextRepo,
            loggerFactory.CreateLogger<VocabularyProgressService>(),
            _fixture.ServiceProvider);

        var wordId = SeedWord("사랑", "love");

        var inputs = new[]
        {
            (Correct: true,  Mode: "MultipleChoice"),
            (Correct: true,  Mode: "Text"),
            (Correct: false, Mode: "Text"),
            (Correct: true,  Mode: "Text"),
            (Correct: true,  Mode: "MultipleChoice"),
        };

        VocabularyProgress live = null!;
        foreach (var (correct, mode) in inputs)
        {
            live = await service.RecordAttemptAsync(new VocabularyAttempt
            {
                VocabularyWordId = wordId,
                UserId = UserA,
                WasCorrect = correct,
                DifficultyWeight = 1.0f,
                InputMode = mode,
                Activity = "VocabularyQuiz",
                ContextType = "Isolated"
            });
            await Task.Delay(3); // ensure distinct LearnedAt timestamps for a stable replay order
        }

        // Replay the live path's own persisted contexts into a fresh aggregate.
        List<VocabularyLearningContext> contexts;
        using (var readScope = _fixture.ServiceProvider.CreateScope())
        {
            var db = readScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            contexts = db.VocabularyLearningContexts.AsNoTracking()
                .Where(c => c.VocabularyProgressId == live.Id)
                .ToList();
        }

        contexts.Should().HaveCount(inputs.Length, "every attempt persists exactly one context");

        var replay = new VocabularyProgress { Id = "replay", VocabularyWordId = wordId, UserId = UserA };
        VocabularyMasteryCalculator.RecalculateInto(replay, contexts, out _);

        replay.TotalAttempts.Should().Be(live.TotalAttempts);
        replay.CorrectAttempts.Should().Be(live.CorrectAttempts);
        replay.ProductionInStreak.Should().Be(live.ProductionInStreak);
        replay.CurrentStreak.Should().BeApproximately(live.CurrentStreak, 0.0001f);
        replay.MasteryScore.Should().BeApproximately(live.MasteryScore, 0.0001f);
    }

    #region Helpers

    private string SeedWord(string target, string native)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var word = new VocabularyWord
        {
            Id = Guid.NewGuid().ToString(),
            TargetLanguageTerm = target,
            NativeLanguageTerm = native,
            Language = "Korean",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.VocabularyWords.Add(word);
        db.SaveChanges();
        return word.Id;
    }

    private void SeedUserProfile(string userId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (db.UserProfiles.Find(userId) != null)
            return;
        db.UserProfiles.Add(new UserProfile
        {
            Id = userId,
            Name = $"User {userId}",
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            PreferredSessionMinutes = 20,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    /// <summary>
    /// Seeds a progress row whose aggregate is made self-consistent by replaying the given contexts —
    /// exactly what the live path would have produced for that history.
    /// </summary>
    private VocabularyProgress SeedProgress(string wordId, string userId, IEnumerable<Ctx> ctxs)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var progress = new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyWordId = wordId,
            UserId = userId
        };

        var ctxList = ctxs.Select(c => new VocabularyLearningContext
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyProgressId = progress.Id,
            Activity = c.Activity,
            InputMode = c.Passive ? "Passive" : c.InputMode,
            ContextType = c.Passive ? "Exposure" : "Isolated",
            WasCorrect = c.Correct,
            DifficultyScore = 1.0f,
            LearnedAt = c.At,
            CreatedAt = c.At,
            UpdatedAt = c.At
        }).ToList();

        VocabularyMasteryCalculator.RecalculateInto(progress, ctxList, out _);
        progress.FirstSeenAt = ctxList.Count > 0 ? ctxList.Min(c => c.LearnedAt) : DateTime.UtcNow;
        progress.CreatedAt = progress.FirstSeenAt;

        db.VocabularyProgresses.Add(progress);
        db.VocabularyLearningContexts.AddRange(ctxList);
        db.SaveChanges();
        return progress;
    }

    /// <summary>Seeds an aggregate-only progress row (no contexts), simulating pre-context-tracking history.</summary>
    private void SeedAggregateOnlyProgress(
        string wordId, string userId, float masteryScore, int totalAttempts,
        int correctAttempts, float currentStreak, int productionInStreak,
        DateTime? nextReviewDate = null, int reviewInterval = 1, float easeFactor = 2.5f)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.VocabularyProgresses.Add(new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyWordId = wordId,
            UserId = userId,
            MasteryScore = masteryScore,
            TotalAttempts = totalAttempts,
            CorrectAttempts = correctAttempts,
            CurrentStreak = currentStreak,
            ProductionInStreak = productionInStreak,
            NextReviewDate = nextReviewDate,
            ReviewInterval = reviewInterval,
            EaseFactor = easeFactor,
            FirstSeenAt = DateTime.UtcNow.AddDays(-60),
            LastPracticedAt = DateTime.UtcNow.AddDays(-20),
            CreatedAt = DateTime.UtcNow.AddDays(-60),
            UpdatedAt = DateTime.UtcNow.AddDays(-20)
        });
        db.SaveChanges();
    }

    /// <summary>Seeds a user-declared "Familiar" progress row with a grace-period schedule but ZERO attempts,
    /// mirroring SetUserDeclaredStatusAsync's Familiar branch (the user said "I know this" without quizzing).</summary>
    private void SeedDeclaredFamiliarProgress(string wordId, string userId, DateTime nextReviewDate)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.VocabularyProgresses.Add(new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyWordId = wordId,
            UserId = userId,
            IsUserDeclared = true,
            UserDeclaredAt = DateTime.UtcNow.AddDays(-2),
            VerificationState = VerificationStatus.Pending,
            TotalAttempts = 0,
            CorrectAttempts = 0,
            NextReviewDate = nextReviewDate,
            ReviewInterval = 30,
            EaseFactor = 2.5f,
            FirstSeenAt = DateTime.UtcNow.AddDays(-2),
            LastPracticedAt = DateTime.UtcNow.AddDays(-2),
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            UpdatedAt = DateTime.UtcNow.AddDays(-2)
        });
        db.SaveChanges();
    }

    /// <summary>Builds the "as if always one word" oracle by replaying every source's contexts.</summary>
    private VocabularyProgress BuildOracle(string keeperWordId, string userId, params VocabularyProgress[] sources)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ids = sources.Select(s => s.Id).ToList();
        var contexts = db.VocabularyLearningContexts.AsNoTracking()
            .Where(c => ids.Contains(c.VocabularyProgressId))
            .ToList();

        var oracle = new VocabularyProgress { Id = "oracle", VocabularyWordId = keeperWordId, UserId = userId };
        VocabularyMasteryCalculator.RecalculateInto(oracle, contexts, out _);
        return oracle;
    }

    private async Task<int> MergeAsync(string keeperWordId, params string[] dupIds)
    {
        EnsureWordsMappedToUser(UserA, new[] { keeperWordId }.Concat(dupIds).ToArray());

        using var scope = _fixture.ServiceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<LearningResourceRepository>();
        return await repo.MergeVocabularyWordsAsync(keeperWordId, dupIds.ToList());
    }

    private void EnsureWordsMappedToUser(string userId, params string[] wordIds)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var resource = db.LearningResources.FirstOrDefault(r => r.UserProfileId == userId);
        if (resource == null)
        {
            resource = new LearningResource
            {
                Id = Guid.NewGuid().ToString(),
                Title = $"Resource {userId}",
                MediaType = "Vocabulary List",
                Language = "Korean",
                UserProfileId = userId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.LearningResources.Add(resource);
        }

        foreach (var wordId in wordIds)
        {
            if (db.ResourceVocabularyMappings.Any(m => m.ResourceId == resource.Id && m.VocabularyWordId == wordId))
                continue;

            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = Guid.NewGuid().ToString(),
                ResourceId = resource.Id,
                VocabularyWordId = wordId
            });
        }

        db.SaveChanges();
    }

    private void EnsureAdditionalResourceMapping(string userId, string wordId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var resource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            UserProfileId = userId,
            Title = $"Extra resource {Guid.NewGuid()}",
            MediaType = "Text",
            Language = "Korean",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.LearningResources.Add(resource);
        db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
        {
            Id = Guid.NewGuid().ToString(),
            ResourceId = resource.Id,
            VocabularyWordId = wordId
        });
        db.SaveChanges();
    }

    private void AddEncodingAids(string wordId)
    {
        UpdateWord(wordId, word =>
        {
            word.MnemonicText = "Imagine a bright key opening the lock.";
            word.MnemonicImageUri = "https://example.test/key.png";
            word.AudioPronunciationUri = "https://example.test/key.mp3";
            word.UpdatedAt = DateTime.UtcNow.AddMinutes(1);
        });
    }

    private int SeedExampleSentence(string wordId, string targetSentence, string? learningResourceId = null)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var example = new ExampleSentence
        {
            VocabularyWordId = wordId,
            LearningResourceId = learningResourceId,
            TargetSentence = targetSentence,
            NativeSentence = "Example native sentence",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.ExampleSentences.Add(example);
        db.SaveChanges();
        return example.Id;
    }

    private string GetResourceIdForUser(string userId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.LearningResources.AsNoTracking().First(r => r.UserProfileId == userId).Id;
    }

    private int SeedMinimalPair(string userId, string wordAId, string wordBId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (wordAId, wordBId) = NormalizePairOrder(wordAId, wordBId);
        var pair = new MinimalPair
        {
            UserId = userId,
            VocabularyWordAId = wordAId,
            VocabularyWordBId = wordBId,
            ContrastLabel = "contrast",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.MinimalPairs.Add(pair);
        db.SaveChanges();
        return pair.Id;
    }

    private int SeedMinimalPairAttempt(string userId, int pairId, string promptWordId, string selectedWordId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var session = new MinimalPairSession
        {
            UserId = userId,
            Mode = "Focus",
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.MinimalPairSessions.Add(session);
        db.SaveChanges();

        var attempt = new MinimalPairAttempt
        {
            UserId = userId,
            SessionId = session.Id,
            PairId = pairId,
            PromptWordId = promptWordId,
            SelectedWordId = selectedWordId,
            IsCorrect = promptWordId == selectedWordId,
            SequenceNumber = 1,
            CreatedAt = DateTime.UtcNow
        };
        db.MinimalPairAttempts.Add(attempt);
        db.SaveChanges();
        return attempt.Id;
    }

    private string SeedPhraseConstituent(string phraseWordId, string constituentWordId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var link = new PhraseConstituent
        {
            Id = Guid.NewGuid().ToString(),
            PhraseWordId = phraseWordId,
            ConstituentWordId = constituentWordId,
            CreatedAt = DateTime.UtcNow
        };
        db.PhraseConstituents.Add(link);
        db.SaveChanges();
        return link.Id;
    }

    private void UpdateWord(string wordId, Action<VocabularyWord> update)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var word = db.VocabularyWords.Single(w => w.Id == wordId);
        update(word);
        db.SaveChanges();
    }

    private VocabularyProgress? GetProgress(string wordId, string userId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.VocabularyProgresses.AsNoTracking()
            .FirstOrDefault(p => p.VocabularyWordId == wordId && p.UserId == userId);
    }

    private int CountProgressFor(string wordId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.VocabularyProgresses.AsNoTracking().Count(p => p.VocabularyWordId == wordId);
    }

    private int CountAllContexts()
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.VocabularyLearningContexts.AsNoTracking().Count();
    }

    private int CountContextsFor(string progressId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.VocabularyLearningContexts.AsNoTracking().Count(c => c.VocabularyProgressId == progressId);
    }

    private bool WordExists(string wordId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.VocabularyWords.AsNoTracking().Any(w => w.Id == wordId);
    }

    private ExampleSentence? GetExampleSentence(int id)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.ExampleSentences.AsNoTracking().FirstOrDefault(e => e.Id == id);
    }

    private MinimalPair? GetMinimalPair(int id)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.MinimalPairs.AsNoTracking().FirstOrDefault(p => p.Id == id);
    }

    private MinimalPairAttempt? GetMinimalPairAttempt(int id)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.MinimalPairAttempts.AsNoTracking().FirstOrDefault(a => a.Id == id);
    }

    private PhraseConstituent? GetPhraseConstituent(string id)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.PhraseConstituents.AsNoTracking().FirstOrDefault(pc => pc.Id == id);
    }

    private bool MinimalPairExists(int id)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.MinimalPairs.AsNoTracking().Any(p => p.Id == id);
    }

    private int CountMinimalPairs(string userId, string wordAId, string wordBId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (wordAId, wordBId) = NormalizePairOrder(wordAId, wordBId);
        return db.MinimalPairs.AsNoTracking()
            .Count(p => p.UserId == userId && p.VocabularyWordAId == wordAId && p.VocabularyWordBId == wordBId);
    }

    private int CountPhraseConstituents(string phraseWordId, string constituentWordId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.PhraseConstituents.AsNoTracking()
            .Count(pc => pc.PhraseWordId == phraseWordId && pc.ConstituentWordId == constituentWordId);
    }

    private VocabularyWord? GetWord(string wordId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.VocabularyWords.AsNoTracking().FirstOrDefault(w => w.Id == wordId);
    }

    private static (string WordAId, string WordBId) NormalizePairOrder(string wordAId, string wordBId) =>
        StringComparer.Ordinal.Compare(wordAId, wordBId) <= 0
            ? (wordAId, wordBId)
            : (wordBId, wordAId);

    #endregion
}
