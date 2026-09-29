using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Models.DailyPlanGeneration;

namespace SentenceStudio.Native.Tests;

internal sealed class NativeSliceFixture : IDisposable
{
    public const string UserA = "native-owner-a";
    public const string UserB = "native-owner-b";
    public const string EmailA = "native-a@example.invalid";
    public const string EmailB = "native-b@example.invalid";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "sentencestudio-native-tests", Guid.NewGuid().ToString("N"));
    private readonly string _database;
    private ServiceProvider _provider;
    private readonly ScratchPreferences _preferences = new();
    private readonly ScratchSecureStorage _secureStorage = new();
    private readonly DeviceUserScopeProvider _userScope = new();
    private readonly ScratchAuthHandler _authHandler = new();
    private readonly ScratchPlanGenerator _generator = new();
    private readonly ScratchConnectivity _connectivity = new();
    private readonly bool _productionAdapters;
    private readonly bool _realPlanGenerator;
    private readonly bool _ownerBoundReadiness;

    public NativeSliceFixture(
        bool productionAdapters = false, bool realPlanGenerator = false,
        bool ownerBoundReadiness = false)
    {
        _productionAdapters = productionAdapters;
        _realPlanGenerator = realPlanGenerator;
        _ownerBoundReadiness = ownerBoundReadiness;
        Directory.CreateDirectory(_directory);
        _database = Path.Combine(_directory, "native-slice.sqlite");
        _authHandler.Register(EmailA, UserA);
        _authHandler.Register(EmailB, UserB);
        _provider = BuildProvider();
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE NativeTestTurnReceipt (
              UserId TEXT NOT NULL,
              ContextKey TEXT NOT NULL,
              TurnId TEXT NOT NULL,
              WordId TEXT NOT NULL,
              PRIMARY KEY (UserId, ContextKey, TurnId)
            );
            """;
        command.ExecuteNonQuery();
    }

    public ScratchSyncReadiness Sync { get; } = new();
    public ScratchRemoteSyncCompletion RemoteSync { get; } = new();
    public ScratchQuizMedia Media { get; } = new();
    public IServiceProvider Provider => _provider;
    public ScratchActivitySessions FaultSessions => _provider
        .GetRequiredService<ScratchActivitySessions>();
    public ScratchAttemptCommitter Committer => _provider
        .GetRequiredService<ScratchAttemptCommitter>();
    public ScratchPreferences Preferences => _preferences;
    public ScratchSecureStorage SecureStorage => _secureStorage;
    public DeviceUserScopeProvider UserScope => _userScope;
    public int RemoteProfileRequests => _authHandler.ProfileRequests;
    public bool? LastPlanGenerationAllowedWrites => _generator.LastAllowWrites;
    public VocabularyQuizPreferences QuizPreferences => _provider
        .GetRequiredService<VocabularyQuizPreferences>();
    public NativeAuthPresenter Auth => _provider.GetRequiredService<NativeAuthPresenter>();
    public NativeDashboardPresenter Dashboard => _provider.GetRequiredService<NativeDashboardPresenter>();
    public NativeQuizPresenter Quiz => _provider.GetRequiredService<NativeQuizPresenter>();
    public IServiceScope Scope() => _provider.CreateScope();
    public NativePlanLease PlanLease(string userId) =>
        _provider.GetRequiredService<NativePlanLeaseFactory>().ForOwner(userId);
    public void MarkPendingForTest(string userId) =>
        _provider.GetRequiredService<NativePendingWrites>().MarkPending(userId);
    public void SetPlanFocus(string userId, params string[] wordIds) =>
        _generator.SetPlan(userId, wordIds.ToList());
    public void SetPlanActivities(
        string userId,
        params (string Type, string? ResourceId, string? SkillId, int Minutes, int Priority)[] activities) =>
        _generator.SetActivities(userId, activities);
    public void SetAuthStatus(HttpStatusCode? status) =>
        _authHandler.ForcedStatus = status;
    public void SetAuthOffline(bool offline) =>
        _authHandler.Offline = offline;
    public void SetConnectivity(bool available) =>
        _connectivity.Available = available;
    public void SetRemoteProfileStatus(HttpStatusCode? status) =>
        _authHandler.ProfileStatus = status;
    public void SetTokenOwner(string? owner) =>
        _authHandler.TokenOwnerOverride = owner;
    public void SetDuplicateTokenOwners(bool enabled) =>
        _authHandler.DuplicateOwnerClaim = enabled;
    public void SetLoginResponseHook(
        Func<string, CancellationToken, Task>? hook) =>
        _authHandler.BeforeLoginResponseAsync = hook;

    public void Activate(string userId)
    {
        _preferences.Set(UserProfileRepository.ActiveProfileIdKey, userId);
        _userScope.SetActiveUser(userId);
    }

    public void LoseOwner()
    {
        _preferences.Remove(UserProfileRepository.ActiveProfileIdKey);
        _userScope.SetActiveUser(null);
    }

    public void Restart()
    {
        _provider.Dispose();
        _userScope.SetActiveUser(null);
        _provider = BuildProvider();
    }

    public async Task SeedAsync(string userId, int wordCount = 4, bool known = false)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var resourceId = ResourceId(userId);
        db.UserProfiles.Add(new UserProfile
        {
            Id = userId,
            Name = userId,
            Email = userId == UserA ? EmailA : EmailB,
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            CreatedAt = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc)
        });
        db.LearningResources.Add(new LearningResource
        {
            Id = resourceId,
            UserProfileId = userId,
            Language = "Korean",
            MediaType = "Vocabulary List",
            Title = "Synthetic vocabulary",
            CreatedAt = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc)
        });
        var ids = new List<string>();
        for (var i = 0; i < wordCount; i++)
        {
            var wordId = WordId(userId, i);
            ids.Add(wordId);
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = wordId,
                TargetLanguageTerm = $"target-{userId}-{i}",
                NativeLanguageTerm = $"native-{userId}-{i}",
                Language = "Korean",
                MnemonicImageUri = $"opaque-photo-{i}"
            });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = $"mapping-{userId}-{i}",
                ResourceId = resourceId,
                VocabularyWordId = wordId
            });
            if (known)
            {
                db.VocabularyProgresses.Add(new VocabularyProgress
                {
                    Id = $"progress-{userId}-{i}",
                    UserId = userId,
                    VocabularyWordId = wordId,
                    MasteryScore = 0.9f,
                    ProductionInStreak = 3,
                    TotalAttempts = 8,
                    CurrentStreak = 6
                });
            }
        }
        await db.SaveChangesAsync();
        _generator.SetPlan(userId, ids);
    }

    public async Task SeedProfileAsync(string userId)
    {
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (await db.UserProfiles.AnyAsync(profile => profile.Id == userId))
            return;
        db.UserProfiles.Add(new UserProfile
        {
            Id = userId,
            Name = userId,
            Email = userId == UserA ? EmailA : EmailB,
            NativeLanguage = "English",
            TargetLanguage = "Korean",
            CreatedAt = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();
    }

    public async Task<string> GenerateQuizPlanAsync(string userId)
    {
        Activate(userId);
        using var scope = _provider.CreateScope();
        var plan = await scope.ServiceProvider.GetRequiredService<IPlanService>()
            .GenerateTodayAsync(new() { Strategy = "deterministic" });
        return Assert.Single(plan.Items, item =>
            item.ActivityType == nameof(PlanActivityType.VocabularyReview)).Id;
    }

    public async Task<(DailyPlanCompletion Item, ActivitySession? Session)> RowsAsync(
        string userId, string planItemId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var item = await db.DailyPlanCompletions.AsNoTracking()
            .SingleAsync(row => row.UserProfileId == userId && row.PlanItemId == planItemId);
        var session = await db.ActivitySessions.AsNoTracking()
            .Where(row => row.UserId == userId && row.ActivityType == "VocabularyReview")
            .OrderByDescending(row => row.Id)
            .FirstOrDefaultAsync();
        return (item, session);
    }

    public async Task<int> AttemptCountAsync(string userId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyLearningContexts.AsNoTracking()
            .CountAsync(context => db.VocabularyProgresses.Any(
                progress => progress.Id == context.VocabularyProgressId
                    && progress.UserId == userId));
    }

    public async Task<int> TotalAttemptsAsync(string userId, string wordId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyProgresses.AsNoTracking()
            .Where(progress => progress.UserId == userId
                && progress.VocabularyWordId == wordId)
            .Select(progress => (int?)progress.TotalAttempts)
            .SingleOrDefaultAsync() ?? 0;
    }

    public async Task<int> ReceiptCountAsync(string userId, string? turnId = null)
    {
        using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = turnId is null
            ? "SELECT COUNT(*) FROM NativeTestTurnReceipt WHERE UserId = $user"
            : "SELECT COUNT(*) FROM NativeTestTurnReceipt WHERE UserId = $user AND TurnId = $turn";
        command.Parameters.AddWithValue("$user", userId);
        if (turnId is not null)
            command.Parameters.AddWithValue("$turn", turnId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public string ResourceId(string userId) => $"resource-{userId}";
    public string WordId(string userId, int index) => $"word-{userId}-{index}";

    public void Dispose()
    {
        _provider.Dispose();
        foreach (var name in new[] {
            "native-slice.sqlite", "native-slice.sqlite-wal", "native-slice.sqlite-shm" })
        {
            var file = Path.Combine(_directory, name);
            if (File.Exists(file))
                File.Delete(file);
        }
        Directory.Delete(_directory);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_database};Pooling=False");
        connection.Open();
        return connection;
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite($"Data Source={_database};Pooling=False")
                .ConfigureWarnings(w =>
                    w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddSingleton<IPreferencesService>(_preferences);
        services.AddSingleton<ISecureStorageService>(_secureStorage);
        services.AddSingleton<IConnectivityService>(_connectivity);
        services.AddSingleton<IFileSystemService>(new ScratchFiles(_directory));
        services.AddSingleton<ISyncService, NoOpSyncService>();
        services.AddSingleton(_userScope);
        services.AddSingleton<IUserScopeProvider>(sp =>
            sp.GetRequiredService<DeviceUserScopeProvider>());
        services.AddTransient<IPlanDateContext>(_ =>
            new PlanDateContext(TimeZoneInfo.Utc,
                () => new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc)));
        if (_realPlanGenerator)
        {
            services.AddSingleton<DeterministicPlanBuilder>();
            services.AddSingleton<GeneratedPlanValidator>();
            services.AddSingleton<IDeterministicPlanGenerator, DeterministicPlanGenerator>();
        }
        else
        {
            services.AddSingleton<IDeterministicPlanGenerator>(_generator);
        }
        services.AddSingleton<IPlanCopyProvider, EnglishPlanCopyProvider>();
        services.AddScoped<IPlanService, PlanService>();
        services.AddSingleton<UserProfileRepository>();
        services.AddSingleton<LearningResourceRepository>();
        services.AddSingleton<SkillProfileRepository>();
        services.AddSingleton<UserActivityRepository>();
        services.AddSingleton<VocabularyProgressRepository>();
        services.AddSingleton<VocabularyLearningContextRepository>();
        services.AddSingleton<VocabularyProgressService>();
        services.AddSingleton<IVocabularyProgressService>(sp =>
            sp.GetRequiredService<VocabularyProgressService>());
        services.AddSingleton<ProgressCacheService>();
        services.AddSingleton<ILlmPlanGenerationService, NoLlmPlan>();
        services.AddSingleton<IProgressService, ProgressService>();
        services.AddSingleton<VocabQuizLaunchValidator>();
        services.AddSingleton<ActivitySessionService>();
        services.AddSingleton<ScratchActivitySessions>();
        services.AddSingleton<IActivitySessionService>(sp =>
            sp.GetRequiredService<ScratchActivitySessions>());
        services.AddSingleton<SpeechVoicePreferences>();
        services.AddSingleton<VocabularyQuizPreferences>();
        services.AddSingleton<IPostLoginRouter, PostLoginRouter>();
        services.AddSingleton<INativeSyncReadiness>(Sync);
        services.AddSingleton<INativePlanCreation, ScratchPlanCreation>();
        services.AddSingleton<INativeQuizMedia>(Media);
        services.AddSingleton<INativeQuizChoices, ScratchChoices>();
        services.AddSingleton<INativeQuizEligibility, ScratchQuizEligibility>();
        services.AddSingleton<INativeQuizGrader, ScratchQuizGrader>();
        if (_ownerBoundReadiness)
            services.AddSingleton<IRemoteSyncCompletion>(RemoteSync);
        services.AddSingleton(sp =>
            new ScratchAttemptCommitter(_database,
                sp.GetRequiredService<IServiceScopeFactory>()));
        services.AddSingleton<INativeQuizAttemptCommitter>(sp =>
            sp.GetRequiredService<ScratchAttemptCommitter>());
        services.AddHttpClient("AuthClient", client =>
            client.BaseAddress = new Uri("https://api.example.invalid"))
            .ConfigurePrimaryHttpMessageHandler(() => _authHandler);
        services.AddSentenceStudioNativeIdentityAuth();
        services.AddSentenceStudioNativeSlice();
        if (_productionAdapters)
        {
            services.AddSentenceStudioNativeProductionAdapters();
            if (_ownerBoundReadiness)
                services.AddOwnerBoundNativeSyncReadiness();
            else
            {
                // No remote server runs in this isolated SQLite fixture.
                services.Replace(ServiceDescriptor.Singleton<INativeSyncReadiness>(Sync));
            }
        }
        else if (_ownerBoundReadiness)
            services.AddOwnerBoundNativeSyncReadiness();
        return services.BuildServiceProvider();
    }

    internal sealed class ScratchPreferences : IPreferencesService
    {
        private readonly ConcurrentDictionary<string, object> _values = new();
        public event Action<string, object?>? Written;

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
        public void Set<T>(string key, T value)
        {
            _values[key] = value!;
            Written?.Invoke(key, value);
        }
        public void Remove(string key) => _values.TryRemove(key, out _);
        public void Clear() => _values.Clear();
    }

    internal sealed class ScratchSecureStorage : ISecureStorageService
    {
        private readonly ConcurrentDictionary<string, string> _values = new();
        public Task<string?> GetAsync(string key) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
        public Task SetAsync(string key, string value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
        public bool Remove(string key) => _values.TryRemove(key, out _);
        public void RemoveAll() => _values.Clear();
    }

    private sealed class ScratchConnectivity : IConnectivityService
    {
        public bool Available { get; set; } = true;
        public bool IsInternetAvailable => Available;
    }

    private sealed class ScratchFiles(string path) : IFileSystemService
    {
        public string AppDataDirectory => path;
        public Task<Stream> OpenAppPackageFileAsync(string filename) =>
            throw new NotSupportedException("The synthetic quiz does not read packaged files.");
    }

    private sealed class NoLlmPlan : ILlmPlanGenerationService
    {
        public Task<DailyPlanResponse?> GeneratePlanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DailyPlanResponse?>(null);
    }

    private sealed class ScratchPlanGenerator : IDeterministicPlanGenerator
    {
        private readonly Dictionary<string, List<string>> _plans = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<(string Type, string? ResourceId, string? SkillId, int Minutes, int Priority)>> _activities =
            new(StringComparer.Ordinal);
        public bool? LastAllowWrites { get; private set; }

        public Task<PlanSkeleton?> GenerateAsync(
            PlanBuildRequest request, CancellationToken cancellationToken = default)
        {
            LastAllowWrites = request.AllowWrites;
            return GenerateAsync(request.UserProfileId, cancellationToken);
        }

        public void SetPlan(string userId, List<string> wordIds) =>
            _plans[userId] = wordIds;

        public void SetActivities(
            string userId,
            params (string Type, string? ResourceId, string? SkillId, int Minutes, int Priority)[] activities) =>
            _activities[userId] = activities.ToList();

        public Task<PlanSkeleton?> GenerateAsync(
            string? userProfileId = null, CancellationToken cancellationToken = default)
        {
            if (userProfileId is null || !_plans.TryGetValue(userProfileId, out var ids))
                return Task.FromResult<PlanSkeleton?>(null);
            var activities = _activities.GetValueOrDefault(userProfileId)
                ?? [(nameof(PlanActivityType.VocabularyReview), null, null, 10, 1)];
            return Task.FromResult<PlanSkeleton?>(new PlanSkeleton
            {
                Activities = activities.Select(activity =>
                    new PlannedActivity {
                        ActivityType = activity.Type,
                        ResourceId = activity.ResourceId,
                        SkillId = activity.SkillId,
                        FocusVocabularyIds = ids.ToList(),
                        EstimatedMinutes = activity.Minutes,
                        Priority = activity.Priority,
                        Rationale = "synthetic"
                    }).ToList(),
                FocusVocabularyIds = ids.ToList(),
                TotalMinutes = activities.Sum(activity => activity.Minutes),
                ResourceSelectionReason = "synthetic"
            });
        }
    }

    private sealed class ScratchAuthHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, string> _users = new();
        public HttpStatusCode? ForcedStatus { get; set; }
        public HttpStatusCode? ProfileStatus { get; set; }
        public string? TokenOwnerOverride { get; set; }
        public bool DuplicateOwnerClaim { get; set; }
        public Func<string, CancellationToken, Task>? BeforeLoginResponseAsync { get; set; }
        public int ProfileRequests { get; private set; }
        public bool Offline { get; set; }
        public void Register(string email, string userId) => _users[email] = userId;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
                throw new HttpRequestException("Synthetic API unavailable.");
            if (request.RequestUri?.AbsolutePath.StartsWith(
                    "/api/v1/profile/", StringComparison.Ordinal) == true)
            {
                ProfileRequests++;
                var owner = Uri.UnescapeDataString(
                    request.RequestUri.AbsolutePath["/api/v1/profile/".Length..]);
                if (ProfileStatus is { } profileStatus)
                    return new HttpResponseMessage(profileStatus);
                var tokenOwner = request.Headers.Authorization?.Parameter is { } bearer
                    ? new JwtSecurityTokenHandler().ReadJwtToken(bearer).Claims
                        .SingleOrDefault(claim => claim.Type == AuthClaimTypes.UserProfileId)?.Value
                    : null;
                return tokenOwner == owner && _users.Values.Contains(owner)
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new { id = owner })
                    }
                    : new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            if (ForcedStatus is { } status)
                return new HttpResponseMessage(status);
            if (request.RequestUri?.AbsolutePath != "/api/auth/login")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            var json = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            var email = json.RootElement.GetProperty("email").GetString();
            var password = json.RootElement.GetProperty("password").GetString();
            if (email is null || !_users.TryGetValue(email, out var userId)
                || password != "fixture-password")
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            if (BeforeLoginResponseAsync is { } beforeLogin)
                await beforeLogin(email, cancellationToken);
            var expiry = DateTime.UtcNow.AddHours(1);
            var ownerClaims = new List<Claim>
            {
                new(AuthClaimTypes.UserProfileId, TokenOwnerOverride ?? userId),
                new(ClaimTypes.Email, email)
            };
            if (DuplicateOwnerClaim)
                ownerClaims.Add(new Claim(AuthClaimTypes.UserProfileId, userId));
            var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                claims: ownerClaims,
                expires: expiry));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new {
                    token,
                    refreshToken = "fixture-refresh",
                    expiresAt = expiry,
                    userName = email,
                    userProfileId = userId
                })
            };
        }
    }
}
