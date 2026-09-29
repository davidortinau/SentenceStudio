using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Services.Timer;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

public sealed class NativeProductionAttemptTests
{
    [Fact]
    public async Task Q25a_RestartLeavesPendingTurnUnscoredUntilNext()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var first = NewAdapter(fixture);
        var pending = await PreparePendingAsync(
            fixture, NativeSliceFixture.UserA, first);

        Assert.Null(await first.FindAsync(
            NativeSliceFixture.UserA, pending.State.LaunchContextKey,
            pending.State.TurnId, CancellationToken.None));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Empty(await ReceiptsAsync(fixture, NativeSliceFixture.UserA));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var resumed = NewPresenter(fixture, NewAdapter(fixture));
        Assert.Equal(NativePhase.ResumeChoice,
            (await resumed.OpenAsync(pending.Launch)).Phase);
        var feedback = await resumed.ResumeAsync();
        Assert.Equal(NativePhase.Feedback, feedback.Phase);
        Assert.Equal(pending.State.TurnId, feedback.TurnId);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        var next = await resumed.NextAsync();
        Assert.Equal(NativePhase.Ready, next.Phase);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Single(await ReceiptsAsync(fixture, NativeSliceFixture.UserA));
        Assert.False((await fixture.RowsAsync(
            NativeSliceFixture.UserA, pending.PlanItemId)).Item.IsCompleted);
    }

    [Fact]
    public async Task Q25b_FailedNextSnapshot_RestartReplaysReceiptWithoutRescoring()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var planItemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(planItemId))!;
        fixture.Activate(NativeSliceFixture.UserA);
        using (var quiz = NewPresenter(fixture, NewAdapter(fixture)))
        {
            var ready = await quiz.OpenAsync(launch);
            Assert.Equal(NativePhase.Ready, ready.Phase);
            Assert.Equal(NativePhase.Feedback,
                (await quiz.AnswerAsync(ready.Choices[0].Text)).Phase);
            var original = (await fixture.RowsAsync(
                NativeSliceFixture.UserA, planItemId)).Session!.StateJson;

            fixture.FaultSessions.FailNextSnapshot = true;
            Assert.Equal(NativePhase.Error, (await quiz.NextAsync()).Phase);
            Assert.Equal(original, (await fixture.RowsAsync(
                NativeSliceFixture.UserA, planItemId)).Session!.StateJson);
            Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
            Assert.Single(await ReceiptsAsync(fixture, NativeSliceFixture.UserA));
            Assert.False((await fixture.RowsAsync(
                NativeSliceFixture.UserA, planItemId)).Item.IsCompleted);
        }

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using (var resumed = NewPresenter(fixture, NewAdapter(fixture)))
        {
            Assert.Equal(NativePhase.ResumeChoice,
                (await resumed.OpenAsync(launch)).Phase);
            var next = await resumed.ResumeAsync();
            Assert.Equal(NativePhase.Ready, next.Phase);
            Assert.Equal(2, next.CurrentTurn);
        }
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Single(await ReceiptsAsync(fixture, NativeSliceFixture.UserA));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var again = NewPresenter(fixture, NewAdapter(fixture));
        Assert.Equal(NativePhase.ResumeChoice, (await again.OpenAsync(launch)).Phase);
        Assert.Equal(2, (await again.ResumeAsync()).CurrentTurn);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.False((await fixture.RowsAsync(
            NativeSliceFixture.UserA, planItemId)).Item.IsCompleted);
    }

    [Fact]
    public async Task ArchivedAndReintroducedQuizCard_ReplaysOriginalReceiptAfterRestart()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        var adapter = NewAdapter(fixture);
        var pending = await PreparePendingAsync(
            fixture, NativeSliceFixture.UserA, adapter);
        var originalCardId = (await fixture.RowsAsync(
            NativeSliceFixture.UserA, pending.PlanItemId)).Item.Id;
        string originalPlanId;
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            originalPlanId = await db.DailyPlans.AsNoTracking()
                .Where(plan => plan.UserProfileId == NativeSliceFixture.UserA)
                .Select(plan => plan.Id).SingleAsync();
        }
        var committed = await adapter.CommitAsync(
            NativeSliceFixture.UserA, pending.State.LaunchContextKey,
            pending.State.TurnId, pending.State.PendingAttempt!,
            CancellationToken.None);

        fixture.SetPlanFocus(NativeSliceFixture.UserA,
            fixture.WordId(NativeSliceFixture.UserA, 1));
        for (var generation = 0; generation < 2; generation++)
        {
            fixture.SetPlanActivities(NativeSliceFixture.UserA,
                ("Reading", fixture.ResourceId(NativeSliceFixture.UserA), null, 15, 1));
            using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
                await lease.Service.GenerateTodayAsync(new() { Strategy = "deterministic" });
            fixture.SetPlanActivities(NativeSliceFixture.UserA,
                ("VocabularyReview", null, null, 10, 1));
            Assert.Equal(pending.PlanItemId,
                await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA));
        }

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var dateKey = pending.State.PlanDate.ToDateTime(
                TimeOnly.MinValue, DateTimeKind.Utc);
            var cards = await db.DailyPlanCompletions.AsNoTracking()
                .Where(card => card.UserProfileId == NativeSliceFixture.UserA
                    && card.Date == dateKey
                    && card.PlanItemId == pending.PlanItemId)
                .ToListAsync();
            Assert.Equal(3, cards.Count);
            Assert.Equal(2, cards.Count(card => card.IsArchived));
            Assert.True(cards.Single(card => card.Id == originalCardId).IsArchived);
            Assert.NotEqual(originalCardId, Assert.Single(
                cards, card => !card.IsArchived).Id);
            var plan = await db.DailyPlans.AsNoTracking().SingleAsync(row =>
                row.UserProfileId == NativeSliceFixture.UserA && row.Date == dateKey);
            Assert.Equal(originalPlanId, plan.Id);
            Assert.Equal(fixture.WordId(NativeSliceFixture.UserA, 1),
                Assert.Single(VocabQuizLaunchValidator.GetPlanFocusVocabularyIds(
                    plan.FocusVocabularyFacts, plan.NarrativeFacts)));
            Assert.NotEqual(1, pending.State.Snapshot.FocusVocabularyIds.Count);
        }

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        adapter = NewAdapter(fixture);
        var found = await adapter.FindAsync(
            NativeSliceFixture.UserA, pending.State.LaunchContextKey,
            pending.State.TurnId, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(committed.Progress.Id, found.Progress.Id);
        var replay = await adapter.CommitAsync(
            NativeSliceFixture.UserA, pending.State.LaunchContextKey,
            pending.State.TurnId, pending.State.PendingAttempt!,
            CancellationToken.None);
        Assert.Equal(committed.Progress.Id, replay.Progress.Id);
        Assert.Equal(committed.Progress.TotalAttempts, replay.Progress.TotalAttempts);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Single(await ReceiptsAsync(fixture, NativeSliceFixture.UserA));

        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Null(await adapter.FindAsync(
            NativeSliceFixture.UserB, pending.State.LaunchContextKey,
            pending.State.TurnId, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.FindAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.CommitAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                pending.State.PendingAttempt!, CancellationToken.None));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task ResetAndRebuiltPlan_ReplaysReceiptUntilHistoricalCardIsMissing()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var adapter = NewAdapter(fixture);
        var pending = await PreparePendingAsync(
            fixture, NativeSliceFixture.UserA, adapter);
        var originalCardId = (await fixture.RowsAsync(
            NativeSliceFixture.UserA, pending.PlanItemId)).Item.Id;
        string originalPlanId;
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            originalPlanId = await db.DailyPlans.AsNoTracking()
                .Where(plan => plan.UserProfileId == NativeSliceFixture.UserA)
                .Select(plan => plan.Id).SingleAsync();
        }
        var committed = await adapter.CommitAsync(
            NativeSliceFixture.UserA, pending.State.LaunchContextKey,
            pending.State.TurnId, pending.State.PendingAttempt!,
            CancellationToken.None);

        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
            await lease.Service.ResetTodayAsync();
        fixture.SetPlanFocus(NativeSliceFixture.UserA,
            fixture.WordId(NativeSliceFixture.UserA, 1));
        Assert.Equal(pending.PlanItemId,
            await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA));
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var dateKey = pending.State.PlanDate.ToDateTime(
                TimeOnly.MinValue, DateTimeKind.Utc);
            var cards = await db.DailyPlanCompletions.AsNoTracking()
                .Where(card => card.UserProfileId == NativeSliceFixture.UserA
                    && card.Date == dateKey
                    && card.PlanItemId == pending.PlanItemId)
                .ToListAsync();
            Assert.True(cards.Single(card => card.Id == originalCardId).IsArchived);
            Assert.NotEqual(originalCardId, Assert.Single(
                cards, card => !card.IsArchived).Id);
            Assert.NotEqual(originalPlanId, await db.DailyPlans.AsNoTracking()
                .Where(plan => plan.UserProfileId == NativeSliceFixture.UserA
                    && plan.Date == dateKey)
                .Select(plan => plan.Id).SingleAsync());
        }

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        adapter = NewAdapter(fixture);
        var found = await adapter.FindAsync(
            NativeSliceFixture.UserA, pending.State.LaunchContextKey,
            pending.State.TurnId, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(committed.Progress.Id, found.Progress.Id);
        var replay = await adapter.CommitAsync(
            NativeSliceFixture.UserA, pending.State.LaunchContextKey,
            pending.State.TurnId, pending.State.PendingAttempt!,
            CancellationToken.None);
        Assert.Equal(committed.Progress.Id, replay.Progress.Id);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Single(await ReceiptsAsync(fixture, NativeSliceFixture.UserA));

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var historical = await db.DailyPlanCompletions.SingleAsync(
                card => card.Id == originalCardId);
            db.DailyPlanCompletions.Remove(historical);
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.FindAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.CommitAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                pending.State.PendingAttempt!, CancellationToken.None));
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task FreshCommit_RejectsArchivedCardAndChangedActiveReplacement()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var adapter = NewAdapter(fixture);
        var pending = await PreparePendingAsync(
            fixture, NativeSliceFixture.UserA, adapter);
        var originalCardId = (await fixture.RowsAsync(
            NativeSliceFixture.UserA, pending.PlanItemId)).Item.Id;

        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            ("Reading", fixture.ResourceId(NativeSliceFixture.UserA), null, 15, 1));
        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
            await lease.Service.GenerateTodayAsync(new() { Strategy = "deterministic" });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.CommitAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                pending.State.PendingAttempt!, CancellationToken.None));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Empty(await ReceiptsAsync(fixture, NativeSliceFixture.UserA));

        fixture.SetPlanFocus(NativeSliceFixture.UserA,
            fixture.WordId(NativeSliceFixture.UserA, 1));
        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            ("VocabularyReview", null, null, 10, 1));
        Assert.Equal(pending.PlanItemId,
            await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA));
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var dateKey = pending.State.PlanDate.ToDateTime(
                TimeOnly.MinValue, DateTimeKind.Utc);
            var cards = await db.DailyPlanCompletions.AsNoTracking()
                .Where(card => card.UserProfileId == NativeSliceFixture.UserA
                    && card.Date == dateKey
                    && card.PlanItemId == pending.PlanItemId)
                .ToListAsync();
            Assert.True(cards.Single(card => card.Id == originalCardId).IsArchived);
            Assert.NotEqual(originalCardId, Assert.Single(
                cards, card => !card.IsArchived).Id);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.CommitAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                pending.State.PendingAttempt!, CancellationToken.None));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Empty(await ReceiptsAsync(fixture, NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task SummaryAndDone_KeepOlderTurnReceiptReplayableWithoutNewScoring()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        var planItemId = await fixture.GenerateQuizPlanAsync(NativeSliceFixture.UserA);
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(planItemId))!;
        var adapter = NewAdapter(fixture);
        using var quiz = NewPresenter(fixture, adapter);
        var question = await quiz.OpenAsync(launch);
        NativeQuizStoredState? finalPending = null;
        for (var turn = 0; turn < 2; turn++)
        {
            Assert.Equal(NativePhase.Ready, question.Phase);
            Assert.Equal(NativePhase.Feedback,
                (await quiz.AnswerAsync(question.Choices[0].Text)).Phase);
            finalPending = NativeQuizStoredState.Deserialize(
                (await fixture.RowsAsync(NativeSliceFixture.UserA, planItemId))
                    .Session!.StateJson);
            question = await quiz.NextAsync();
        }

        Assert.Equal(NativePhase.Summary, question.Phase);
        var summary = NativeQuizStoredState.Deserialize(
            (await fixture.RowsAsync(NativeSliceFixture.UserA, planItemId))
                .Session!.StateJson);
        Assert.True(summary.SummaryVisible);
        Assert.Equal(finalPending!.TurnId, summary.TurnId);
        Assert.Null(summary.PendingAttempt);
        Assert.False((await fixture.RowsAsync(
            NativeSliceFixture.UserA, planItemId)).Item.IsCompleted);
        var firstReceipt = await adapter.FindAsync(
            NativeSliceFixture.UserA, finalPending.LaunchContextKey,
            finalPending.TurnId, CancellationToken.None);
        Assert.NotNull(firstReceipt);

        Assert.Equal(NativeDestination.TodaysPlan,
            (await quiz.DoneAsync()).Destination);
        Assert.True((await fixture.RowsAsync(
            NativeSliceFixture.UserA, planItemId)).Item.IsCompleted);
        var replay = await adapter.CommitAsync(
            NativeSliceFixture.UserA, finalPending.LaunchContextKey,
            finalPending.TurnId, finalPending.PendingAttempt!,
            CancellationToken.None);
        Assert.Equal(firstReceipt.Progress.Id, replay.Progress.Id);
        Assert.Equal(2, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(2, (await ReceiptsAsync(
            fixture, NativeSliceFixture.UserA)).Count);
    }

    [Fact]
    public async Task TwoOwners_DurableReceiptAndExactReplayStayIsolatedAcrossRestart()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        var adapter = NewAdapter(fixture);
        var a = await PreparePendingAsync(fixture, NativeSliceFixture.UserA, adapter);
        var receiptA = await adapter.CommitAsync(
            NativeSliceFixture.UserA, a.State.LaunchContextKey, a.State.TurnId,
            a.State.PendingAttempt!, CancellationToken.None);
        Assert.Equal(NativeSliceFixture.UserA, receiptA.Progress.UserId);
        Assert.Equal(a.State.PendingAttempt!.VocabularyWordId,
            receiptA.Progress.VocabularyWordId);
        Assert.Equal(a.SourceJson, (await fixture.RowsAsync(
            NativeSliceFixture.UserA, a.PlanItemId)).Session!.StateJson);
        Assert.False((await fixture.RowsAsync(
            NativeSliceFixture.UserA, a.PlanItemId)).Item.IsCompleted);
        Assert.Single(await ReceiptsAsync(fixture, NativeSliceFixture.UserA));
        Assert.Equal(0, await fixture.ReceiptCountAsync(NativeSliceFixture.UserA));

        var b = await PreparePendingAsync(fixture, NativeSliceFixture.UserB, adapter);
        Assert.Null(await adapter.FindAsync(
            NativeSliceFixture.UserB, a.State.LaunchContextKey,
            a.State.TurnId, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.FindAsync(NativeSliceFixture.UserA,
                a.State.LaunchContextKey, a.State.TurnId, CancellationToken.None));
        var receiptB = await adapter.CommitAsync(
            NativeSliceFixture.UserB, b.State.LaunchContextKey, b.State.TurnId,
            b.State.PendingAttempt!, CancellationToken.None);
        Assert.NotEqual(receiptA.Progress.Id, receiptB.Progress.Id);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserB));
        Assert.Single(await ReceiptsAsync(fixture, NativeSliceFixture.UserB));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        adapter = NewAdapter(fixture);
        var found = await adapter.FindAsync(
            NativeSliceFixture.UserA, a.State.LaunchContextKey,
            a.State.TurnId, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(receiptA.Progress.Id, found.Progress.Id);
        Assert.Equal(receiptA.Progress.MasteryScore, found.Progress.MasteryScore);
        var replay = await adapter.CommitAsync(
            NativeSliceFixture.UserA, a.State.LaunchContextKey, a.State.TurnId,
            a.State.PendingAttempt!, CancellationToken.None);
        Assert.Equal(receiptA.Progress.Id, replay.Progress.Id);
        Assert.Equal(receiptA.Progress.TotalAttempts, replay.Progress.TotalAttempts);
        Assert.Equal(1, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, receiptA.Progress.VocabularyWordId));
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Single(await ReceiptsAsync(fixture, NativeSliceFixture.UserA));

        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Equal(receiptB.Progress.Id, (await adapter.FindAsync(
            NativeSliceFixture.UserB, b.State.LaunchContextKey,
            b.State.TurnId, CancellationToken.None))!.Progress.Id);
    }

    [Fact]
    public async Task WrongOwnerContextWordOrChangedDuplicate_NeverCreditsAnotherAttempt()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        var adapter = NewAdapter(fixture);
        var pending = await PreparePendingAsync(
            fixture, NativeSliceFixture.UserA, adapter);
        var original = pending.State.PendingAttempt!;

        fixture.Activate(NativeSliceFixture.UserB);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.CommitAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                original, CancellationToken.None));
        fixture.LoseOwner();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.FindAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.CommitAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                original, CancellationToken.None));

        fixture.Activate(NativeSliceFixture.UserA);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.CommitAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey + "-wrong", pending.State.TurnId,
                original, CancellationToken.None));
        var foreignWord = Clone(original);
        foreignWord.VocabularyWordId = fixture.WordId(NativeSliceFixture.UserB, 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.CommitAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                foreignWord, CancellationToken.None));
        var foreignOwner = Clone(original);
        foreignOwner.UserId = NativeSliceFixture.UserB;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.CommitAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                foreignOwner, CancellationToken.None));
        Assert.Empty(await ReceiptsAsync(fixture, NativeSliceFixture.UserA));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserB));

        await adapter.CommitAsync(NativeSliceFixture.UserA,
            pending.State.LaunchContextKey, pending.State.TurnId,
            original, CancellationToken.None);
        var changed = Clone(original);
        changed.UserInput = $"{original.UserInput}-changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.CommitAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                changed, CancellationToken.None));
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Single(await ReceiptsAsync(fixture, NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task AlteredPendingSnapshotOrReleasedReceiptIndex_FailsClosedOnReplay()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var adapter = NewAdapter(fixture);
        var pending = await PreparePendingAsync(
            fixture, NativeSliceFixture.UserA, adapter);
        await adapter.CommitAsync(
            NativeSliceFixture.UserA, pending.State.LaunchContextKey,
            pending.State.TurnId, pending.State.PendingAttempt!,
            CancellationToken.None);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var session = await db.ActivitySessions.SingleAsync(row =>
                row.UserId == NativeSliceFixture.UserA
                && row.ActivityType == "VocabularyReview"
                && row.LaunchContextKey == pending.State.LaunchContextKey);
            var tampered = Clone(pending.State.PendingAttempt!);
            tampered.UserInput = "changed-after-commit";
            session.StateJson = (pending.State with { PendingAttempt = tampered }).Serialize();
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.FindAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                CancellationToken.None));

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var session = await db.ActivitySessions.SingleAsync(row =>
                row.UserId == NativeSliceFixture.UserA
                && row.ActivityType == "VocabularyReview"
                && row.LaunchContextKey == pending.State.LaunchContextKey);
            session.StateJson = pending.SourceJson;
            var receipt = await db.ActivitySessions.SingleAsync(row =>
                row.UserId == NativeSliceFixture.UserA
                && row.ActivityType == NativeQuizAttemptCommitterAdapter.ReceiptActivityType);
            receipt.Status = ActivitySessionStatus.Abandoned;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.FindAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.CommitAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                pending.State.PendingAttempt!, CancellationToken.None));
        Assert.Equal(1, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, pending.State.PendingAttempt!.VocabularyWordId));
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task ParallelDuplicateCommits_CreateOneContextOneProgressAndOneReceipt()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var adapter = NewAdapter(fixture);
        var pending = await PreparePendingAsync(
            fixture, NativeSliceFixture.UserA, adapter);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return await adapter.CommitAsync(
                NativeSliceFixture.UserA, pending.State.LaunchContextKey,
                pending.State.TurnId, pending.State.PendingAttempt!,
                CancellationToken.None);
        })).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(commits);
        Assert.All(results, receipt =>
        {
            Assert.Equal(results[0].Progress.Id, receipt.Progress.Id);
            Assert.Equal(results[0].Progress.TotalAttempts,
                receipt.Progress.TotalAttempts);
            Assert.Equal(1, receipt.Progress.TotalAttempts);
        });
        Assert.Equal(1, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, pending.State.PendingAttempt!.VocabularyWordId));
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        var receiptRow = Assert.Single(
            await ReceiptsAsync(fixture, NativeSliceFixture.UserA));
        Assert.Equal(ActivitySessionStatus.InProgress, receiptRow.Status);
        Assert.Equal(pending.SourceJson, (await fixture.RowsAsync(
            NativeSliceFixture.UserA, pending.PlanItemId)).Session!.StateJson);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ActivitySessions.Add(new ActivitySession
            {
                UserId = receiptRow.UserId,
                ActivityType = receiptRow.ActivityType,
                LaunchContextKey = receiptRow.LaunchContextKey,
                StateJson = receiptRow.StateJson,
                Status = ActivitySessionStatus.InProgress,
                StartedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await Assert.ThrowsAsync<DbUpdateException>(
                () => db.SaveChangesAsync());
        }
        Assert.Single(await ReceiptsAsync(fixture, NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task ReceiptInsertFault_RollsBackEarlierProgressAndContextThenRetrySucceeds()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var adapter = NewAdapter(fixture);
        var pending = await PreparePendingAsync(
            fixture, NativeSliceFixture.UserA, adapter);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlRawAsync($"""
                CREATE TRIGGER RejectNativeAttemptReceipt
                BEFORE INSERT ON "ActivitySession"
                WHEN NEW."ActivityType" = '{NativeQuizAttemptCommitterAdapter.ReceiptActivityType}'
                BEGIN SELECT RAISE(ABORT, 'injected receipt failure'); END;
                """);
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => adapter.CommitAsync(
            NativeSliceFixture.UserA, pending.State.LaunchContextKey,
            pending.State.TurnId, pending.State.PendingAttempt!,
            CancellationToken.None));
        Assert.Equal(0, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, pending.State.PendingAttempt!.VocabularyWordId));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Empty(await ReceiptsAsync(fixture, NativeSliceFixture.UserA));
        Assert.Equal(pending.SourceJson, (await fixture.RowsAsync(
            NativeSliceFixture.UserA, pending.PlanItemId)).Session!.StateJson);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER RejectNativeAttemptReceipt;");
        }
        var committed = await adapter.CommitAsync(
            NativeSliceFixture.UserA, pending.State.LaunchContextKey,
            pending.State.TurnId, pending.State.PendingAttempt!,
            CancellationToken.None);
        Assert.Equal(1, committed.Progress.TotalAttempts);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Single(await ReceiptsAsync(fixture, NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task PlanCompletedOrAssignedFocusChanged_RejectsBeforeScoring()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var adapter = NewAdapter(fixture);
        var pending = await PreparePendingAsync(
            fixture, NativeSliceFixture.UserA, adapter);
        string? originalFocus;
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var card = await db.DailyPlanCompletions.SingleAsync(row =>
                row.UserProfileId == NativeSliceFixture.UserA
                && row.PlanItemId == pending.PlanItemId);
            card.IsCompleted = true;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.CommitAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                pending.State.PendingAttempt!, CancellationToken.None));
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var card = await db.DailyPlanCompletions.SingleAsync(row =>
                row.UserProfileId == NativeSliceFixture.UserA
                && row.PlanItemId == pending.PlanItemId);
            card.IsCompleted = false;
            var plan = await db.DailyPlans.SingleAsync(row =>
                row.UserProfileId == NativeSliceFixture.UserA);
            originalFocus = plan.FocusVocabularyFacts;
            plan.FocusVocabularyFacts = JsonSerializer.Serialize(
                new { vocabularyIds = new[] { "not-assigned" } });
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.CommitAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                pending.State.PendingAttempt!, CancellationToken.None));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Empty(await ReceiptsAsync(fixture, NativeSliceFixture.UserA));

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var plan = await db.DailyPlans.SingleAsync(row =>
                row.UserProfileId == NativeSliceFixture.UserA);
            plan.FocusVocabularyFacts = originalFocus;
            await db.SaveChangesAsync();
        }
        var result = await adapter.CommitAsync(
            NativeSliceFixture.UserA, pending.State.LaunchContextKey,
            pending.State.TurnId, pending.State.PendingAttempt!,
            CancellationToken.None);
        Assert.Equal(1, result.Progress.TotalAttempts);
    }

    [Fact]
    public async Task LegacyNarrativeFocusFallback_MatchesTheAssignedQuizCard()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var adapter = NewAdapter(fixture);
        var pending = await PreparePendingAsync(
            fixture, NativeSliceFixture.UserA, adapter);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var plan = await db.DailyPlans.SingleAsync(row =>
                row.UserProfileId == NativeSliceFixture.UserA);
            plan.FocusVocabularyFacts = null;
            plan.NarrativeFacts = JsonSerializer.Serialize(new
            {
                vocabInsight = new
                {
                    previewWords = pending.State.Snapshot.FocusVocabularyIds
                        .Select(id => new { wordId = id }).ToArray()
                }
            });
            await db.SaveChangesAsync();
        }

        var committed = await adapter.CommitAsync(
            NativeSliceFixture.UserA, pending.State.LaunchContextKey,
            pending.State.TurnId, pending.State.PendingAttempt!,
            CancellationToken.None);
        Assert.Equal(1, committed.Progress.TotalAttempts);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.False((await fixture.RowsAsync(
            NativeSliceFixture.UserA, pending.PlanItemId)).Item.IsCompleted);
    }

    [Fact]
    public async Task PhraseScoringAndMissingPersistedContext_FailClosedWithoutDoubleCounting()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        var adapter = NewAdapter(fixture);
        var pending = await PreparePendingAsync(
            fixture, NativeSliceFixture.UserA, adapter);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var word = await db.VocabularyWords.SingleAsync(row =>
                row.Id == pending.State.PendingAttempt!.VocabularyWordId);
            word.LexicalUnitType = LexicalUnitType.Phrase;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            adapter.CommitAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                pending.State.PendingAttempt!, CancellationToken.None));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Empty(await ReceiptsAsync(fixture, NativeSliceFixture.UserA));

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var word = await db.VocabularyWords.SingleAsync(row =>
                row.Id == pending.State.PendingAttempt!.VocabularyWordId);
            word.LexicalUnitType = LexicalUnitType.Word;
            await db.SaveChangesAsync();
        }
        var receipt = await adapter.CommitAsync(
            NativeSliceFixture.UserA, pending.State.LaunchContextKey,
            pending.State.TurnId, pending.State.PendingAttempt!,
            CancellationToken.None);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var context = await db.VocabularyLearningContexts.SingleAsync(row =>
                row.VocabularyProgressId == receipt.Progress.Id);
            db.VocabularyLearningContexts.Remove(context);
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.FindAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.CommitAsync(NativeSliceFixture.UserA,
                pending.State.LaunchContextKey, pending.State.TurnId,
                pending.State.PendingAttempt!, CancellationToken.None));
        Assert.Equal(1, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, receipt.Progress.VocabularyWordId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScoringMatchesSourceServiceIncludingLegacyAndQuizCounters(
        bool previouslyKnown)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, known: previouslyKnown);
        await fixture.SeedAsync(NativeSliceFixture.UserB, known: previouslyKnown);
        var adapter = NewAdapter(fixture);
        var pending = await PreparePendingAsync(
            fixture, NativeSliceFixture.UserA, adapter);
        var native = await adapter.CommitAsync(
            NativeSliceFixture.UserA, pending.State.LaunchContextKey,
            pending.State.TurnId, pending.State.PendingAttempt!,
            CancellationToken.None);

        fixture.Activate(NativeSliceFixture.UserB);
        var sourceAttempt = Clone(pending.State.PendingAttempt!);
        sourceAttempt.UserId = NativeSliceFixture.UserB;
        sourceAttempt.VocabularyWordId = fixture.WordId(NativeSliceFixture.UserB, 0);
        using var scope = fixture.Scope();
        var service = scope.ServiceProvider.GetRequiredService<IVocabularyProgressService>();
        var source = await service.RecordAttemptAsync(sourceAttempt);
        Assert.Equal(source.TotalAttempts, native.Progress.TotalAttempts);
        Assert.Equal(source.CorrectAttempts, native.Progress.CorrectAttempts);
        Assert.Equal(source.CurrentStreak, native.Progress.CurrentStreak);
        Assert.Equal(source.MasteryScore, native.Progress.MasteryScore);
        Assert.Equal(source.ProductionInStreak, native.Progress.ProductionInStreak);
        Assert.Equal(source.ReviewInterval, native.Progress.ReviewInterval);
        Assert.Equal(source.EaseFactor, native.Progress.EaseFactor);
        Assert.Equal(source.QuizRecognitionDemonstrations,
            native.Progress.QuizRecognitionDemonstrations);
        Assert.Equal(source.QuizProductionDemonstrations,
            native.Progress.QuizProductionDemonstrations);
#pragma warning disable CS0618
        Assert.Equal(source.RecognitionAttempts, native.Progress.RecognitionAttempts);
        Assert.Equal(source.RecognitionCorrect, native.Progress.RecognitionCorrect);
        Assert.Equal(source.ProductionAttempts, native.Progress.ProductionAttempts);
        Assert.Equal(source.ProductionCorrect, native.Progress.ProductionCorrect);
        Assert.Equal(source.MultipleChoiceCorrect, native.Progress.MultipleChoiceCorrect);
        Assert.Equal(source.TextEntryCorrect, native.Progress.TextEntryCorrect);
        Assert.Equal(source.IsPromoted, native.Progress.IsPromoted);
        Assert.Equal(source.IsCompleted, native.Progress.IsCompleted);
#pragma warning restore CS0618

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var nativeContext = await db.VocabularyLearningContexts.AsNoTracking()
            .SingleAsync(row => row.VocabularyProgressId == native.Progress.Id);
        var sourceContext = await db.VocabularyLearningContexts.AsNoTracking()
            .SingleAsync(row => row.VocabularyProgressId == source.Id);
        Assert.Equal(sourceContext.WasCorrect, nativeContext.WasCorrect);
        Assert.Equal(sourceContext.InputMode, nativeContext.InputMode);
        Assert.Equal(sourceContext.DifficultyScore, nativeContext.DifficultyScore);
        Assert.Equal(sourceContext.ResponseTimeMs, nativeContext.ResponseTimeMs);
        Assert.Equal(sourceContext.UserInput, nativeContext.UserInput);
        Assert.Equal(sourceContext.ExpectedAnswer, nativeContext.ExpectedAnswer);
        Assert.Equal(sourceContext.CorrectAnswersInContext,
            nativeContext.CorrectAnswersInContext);
    }

    private static NativeQuizAttemptCommitterAdapter NewAdapter(
        NativeSliceFixture fixture)
    {
        using var scope = fixture.Scope();
        var services = scope.ServiceProvider;
        return new NativeQuizAttemptCommitterAdapter(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IUserScopeProvider>(),
            services.GetRequiredService<IPreferencesService>());
    }

    private static NativeQuizPresenter NewPresenter(
        NativeSliceFixture fixture, INativeQuizAttemptCommitter committer)
    {
        using var scope = fixture.Scope();
        var services = scope.ServiceProvider;
        return new NativeQuizPresenter(
            services.GetRequiredService<NativePlanLeaseFactory>(),
            services.GetRequiredService<DeviceUserScopeProvider>(),
            services.GetRequiredService<IPreferencesService>(),
            services.GetRequiredService<UserProfileRepository>(),
            services.GetRequiredService<LearningResourceRepository>(),
            services.GetRequiredService<VocabularyProgressRepository>(),
            services.GetRequiredService<VocabQuizLaunchValidator>(),
            services.GetRequiredService<IProgressService>(),
            services.GetRequiredService<IActivitySessionService>(),
            services.GetRequiredService<VocabularyQuizPreferences>(),
            services.GetRequiredService<INativeQuizChoices>(),
            services.GetRequiredService<INativeQuizEligibility>(),
            services.GetRequiredService<INativeQuizGrader>(),
            services.GetRequiredService<INativeQuizMedia>(),
            committer,
            services.GetRequiredService<INativeSyncReadiness>(),
            services.GetRequiredService<NativePendingWrites>(),
            services.GetRequiredService<ILogger<NativeQuizPresenter>>(),
            services.GetRequiredService<ILogger<ActivityTimerService>>());
    }

    private static async Task<PendingTurn> PreparePendingAsync(
        NativeSliceFixture fixture, string userId,
        NativeQuizAttemptCommitterAdapter adapter)
    {
        var planItemId = await fixture.GenerateQuizPlanAsync(userId);
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(planItemId))!;
        using var quiz = NewPresenter(fixture, adapter);
        var ready = await quiz.OpenAsync(launch);
        Assert.Equal(NativePhase.Ready, ready.Phase);
        var answer = ready.Mode == "Text"
            ? "definitely-not-the-word" : ready.Choices[0].Text;
        var feedback = await quiz.AnswerAsync(answer);
        Assert.Equal(NativePhase.Feedback, feedback.Phase);
        if (feedback.RequiresCorrection)
        {
            var corrected = await quiz.SubmitCorrectionAsync(
                feedback.CorrectAnswerAfterGrading!);
            Assert.Equal(NativePhase.Feedback, corrected.Phase);
            Assert.False(corrected.RequiresCorrection);
        }
        var source = (await fixture.RowsAsync(userId, planItemId)).Session!;
        var state = NativeQuizStoredState.Deserialize(source.StateJson);
        Assert.NotNull(state.PendingAttempt);
        Assert.Equal(ready.Prompt!.WordId, state.PendingAttempt.VocabularyWordId);
        return new PendingTurn(planItemId, launch, state, source.StateJson);
    }

    private static async Task<List<ActivitySession>> ReceiptsAsync(
        NativeSliceFixture fixture, string userId)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ActivitySessions.AsNoTracking()
            .Where(row => row.UserId == userId
                && row.ActivityType == NativeQuizAttemptCommitterAdapter.ReceiptActivityType)
            .ToListAsync();
    }

    private static VocabularyAttempt Clone(VocabularyAttempt attempt) =>
        JsonSerializer.Deserialize<VocabularyAttempt>(
            JsonSerializer.Serialize(attempt))!;

    private sealed record PendingTurn(
        string PlanItemId, NativeQuizLaunch Launch,
        NativeQuizStoredState State, string SourceJson);
}
