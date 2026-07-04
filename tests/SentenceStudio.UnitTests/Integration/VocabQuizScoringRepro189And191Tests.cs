using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.UnitTests.PlanGeneration;
using Xunit;
using Xunit.Abstractions;

namespace SentenceStudio.UnitTests.Integration;

/// <summary>
/// Failing repro tests for the Vocabulary Quiz scoring bugs identified by Captain
/// in issues #189 and #191. Author: Jayne (Tester) — Stream B Step 1.
///
/// These tests exist to:
///   1. Pin down the EXPECTED post-state of VocabularyProgress after well-defined
///      quiz interactions, so Wash has unambiguous targets to fix against.
///   2. Disambiguate competing hypotheses for #189 (service double-increment vs
///      UI panel reading legacy obsolete fields).
///   3. Expose how aggressively new words rotate out today (#191), so the team
///      can decide on a corrected curve.
///
/// Tests use the same in-memory SQLite + real EF Core fixture as
/// <see cref="MasteryAlgorithmIntegrationTests"/>, so the failure signatures
/// reflect production code paths, not mocks.
/// </summary>
public class VocabQuizScoringRepro189And191Tests
    : IClassFixture<PlanGenerationTestFixture>, IDisposable
{
    private readonly PlanGenerationTestFixture _fixture;
    private readonly VocabularyProgressService _progressService;
    private readonly ITestOutputHelper _output;

    public VocabQuizScoringRepro189And191Tests(
        PlanGenerationTestFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _fixture.ClearAllData();
        _fixture.SeedUserProfile();

        var scope = fixture.ServiceProvider.CreateScope();
        var progressRepo = scope.ServiceProvider.GetRequiredService<VocabularyProgressRepository>();
        var contextRepo = new VocabularyLearningContextRepository(
            fixture.ServiceProvider,
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<VocabularyLearningContextRepository>());

        _progressService = new VocabularyProgressService(
            progressRepo,
            contextRepo,
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<VocabularyProgressService>(),
            fixture.ServiceProvider);
    }

    public void Dispose() { }

    // ---------------------------------------------------------------------
    // #189 — Attempt counting / accuracy correctness
    // ---------------------------------------------------------------------
    //
    // Captain's report: After ONE correct recognition turn on the word 털,
    // the Learning Details panel shows "2 production attempts / 50% accuracy".
    //
    // Two competing hypotheses:
    //   (a) ProgressService is double-incrementing on a single attempt.
    //   (b) The Learning Details panel is reading legacy obsolete fields
    //       that don't match the new streak-based truth.
    //
    // This test asserts the EXPECTED post-state per the bug report. If it
    // FAILS, hypothesis (a) is correct — the service is buggy. If it PASSES,
    // hypothesis (b) is correct — the bug is purely in the UI panel reading
    // wrong fields, and the panel needs to be fixed (not the service).
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Repro189_SingleCorrectRecognitionAttempt_ProducesExpectedPanelState()
    {
        // Arrange — a brand-new word with no prior progress, mimicking 털.
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        var attempt = MakeRecognitionAttempt(wordId, wasCorrect: true);

        // Act — one correct multiple-choice (recognition) turn, exactly as the
        // quiz issues via VocabQuiz.razor → ProgressService.RecordAttemptAsync.
        var result = await _progressService.RecordAttemptAsync(attempt);

        // Dump the row for the PR description so Wash sees the actual values.
        _output.WriteLine(DumpProgress(result));

        // Assert — every field the Learning Details panel reads must match the
        // narrative of "ONE correct recognition turn".
        using (new FluentAssertions.Execution.AssertionScope())
        {
            // Core counters (panel rows TotalAttempts / CorrectAttempts / Accuracy).
            result.TotalAttempts.Should().Be(1,
                "exactly one attempt was recorded");
            result.CorrectAttempts.Should().Be(1,
                "the single attempt was correct");
            result.Accuracy.Should().Be(1.0f,
                "1 correct ÷ 1 total = 100%, NOT the 50% Captain reported");

            // Streak fields (panel rows CurrentStreak / ProductionInStreak).
            result.CurrentStreak.Should().Be(1f,
                "one correct MC at DifficultyWeight=1.0 yields streak 1");
            result.ProductionInStreak.Should().Be(0,
                "MultipleChoice is recognition, not production — must NOT be 2");

            // Legacy obsolete fields — must not be incremented as production.
#pragma warning disable CS0618
            result.RecognitionAttempts.Should().Be(1,
                "Phase=Recognition for InputMode=MultipleChoice");
            result.RecognitionCorrect.Should().Be(1);
            result.ProductionAttempts.Should().Be(0,
                "no production attempts occurred");
            result.ProductionCorrect.Should().Be(0);
            result.ApplicationAttempts.Should().Be(0);
            result.ApplicationCorrect.Should().Be(0);
#pragma warning restore CS0618
        }
    }

    /// <summary>
    /// Companion test for #189: belt-and-suspenders assertion that a single
    /// recognition (MultipleChoice) attempt does not touch any obsolete
    /// production-side counter. If this test passes today (which the service
    /// code suggests it will), the bug is in the panel layer — the panel must
    /// stop displaying these legacy fields and read only the streak-based
    /// truth.
    /// </summary>
    [Fact]
    public async Task Repro189_SingleCorrectRecognition_LegacyProductionFieldsRemainZero()
    {
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        var result = await _progressService.RecordAttemptAsync(
            MakeRecognitionAttempt(wordId, wasCorrect: true));

        _output.WriteLine(DumpProgress(result));

#pragma warning disable CS0618
        result.ProductionAttempts.Should().Be(0,
            "Phase derived from MultipleChoice is Recognition; ProductionAttempts must NEVER be incremented for an MC turn (#189)");
        result.ProductionCorrect.Should().Be(0,
            "ProductionCorrect must stay zero on a recognition-only turn (#189)");
        result.ProductionAccuracy.Should().Be(0,
            "ProductionAccuracy = ProductionCorrect/ProductionAttempts must be 0 with no production turns (#189)");
#pragma warning restore CS0618
    }

    [Fact]
    public async Task RecordAttemptAsync_VocabQuizCorrectAttempts_IncrementPersistentDemonstrationCounters()
    {
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        var afterRecognition = await _progressService.RecordAttemptAsync(
            MakeRecognitionAttempt(wordId, wasCorrect: true));

        afterRecognition.QuizRecognitionDemonstrations.Should().Be(1,
            "correct Vocab Quiz MultipleChoice attempts count as persistent recognition demonstrations");
        afterRecognition.QuizProductionDemonstrations.Should().Be(0);

        var afterWrongText = await _progressService.RecordAttemptAsync(new VocabularyAttempt
        {
            VocabularyWordId = wordId,
            UserId = PlanGenerationTestFixture.TestUserId,
            Activity = "VocabularyQuiz",
            InputMode = "Text",
            ContextType = "Isolated",
            WasCorrect = false,
            DifficultyWeight = 1.5f,
            ResponseTimeMs = 1500
        });

        afterWrongText.QuizRecognitionDemonstrations.Should().Be(1,
            "wrong answers do not reset or decrement persistent demonstrations");
        afterWrongText.QuizProductionDemonstrations.Should().Be(0,
            "wrong Text attempts do not add production demonstrations");

        var afterCorrectText = await _progressService.RecordAttemptAsync(new VocabularyAttempt
        {
            VocabularyWordId = wordId,
            UserId = PlanGenerationTestFixture.TestUserId,
            Activity = "VocabularyQuiz",
            InputMode = "Text",
            ContextType = "Isolated",
            WasCorrect = true,
            DifficultyWeight = 1.5f,
            ResponseTimeMs = 1500
        });

        afterCorrectText.QuizRecognitionDemonstrations.Should().Be(1);
        afterCorrectText.QuizProductionDemonstrations.Should().Be(1,
            "correct Vocab Quiz Text attempts count as persistent production demonstrations");
    }

    [Fact]
    public async Task RecordAttemptAsync_NonQuizCorrectAttempts_DoNotIncrementQuizDemonstrations()
    {
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();

        var result = await _progressService.RecordAttemptAsync(new VocabularyAttempt
        {
            VocabularyWordId = wordId,
            UserId = PlanGenerationTestFixture.TestUserId,
            Activity = "Reading",
            InputMode = "Text",
            ContextType = "Isolated",
            WasCorrect = true,
            DifficultyWeight = 1.5f,
            ResponseTimeMs = 1500
        });

        result.QuizRecognitionDemonstrations.Should().Be(0);
        result.QuizProductionDemonstrations.Should().Be(0,
            "the dedicated counters are scoped to Vocab Quiz attempts only");
    }

    // ---------------------------------------------------------------------
    // #191 — Latter quiz rounds rapidly empty
    // ---------------------------------------------------------------------
    //
    // Captain's report: User mastered 26 new words in 58 turns over 8 rounds.
    // Round 8 showed only 2 words — new words rotate out far too quickly.
    //
    // Hypothesis: VocabularyQuizItem.ReadyToRotateOut Tier 2
    // (mastery >= 0.50 OR streak >= 3) lets a brand-new word rotate after
    // only 2-3 correct MC turns followed by a single Text turn — far less
    // demonstration than 8 rounds × ~7 turns implies.
    //
    // The walk below mimics VocabQuiz.razor's ChooseInteractionMode logic:
    //   • Default mode is MultipleChoice.
    //   • Mode flips to Text once CurrentStreak >= 3 OR MasteryScore >= 0.50.
    // We simulate ALL CORRECT answers and record the first turn at which
    // VocabularyQuizItem.ReadyToRotateOut becomes true.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Repro191_NewWord_AllCorrect_DoesNotRotateOutBeforeFifthTurn()
    {
        // Arrange — one fresh word, no prior progress.
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();
        var word = SeedWordReference(wordId);

        var quizItem = new VocabularyQuizItem
        {
            Word = word,
            Progress = await _progressService.GetProgressAsync(wordId),
            IsCurrent = true,
        };

        // Walk — at most 8 turns of all-correct answers, mirroring the quiz's
        // mode-selection rule. Track ReadyToRotateOut at each turn boundary.
        const int maxTurns = 8;
        int? firstRotateTurn = null;
        var trace = new List<string>();

        for (int turn = 1; turn <= maxTurns; turn++)
        {
            // Replicate VocabQuiz.razor mode-selection from the current Progress.
            string mode = ChooseQuizModeForTurn(quizItem);
            float weight = mode == "Text" ? 1.5f : 1.0f;

            var attempt = new VocabularyAttempt
            {
                VocabularyWordId = wordId,
                UserId = PlanGenerationTestFixture.TestUserId,
                Activity = "VocabularyQuiz",
                InputMode = mode,
                ContextType = "Isolated",
                WasCorrect = true,
                DifficultyWeight = weight,
                ResponseTimeMs = 1500
            };

            quizItem.Progress = await _progressService.RecordAttemptAsync(attempt);

            // Mirror the per-turn session counter bumps from VocabQuiz.razor.
            quizItem.SessionCorrectCount++;
            if (mode == "MultipleChoice") quizItem.SessionMCCorrect++;
            else quizItem.SessionTextCorrect++;

            trace.Add(
                $"turn={turn} mode={mode} streak={quizItem.Progress.CurrentStreak:F2} "
              + $"prodInStreak={quizItem.Progress.ProductionInStreak} "
              + $"mastery={quizItem.Progress.MasteryScore:F3} "
              + $"sessMC={quizItem.SessionMCCorrect} sessText={quizItem.SessionTextCorrect} "
              + $"ReadyToRotateOut={quizItem.ReadyToRotateOut}");

            if (quizItem.ReadyToRotateOut && firstRotateTurn is null)
                firstRotateTurn = turn;
        }

        foreach (var line in trace) _output.WriteLine(line);
        _output.WriteLine($"First ReadyToRotateOut turn: {firstRotateTurn?.ToString() ?? "never"}");

        // Assertion — Captain reported that 26 fresh words mastered in just
        // 58 turns over 8 rounds is too aggressive. A brand-new word receiving
        // ALL CORRECT answers should NOT yet be eligible for rotation by turn 4.
        // The expected curve is TBD (Wash + Captain to define), but turn-4
        // rotation is well below any reasonable mastery bar — failing here
        // exposes that #191 is real.
        firstRotateTurn.Should().BeGreaterOrEqualTo(5,
            "a brand-new word with 4 all-correct turns demonstrates too little mastery "
          + "to rotate out — current Tier 2 logic flips this at turn 4 (3 MC + 1 Text), "
          + "which is the rapid-empty behavior #191 describes");
    }

    /// <summary>
    /// Documents (without prescribing) the current rotation behavior so that
    /// when Wash lands the fix, the diff in this number tells everyone the
    /// curve actually changed. Passing test — pure characterization.
    /// </summary>
    [Fact]
    public async Task Repro191_CharacterizeCurrentBehavior_FreshWordRotatesAtTurnN()
    {
        var resource = _fixture.SeedResource(vocabWordCount: 1);
        var wordId = _fixture.GetResourceVocabularyWordIds(resource.Id).First();
        var word = SeedWordReference(wordId);

        var quizItem = new VocabularyQuizItem
        {
            Word = word,
            Progress = await _progressService.GetProgressAsync(wordId),
        };

        int? firstRotateTurn = null;
        for (int turn = 1; turn <= 12 && firstRotateTurn is null; turn++)
        {
            string mode = ChooseQuizModeForTurn(quizItem);
            quizItem.Progress = await _progressService.RecordAttemptAsync(new VocabularyAttempt
            {
                VocabularyWordId = wordId,
                UserId = PlanGenerationTestFixture.TestUserId,
                Activity = "VocabularyQuiz",
                InputMode = mode,
                ContextType = "Isolated",
                WasCorrect = true,
                DifficultyWeight = mode == "Text" ? 1.5f : 1.0f,
                ResponseTimeMs = 1500
            });

            quizItem.SessionCorrectCount++;
            if (mode == "MultipleChoice") quizItem.SessionMCCorrect++;
            else quizItem.SessionTextCorrect++;

            if (quizItem.ReadyToRotateOut) firstRotateTurn = turn;
        }

        _output.WriteLine($"Characterization: fresh word with all-correct answers "
            + $"first becomes ReadyToRotateOut at turn {firstRotateTurn?.ToString() ?? "never"}.");

        firstRotateTurn.Should().NotBeNull(
            "if rotation never happens, the test setup is wrong — a non-failing snapshot "
          + "is fine here, but it must capture some turn number for the team to debate");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static VocabularyAttempt MakeRecognitionAttempt(string wordId, bool wasCorrect) =>
        new VocabularyAttempt
        {
            VocabularyWordId = wordId,
            UserId = PlanGenerationTestFixture.TestUserId,
            Activity = "VocabularyQuiz",
            // VocabQuiz.razor sends the literal string "MultipleChoice"
            // (not InputMode.MultipleChoice.ToString(), which happens to
            // match) when userMode is the default for a fresh word.
            InputMode = "MultipleChoice",
            WasCorrect = wasCorrect,
            DifficultyWeight = 1.0f,
            ContextType = "Isolated",
            ResponseTimeMs = 1500
        };

    /// <summary>
    /// Mirror of VocabQuiz.razor's ChooseInteractionMode (lines 792-801):
    ///   • PendingRecognitionCheck → MultipleChoice
    ///   • CurrentStreak >= 3 OR MasteryScore >= 0.50 → Text
    ///   • else → MultipleChoice
    /// </summary>
    private static string ChooseQuizModeForTurn(VocabularyQuizItem item)
    {
        if (item.PendingRecognitionCheck) return "MultipleChoice";
        var streak = item.Progress?.CurrentStreak ?? 0f;
        var mastery = item.Progress?.MasteryScore ?? 0f;
        return (streak >= 3f || mastery >= 0.50f) ? "Text" : "MultipleChoice";
    }

    /// <summary>
    /// Loads the seeded VocabularyWord row so VocabularyQuizItem has a
    /// proper Word reference (required = constructor-init only).
    /// </summary>
    private VocabularyWord SeedWordReference(string wordId)
    {
        using var scope = _fixture.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return db.VocabularyWords.AsQueryable().First(w => w.Id == wordId);
    }

    private static string DumpProgress(VocabularyProgress p)
    {
#pragma warning disable CS0618
        return $"VocabularyProgress dump:\n"
             + $"  TotalAttempts={p.TotalAttempts}, CorrectAttempts={p.CorrectAttempts}, Accuracy={p.Accuracy:F3}\n"
             + $"  CurrentStreak={p.CurrentStreak:F2}, ProductionInStreak={p.ProductionInStreak}, MasteryScore={p.MasteryScore:F3}\n"
             + $"  RecognitionAttempts={p.RecognitionAttempts}, RecognitionCorrect={p.RecognitionCorrect}\n"
             + $"  ProductionAttempts={p.ProductionAttempts}, ProductionCorrect={p.ProductionCorrect}\n"
             + $"  ApplicationAttempts={p.ApplicationAttempts}, ApplicationCorrect={p.ApplicationCorrect}\n"
             + $"  IsKnown={p.IsKnown}, Status={p.Status}";
#pragma warning restore CS0618
    }
}
