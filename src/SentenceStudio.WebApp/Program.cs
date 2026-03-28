using ElevenLabs;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI;
using SentenceStudio;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Infrastructure;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebApp.Auth;
using SentenceStudio.WebApp.Components;
using SentenceStudio.WebApp.Platform;
using SentenceStudio.WebUI.Services;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

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
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
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

builder.Services.AddSingleton<IPreferencesService>(_ => new WebPreferencesService(preferencesPath));
builder.Services.AddSingleton<ISecureStorageService, WebSecureStorageService>();
builder.Services.AddSingleton<IConnectivityService, WebConnectivityService>();
builder.Services.AddSingleton<IFilePickerService, WebFilePickerService>();
builder.Services.AddSingleton<IAudioPlaybackService, WebAudioPlaybackService>();
builder.Services.AddSingleton<IFileSystemService>(_ => new WebFileSystemService(appDataRoot, appLibRawAssets));
builder.Services.AddSingleton(WebAudioManagerProxy.Create());
// No-op sync service — server doesn't sync to itself, but Blazor [Inject] requires registration
builder.Services.AddSingleton<SentenceStudio.Services.ISyncService>(
    new SentenceStudio.Services.NoOpSyncService());

var apiBaseUrl = builder.Configuration.GetValue<string>("ApiBaseUrl") ?? "https+http://api";
// Server-side IAuthService using Identity directly (UserManager + SignInManager)
builder.Services.AddScoped<IAuthService, ServerAuthService>();
builder.Services.AddTransient<AuthenticatedHttpMessageHandler>();
builder.Services.AddApiClients(new Uri(apiBaseUrl));
builder.Services.AddConversationAgentServices();

// OpenAI key — needed by ConversationAgentService which uses IChatClient directly for
// multi-turn conversation with ConversationMemory middleware. Removing this requires
// refactoring ConversationAgentService to use the gateway client instead.
var openAiApiKey = builder.Configuration["Settings:OpenAIKey"];
if (string.IsNullOrWhiteSpace(openAiApiKey))
{
    openAiApiKey = Environment.GetEnvironmentVariable("AI__OpenAI__ApiKey");
}
if (string.IsNullOrWhiteSpace(openAiApiKey))
{
    openAiApiKey = "not-configured";
}
builder.Configuration["Settings:OpenAIKey"] = openAiApiKey;
builder.Services
    .AddChatClient(new OpenAIClient(openAiApiKey).GetChatClient("gpt-4o-mini").AsIChatClient())
    .UseLogging();

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
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();

app.MapAccountEndpoints();

app.MapRazorComponents<App>()
    .AddAdditionalAssemblies(typeof(SentenceStudio.WebUI.Routes).Assembly)
    .AddInteractiveServerRenderMode();

app.Run();

static void RegisterSentenceStudioServices(IServiceCollection services)
{
    services.AddSentenceStudioCoreServices();
    services.AddBlazorUIServices();
    
    // Release notes service (reads from embedded resources)
    services.AddSingleton<ReleaseNotesService>();
}
