using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;
using SentenceStudio.Services.Timer;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Pages;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Audio;

public class QuizAudioPersistenceTests
{
    [Fact]
    public async Task Q25a_GradedPendingBeforeNext_FailedJsStopStillPersistsOneOwnedAttemptAndSnapshot()
    {
        await using var fixture = await QuizSqliteFixture.CreateAsync();
        var js = new FailingStopJsRuntime();
        var logger = new RecordingLogger<VocabQuiz>();
        var quiz = fixture.CreateQuiz(js, logger);

        await InvokeQuizAsync(quiz, "PersistSnapshotAsync");
        await InvokeQuizAsync(quiz, "CheckAnswer");

        var pending = GetField<VocabularyAttempt?>(quiz, "pendingAttempt");
        pending.Should().NotBeNull();
        pending!.UserId.Should().Be(fixture.OwnerId);
        pending.VocabularyWordId.Should().Be(fixture.WordId);
        pending.WasCorrect.Should().BeFalse();

        var beforeNext = await fixture.ReadRowsAsync();
        beforeNext.Progress.Should().NotContain(p => p.UserId == fixture.OwnerId);
        beforeNext.Contexts.Should().BeEmpty();
        var initialSnapshot = VocabQuizSessionSnapshot.Deserialize(OwnerSession(fixture, beforeNext).StateJson);
        initialSnapshot.TotalTurns.Should().Be(0);
        initialSnapshot.BatchPool.Should().ContainSingle().Which.WordId.Should().Be(fixture.WordId);

        SetMember(quiz, "_browserAudioAttempted", true);
        await quiz.DisposeAsync();

        js.StopCalls.Should().Be(1);
        GetField<VocabularyAttempt?>(quiz, "pendingAttempt").Should().BeNull();
        var afterDisposal = await fixture.ReadRowsAsync();
        AssertOneOwnedAttempt(fixture, afterDisposal);
        var savedSnapshot = VocabQuizSessionSnapshot.Deserialize(OwnerSession(fixture, afterDisposal).StateJson);
        savedSnapshot.TotalTurns.Should().Be(1);
        savedSnapshot.CurrentTurnInRound.Should().Be(0);
        savedSnapshot.BatchPool.Should().ContainSingle().Which.WordId.Should().Be(fixture.WordId);
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Warning
            && entry.Error is JSException
            && entry.Message.Contains("continuing to save pending progress"));
    }

    [Fact]
    public async Task Q25b_CommittedBeforeNextSnapshot_DisposalPersistsLatestStateWithoutSecondAttempt()
    {
        await using var fixture = await QuizSqliteFixture.CreateAsync();
        var logger = new RecordingLogger<VocabQuiz>();
        var quiz = fixture.CreateQuiz(new FailingStopJsRuntime(), logger);

        await InvokeQuizAsync(quiz, "PersistSnapshotAsync");
        await InvokeQuizAsync(quiz, "CheckAnswer");
        GetField<VocabularyAttempt?>(quiz, "pendingAttempt").Should().NotBeNull();

        var pausedSnapshots = new PausingSnapshotService(fixture.Sessions);
        SetMember(quiz, "SessionService", pausedSnapshots);
        var next = InvokeQuizAsync(quiz, "SubmitTextInput");
        await pausedSnapshots.SnapshotReached.WaitAsync(TimeSpan.FromSeconds(10));

        GetField<VocabularyAttempt?>(quiz, "pendingAttempt").Should().BeNull();
        var beforeSnapshotCommit = await fixture.ReadRowsAsync();
        AssertOneOwnedAttempt(fixture, beforeSnapshotCommit);
        VocabQuizSessionSnapshot.Deserialize(OwnerSession(fixture, beforeSnapshotCommit).StateJson)
            .TotalTurns.Should().Be(0, "the next snapshot is held after the real attempt write");

        await quiz.DisposeAsync();
        var afterDisposal = await fixture.ReadRowsAsync();
        AssertOneOwnedAttempt(fixture, afterDisposal);
        var disposalSnapshot = VocabQuizSessionSnapshot.Deserialize(OwnerSession(fixture, afterDisposal).StateJson);
        disposalSnapshot.TotalTurns.Should().Be(1);
        disposalSnapshot.CurrentTurnInRound.Should().Be(1);
        disposalSnapshot.SessionItemsWordIds.Should().ContainSingle().Which.Should().Be(fixture.WordId);

        pausedSnapshots.Resume();
        await next.WaitAsync(TimeSpan.FromSeconds(10));

        var afterNext = await fixture.ReadRowsAsync();
        AssertOneOwnedAttempt(fixture, afterNext);
        OwnerSession(fixture, afterNext).Id.Should().Be(OwnerSession(fixture, afterDisposal).Id);
        VocabQuizSessionSnapshot.Deserialize(OwnerSession(fixture, afterNext).StateJson)
            .TotalTurns.Should().Be(1);
        logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Error);
    }

    private static ActivitySession OwnerSession(QuizSqliteFixture fixture, QuizRows rows)
    {
        var session = rows.Sessions.Should().ContainSingle(s => s.UserId == fixture.OwnerId).Which;
        session.ActivityType.Should().Be("VocabularyReview");
        session.LaunchContextKey.Should().Be(fixture.LaunchContextKey);
        session.Status.Should().Be(ActivitySessionStatus.InProgress);
        rows.Sessions.Should().ContainSingle(s => s.UserId == fixture.OtherId)
            .Which.StateJson.Should().Be(fixture.OtherSnapshot);
        return session;
    }

    private static void AssertOneOwnedAttempt(QuizSqliteFixture fixture, QuizRows rows)
    {
        var progress = rows.Progress.Should().ContainSingle(p => p.UserId == fixture.OwnerId).Which;
        progress.VocabularyWordId.Should().Be(fixture.WordId);
        progress.TotalAttempts.Should().Be(1);
        progress.CorrectAttempts.Should().Be(0);
        rows.Progress.Should().ContainSingle(p => p.UserId == fixture.OtherId)
            .Which.TotalAttempts.Should().Be(4);

        var context = rows.Contexts.Should().ContainSingle().Which;
        context.VocabularyProgress.UserId.Should().Be(fixture.OwnerId);
        context.VocabularyProgressId.Should().Be(progress.Id);
        context.Activity.Should().Be("VocabularyQuiz");
        context.InputMode.Should().Be("Text");
        context.UserInput.Should().Be("wrong");
        context.ExpectedAnswer.Should().Be("book");
        context.WasCorrect.Should().BeFalse();
    }

    private static Task InvokeQuizAsync(VocabQuiz quiz, string method) =>
        (Task)typeof(VocabQuiz).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(quiz, null)!;

    private static void SetMember(object component, string name, object? value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            | BindingFlags.DeclaredOnly;
        for (var type = component.GetType(); type is not null; type = type.BaseType)
        {
            var member = type.GetProperty(name, flags);
            if (member is not null)
            {
                member.SetValue(component, value);
                return;
            }
            var field = type.GetField(name, flags);
            if (field is not null)
            {
                field.SetValue(component, value);
                return;
            }
        }
        throw new InvalidOperationException($"Missing component member: {name}");
    }

    private static T GetField<T>(object component, string name) =>
        (T)typeof(VocabQuiz).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(component)!;

    private sealed record QuizRows(
        List<VocabularyProgress> Progress,
        List<VocabularyLearningContext> Contexts,
        List<ActivitySession> Sessions);

    private sealed class QuizSqliteFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private ServiceProvider _provider = null!;
        private readonly OwnedPreferences _preferences = new();

        public string OwnerId { get; } = Guid.NewGuid().ToString();
        public string OtherId { get; } = Guid.NewGuid().ToString();
        public string WordId { get; } = Guid.NewGuid().ToString();
        public string LaunchContextKey { get; } =
            VocabQuizSessionSnapshot.BuildLaunchContextKey(null, null, null, false, null);
        public string OtherSnapshot { get; } =
            new VocabQuizSessionSnapshot { TotalTurns = 99 }.Serialize();
        public ActivitySessionService Sessions => _provider.GetRequiredService<ActivitySessionService>();

        public static async Task<QuizSqliteFixture> CreateAsync()
        {
            var fixture = new QuizSqliteFixture();
            await fixture._connection.OpenAsync();
            fixture._preferences.Set(UserProfileRepository.ActiveProfileIdKey, fixture.OwnerId);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(fixture._connection));
            services.AddSingleton<IPreferencesService>(fixture._preferences);
            services.AddSingleton<VocabularyProgressRepository>();
            services.AddSingleton<VocabularyLearningContextRepository>();
            services.AddSingleton<VocabularyProgressService>();
            services.AddSingleton<ProgressCacheService>();
            services.AddSingleton<ActivitySessionService>();
            fixture._provider = services.BuildServiceProvider();

            await using (var scope = fixture._provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Database.EnsureCreatedAsync();
                db.UserProfiles.AddRange(
                    new UserProfile { Id = fixture.OwnerId, Name = "Quiz owner" },
                    new UserProfile { Id = fixture.OtherId, Name = "Other quiz owner" });
                db.VocabularyWords.Add(new VocabularyWord
                {
                    Id = fixture.WordId,
                    TargetLanguageTerm = "책",
                    NativeLanguageTerm = "book",
                    LexicalUnitType = LexicalUnitType.Word
                });
                db.VocabularyProgresses.Add(new VocabularyProgress
                {
                    VocabularyWordId = fixture.WordId,
                    UserId = fixture.OtherId,
                    TotalAttempts = 4,
                    CorrectAttempts = 2
                });
                await db.SaveChangesAsync();
            }

            await fixture.Sessions.SaveSnapshotAsync(
                fixture.OtherId, "VocabularyReview", fixture.LaunchContextKey, fixture.OtherSnapshot);
            return fixture;
        }

        public VocabQuiz CreateQuiz(IJSRuntime js, ILogger<VocabQuiz> logger)
        {
            var item = new VocabularyQuizItem
            {
                Word = new VocabularyWord
                {
                    Id = WordId,
                    TargetLanguageTerm = "책",
                    NativeLanguageTerm = "book"
                }
            };
            var quiz = new VocabQuiz();
            SetMember(quiz, "Localize", new BlazorLocalizationService());
            SetMember(quiz, "JS", js);
            SetMember(quiz, "Logger", logger);
            SetMember(quiz, "ActivityTimer", new NoopTimer());
            SetMember(quiz, "Preferences", _preferences);
            SetMember(quiz, "ProgressService", _provider.GetRequiredService<VocabularyProgressService>());
            SetMember(quiz, "CacheService", _provider.GetRequiredService<ProgressCacheService>());
            SetMember(quiz, "SessionService", Sessions);
            SetMember(quiz, "activeUserId", OwnerId);
            SetMember(quiz, "launchContextKey", LaunchContextKey);
            SetMember(quiz, "activityTimerStarted", true);
            SetMember(quiz, "currentItem", item);
            SetMember(quiz, "batchPool", new List<VocabularyQuizItem> { item });
            SetMember(quiz, "vocabItems", new List<VocabularyQuizItem> { item });
            SetMember(quiz, "roundWordOrder", new List<VocabularyQuizItem> { item });
            SetMember(quiz, "userMode", "Text");
            SetMember(quiz, "userInput", "wrong");
            SetMember(quiz, "correctAnswer", "book");
            return quiz;
        }

        public async Task<QuizRows> ReadRowsAsync()
        {
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return new QuizRows(
                await db.VocabularyProgresses.AsNoTracking().ToListAsync(),
                await db.VocabularyLearningContexts.AsNoTracking()
                    .Include(context => context.VocabularyProgress).ToListAsync(),
                await db.ActivitySessions.AsNoTracking().ToListAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class PausingSnapshotService(IActivitySessionService inner) : IActivitySessionService
    {
        private bool _pauseFirstSave = true;
        private readonly TaskCompletionSource<bool> _snapshotReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _resume =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SnapshotReached => _snapshotReached.Task;
        public void Resume() => _resume.TrySetResult(true);

        public Task<ActivitySession?> GetResumableAsync(
            string userId, string activityType, string launchContextKey)
            => inner.GetResumableAsync(userId, activityType, launchContextKey);

        public async Task<ActivitySession?> SaveSnapshotAsync(
            string userId, string activityType, string launchContextKey, string stateJson)
        {
            if (_pauseFirstSave)
            {
                _pauseFirstSave = false;
                _snapshotReached.TrySetResult(true);
                await _resume.Task;
            }
            return await inner.SaveSnapshotAsync(userId, activityType, launchContextKey, stateJson);
        }

        public Task CompleteAsync(string userId, string activityType, string launchContextKey)
            => inner.CompleteAsync(userId, activityType, launchContextKey);

        public Task AbandonAsync(string userId, int sessionId) => inner.AbandonAsync(userId, sessionId);
    }

    private sealed class OwnedPreferences : IPreferencesService
    {
        private readonly Dictionary<string, object?> _values = new();
        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T stored ? stored : defaultValue;
        public void Set<T>(string key, T value) => _values[key] = value;
        public void Remove(string key) => _values.Remove(key);
        public void Clear() => _values.Clear();
    }

    private sealed class FailingStopJsRuntime : IJSRuntime
    {
        public int StopCalls { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "audioInterop.stop")
                throw new InvalidOperationException($"Unexpected JS call: {identifier}");
            StopCalls++;
            throw new JSException("Browser stop failed while quiz is disposing");
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Error, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(
            LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((level, exception, formatter(state, exception)));
    }

    private sealed class NoopTimer : IActivityTimerService
    {
        public bool IsActive => false;
        public bool IsRunning => false;
        public TimeSpan ElapsedTime => TimeSpan.Zero;
        public string? CurrentActivityType => null;
        public string? CurrentActivityId => null;
        public event EventHandler? TimerStateChanged { add { } remove { } }
        public event EventHandler<TimeSpan>? TimerTick { add { } remove { } }
        public ActivityTimerLease StartSession(
            string activityType, string? activityId = null, string? resourceId = null, string? skillId = null)
            => throw new NotSupportedException();
        public Task<ActivityTimerLease?> StartValidatedSessionAsync(
            ActivityTimerStartRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public void Pause() => throw new NotSupportedException();
        public bool Pause(ActivityTimerLease lease) => throw new NotSupportedException();
        public void Resume() => throw new NotSupportedException();
        public bool Resume(ActivityTimerLease lease) => throw new NotSupportedException();
        public TimeSpan StopSession() => throw new NotSupportedException();
        public TimeSpan StopSession(ActivityTimerLease lease) => throw new NotSupportedException();
        public void CancelSession() => throw new NotSupportedException();
        public bool CancelSession(ActivityTimerLease lease) => throw new NotSupportedException();
    }
}
