using System.Text.Json.Nodes;
using CoreSync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Shared.Models;
using Xunit;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Phase 2b (Captain 2026-11-03, docs/sync-all-tables-plan.md): a quiz run syncs between every
/// client in one saved format (docs/activity-session-format.md). These rewrite the run's row the
/// way another client would have left it, then resume it here.
/// </summary>
public sealed class NativeQuizCrossClientResumeTests
{
    [Theory]
    [InlineData(null)]
    // The other install asked the question in its own direction; this device's setting differs.
    [InlineData("NativeToTarget")]
    public async Task AnswerGradedByAnotherInstall_IsMovedPastWithoutScoringItHere(string? resumingDirection)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planId);
        using (var quiz = fixture.Quiz)
        {
            var question = await quiz.OpenAsync(launch!);
            Assert.Equal(NativePhase.Feedback,
                (await quiz.AnswerAsync(question.Choices[0].Text)).Phase);
        }
        var graded = await SavedStateAsync(fixture, planId);
        Assert.NotNull(graded.PendingAttempt);
        await RewriteAsync(fixture, planId, state => state["savedBy"] = "native:another-install");

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        if (resumingDirection is not null)
            fixture.QuizPreferences.DisplayDirection = resumingDirection;
        using var resumed = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice, (await resumed.OpenAsync(launch!)).Phase);
        var next = await resumed.ResumeAsync();

        Assert.Equal(NativePhase.Ready, next.Phase);
        Assert.NotEqual(graded.PendingAttempt!.VocabularyWordId, next.Prompt!.WordId);
        // The saved counters already include the graded answer; only its install scores it.
        Assert.Equal(1, next.TotalTurns);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(0, await fixture.ReceiptCountAsync(NativeSliceFixture.UserA));
        var saved = await SavedStateAsync(fixture, planId);
        Assert.Null(saved.PendingAttempt);
        Assert.StartsWith("native:", saved.SavedBy);
        Assert.NotEqual("native:another-install", saved.SavedBy);
    }

    [Fact]
    public async Task AnswerGradedByThisInstall_IsStillFinishedHere()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planId);
        using (var quiz = fixture.Quiz)
        {
            var question = await quiz.OpenAsync(launch!);
            await quiz.AnswerAsync(question.Choices[0].Text);
        }

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var resumed = fixture.Quiz;
        await resumed.OpenAsync(launch!);
        Assert.Equal(NativePhase.Feedback, (await resumed.ResumeAsync()).Phase);
        await resumed.NextAsync();
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task RunSavedByTheWeb_ResumesAtItsQuestion_AndThisDeviceRecordsItsOwnFields()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planId);
        string questionWord;
        using (var quiz = fixture.Quiz)
            questionWord = (await quiz.OpenAsync(launch!)).Prompt!.WordId;
        // What the web/MAUI quiz page saves: no plan row, ad-hoc item, re-practice or install.
        await RewriteAsync(fixture, planId, state =>
        {
            state.Remove("planItemRowId");
            state.Remove("adHocPlanItemId");
            state.Remove("isRepractice");
            state.Remove("savedBy");
            state["turnId"] = Guid.NewGuid().ToString("N");
        });

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var resumed = fixture.Quiz;
        var choice = await resumed.OpenAsync(launch!);
        Assert.Equal(NativePhase.ResumeChoice, choice.Phase);
        Assert.Null(choice.ErrorKey);
        var question = await resumed.ResumeAsync();
        Assert.Equal(NativePhase.Ready, question.Phase);
        Assert.Equal(questionWord, question.Prompt!.WordId);

        await resumed.AnswerAsync(question.Choices[0].Text);
        await resumed.NextAsync();
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        var saved = await SavedStateAsync(fixture, planId);
        var (item, _) = await fixture.RowsAsync(NativeSliceFixture.UserA, planId);
        Assert.Equal(item.Id, saved.PlanItemRowId);
        Assert.False(saved.IsRepractice);
        Assert.StartsWith("native:", saved.SavedBy);
    }

    [Fact]
    public async Task RoundFinishedByMovingPastAnotherInstallsAnswer_CompletesTheCardOnDone()
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planId);
        using (var quiz = fixture.Quiz)
        {
            var question = await quiz.OpenAsync(launch!);
            await quiz.AnswerAsync(await CorrectAnswerAsync(fixture, question));
            question = await quiz.NextAsync();
            Assert.Equal(NativePhase.Ready, question.Phase);
            // The round's last answer is graded on "another install" and left unconfirmed.
            await quiz.AnswerAsync(await CorrectAnswerAsync(fixture, question));
        }
        await RewriteAsync(fixture, planId, state => state["savedBy"] = "native:another-install");

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var resumed = fixture.Quiz;
        await resumed.OpenAsync(launch!);
        Assert.Equal(NativePhase.Summary, (await resumed.ResumeAsync()).Phase);
        var summary = await SavedStateAsync(fixture, planId);
        Assert.True(summary.SummaryVisible);
        Assert.True(summary.TurnScoredElsewhere);

        Assert.Equal(NativeDestination.TodaysPlan, (await resumed.DoneAsync()).Destination);
        Assert.True((await fixture.RowsAsync(NativeSliceFixture.UserA, planId)).Item.IsCompleted);
        // Only the first answer was scored here; the last is the other install's.
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task OwnSummaryWithoutAReceiptForItsLastAnswer_IsStillRefused()
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planId);
        using (var quiz = fixture.Quiz)
        {
            var state = await quiz.OpenAsync(launch!);
            for (var turn = 0; turn < 10 && state.Phase == NativePhase.Ready; turn++)
            {
                await quiz.AnswerAsync(await CorrectAnswerAsync(fixture, state));
                state = await quiz.NextAsync();
            }
            Assert.Equal(NativePhase.Summary, state.Phase);
        }
        // An edited summary that claims a last answer this install never scored.
        await RewriteAsync(fixture, planId, state => state["turnId"] = Guid.NewGuid().ToString("N"));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var resumed = fixture.Quiz;
        await resumed.OpenAsync(launch!);
        Assert.Equal(NativePhase.Summary, (await resumed.ResumeAsync()).Phase);
        Assert.NotEqual(NativeDestination.TodaysPlan, (await resumed.DoneAsync()).Destination);
        Assert.False((await fixture.RowsAsync(NativeSliceFixture.UserA, planId)).Item.IsCompleted);
    }

    [Fact]
    public async Task SummaryFromAnotherClient_WithNoAnswerScoredDuringTheRun_DoesNotCompleteTheCard()
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planId);
        using (var quiz = fixture.Quiz)
        {
            var state = await quiz.OpenAsync(launch!);
            for (var turn = 0; turn < 10 && state.Phase == NativePhase.Ready; turn++)
            {
                await quiz.AnswerAsync(await CorrectAnswerAsync(fixture, state));
                state = await quiz.NextAsync();
            }
            Assert.Equal(NativePhase.Summary, state.Phase);
        }
        // A summary another client saved, so no receipt is here, for a run in which no answer
        // left a learning record (an edited state): the answers above predate its start.
        await RewriteAsync(fixture, planId, state =>
        {
            state["savedBy"] = "native:another-install";
            state["turnId"] = Guid.NewGuid().ToString("N");
        });
        await MoveRunStartAsync(fixture, planId, TimeSpan.FromHours(1));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var resumed = fixture.Quiz;
        await resumed.OpenAsync(launch!);
        Assert.Equal(NativePhase.Summary, (await resumed.ResumeAsync()).Phase);
        Assert.NotEqual(NativeDestination.TodaysPlan, (await resumed.DoneAsync()).Destination);
        Assert.False((await fixture.RowsAsync(NativeSliceFixture.UserA, planId)).Item.IsCompleted);
    }

    [Fact]
    public async Task SavesFromBeforeRunsSynced_AreClaimedByThisInstall_WebSavesAreNot()
    {
        using var fixture = new NativeSliceFixture();
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        ActivitySession Run(string key, string json, string owner = NativeSliceFixture.UserA) => new()
        {
            Id = SentenceStudio.Services.SyncIds.ActivitySession(owner, "VocabularyReview", key),
            UserId = owner, ActivityType = "VocabularyReview",
            LaunchContextKey = key, StateJson = json, Status = ActivitySessionStatus.InProgress,
            StartedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.ActivitySessions.AddRange(
            Run("native-old", """{"version":1,"planItemRowId":"row-1","snapshot":{}}"""),
            Run("web", """{"version":1,"snapshot":{}}"""),
            Run("native-new", """{"version":1,"planItemRowId":"row-2","snapshot":{},"savedBy":"native:other"}"""),
            Run("other-learner", """{"version":1,"planItemRowId":"row-3","snapshot":{}}""",
                NativeSliceFixture.UserB));
        await db.SaveChangesAsync();
        var preferences = new NativeSliceFixture.ScratchPreferences();
        var installId = NativeInstallId.Get(preferences);

        await NativeOwnerStores.ClaimLegacyQuizSavesOnceAsync(
            db, NativeSliceFixture.UserA, preferences, default);

        string? SavedBy(string key) => System.Text.Json.Nodes.JsonNode.Parse(db.ActivitySessions
            .AsNoTracking().Single(row => row.LaunchContextKey == key).StateJson)!["savedBy"]?.GetValue<string>();
        Assert.Equal(installId, SavedBy("native-old"));
        Assert.Null(SavedBy("web"));
        Assert.Equal("native:other", SavedBy("native-new"));
        // Only the activated owner's runs are written.
        Assert.Null(SavedBy("other-learner"));

        // Once claimed, a later run without savedBy (downloaded from another client after the
        // first sync) is never claimed.
        db.ActivitySessions.Add(Run("downloaded", """{"version":1,"planItemRowId":"row-4","snapshot":{}}"""));
        await db.SaveChangesAsync();
        await NativeOwnerStores.ClaimLegacyQuizSavesOnceAsync(
            db, NativeSliceFixture.UserA, preferences, default);
        Assert.Null(SavedBy("downloaded"));
    }

    [Fact]
    public async Task WebRunWithAWordMasteredMidRound_ResumesAndScoresTheNextAnswer()
    {
        // Seen on the local stack 2026-11-03: the web removes a word mastered mid-round from the
        // batch and the round order, and keeps it in the round's session list.
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(planId);
        string questionWord;
        using (var quiz = fixture.Quiz)
            questionWord = (await quiz.OpenAsync(launch!)).Prompt!.WordId;
        string? masteredWord = null;
        await RewriteAsync(fixture, planId, state =>
        {
            state.Remove("savedBy");
            state.Remove("planItemRowId");
            var snapshot = state["snapshot"]!.AsObject();
            var round = snapshot["roundWordOrder"]!.AsArray();
            masteredWord = round[^1]!.GetValue<string>();
            round.RemoveAt(round.Count - 1);
            var batch = snapshot["batchPool"]!.AsArray();
            batch.Remove(batch.Single(item => item!["wordId"]!.GetValue<string>() == masteredWord));
            snapshot["wordsMastered"] = 1;
        });
        Assert.Contains(masteredWord!, (await SavedStateAsync(fixture, planId)).Snapshot.SessionItemsWordIds);

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var resumed = fixture.Quiz;
        var choice = await resumed.OpenAsync(launch!);
        Assert.Equal(NativePhase.ResumeChoice, choice.Phase);
        Assert.Null(choice.ErrorKey);
        var question = await resumed.ResumeAsync();
        Assert.Equal(NativePhase.Ready, question.Phase);
        Assert.Equal(questionWord, question.Prompt!.WordId);
        Assert.Equal(3, question.TurnsInRound);

        // The real scorer accepts the run and records the answer.
        Assert.Equal(NativePhase.Feedback,
            (await resumed.AnswerAsync(await CorrectAnswerAsync(fixture, question))).Phase);
        Assert.NotEqual(NativePhase.Error, (await resumed.NextAsync()).Phase);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public void UploadLeavesOutDeviceOnlyReceipts_ByTypeOrId()
    {
        static SyncItem Row(string id, string type) => new("ActivitySession", ChangeType.Insert,
            new Dictionary<string, object?> { ["Id"] = id, ["ActivityType"] = type });

        Assert.False(NativeLocalOnlyMappingExportProvider.IsDeviceOnlyRow(
            Row("as:u:VocabularyReview:k", ActivitySessionTypes.VocabularyReview)));
        Assert.True(NativeLocalOnlyMappingExportProvider.IsDeviceOnlyRow(
            Row(Guid.NewGuid().ToString(), ActivitySessionTypes.NativeAttemptReceipt)));
        Assert.True(NativeLocalOnlyMappingExportProvider.IsDeviceOnlyRow(
            Row(Guid.NewGuid().ToString(), ActivitySessionTypes.NativeSentenceReceipt)));
        Assert.True(NativeLocalOnlyMappingExportProvider.IsDeviceOnlyRow(
            new SyncItem("ActivitySession", ChangeType.Delete,
                new Dictionary<string, object?> { ["Id"] = ActivitySessionTypes.NewReceiptId() })));
        Assert.False(NativeLocalOnlyMappingExportProvider.IsDeviceOnlyRow(
            new SyncItem("VocabularyProgress", ChangeType.Insert,
                new Dictionary<string, object?> { ["Id"] = ActivitySessionTypes.NewReceiptId() })));
    }

    private static async Task<string> CorrectAnswerAsync(NativeSliceFixture fixture, NativeQuizState state)
    {
        using var scope = fixture.Scope();
        var word = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyWords.AsNoTracking().SingleAsync(row => row.Id == state.Prompt!.WordId);
        return state.Prompt!.IsTargetLanguagePrompt ? word.NativeLanguageTerm! : word.TargetLanguageTerm!;
    }

    private static async Task<VocabQuizSavedState> SavedStateAsync(
        NativeSliceFixture fixture, string planId) =>
        VocabQuizSavedState.Deserialize(
            (await fixture.RowsAsync(NativeSliceFixture.UserA, planId)).Session!.StateJson);

    private static async Task RewriteAsync(
        NativeSliceFixture fixture, string planId, Action<JsonObject> change)
    {
        var (_, session) = await fixture.RowsAsync(NativeSliceFixture.UserA, planId);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.ActivitySessions.SingleAsync(item => item.Id == session!.Id);
        var state = JsonNode.Parse(row.StateJson)!.AsObject();
        change(state);
        row.StateJson = state.ToJsonString();
        await db.SaveChangesAsync();
    }

    private static async Task MoveRunStartAsync(
        NativeSliceFixture fixture, string planId, TimeSpan by)
    {
        var (_, session) = await fixture.RowsAsync(NativeSliceFixture.UserA, planId);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.ActivitySessions.SingleAsync(item => item.Id == session!.Id);
        row.StartedAt += by;
        await db.SaveChangesAsync();
    }
}
