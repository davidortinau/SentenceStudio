using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;
using SentenceStudio.UnitTests.Logging;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.UnitTests.Services.Plans;

/// <summary>
/// Shared harness for the coach plan-revision tests. Real SQLite, real
/// <see cref="ApplicationDbContext"/>, real transactions — the merge rules and
/// the rollback path are only meaningful against a relational provider.
/// </summary>
public sealed class CoachPlanRevisionHarness : IDisposable
{
    public const string UserA = "coach-user-a";
    public const string UserB = "coach-user-b";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public MutableScope Scope { get; }
    public FixedDateContext Date { get; }
    public ConstraintAwareGenerator Generator { get; }
    public PostSaveSabotageInterceptor Sabotage { get; }

    /// <summary>Captured log records (message + structured state) for this harness.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    public CoachPlanRevisionHarness()
        : this(useRetryingExecutionStrategy: false)
    {
    }

    /// <param name="useRetryingExecutionStrategy">
    /// When true the context is configured with an execution strategy whose
    /// <c>RetriesOnFailure</c> is true, exactly like
    /// <c>NpgsqlRetryingExecutionStrategy</c> in the deployed API. EF then
    /// refuses any user-initiated transaction that is not wrapped in
    /// <c>CreateExecutionStrategy().ExecuteAsync(...)</c>, which is the
    /// production failure this harness reproduces on SQLite.
    /// Private because xUnit class fixtures may declare one public
    /// constructor — use <see cref="CreateWithRetryingExecutionStrategy"/>.
    /// </param>
    private CoachPlanRevisionHarness(bool useRetryingExecutionStrategy)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        Sabotage = new PostSaveSabotageInterceptor();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseSqlite(_connection, sqlite =>
                {
                    if (useRetryingExecutionStrategy)
                    {
                        sqlite.ExecutionStrategy(deps => new RetryingTestExecutionStrategy(deps));
                    }
                })
               .AddInterceptors(Sabotage)
               .ConfigureWarnings(w =>
                   w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(Logs);
        });

        Scope = new MutableScope(UserA);
        Date = new FixedDateContext(new DateOnly(2026, 8, 14));
        Generator = new ConstraintAwareGenerator();

        services.AddSingleton<IUserScopeProvider>(Scope);
        services.AddSingleton<IPlanDateContext>(Date);
        services.AddSingleton<IDeterministicPlanGenerator>(Generator);
        services.AddSingleton<IPlanCopyProvider, EnglishPlanCopyProvider>();
        services.AddScoped<IPlanService, PlanService>();

        _provider = services.BuildServiceProvider();

        using var bootstrap = _provider.CreateScope();
        bootstrap.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();
    }

    /// <summary>A service on its own DI scope, mirroring one HTTP request.</summary>
    public IPlanService NewService() => _provider.CreateScope().ServiceProvider.GetRequiredService<IPlanService>();

    /// <summary>
    /// A harness whose provider reports <c>RetriesOnFailure = true</c>, which is
    /// what makes EF reject a hand-rolled transaction. Use it to guard the
    /// Npgsql-compatible execution-strategy path.
    /// </summary>
    public static CoachPlanRevisionHarness CreateWithRetryingExecutionStrategy() =>
        new(useRetryingExecutionStrategy: true);

    /// <summary>
    /// Mirrors <c>NpgsqlRetryingExecutionStrategy</c>'s contract: it advertises
    /// that it retries, so EF applies the "wrap user-initiated transactions in
    /// the execution strategy" rule. It never actually retries, so tests stay
    /// deterministic and a genuine failure surfaces immediately.
    /// </summary>
    private sealed class RetryingTestExecutionStrategy : ExecutionStrategy
    {
        public RetryingTestExecutionStrategy(ExecutionStrategyDependencies dependencies)
            : base(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(1))
        {
        }

        protected override bool ShouldRetryOn(Exception exception) => false;
    }

    /// <summary>A service plus the exact context instance backing it.</summary>
    public (IPlanService Service, ApplicationDbContext Db) NewServiceWithContext()
    {
        var scope = _provider.CreateScope();
        return (
            scope.ServiceProvider.GetRequiredService<IPlanService>(),
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    public ApplicationDbContext NewDbContext() =>
        _provider.CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>();

    public DateTime DateKey => Date.UserLocalDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    public List<DailyPlanCompletion> Rows(string userId)
    {
        using var db = NewDbContext();
        return db.DailyPlanCompletions.AsNoTracking()
            .Where(c => c.UserProfileId == userId && c.Date == DateKey)
            .OrderBy(c => c.Priority).ThenBy(c => c.PlanItemId)
            .ToList();
    }

    public DailyPlanCompletion Row(string userId, string planItemId) =>
        Rows(userId).Single(r => r.PlanItemId == planItemId);

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    /// <summary>
    /// Deletes a chosen completion row immediately after a successful
    /// SaveChanges, inside the caller's still-open transaction. It fakes the
    /// only failure mode the revision path can detect post-write — a completed
    /// item disappearing — so the rollback branch is exercised against real SQL
    /// rather than mocked out.
    /// </summary>
    public sealed class PostSaveSabotageInterceptor : SaveChangesInterceptor
    {
        private string? _armedCompletionId;

        public void ArmDeleteOf(string completionId) => _armedCompletionId = completionId;

        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        {
            Sabotage(eventData);
            return base.SavedChanges(eventData, result);
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Sabotage(eventData);
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }

        private void Sabotage(SaveChangesCompletedEventData eventData)
        {
            if (_armedCompletionId is null || eventData.Context is null)
            {
                return;
            }

            var id = _armedCompletionId;
            _armedCompletionId = null;
            eventData.Context.Database.ExecuteSqlRaw(
                "DELETE FROM DailyPlanCompletion WHERE Id = {0}", id);
        }
    }

    public sealed class MutableScope : IUserScopeProvider
    {
        private string _userId;
        public MutableScope(string userId) => _userId = userId;
        public string UserProfileId => _userId;
        public void SetUser(string userId) => _userId = userId;
        public bool TryGetUserProfileId(out string userProfileId)
        {
            userProfileId = _userId;
            return true;
        }
    }

    public sealed class FixedDateContext : IPlanDateContext
    {
        public FixedDateContext(DateOnly localDate)
        {
            UserLocalDate = localDate;
            UtcNow = localDate.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc);
        }
        public DateOnly UserLocalDate { get; }
        public DateTime UtcNow { get; private set; }
        public TimeZoneInfo TimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
        public DateOnly ToUserLocal(DateTime utc) => DateOnly.FromDateTime(utc);
        public DateTime ToUtcMidnight(DateOnly userLocal) => userLocal.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
    }

    /// <summary>
    /// A generator that answers differently depending on the supplied
    /// constraints, and that records every request so tests can prove the
    /// preview path really did suppress writes.
    /// </summary>
    public sealed class ConstraintAwareGenerator : IDeterministicPlanGenerator
    {
        private List<(string Type, string? ResourceId, string? SkillId, int Minutes, int Priority)> _default = new();
        private List<(string Type, string? ResourceId, string? SkillId, int Minutes, int Priority)>? _constrained;
        private bool _returnNullWhenConstrained;
        private bool _throwOnGenerate;
        private Exception? _generateFailure;

        public List<PlanBuildRequest> Requests { get; } = new();

        public void SetDefault(params (string Type, string? ResourceId, string? SkillId, int Minutes, int Priority)[] items)
            => _default = items.ToList();

        public void SetConstrained(params (string Type, string? ResourceId, string? SkillId, int Minutes, int Priority)[] items)
            => _constrained = items.ToList();

        public void ReturnNullWhenConstrained() => _returnNullWhenConstrained = true;

        /// <summary>Makes the generator throw, exercising PlanService's fallback log.</summary>
        public void ThrowOnGenerate() => _throwOnGenerate = true;

        /// <summary>
        /// Makes the generator throw a specific exception, so a test can plant learner text in
        /// the message, the inner chain, and Data and prove none of it is logged.
        /// </summary>
        public void ThrowOnGenerate(Exception failure)
        {
            _throwOnGenerate = true;
            _generateFailure = failure;
        }

        public Task<PlanSkeleton?> GenerateAsync(string? userProfileId = null, CancellationToken ct = default)
            => GenerateAsync(new PlanBuildRequest { UserProfileId = userProfileId }, ct);

        public Task<PlanSkeleton?> GenerateAsync(PlanBuildRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);

            if (_throwOnGenerate)
            {
                throw _generateFailure ?? new InvalidOperationException("generator failure (test)");
            }

            if (request.Constraints is not null && _returnNullWhenConstrained)
            {
                return Task.FromResult<PlanSkeleton?>(null);
            }

            var source = request.Constraints is not null && _constrained is not null ? _constrained : _default;

            // Echo the trusted focus set the way the real builder does, so
            // preview/apply parity over the selected words is observable.
            var focusIds = request.FocusVocabularyWordIds?.ToList() ?? new List<string>();

            return Task.FromResult<PlanSkeleton?>(new PlanSkeleton
            {
                Activities = source.Select(a => new PlannedActivity
                {
                    ActivityType = a.Type,
                    ResourceId = a.ResourceId,
                    SkillId = a.SkillId,
                    EstimatedMinutes = a.Minutes,
                    Priority = a.Priority,
                    Rationale = "test",
                    FocusVocabularyIds = focusIds.ToList(),
                }).ToList(),
                FocusVocabularyIds = focusIds,
                TotalMinutes = source.Sum(a => a.Minutes),
                ResourceSelectionReason = "test reason",
            });
        }
    }
}
