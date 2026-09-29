using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Services;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;

var api = NativeApiEndpoint.Validate(
    Environment.GetEnvironmentVariable("NATIVE_IOS_TEST_API_URL")
        ?? throw new InvalidOperationException("An isolated API URL is required."),
    allowLocalHttp: true);
var email = Environment.GetEnvironmentVariable("NATIVE_IOS_TEST_EMAIL")
    ?? throw new InvalidOperationException("The canonical test account email is required.");
var password = Environment.GetEnvironmentVariable("NATIVE_IOS_TEST_PASSWORD")
    ?? throw new InvalidOperationException("The canonical test account password is required.");
var database = Environment.GetEnvironmentVariable("NATIVE_IOS_TEST_DATABASE")
    ?? throw new InvalidOperationException("An isolated new SQLite path is required.");
if (File.Exists(database))
    throw new InvalidOperationException("Live API smoke refuses to overwrite an existing database.");
Directory.CreateDirectory(Path.GetDirectoryName(database)
    ?? throw new InvalidOperationException("The SQLite path has no directory."));

var preferences = new SmokePreferences();
var keychain = new SmokeSecureStorage();
using var services = Build();
using (var scope = services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    if (!await db.Database.EnsureCreatedAsync())
        throw new InvalidOperationException("A brand-new scratch database was expected.");
}

var auth = services.GetRequiredService<NativeAuthPresenter>();
var initial = await auth.RestoreAsync();
Require(initial.Destination == NativeDestination.SignIn,
    "First launch must not restore an unrelated session.");
var loggedIn = await auth.SignInAsync(email, password);
Require(loggedIn is { Phase: NativePhase.Ready, Destination: NativeDestination.Dashboard }
    && !string.IsNullOrWhiteSpace(loggedIn.UserId),
    $"Live sign-in failed: {loggedIn.Phase}/{loggedIn.Destination}/{loggedIn.ErrorKey}");
var userId = loggedIn.UserId!;
Require(preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty) == userId,
    "The active local scope did not match the API token claim.");
using (var scope = services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    Require(await db.UserProfiles.CountAsync() == 1
        && await db.UserProfiles.AnyAsync(profile =>
            profile.Id == userId && profile.Email == email
            && !string.IsNullOrEmpty(profile.TargetLanguage)),
        "An authenticated owner-scoped profile was not stored in real SQLite.");
}
var plan = await services.GetRequiredService<NativeDashboardPresenter>().LoadAsync();
Require(plan.Phase is NativePhase.Empty or NativePhase.Ready,
    $"Dashboard did not read the isolated owner-scoped SQLite store: {plan.Phase}");
Console.WriteLine("LIVE_API_SQLITE_SIGNIN_OWNER_SCOPED_PASS");

using var restartedServices = Build();
var restored = await restartedServices.GetRequiredService<NativeAuthPresenter>().RestoreAsync();
Require(restored is { Phase: NativePhase.Ready, Destination: NativeDestination.Dashboard }
    && restored.UserId == userId,
    "Cold service-provider restart did not restore the same authenticated owner.");
Require(await restartedServices.GetRequiredService<NativeDashboardPresenter>()
    .LoadAsync() is { Phase: NativePhase.Empty or NativePhase.Ready },
    "Dashboard did not reload owner-scoped data after restart.");
Console.WriteLine("LIVE_API_SQLITE_RESTART_PASS");

ServiceProvider Build()
{
    var registrations = new ServiceCollection();
    registrations.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
    registrations.AddDbContext<ApplicationDbContext>(
        options => options.UseSqlite($"Data Source={database};Pooling=False"));
    registrations.AddSingleton<IPreferencesService>(preferences);
    registrations.AddSingleton<ISecureStorageService>(keychain);
    registrations.AddSingleton<IConnectivityService>(new SmokeConnectivity());
    registrations.AddSingleton<IFileSystemService>(
        new SmokeFileSystem(Path.GetDirectoryName(database)!));
    registrations.AddSingleton<ISyncService, NoOpSyncService>();
    registrations.AddSingleton<DeviceUserScopeProvider>();
    registrations.AddSingleton<IUserScopeProvider>(provider =>
        provider.GetRequiredService<DeviceUserScopeProvider>());
    registrations.AddTransient<IPlanDateContext>(_ => new PlanDateContext(TimeZoneInfo.Local));
    registrations.AddSingleton<UserProfileRepository>();
    registrations.AddSingleton<LearningResourceRepository>();
    registrations.AddSingleton<SkillProfileRepository>();
    registrations.AddSingleton<UserActivityRepository>();
    registrations.AddSingleton<VocabularyProgressRepository>();
    registrations.AddSingleton<VocabularyLearningContextRepository>();
    registrations.AddSingleton<VocabularyProgressService>();
    registrations.AddSingleton<IVocabularyProgressService>(provider =>
        provider.GetRequiredService<VocabularyProgressService>());
    registrations.AddSingleton<ProgressCacheService>();
    registrations.AddSingleton<IProgressService, ProgressService>();
    registrations.AddSingleton<DeterministicPlanBuilder>();
    registrations.AddSingleton<IDeterministicPlanGenerator, DeterministicPlanGenerator>();
    registrations.AddSingleton<IPostLoginRouter, PostLoginRouter>();
    registrations.AddSingleton<VocabQuizLaunchValidator>();
    registrations.AddSingleton<ActivitySessionService>();
    registrations.AddSingleton<IActivitySessionService>(provider =>
        provider.GetRequiredService<ActivitySessionService>());
    registrations.AddSingleton<SpeechVoicePreferences>();
    registrations.AddSingleton<VocabularyQuizPreferences>();
    registrations.AddHttpClient("AuthClient", client => client.BaseAddress = api);
    registrations.AddSingleton<NativeProfileBootstrap>();
    registrations.AddSingleton<IdentityAuthService>(provider =>
        new IdentityAuthService(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<ISecureStorageService>(),
            provider.GetRequiredService<IPreferencesService>(),
            provider.GetRequiredService<ILogger<IdentityAuthService>>()));
    registrations.AddSingleton<IAuthService, NativeProfileAuthService>();
    registrations.AddSentenceStudioNativeSlice();
    return registrations.BuildServiceProvider(validateScopes: true);
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

internal sealed class SmokeConnectivity : IConnectivityService
{
    public bool IsInternetAvailable => true;
}

internal sealed class SmokeFileSystem(string appDataDirectory) : IFileSystemService
{
    public string AppDataDirectory => appDataDirectory;
    public Task<Stream> OpenAppPackageFileAsync(string filename) =>
        throw new FileNotFoundException("The live API smoke has no bundled media.", filename);
}

internal sealed class SmokePreferences : IPreferencesService
{
    private readonly ConcurrentDictionary<string, object> _values = new();
    public T Get<T>(string key, T defaultValue) =>
        _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
    public void Set<T>(string key, T value) => _values[key] = value!;
    public void Remove(string key) => _values.TryRemove(key, out _);
    public void Clear() => throw new NotSupportedException("Bulk preference clearing is not part of the smoke.");
}

internal sealed class SmokeSecureStorage : ISecureStorageService
{
    private readonly ConcurrentDictionary<string, string> _values = new();
    public Task<string?> GetAsync(string key) =>
        Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
    public Task SetAsync(string key, string value)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }
    public bool Remove(string key)
    {
        _values.TryRemove(key, out _);
        return !_values.ContainsKey(key);
    }
    public void RemoveAll() =>
        throw new NotSupportedException("Bulk token clearing is not part of the smoke.");
}
