using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native.Tests;

public sealed class NativeQuizSentenceSourceTests
{
    [Fact]
    public async Task MultiSentenceGrade_OnlyMeaningfulTargetUseCreditsAndEachResultStaysDistinct()
    {
        using var fixture = new NativeSliceFixture(sentenceGradingEnabled: true);
        await SeedSyntheticEnglishAsync(fixture);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        var itemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(itemId);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(launch!);
        var sentencePrompt = quiz.OpenSentenceShortcut();
        var target = sentencePrompt.Sentence!.TargetLanguageTerm;
        var input = $"{target} goes home.\n{target} needs another try.";
        Assert.Equal(2, VocabQuizSentenceUsagePolicy.SplitEntries(input).Count);
        Assert.IsType<NativeQuizSentenceGrader>(fixture.Provider
            .GetRequiredService<INativeQuizSentenceGrader>());

        var result = await quiz.SubmitSentenceAsync(input);
        Assert.Equal(NativePhase.Ready, result.Phase);
        Assert.NotNull(result.Sentence);
        Assert.Null(result.ErrorKey);
        Assert.Equal(2, fixture.SentenceGateway.Prompts.Count);
        Assert.Equal(2, result.SentenceResults.Length);
        Assert.True(result.SentenceResults[0].UsageCorrect);
        Assert.False(result.SentenceResults[1].UsageCorrect);
        Assert.Contains("Meaningful usage", result.SentenceResults[0].UsageExplanation);
        Assert.Equal("Grammar feedback", result.SentenceResults[0].GrammarNotes);
        Assert.Equal(1, result.SentenceSuccessCount);
        Assert.Equal(2, result.TotalTurns);
        Assert.Equal(1, result.CorrectCount);
        Assert.Equal(1, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, question.Prompt!.WordId));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserB));
        Assert.Equal(2, fixture.SentenceGateway.Prompts.Count);
        Assert.All(fixture.SentenceGateway.Prompts, prompt =>
        {
            Assert.Contains("target word to evaluate", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(target, prompt);
            Assert.Contains("usage_correct", prompt);
        });

        var stored = NativeQuizStoredState.Deserialize(
            (await fixture.RowsAsync(NativeSliceFixture.UserA, itemId))
                .Session!.StateJson);
        var writer = fixture.Provider.GetRequiredService<INativeQuizSentenceCommitter>();
        var context = stored.LaunchContextKey;
        var found = await writer.FindAsync(NativeSliceFixture.UserA, context,
            stored.TurnId, stored.SentenceSubmissionId!, CancellationToken.None);
        Assert.Equal(1, found!.SuccessCount);
        var replay = await writer.CommitAsync(NativeSliceFixture.UserA, context,
            stored.TurnId, stored.SentenceSubmissionId!,
            found.Results, CancellationToken.None);
        Assert.Equal(found.SuccessCount, replay.SuccessCount);
        Assert.Equal(1, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, question.Prompt.WordId));
        var changed = found.Results.Select(grade => grade with
        {
            UsageCorrect = false
        }).ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.CommitAsync(NativeSliceFixture.UserA, context,
                stored.TurnId, stored.SentenceSubmissionId!, changed,
                CancellationToken.None));

        var next = await quiz.ContinueSentenceShortcutAsync();
        Assert.Null(next.Sentence);
        Assert.True(next.Phase is NativePhase.Ready or NativePhase.Summary);
    }

    [Fact]
    public async Task SentenceReceipt_RejectsDifferentOwnerWithoutScoringThem()
    {
        using var fixture = new NativeSliceFixture(sentenceGradingEnabled: true);
        await SeedSyntheticEnglishAsync(fixture);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        var itemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        await quiz.OpenAsync((await fixture.Dashboard.SelectAssignedQuizAsync(itemId))!);
        var prompt = quiz.OpenSentenceShortcut().Sentence!;
        await quiz.SubmitSentenceAsync($"{prompt.TargetLanguageTerm} goes home.");
        var stored = NativeQuizStoredState.Deserialize(
            (await fixture.RowsAsync(NativeSliceFixture.UserA, itemId))
                .Session!.StateJson);
        var writer = fixture.Provider.GetRequiredService<INativeQuizSentenceCommitter>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            writer.FindAsync(NativeSliceFixture.UserB, stored.LaunchContextKey,
                stored.TurnId, stored.SentenceSubmissionId!, CancellationToken.None));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task SentenceResultAndReceipt_SurviveRestartWithoutRegradingOrDoubleCredit()
    {
        using var fixture = new NativeSliceFixture(sentenceGradingEnabled: true);
        await SeedSyntheticEnglishAsync(fixture);
        var itemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(itemId);
        var question = await quiz.OpenAsync(launch!);
        var target = quiz.OpenSentenceShortcut().Sentence!.TargetLanguageTerm;
        Assert.Equal(NativePhase.Ready,
            (await quiz.SubmitSentenceAsync($"{target} goes home.")).Phase);
        Assert.Equal(1, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, question.Prompt!.WordId));
        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        var afterRestart = fixture.Dashboard;
        var resumedLaunch = await afterRestart.SelectAssignedQuizAsync(itemId);
        using var resumed = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice,
            (await resumed.OpenAsync(resumedLaunch!)).Phase);
        var result = await resumed.ResumeAsync();
        Assert.Equal(NativePhase.Ready, result.Phase);
        Assert.Single(result.SentenceResults);
        Assert.Equal(1, result.SentenceSuccessCount);
        Assert.Single(fixture.SentenceGateway.Prompts);
        Assert.Equal(1, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, question.Prompt.WordId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedSentenceWithFailedFinalSnapshot_RetryOrRestartUsesReceiptOnce(
        bool retryInProcess)
    {
        using var fixture = new NativeSliceFixture(sentenceGradingEnabled: true);
        await SeedSyntheticEnglishAsync(fixture);
        var itemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(itemId);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(launch!);
        var target = quiz.OpenSentenceShortcut().Sentence!.TargetLanguageTerm;
        fixture.FaultSessions.FailSnapshotAtCall =
            fixture.FaultSessions.SnapshotCalls + 3;

        var failed = await quiz.SubmitSentenceAsync($"{target} goes home.");
        Assert.Equal(NativePhase.Error, failed.Phase);
        Assert.Equal(NativeText.SaveFailed, failed.ErrorKey);
        Assert.Equal(1, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, question.Prompt!.WordId));
        if (retryInProcess)
        {
            var retried = await quiz.RetryPendingSentenceAsync();
            Assert.Equal(NativePhase.Ready, retried.Phase);
            Assert.Single(retried.SentenceResults);
        }

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        var refreshed = await fixture.Dashboard.SelectAssignedQuizAsync(itemId);
        using var resumed = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice,
            (await resumed.OpenAsync(refreshed!)).Phase);
        var afterRestart = await resumed.ResumeAsync();
        Assert.Equal(NativePhase.Ready, afterRestart.Phase);
        Assert.Single(afterRestart.SentenceResults);
        Assert.Equal(1, afterRestart.SentenceSuccessCount);
        Assert.Equal(1, afterRestart.TotalTurns);
        Assert.Equal(1, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, question.Prompt.WordId));
    }

    [Fact]
    public void SharedTargetMatching_RejectsWrongLexemeAndDeduplicatesSourceEntries()
    {
        var target = new VocabularyWord { TargetLanguageTerm = "느끼다" };
        var grade = new GradeResponse
        {
            VocabularyAnalysis =
            [
                new VocabularyAnalysis
                {
                    UsedForm = "다르게 느끼다!",
                    DictionaryForm = "다르다",
                    UsageCorrect = true
                }
            ]
        };
        Assert.True(VocabQuizSentenceUsagePolicy
            .GetTargetWordAnalysis(grade, target)!.UsageCorrect);
        grade.VocabularyAnalysis[0].UsedForm = "다르다";
        Assert.Null(VocabQuizSentenceUsagePolicy.GetTargetWordAnalysis(grade, target));
        Assert.Equal(2, VocabQuizSentenceUsagePolicy
            .SplitEntries("  I speak.\nI speak.\nI listen. ").Count);
    }

    [Fact]
    public async Task GradingOffline_ReportsUncreditedSentenceAndAllowsSameInputRetry()
    {
        using var fixture = new NativeSliceFixture(sentenceGradingEnabled: true);
        await SeedSyntheticEnglishAsync(fixture);
        var itemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(itemId))!);
        var target = quiz.OpenSentenceShortcut().Sentence!.TargetLanguageTerm;
        var text = $"{target} goes home.";
        fixture.SentenceGateway.Offline = true;
        var unavailable = await quiz.SubmitSentenceAsync(text);
        Assert.Equal(NativePhase.Ready, unavailable.Phase);
        var unavailableGrade = Assert.Single(unavailable.SentenceResults);
        Assert.True(unavailableGrade.GradeUnavailable);
        Assert.False(unavailableGrade.HasVerifiedTargetUse);
        Assert.Null(unavailableGrade.Accuracy);
        Assert.Null(unavailableGrade.Fluency);
        Assert.Equal(0, unavailable.SentenceSuccessCount);
        Assert.Equal(0, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, question.Prompt!.WordId));

        fixture.SentenceGateway.Offline = false;
        var retried = await quiz.SubmitSentenceAsync(text);
        Assert.Equal(NativePhase.Ready, retried.Phase);
        Assert.False(Assert.Single(retried.SentenceResults).GradeUnavailable);
        Assert.Equal(1, retried.SentenceSuccessCount);
        Assert.Equal(1, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, question.Prompt.WordId));
    }

    [Fact]
    public async Task KoreanGradeUnavailable_UsesLocalizedFeedbackAndDoesNotCredit()
    {
        using var fixture = new NativeSliceFixture(sentenceGradingEnabled: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var itemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(itemId))!);
        var target = quiz.OpenSentenceShortcut().Sentence!.TargetLanguageTerm;
        fixture.SentenceGateway.Offline = true;
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
            var state = await quiz.SubmitSentenceAsync($"{target} goes home.");
            Assert.Equal(NativePhase.Ready, state.Phase);
            var result = Assert.Single(state.SentenceResults);
            Assert.True(result.GradeUnavailable);
            Assert.Contains("채점", result.UsageExplanation);
            Assert.Equal(0, state.SentenceSuccessCount);
            Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("과일")]
    [InlineData("fruit")]
    public async Task EnglishGlossOnly_CannotCreditKoreanWordEvenWhenAiClaimsSuccess(
        string claimedUsedForm)
    {
        using var fixture = new NativeSliceFixture(sentenceGradingEnabled: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            string[] terms = ["과일", "사과", "우유", "물"];
            for (var index = 0; index < terms.Length; index++)
            {
                var word = await db.VocabularyWords.SingleAsync(item =>
                    item.Id == fixture.WordId(NativeSliceFixture.UserA, index));
                word.TargetLanguageTerm = terms[index];
                word.Language = "Korean";
            }
            await db.SaveChangesAsync();
        }
        fixture.SetPlanFocus(
            NativeSliceFixture.UserA, fixture.WordId(NativeSliceFixture.UserA, 0));
        var itemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(itemId))!);
        Assert.Equal("과일", quiz.OpenSentenceShortcut().Sentence!.TargetLanguageTerm);
        fixture.SentenceGateway.ForcedUsageCorrect = true;
        fixture.SentenceGateway.UsedFormOverride = claimedUsedForm;
        fixture.SentenceGateway.FalsePraise = true;

        var rejected = await quiz.SubmitSentenceAsync("I eat fruit.");
        Assert.Equal(NativePhase.Ready, rejected.Phase);
        var rejectedGrade = Assert.Single(rejected.SentenceResults);
        Assert.False(rejectedGrade.UsageCorrect);
        Assert.False(rejectedGrade.GradeUnavailable);
        Assert.False(rejectedGrade.HasVerifiedTargetUse);
        Assert.Null(rejectedGrade.TargetUsedForm);
        Assert.Null(rejectedGrade.Accuracy);
        Assert.Null(rejectedGrade.Fluency);
        Assert.Empty(rejectedGrade.AccuracyExplanation);
        Assert.Empty(rejectedGrade.FluencyExplanation);
        Assert.Empty(rejectedGrade.RecommendedSentence);
        Assert.Empty(rejectedGrade.GrammarNotes);
        Assert.Contains("couldn't confirm", rejectedGrade.UsageExplanation);
        Assert.Equal(0, rejected.SentenceSuccessCount);
        Assert.Equal(0, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, question.Prompt!.WordId));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        fixture.SentenceGateway.FalsePraise = false;
        fixture.SentenceGateway.UsedFormOverride = "과일을";
        var korean = await quiz.SubmitSentenceAsync("저는 과일을 먹었어요.");
        Assert.Equal(NativePhase.Ready, korean.Phase);
        var verifiedGrade = Assert.Single(korean.SentenceResults);
        Assert.True(verifiedGrade.UsageCorrect);
        Assert.False(verifiedGrade.GradeUnavailable);
        Assert.True(verifiedGrade.HasVerifiedTargetUse);
        Assert.Equal("과일을", verifiedGrade.TargetUsedForm);
        Assert.Equal(87d, verifiedGrade.Accuracy);
        Assert.Equal(91d, verifiedGrade.Fluency);
        Assert.Equal("Grammar feedback", verifiedGrade.GrammarNotes);
        Assert.Equal(1, korean.SentenceSuccessCount);
        Assert.Equal(2, korean.TotalTurns);
        Assert.Equal(1, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, question.Prompt.WordId));
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task VerifiedKoreanForm_RetainsGrammarFeedbackWhenModelGradesUsageIncorrect()
    {
        using var fixture = new NativeSliceFixture(sentenceGradingEnabled: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        VocabularyWord word;
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            word = await db.VocabularyWords.SingleAsync(item =>
                item.Id == fixture.WordId(NativeSliceFixture.UserA, 0));
            word.TargetLanguageTerm = "과일";
            word.Language = "Korean";
            await db.SaveChangesAsync();
        }
        fixture.SentenceGateway.ForcedUsageCorrect = false;
        fixture.SentenceGateway.UsedFormOverride = "과일을";

        var grade = await fixture.Provider.GetRequiredService<INativeQuizSentenceGrader>()
            .GradeAsync(NativeSliceFixture.UserA, "저는 과일을 먹었어요.",
                word, "Korean", "English", CancellationToken.None);

        Assert.False(grade.UsageCorrect);
        Assert.False(grade.GradeUnavailable);
        Assert.True(grade.HasVerifiedTargetUse);
        Assert.Equal("과일을", grade.TargetUsedForm);
        Assert.Equal(87d, grade.Accuracy);
        Assert.Equal(91d, grade.Fluency);
        Assert.Equal("Grammar feedback", grade.GrammarNotes);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public void LegacySentenceFeedback_WithClaimedUsedForm_DefaultsToUnverified()
    {
        const string legacySnapshot = """
            {
              "Sentence": "I eat fruit.",
              "UsageCorrect": true,
              "UsageExplanation": "The target word is used naturally.",
              "Accuracy": 100,
              "Fluency": 100,
              "AccuracyExplanation": "Perfect grammar.",
              "FluencyExplanation": "Fluent.",
              "RecommendedSentence": "I eat fruit.",
              "GrammarNotes": "Formal polite Korean grammar.",
              "GradeUnavailable": false,
              "TargetUsedForm": "fruit"
            }
            """;
        var result = System.Text.Json.JsonSerializer
            .Deserialize<NativeQuizSentenceGrade>(legacySnapshot);

        Assert.NotNull(result);
        Assert.False(result.HasVerifiedTargetUse);
        Assert.Equal("fruit", result.TargetUsedForm);
        Assert.True(result.UsageCorrect);
    }

    [Fact]
    public async Task ReceiptWriter_RefusesUnverifiedEnglishGlossEvenIfGraderForgesSuccess()
    {
        using var fixture = new NativeSliceFixture(
            sentenceGradingEnabled: true,
            sentenceGraderOverride: new ForgedEnglishGlossGrader());
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.VocabularyWords.SingleAsync(word =>
                word.Id == fixture.WordId(NativeSliceFixture.UserA, 0)))
                .TargetLanguageTerm = "과일";
            await db.SaveChangesAsync();
        }
        fixture.SetPlanFocus(
            NativeSliceFixture.UserA, fixture.WordId(NativeSliceFixture.UserA, 0));
        var itemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        using var quiz = fixture.Quiz;
        await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(itemId))!);
        Assert.Equal("과일", quiz.OpenSentenceShortcut().Sentence!.TargetLanguageTerm);

        var denied = await quiz.SubmitSentenceAsync("I eat fruit.");
        Assert.Equal(NativePhase.Error, denied.Phase);
        Assert.Equal(NativeText.SaveFailed, denied.ErrorKey);
        Assert.Equal(0, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, fixture.WordId(NativeSliceFixture.UserA, 0)));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        using var receiptScope = fixture.Scope();
        var receipts = await receiptScope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>()
            .ActivitySessions.AsNoTracking().CountAsync(session =>
                session.UserId == NativeSliceFixture.UserA
                && session.ActivityType == NativeQuizSentenceAttemptCommitter.ReceiptActivityType);
        Assert.Equal(0, receipts);
    }

    [Fact]
    public void TargetSurfaceProof_AllowsInflectedKoreanFormsNotNativeGlossOrSubstrings()
    {
        var noun = new VocabularyWord { TargetLanguageTerm = "과일" };
        Assert.True(VocabQuizSentenceUsagePolicy.HasVerifiedTargetUse(
            "저는 과일을 먹었어요.", noun, "Korean", "과일을"));
        Assert.True(VocabQuizSentenceUsagePolicy.HasVerifiedTargetUse(
            "저는 과일을 먹었어요.", noun, "Korean", "과일"));
        Assert.False(VocabQuizSentenceUsagePolicy.HasVerifiedTargetUse(
            "과일상에서 샀어요.", noun, "Korean", "과일"));
        Assert.False(VocabQuizSentenceUsagePolicy.HasVerifiedTargetUse(
            "I eat fruit.", noun, "Korean", "fruit"));
        Assert.False(VocabQuizSentenceUsagePolicy.HasVerifiedTargetUse(
            "I eat fruit.", noun, "Korean", "과일"));
        Assert.False(VocabQuizSentenceUsagePolicy.HasVerifiedTargetUse(
            "나는 fruit를 먹어요.", noun, "Korean", "fruit"));

        var irregularVerb = new VocabularyWord { TargetLanguageTerm = "듣다" };
        Assert.True(VocabQuizSentenceUsagePolicy.HasVerifiedTargetUse(
            "어제 음악을 들었어요.", irregularVerb, "Korean", "들었어요"));
        Assert.False(VocabQuizSentenceUsagePolicy.HasVerifiedTargetUse(
            "I listened yesterday.", irregularVerb, "Korean", "들었어요"));

        var englishWord = new VocabularyWord { TargetLanguageTerm = "fruit" };
        Assert.True(VocabQuizSentenceUsagePolicy.HasVerifiedTargetUse(
            "I ate fruit.", englishWord, "English", "fruit"));
        Assert.False(VocabQuizSentenceUsagePolicy.HasVerifiedTargetUse(
            "I ate fruitcake.", englishWord, "English", "fruit"));
    }

    private static async Task SeedSyntheticEnglishAsync(NativeSliceFixture fixture)
    {
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.UserProfiles.SingleAsync(
            profile => profile.Id == NativeSliceFixture.UserA)).TargetLanguage = "English";
        foreach (var word in await db.VocabularyWords
            .Where(item => item.Id.StartsWith("word-" + NativeSliceFixture.UserA))
            .ToListAsync())
            word.Language = "English";
        await db.SaveChangesAsync();
    }
}

internal sealed class ForgedEnglishGlossGrader : INativeQuizSentenceGrader
{
    public Task<NativeQuizSentenceGrade> GradeAsync(
        string userId, string sentence, VocabularyWord word,
        string targetLanguage, string nativeLanguage, CancellationToken cancellationToken) =>
        Task.FromResult(new NativeQuizSentenceGrade(
            sentence, true, "Model incorrectly claimed meaningful target use.",
            100, 100, "", "", "", "")
        {
            TargetUsedForm = "fruit"
        });
}

internal sealed class ScratchSentenceGateway : IOwnerBoundNativeSentenceGateway
{
    public List<string> Prompts { get; } = [];
    public bool Offline { get; set; }
    public bool? ForcedUsageCorrect { get; set; }
    public string? UsedFormOverride { get; set; }
    public bool FalsePraise { get; set; }

    public Task<GradeResponse?> GradeAsync(
        string userId, string renderedSourcePrompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Offline)
            throw new HttpRequestException("Synthetic sentence grader offline.");
        Prompts.Add(renderedSourcePrompt);
        var target = Regex.Match(renderedSourcePrompt,
            "The target word to evaluate is \"([^\"]+)\"").Groups[1].Value;
        var usageCorrect = ForcedUsageCorrect ?? renderedSourcePrompt.Contains(
            "goes home", StringComparison.Ordinal);
        return Task.FromResult<GradeResponse?>(new GradeResponse
        {
            Accuracy = FalsePraise ? 100 : 87,
            Fluency = FalsePraise ? 100 : 91,
            AccuracyExplanation = FalsePraise ? "Your sentence is perfectly fine." : "",
            FluencyExplanation = FalsePraise ? "Fluent target-language sentence." : "",
            GrammarNotes = new GrammarNotes
            {
                Explanation = FalsePraise
                    ? "Correct formal polite -습니다 style." : "Grammar feedback",
                RecommendedTranslation = FalsePraise
                    ? "This target word is used naturally." : ""
            },
            VocabularyAnalysis =
            [
                new VocabularyAnalysis
                {
                    DictionaryForm = target,
                    UsedForm = UsedFormOverride ?? target,
                    UsageCorrect = usageCorrect,
                    UsageExplanation = usageCorrect
                        ? FalsePraise
                            ? $"The target word {target} is used naturally and correctly."
                            : "Meaningful usage"
                        : "Needs another try"
                }
            ]
        });
    }
}
