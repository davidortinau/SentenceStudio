using ElevenLabs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI;
using SentenceStudio;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Infrastructure;
using SentenceStudio.Services;
using SentenceStudio.Services.Theme;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebApp.Auth;
using SentenceStudio.WebApp.Components;
using SentenceStudio.WebApp.Platform;
using SentenceStudio.WebApp.Platform.Theme;
using SentenceStudio.WebUI.Services;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("SentenceStudio.WebApp");

// PostgreSQL requires UTC DateTimes — enable legacy mode for SQLite-era DateTime.Now values
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// WebApp uses Aspire-managed PostgreSQL directly (no local sync needed)
builder.AddNpgsqlDbContext<ApplicationDbContext>("sentencestudio", configureDbContextOptions: options =>
{
    options.ConfigureWarnings(w =>
        w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

// Preferences stay local to the webapp
var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
if (string.IsNullOrEmpty(localAppData))
    localAppData = Path.GetTempPath();
var appDataRoot = Path.Combine(localAppData, "sentencestudio", "webapp");
Directory.CreateDirectory(appDataRoot);
var preferencesPath = Path.Combine(appDataRoot, "preferences.json");

var appLibRawAssets = Path.GetFullPath(
    Path.Combine(builder.Environment.ContentRootPath, "..", "SentenceStudio.AppLib", "Resources", "Raw"));
if (!Directory.Exists(appLibRawAssets))
{
    appLibRawAssets = Path.Combine(builder.Environment.ContentRootPath, "Resources", "Raw");
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Localization — AppResources lives in SentenceStudio.Shared under Resources/Strings.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources/Strings");

var supportedCultures = new[] { new CultureInfo("en"), new CultureInfo("ko") };
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture("en");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
    // Cookie first (Profile save writes it); Accept-Language as fallback for new visitors.
    options.RequestCultureProviders.Clear();
    options.RequestCultureProviders.Add(new CookieRequestCultureProvider());
    options.RequestCultureProviders.Add(new AcceptLanguageHeaderRequestCultureProvider());
});

builder.Services.AddHttpContextAccessor();

// Identity cookie auth — ApplicationDbContext is registered below via AddDataServices.
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequiredLength = 8;
    options.Password.RequireUppercase = true;
    options.Password.RequireDigit = true;
    options.Password.RequireNonAlphanumeric = false;
    options.SignIn.RequireConfirmedEmail = false;
    options.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders()
.AddClaimsPrincipalFactory<SentenceStudio.WebApp.Auth.AppUserClaimsPrincipalFactory>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/auth/login";
    options.LogoutPath = "/account-action/SignOut";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;

    // Explicit, and different per environment, because the default (SameAsRequest) is wrong on
    // this host in both directions.
    //
    // In Development the app serves the SAME site on two origins — Aspire publishes
    // http://localhost:<p> and https://localhost:<q>, and UseHttpsRedirection is deliberately off
    // below — so SameAsRequest makes the session cookie scheme-locked to whichever origin the
    // learner happened to sign in on. Signing in over https writes a Secure cookie that the
    // browser will never send to the http origin, so a full-document navigation there arrives
    // anonymous and AuthorizeRouteView sends it to /auth/login. Interactive navigation stays on
    // the current origin and keeps working, which is exactly why this looked like "the dashboard
    // is fine but deep links sign me out". Cookies are not scheme-scoped otherwise, so dropping
    // Secure on loopback restores one session across both dev origins.
    //
    // Everywhere else it is Always, which is strictly stronger than SameAsRequest: the cookie can
    // never be issued without Secure, including on a request that reached the app over http
    // because a proxy terminated TLS and did not forward the scheme.
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.None
        : CookieSecurePolicy.Always;

    options.ExpireTimeSpan = TimeSpan.FromDays(90);
    options.SlidingExpiration = true;
});

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// Email sender — ConsoleEmailSender logs to Aspire structured logs in development;
// swap for SmtpEmailSender in production.
builder.Services.AddSingleton<IAppEmailSender, ConsoleEmailSender>();

// Webapp-only per-user state: captured per-circuit by CircuitUserStateHandler
// so the singleton WebPreferencesService can resolve active_profile_id during
// the Blazor InteractiveServer pass (HttpContext is null in that context).
// See CircuitUserStateAccessor.cs for the full pattern explanation.
builder.Services.AddSingleton<SentenceStudio.WebApp.Platform.CircuitUserStateAccessor>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler,
    SentenceStudio.WebApp.Platform.CircuitUserStateHandler>();

builder.Services.AddSingleton<IPreferencesService>(sp =>
    new WebPreferencesService(
        preferencesPath,
        sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
        sp.GetRequiredService<SentenceStudio.WebApp.Platform.CircuitUserStateAccessor>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<WebPreferencesService>()));
builder.Services.AddSingleton<ISecureStorageService>(sp =>
    new WebSecureStorageService(
        sp.GetRequiredService<IPreferencesService>(),
        sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<WebSecureStorageService>()));
builder.Services.AddSingleton<IConnectivityService, WebConnectivityService>();
builder.Services.AddScoped<IFilePickerService, WebFilePickerService>();
builder.Services.AddSingleton<IAudioPlaybackService, WebAudioPlaybackService>();
builder.Services.AddSingleton<IFileSystemService>(_ => new WebFileSystemService(appDataRoot, appLibRawAssets));
builder.Services.AddSingleton(WebAudioManagerProxy.Create());
// No-op sync service — server doesn't sync to itself, but Blazor [Inject] requires registration
builder.Services.AddSingleton<SentenceStudio.Services.ISyncService>(
    new SentenceStudio.Services.NoOpSyncService());
// Post-login routing decision (issue #187). Webapp also needs the router because
// MainLayout consults it on every authenticated render.
builder.Services.AddSingleton<SentenceStudio.Services.IPostLoginRouter, SentenceStudio.Services.PostLoginRouter>();

var apiBaseUrl = builder.Configuration.GetValue<string>("ApiBaseUrl") ?? "https+http://api";
// Server-side IAuthService using Identity directly (UserManager + SignInManager)
builder.Services.AddScoped<IAuthService, ServerAuthService>();
builder.Services.AddTransient<AuthenticatedHttpMessageHandler>();
builder.Services.AddApiClients(new Uri(apiBaseUrl));

// The Sam opportunity operator client. Development-only, matching the API routes it calls, which
// are not mapped outside Development at all. Registering it conditionally means the operator page
// cannot resolve it in any other environment even if its route registration survived — two
// independent mechanisms rather than one, because this client can put learner text on a screen.
if (builder.Environment.IsDevelopment())
{
    builder.Services
        .AddHttpClient<SentenceStudio.WebApp.Operator.SamOpportunityOperatorClient>(client =>
            client.BaseAddress = new Uri(apiBaseUrl))
        .AddHttpMessageHandler<AuthenticatedHttpMessageHandler>();
}

// OpenAI key — retained for the OpenAI audio (TTS) fallback path below.
var openAiApiKey = builder.Configuration["Settings:OpenAIKey"];
if (string.IsNullOrWhiteSpace(openAiApiKey))
{
    openAiApiKey = Environment.GetEnvironmentVariable("AI__OpenAI__ApiKey");
}
if (string.IsNullOrWhiteSpace(openAiApiKey))
{
    openAiApiKey = "not-configured";
}
// Resilient HttpClient for OpenAI — server defaults (AddServiceDefaults) provide
// Polly retry/circuit-breaker via ConfigureHttpClientDefaults.
builder.Services.AddResilientOpenAIHttpClient();

// openAiApiKey is retained only for the OpenAI audio (TTS) fallback path; chat → Foundry
// uses keyless Entra auth (DefaultAzureCredential) below.
builder.Configuration["Settings:OpenAIKey"] = openAiApiKey;

var aiEndpoint = builder.Configuration["AI:OpenAI:Endpoint"];
if (!string.IsNullOrWhiteSpace(aiEndpoint))
{
    // Default (fast) + keyed fast/reasoning chat clients via AzureOpenAIClient + Entra.
    var azureEndpoint = AiClientRegistration.AzureResourceEndpoint(builder.Configuration);
    builder.Services.AddTieredChatClients(builder.Configuration, sp =>
    {
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("openai");
        var options = new AzureOpenAIClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
        };
        return new AzureOpenAIClient(new Uri(azureEndpoint), new DefaultAzureCredential(), options);
    });
}

var elevenLabsKey = builder.Configuration["Settings:ElevenLabsKey"];
if (string.IsNullOrWhiteSpace(elevenLabsKey))
{
    elevenLabsKey = Environment.GetEnvironmentVariable("ElevenLabsKey");
}
if (string.IsNullOrWhiteSpace(elevenLabsKey))
{
    elevenLabsKey = "not-configured";
}
builder.Configuration["Settings:ElevenLabsKey"] = elevenLabsKey;
builder.Services.AddSingleton(new ElevenLabsClient(elevenLabsKey));

RegisterSentenceStudioServices(builder.Services);

builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
    options.Preload = true;
});

var app = builder.Build();

// Apply EF Core migrations at startup (once, not per-request)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();

    // Run vocabulary classification backfill (idempotent)
    var backfillService = scope.ServiceProvider.GetRequiredService<VocabularyClassificationBackfillService>();
    await backfillService.BackfillLexicalUnitTypesAsync();
    
    // Run phrase constituent backfill (idempotent, after classification)
    await backfillService.BackfillPhraseConstituentsAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Skip HTTPS redirect in development — Aspire may terminate TLS at the proxy.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseSecurityHeaders();
// Apply request localization BEFORE static assets / auth / routing so all downstream
// components see the correct CurrentUICulture.
app.UseRequestLocalization();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();

app.MapAccountEndpoints();

app.MapRazorComponents<App>()
    .AddAdditionalAssemblies(typeof(SentenceStudio.WebUI.Routes).Assembly)
    .AddInteractiveServerRenderMode()
    .AllowAnonymous();

app.Run();

static void RegisterSentenceStudioServices(IServiceCollection services)
{
    services.AddSentenceStudioCoreServices();
    services.AddBlazorUIServices(useCircuitScopedActivityTimer: true);

    // Appearance state, browser-scoped. Scoped, not singleton: a Blazor circuit is a DI scope, so
    // each browser gets its own theme/mode/text-size tuple and its own ThemeChanged invocation
    // list. Registered as a singleton this was one process-wide object shared by every signed-in
    // learner — one person switching theme moved and repainted everybody else's browser.
    // Persistence is the per-browser ss_appearance cookie, which the SSR pass can read
    // synchronously off the request and the circuit reads back through JS interop.
    services.AddBrowserAppearance();
    
    // Release notes service (reads from embedded resources)
    services.AddSingleton<ReleaseNotesService>();

    // Override the device-local IPlanDateContext (from CoreServiceExtensions) with a
    // user-profile-backed resolver. On Azure, TimeZoneInfo.Local = UTC which is wrong
    // for plan-date keying. Reads the authenticated user's persisted IanaTimeZoneId
    // from the database. Mirrors the API's HttpPlanDateContext but sources timezone
    // from UserProfile rather than an HTTP header.
    //
    // Registered Transient (NOT Scoped) to match AppLib's device registration: the
    // singleton plan services (GeneratedPlanValidator captures it in its ctor;
    // ProgressService/DeterministicPlanBuilder resolve it from the ROOT provider)
    // cannot consume a scoped service — DI validation fails at startup and root
    // resolution throws at runtime. Transient is safe here because the current user
    // is resolved via CircuitUserStateAccessor (AsyncLocal, ambient across scopes),
    // not via the DI scope, so each construction still sees the right user.
    services.AddTransient<SentenceStudio.Services.Plans.IPlanDateContext,
        SentenceStudio.WebApp.Platform.WebAppPlanDateContext>();

    // Service for persisting the browser-reported IANA timezone to UserProfile.
    // Called from a Blazor component on first interactive circuit connect via JS interop.
    services.AddScoped<SentenceStudio.WebApp.Platform.TimeZoneCaptureService>();
}

/// <summary>
/// Entry-point handle for <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
/// <remarks>
/// Top-level statements generate an internal <c>Program</c>, which the test host cannot bind to.
/// Declaring the partial publicly is the documented way to make the real pipeline — real
/// authentication, real authorization, real endpoint routing — testable end to end, rather than
/// re-declaring a lookalike pipeline in the test project that can drift from this one.
/// </remarks>
public partial class Program;
