using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Native;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native.Tests;

internal sealed class ScratchAmbientSync : ISyncService
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);
    public bool IsInitialSyncInProgress => false;
    public event Action? InitialSyncCompleted { add { } remove { } }
    public void BeginInitialSync() { }
    public Task InitializeDatabaseAsync() => Task.CompletedTask;
    public Task TriggerSyncAsync()
    {
        Interlocked.Increment(ref _calls);
        return Task.CompletedTask;
    }
}

internal sealed class ScratchSyncReadiness : INativeSyncReadiness
{
    public NativeSyncOutcome Outcome { get; set; } = NativeSyncOutcome.Confirmed;
    public Action<string>? DuringSynchronization { get; set; }
    public List<string> RequestedOwners { get; } = [];

    public Task<NativeSyncOutcome> SynchronizeAsync(
        string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestedOwners.Add(userId);
        DuringSynchronization?.Invoke(userId);
        return Task.FromResult(Outcome);
    }
}

internal sealed class ScratchRemoteSyncCompletion : IRemoteSyncCompletion
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _calls;

    public Func<CancellationToken, Task> RunAsync { get; set; } =
        _ => Task.CompletedTask;

    public int Calls => Volatile.Read(ref _calls);

    public async Task SynchronizeForReadinessAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Increment(ref _calls);
            await RunAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal sealed class ScratchQuizMedia : INativeQuizMedia
{
    private readonly TaskCompletionSource<bool> _playbackStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool>? _pendingPlayback;

    public bool AudioAvailable { get; set; }
    public bool PhotoAvailable { get; set; }
    public bool PlaySucceeds { get; set; } = true;
    public bool HoldPlaybackCompletion { get; set; }
    public bool AudioAvailabilityOffline { get; set; }
    public bool PlaybackOffline { get; set; }
    public bool PhotoAvailabilityOffline { get; set; }
    public List<(string Text, string Language)> Played { get; } = [];
    public int Stopped { get; private set; }
    public Task PlaybackStarted => _playbackStarted.Task;
    public TaskCompletionSource<bool>? PendingPlayback =>
        Volatile.Read(ref _pendingPlayback);
    public bool FinishPlayback(bool completed) =>
        PendingPlayback?.TrySetResult(completed) ?? false;

    public Task<bool> CanPlayPromptAsync(
        string text, string language, CancellationToken cancellationToken) =>
        AudioAvailabilityOffline
            ? Task.FromException<bool>(new HttpRequestException("Scratch audio offline."))
            : Task.FromResult(AudioAvailable);

    public Task<bool> PlayPromptAsync(
        string text, string language, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (PlaybackOffline)
            throw new HttpRequestException("Scratch playback offline.");
        Played.Add((text, language));
        if (HoldPlaybackCompletion && PlaySucceeds)
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _pendingPlayback, completion);
            _playbackStarted.TrySetResult(true);
            return completion.Task.WaitAsync(cancellationToken);
        }
        return Task.FromResult(PlaySucceeds);
    }

    public Task<bool> CanShowPhotoAsync(string photoUri, CancellationToken cancellationToken) =>
        PhotoAvailabilityOffline
            ? Task.FromException<bool>(new HttpRequestException("Scratch photo offline."))
            : Task.FromResult(PhotoAvailable);

    public Task<NativeQuizPhotoAsset?> LoadPhotoAsync(
        string photoUri, CancellationToken cancellationToken) =>
        PhotoAvailabilityOffline
            ? Task.FromException<NativeQuizPhotoAsset?>(
                new HttpRequestException("Scratch photo offline."))
            : Task.FromResult(PhotoAvailable
                ? new NativeQuizPhotoAsset([137, 80, 78, 71], "image/png")
                : null);

    public void StopPlayback()
    {
        Stopped++;
        PendingPlayback?.TrySetResult(false);
    }
}

/// <summary>
/// Single-threaded fixture adapter over the real SQLite PlanService.
/// This is not a production compare-and-create; concurrent native generation
/// remains disabled until Shared supplies a transactional owner/date API.
/// </summary>
internal sealed class ScratchPlanCreation(NativePlanLeaseFactory leases) : INativePlanCreation
{
    public async Task<NativePlanCreateResult> CreateIfAbsentAsync(
        string userId, DateOnly localDate, CancellationToken cancellationToken)
    {
        using var lease = leases.ForOwner(userId);
        if (lease.LocalDate != localDate)
            throw new InvalidOperationException("Scratch plan date changed.");
        var existing = await lease.Service.GetTodayAsync(cancellationToken);
        if (existing is not null)
            return new NativePlanCreateResult(existing, Created: false);
        var generated = await lease.Service.GenerateTodayAsync(
            new GenerateTodaysPlanRequest { Strategy = "deterministic" },
            cancellationToken);
        return new NativePlanCreateResult(generated, Created: true);
    }
}

internal sealed class ScratchChoices : INativeQuizChoices
{
    public Task<ImmutableArray<NativeQuizChoice>> CreateAsync(
        string userId, VocabularyWord question, IReadOnlyList<VocabularyWord> reachableWords,
        bool promptUsesNativeLanguage, CancellationToken cancellationToken)
    {
        var correct = promptUsesNativeLanguage
            ? question.TargetLanguageTerm : question.NativeLanguageTerm;
        var distractors = reachableWords
            .Where(word => word.Id != question.Id)
            .Select(word => promptUsesNativeLanguage
                ? word.TargetLanguageTerm : word.NativeLanguageTerm)
            .Where(term => !string.IsNullOrWhiteSpace(term)
                && !string.Equals(term, correct, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(term => new NativeQuizChoice(term!));
        return Task.FromResult(Enumerable.Repeat(new NativeQuizChoice(correct!), 1)
            .Concat(distractors).ToImmutableArray());
    }
}

/// <summary>Test-only source-shaped selection until Shared owns this policy.</summary>
internal sealed class ScratchQuizEligibility : INativeQuizEligibility
{
    public Task<NativeQuizEligibilityResult> SelectAsync(
        string userId, bool dueOnly,
        IReadOnlyList<VocabularyQuizItem> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count > 0 && candidates.All(item => item.IsKnown))
            return Task.FromResult(new NativeQuizEligibilityResult([], AllMastered: true));
        var eligible = candidates.Where(item =>
            !item.IsKnown && !(item.Progress?.IsInGracePeriod ?? false));
        if (dueOnly)
            eligible = eligible.Where(item =>
                item.Progress?.TotalAttempts == 0
                || item.Progress?.IsDueForReview == true);
        return Task.FromResult(new NativeQuizEligibilityResult(
            eligible.OrderBy(item => item.Progress?.MasteryScore ?? 0f)
                .Take(20).ToList(), AllMastered: false));
    }
}

/// <summary>Test-only Razor grading substitute; production has no default grade.</summary>
internal sealed class ScratchQuizGrader : INativeQuizGrader
{
    public Task<FuzzyMatchResult> EvaluateAsync(
        string userId, string mode, string answer,
        string expectedAnswer, CancellationToken cancellationToken) =>
        Task.FromResult(mode switch
        {
            "Text" => FuzzyMatcher.Evaluate(answer, expectedAnswer),
            "MultipleChoice" => new FuzzyMatchResult
            {
                IsCorrect = string.Equals(
                    answer.Trim(), expectedAnswer.Trim(),
                    StringComparison.OrdinalIgnoreCase)
            },
            _ => throw new NotSupportedException("Unsupported scratch quiz mode.")
        });
}

internal sealed class ScratchActivitySessions(ActivitySessionService inner)
    : IActivitySessionService
{
    public bool FailNextSnapshot { get; set; }
    public int? FailSnapshotAtCall { get; set; }
    public int SnapshotCalls { get; private set; }
    public bool FailNextComplete { get; set; }
    private TaskCompletionSource? _snapshotEntered;
    private TaskCompletionSource? _snapshotRelease;

    public Task HoldNextSnapshot()
    {
        _snapshotEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _snapshotRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return _snapshotEntered.Task;
    }

    public void ReleaseHeldSnapshot() => _snapshotRelease?.TrySetResult();

    public Task<ActivitySession?> GetResumableAsync(
        string userId, string activityType, string launchContextKey) =>
        inner.GetResumableAsync(userId, activityType, launchContextKey);

    public async Task<ActivitySession?> SaveSnapshotAsync(
        string userId, string activityType, string launchContextKey, string stateJson)
    {
        SnapshotCalls++;
        if (_snapshotEntered is { } entered)
        {
            _snapshotEntered = null;
            entered.TrySetResult();
            await _snapshotRelease!.Task;
            _snapshotRelease = null;
        }
        if (FailNextSnapshot || SnapshotCalls == FailSnapshotAtCall)
        {
            FailNextSnapshot = false;
            FailSnapshotAtCall = null;
            throw new DbUpdateException("Synthetic failure before the next snapshot.");
        }
        return await inner.SaveSnapshotAsync(userId, activityType, launchContextKey, stateJson);
    }

    public Task CompleteAsync(
        string userId, string activityType, string launchContextKey)
    {
        if (FailNextComplete)
        {
            FailNextComplete = false;
            throw new DbUpdateException("Synthetic activity completion failure.");
        }
        return inner.CompleteAsync(userId, activityType, launchContextKey);
    }

    public Task AbandonAsync(string userId, int sessionId) =>
        inner.AbandonAsync(userId, sessionId);
}

/// <summary>
/// Test-only durable receipt seam. This uses the real shared scoring service
/// and a scratch-only SQLite receipt table, but is NOT the production atomic
/// shared writer: a crash between RecordAttemptAsync and the receipt insert
/// would still be unsafe. The production port remains fail-closed.
/// </summary>
internal sealed class ScratchAttemptCommitter(
    string databasePath,
    IServiceScopeFactory scopes) : INativeQuizAttemptCommitter
{
    public bool FailNextWrite { get; set; }

    public async Task<NativeQuizCommitReceipt?> FindAsync(
        string userId, string launchContextKey, string turnId, CancellationToken cancellationToken)
    {
        await using var connection =
            new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT WordId FROM NativeTestTurnReceipt
            WHERE UserId = $user AND ContextKey = $context AND TurnId = $turn
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$context", launchContextKey);
        command.Parameters.AddWithValue("$turn", turnId);
        var wordId = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (wordId is null)
            return null;

        using var scope = scopes.CreateScope();
        var progress = await scope.ServiceProvider
            .GetRequiredService<VocabularyProgressRepository>()
            .GetByWordIdAndUserIdAsync(wordId, userId)
            ?? throw new InvalidOperationException("Durable receipt has no scored progress.");
        return new NativeQuizCommitReceipt(turnId, progress);
    }

    public async Task<NativeQuizCommitReceipt> CommitAsync(
        string userId, string launchContextKey, string turnId,
        VocabularyAttempt attempt, CancellationToken cancellationToken)
    {
        if (FailNextWrite)
        {
            FailNextWrite = false;
            throw new DbUpdateException("Synthetic attempt write failure.");
        }
        if (await FindAsync(userId, launchContextKey, turnId, cancellationToken) is { } prior)
            return prior;

        using var scope = scopes.CreateScope();
        var progress = await scope.ServiceProvider
            .GetRequiredService<IVocabularyProgressService>()
            .RecordAttemptAsync(attempt);
        await using var connection =
            new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO NativeTestTurnReceipt (UserId, ContextKey, TurnId, WordId)
            VALUES ($user, $context, $turn, $word)
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$context", launchContextKey);
        command.Parameters.AddWithValue("$turn", turnId);
        command.Parameters.AddWithValue("$word", attempt.VocabularyWordId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new NativeQuizCommitReceipt(turnId, progress);
    }
}
