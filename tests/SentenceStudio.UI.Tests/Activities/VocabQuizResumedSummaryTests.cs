using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;
using SentenceStudio.Services.Timer;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Pages;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Backport B41 (rule 6 of docs/activity-session-format.md): Done on a round summary another
/// client saved completed the run without checking that any of the round's answers had been
/// scored. The page now completes such a summary only when a quiz answer on one of the round's
/// words was scored for the learner since the run started (5 minutes of clock drift allowed),
/// like native NativeQuizAttemptCommitterAdapter.RoundWasScoredAsync. A summary alone completes
/// nothing; the run stays in progress.
/// </summary>
public sealed class VocabQuizResumedSummaryTests
{
    [Fact]
    public async Task ResumedSummary_NothingScored_DoneLeavesTheRunInProgress()
    {
        await using var fixture = await QuizFixture.CreateAsync();
        var quiz = await fixture.ResumeSummaryAsync();

        await Invoke(quiz, "GoBack");

        (await fixture.RunAsync()).Status.Should().Be(ActivitySessionStatus.InProgress,
            "a summary alone completes nothing");
        fixture.Navigation.Visited.Should().ContainSingle("Done still leaves the page");
    }

    [Fact]
    public async Task ResumedSummary_ARoundAnswerWasScoredSinceTheRunStarted_DoneCompletesTheRun()
    {
        await using var fixture = await QuizFixture.CreateAsync();
        await fixture.AddLearningRecordAsync(fixture.OwnerId, "VocabularyQuiz", TimeSpan.FromMinutes(2));
        var quiz = await fixture.ResumeSummaryAsync();

        await Invoke(quiz, "GoBack");

        var run = await fixture.RunAsync();
        run.Status.Should().Be(ActivitySessionStatus.Completed);
        run.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ResumedSummary_ARecordWithinTheClockDrift_CountsAsScored()
    {
        await using var fixture = await QuizFixture.CreateAsync();
        // Stamped by a client whose clock runs three minutes behind.
        await fixture.AddLearningRecordAsync(fixture.OwnerId, "VocabularyQuiz", TimeSpan.FromMinutes(-3));
        var quiz = await fixture.ResumeSummaryAsync();

        await Invoke(quiz, "GoBack");

        (await fixture.RunAsync()).Status.Should().Be(ActivitySessionStatus.Completed);
    }

    [Theory]
    [InlineData("other-learner", "VocabularyQuiz", 2)]   // another learner's answer on the word
    [InlineData("owner", "Clozure", 2)]                  // the word was scored, but not by the quiz
    [InlineData("owner", "VocabularyQuiz", -30)]         // scored before this run started
    public async Task ResumedSummary_NoQualifyingRecord_DoneLeavesTheRunInProgress(
        string who, string activity, int minutesAfterStart)
    {
        await using var fixture = await QuizFixture.CreateAsync();
        var learner = who == "owner" ? fixture.OwnerId : fixture.OtherId;
        await fixture.AddLearningRecordAsync(learner, activity, TimeSpan.FromMinutes(minutesAfterStart));
        var quiz = await fixture.ResumeSummaryAsync();

        await Invoke(quiz, "GoBack");

        (await fixture.RunAsync()).Status.Should().Be(ActivitySessionStatus.InProgress);
    }

    [Fact]
    public async Task ARunThisPageScored_DoneCompletesIt()
    {
        await using var fixture = await QuizFixture.CreateAsync();
        var quiz = await fixture.ResumeSummaryAsync();
        // The page scores an answer itself in this run (here: the next round's first answer).
        SetMember(quiz, "pendingAttempt", new VocabularyAttempt
        {
            VocabularyWordId = fixture.WordId,
            UserId = fixture.OwnerId,
            Activity = "VocabularyQuiz",
            InputMode = "Text",
            WasCorrect = true,
            DifficultyWeight = 1.5f,
            ContextType = "Isolated",
            UserInput = "book",
            ExpectedAnswer = "book"
        });
        await Invoke(quiz, "RecordPendingAttemptAsync");

        await Invoke(quiz, "GoBack");

        (await fixture.RunAsync()).Status.Should().Be(ActivitySessionStatus.Completed);
    }

    private static Task Invoke(VocabQuiz quiz, string method) =>
        (Task)typeof(VocabQuiz).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(quiz, null)!;

    private static void SetMember(object component, string name, object? value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = component.GetType();
        var property = type.GetProperty(name, flags);
        if (property is not null)
        {
            property.SetValue(component, value);
            return;
        }
        var field = type.GetField(name, flags)
            ?? throw new InvalidOperationException($"Missing component member: {name}");
        field.SetValue(component, value);
    }

    private static T GetMember<T>(object component, string name) =>
        (T)component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(component)!;

    private sealed class QuizFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly Preferences _preferences = new();
        private ServiceProvider _provider = null!;

        public string OwnerId { get; } = Guid.NewGuid().ToString();
        public string OtherId { get; } = Guid.NewGuid().ToString();
        public string WordId { get; } = Guid.NewGuid().ToString();
        public string LaunchContextKey { get; } =
            VocabQuizSessionSnapshot.BuildLaunchContextKey(null, null, null, false, null);
        public RecordingNavigation Navigation { get; } = new();
        private ActivitySessionService Sessions => _provider.GetRequiredService<ActivitySessionService>();

        public static async Task<QuizFixture> CreateAsync()
        {
            var fixture = new QuizFixture();
            await fixture._connection.OpenAsync();
            fixture._preferences.Set(UserProfileRepository.ActiveProfileIdKey, fixture.OwnerId);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(fixture._connection));
            services.AddSingleton<IPreferencesService>(fixture._preferences);
            services.AddSingleton<IFileSystemService>(new NoFiles());
            services.AddSingleton<LearningResourceRepository>();
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
                    new UserProfile { Id = fixture.OtherId, Name = "Other learner" });
                var resource = new LearningResource
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = "Owner words",
                    Language = "Korean",
                    UserProfileId = fixture.OwnerId,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.LearningResources.Add(resource);
                db.VocabularyWords.Add(new VocabularyWord
                {
                    Id = fixture.WordId,
                    TargetLanguageTerm = "책",
                    NativeLanguageTerm = "book",
                    Language = "Korean",
                    LexicalUnitType = LexicalUnitType.Word,
                    UserProfileId = fixture.OwnerId
                });
                db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
                {
                    Id = Guid.NewGuid().ToString(), ResourceId = resource.Id, VocabularyWordId = fixture.WordId
                });
                await db.SaveChangesAsync();
            }

            // Another client's save: the round is over and its summary is showing.
            var state = new VocabQuizSavedState
            {
                UserId = fixture.OwnerId,
                PlanDate = DateOnly.FromDateTime(DateTime.UtcNow),
                LaunchContextKey = fixture.LaunchContextKey,
                TurnId = Guid.NewGuid().ToString("N"),
                SummaryVisible = true,
                Snapshot = new VocabQuizSessionSnapshot
                {
                    BatchPool = [new VocabQuizBatchItemSnapshot { WordId = fixture.WordId, SessionCorrectCount = 1, WasCorrectThisSession = true }],
                    RoundWordOrder = [fixture.WordId],
                    CurrentTurnInRound = 1,
                    RoundsCompleted = 1,
                    TotalTurns = 1,
                    CorrectCount = 1,
                    SessionItemsWordIds = [fixture.WordId],
                    UserMode = "Text"
                }
            };
            await fixture.Sessions.SaveSnapshotAsync(
                fixture.OwnerId, ActivitySessionTypes.VocabularyReview, fixture.LaunchContextKey, state.Serialize());
            return fixture;
        }

        public async Task<ActivitySession> RunAsync()
        {
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ActivitySessions.AsNoTracking().SingleAsync(s => s.UserId == OwnerId);
        }

        /// <summary>A learning record for the word, stamped relative to the run's start.</summary>
        public async Task AddLearningRecordAsync(string learner, string activity, TimeSpan afterRunStart)
        {
            var run = await RunAsync();
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var progress = new VocabularyProgress { VocabularyWordId = WordId, UserId = learner, TotalAttempts = 1 };
            db.VocabularyProgresses.Add(progress);
            db.VocabularyLearningContexts.Add(new VocabularyLearningContext
            {
                VocabularyProgressId = progress.Id,
                Activity = activity,
                InputMode = "Text",
                WasCorrect = true,
                LearnedAt = run.StartedAt + afterRunStart
            });
            await db.SaveChangesAsync();
        }

        /// <summary>Opens the page on the saved run and resumes it at its summary, as Resume does.</summary>
        public async Task<VocabQuiz> ResumeSummaryAsync()
        {
            var quiz = new VocabQuiz();
            SetMember(quiz, "Localize", new BlazorLocalizationService());
            SetMember(quiz, "Logger", NullLogger<VocabQuiz>.Instance);
            SetMember(quiz, "Toast", new ToastService());
            SetMember(quiz, "NavManager", Navigation);
            SetMember(quiz, "ActivityTimer", new IdleTimer());
            SetMember(quiz, "Preferences", _preferences);
            SetMember(quiz, "ResourceRepo", _provider.GetRequiredService<LearningResourceRepository>());
            SetMember(quiz, "ProgressService", _provider.GetRequiredService<VocabularyProgressService>());
            SetMember(quiz, "CacheService", _provider.GetRequiredService<ProgressCacheService>());
            SetMember(quiz, "SessionService", Sessions);
            SetMember(quiz, "LearningContexts", _provider.GetRequiredService<VocabularyLearningContextRepository>());
            SetMember(quiz, "activeUserId", OwnerId);
            SetMember(quiz, "launchContextKey", LaunchContextKey);
            SetMember(quiz, "activityTimerStarted", true);
            SetMember(quiz, "resumableSession",
                await Sessions.GetResumableAsync(OwnerId, ActivitySessionTypes.VocabularyReview, LaunchContextKey));

            await Invoke(quiz, "ResumePreviousSessionAsync");

            GetMember<bool>(quiz, "showSessionSummary").Should().BeTrue("the saved summary is showing");
            GetMember<bool>(quiz, "resumeRejected").Should().BeFalse();
            return quiz;
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingNavigation : NavigationManager
    {
        public RecordingNavigation() => Initialize("http://localhost/", "http://localhost/vocab-quiz");
        public List<string> Visited { get; } = new();
        protected override void NavigateToCore(string uri, bool forceLoad) => Visited.Add(uri);
    }

    private sealed class Preferences : IPreferencesService
    {
        private readonly Dictionary<string, object?> _values = new();
        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T stored ? stored : defaultValue;
        public void Set<T>(string key, T value) => _values[key] = value;
        public void Remove(string key) => _values.Remove(key);
        public void Clear() => _values.Clear();
    }

    private sealed class NoFiles : IFileSystemService
    {
        public string AppDataDirectory => Path.GetTempPath();
        public Task<Stream> OpenAppPackageFileAsync(string filename) => throw new NotSupportedException();
    }

    /// <summary>No timer lease is held, so Done's timer stop does nothing.</summary>
    private sealed class IdleTimer : IActivityTimerService
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
