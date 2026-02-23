# SentenceStudio Multi-Project Restructuring Plan

> **Goal:** Break the monolithic `SentenceStudio` project into a multi-project architecture that supports shared Blazor UI across MAUI Hybrid and Web, per-platform head projects, and future platforms (native macOS via Platform.Maui.MacOS, tvOS).

---

## 1. Target Project Structure

```
src/
├── SentenceStudio.UI/                    # NEW — Razor Class Library (RCL)
│   │                                      # net10.0 (no MAUI dependency)
│   ├── Pages/                             # All 27 Blazor pages (from WebUI/Pages/)
│   ├── Shared/                            # Shared Blazor components (ActivityTimer, AudioPlayer, etc.)
│   ├── Layout/                            # MainLayout.razor, NavMenu.razor
│   ├── Services/                          # Blazor UI services (Toast, Modal, Navigation, JsInterop)
│   ├── Routes.razor
│   ├── _Imports.razor
│   ├── wwwroot/                           # index.html, css/, js/
│   └── SentenceStudio.UI.csproj
│
├── SentenceStudio.Shared/                # EXISTING — Enhanced domain library
│   │                                      # net10.0 (no MAUI dependency)
│   ├── Models/                            # All domain models (existing + moved from monolith)
│   ├── Data/                              # EF Core DbContext, migrations
│   ├── Repositories/                      # All repositories (existing + moved from monolith)
│   ├── Services/                          # Pure .NET services (AI, language, progress, plan gen)
│   │   ├── LanguageSegmentation/          # All segmenters
│   │   ├── Progress/                      # ProgressService, ProgressCacheService
│   │   ├── PlanGeneration/                # DeterministicPlanBuilder, LlmPlanGenerationService
│   │   ├── Agents/                        # ConversationAgentService
│   │   ├── DTOs/                          # All DTO classes
│   │   ├── Timer/                         # ActivityTimerService
│   │   └── (individual services)          # AiService, TeacherService, etc.
│   ├── Common/                            # Constants, localization core, extensions
│   ├── Abstractions/                      # NEW — Platform abstraction interfaces
│   │   ├── IFileSystemService.cs
│   │   ├── IPreferencesService.cs
│   │   ├── ISecureStorageService.cs
│   │   ├── IAudioPlaybackService.cs
│   │   └── IFilePickerService.cs
│   ├── Helpers/
│   ├── Converters/
│   └── SentenceStudio.Shared.csproj
│
├── SentenceStudio.AppLib/                # NEW — MAUI App Library
│   │                                      # net10.0 (MAUI class library, no single-TFM)
│   ├── MauiProgramExtensions.cs           # UseSharedMauiApp() extension method
│   ├── BlazorHostPage.cs                  # BlazorWebView host page
│   ├── App.cs                             # Application class
│   ├── AppShell.cs                        # Minimal shell (no flyout, just Blazor container)
│   ├── Platforms/                          # Platform service implementations
│   │   ├── MauiFileSystemService.cs       # FileSystem.AppDataDirectory, OpenAppPackageFileAsync
│   │   ├── MauiPreferencesService.cs      # Preferences.Get/Set/Remove
│   │   ├── MauiSecureStorageService.cs    # SecureStorage wrapper
│   │   ├── MauiAudioPlaybackService.cs    # Plugin.Maui.Audio wrapper
│   │   └── MauiFilePickerService.cs       # LukeMauiFilePicker wrapper
│   ├── Handlers/                          # Handler customizations (Entry, Picker, WebView)
│   ├── Resources/                         # Fonts, images, raw assets, strings, themes
│   │   ├── Fonts/
│   │   ├── Images/
│   │   ├── Raw/                           # Scriban templates, prompt files
│   │   ├── Strings/                       # .resx localization files
│   │   └── Themes/
│   ├── Themes/                            # MyTheme.cs, ApplicationTheme.Icons.cs
│   └── SentenceStudio.AppLib.csproj
│
├── SentenceStudio.Platforms.MacCatalyst/  # NEW — Mac Catalyst head
│   │                                      # net10.0-maccatalyst only
│   ├── MauiProgram.cs                     # builder.UseSharedMauiApp() + Mac-specific config
│   ├── AppDelegate.cs
│   ├── Info.plist
│   ├── Entitlements.plist
│   └── SentenceStudio.Platforms.MacCatalyst.csproj
│
├── SentenceStudio.Platforms.iOS/          # NEW — iOS head
│   │                                      # net10.0-ios only
│   ├── MauiProgram.cs
│   ├── AppDelegate.cs
│   ├── Info.plist
│   └── SentenceStudio.Platforms.iOS.csproj
│
├── SentenceStudio.Platforms.Android/      # NEW — Android head
│   │                                      # net10.0-android only
│   ├── MauiProgram.cs
│   ├── MainActivity.cs
│   ├── MainApplication.cs
│   ├── AndroidManifest.xml
│   └── SentenceStudio.Platforms.Android.csproj
│
├── SentenceStudio.Platforms.Windows/      # NEW — Windows head (when ready)
│   │                                      # net10.0-windows10.0.19041.0
│   ├── MauiProgram.cs
│   ├── App.xaml / App.xaml.cs
│   ├── Package.appxmanifest
│   └── SentenceStudio.Platforms.Windows.csproj
│
├── SentenceStudio.Web/                    # EXISTING — Enhanced Blazor web app
│   │                                      # net10.0 (ASP.NET Core)
│   ├── Program.cs                         # Web-specific DI (registers web impls of abstractions)
│   ├── Services/                          # Web platform implementations
│   │   ├── WebFileSystemService.cs
│   │   ├── WebPreferencesService.cs       # localStorage via JS interop
│   │   └── WebAudioPlaybackService.cs     # HTML5 Audio API
│   └── SentenceStudio.Web.csproj
│
├── SentenceStudio.AppHost/               # EXISTING — Aspire orchestration
├── SentenceStudio.ServiceDefaults/        # EXISTING
├── SentenceStudio.WebServiceDefaults/     # EXISTING
└── SentenceStudio.sln
```

### Dependency Graph

```
Platform Heads (MacCatalyst, iOS, Android, Windows)
    └─► SentenceStudio.AppLib (MAUI app library)
            ├─► SentenceStudio.UI (RCL — Blazor pages & components)
            │       └─► SentenceStudio.Shared (models, services, repos, abstractions)
            └─► SentenceStudio.Shared

SentenceStudio.Web (Blazor web)
    ├─► SentenceStudio.UI (RCL)
    └─► SentenceStudio.Shared

Future: SentenceStudio.Platforms.MacOS (AppKit via Platform.Maui.MacOS)
    └─► SentenceStudio.AppLib (or directly → Shared, depending on AppKit needs)
```

---

## 2. What Moves Where from the Current Monolith

### → `SentenceStudio.UI` (New RCL)

| Current Location | Files | Notes |
|---|---|---|
| `WebUI/Pages/` | 27 .razor files | All Blazor pages |
| `WebUI/Shared/` | ActivityTimer, AudioPlayer, InteractiveText, PageHeader, ToastContainer, WaveformDisplay | Shared components |
| `WebUI/Layout/` | MainLayout.razor, NavMenu.razor | Layout system |
| `WebUI/Services/` | BlazorLocalizationService, BlazorNavigationService, JsInteropService, ModalService, NavigationMemoryService, ToastService | Blazor UI services |
| `WebUI/Routes.razor` | 1 file | Router config |
| `WebUI/_Imports.razor` | 1 file | Needs namespace updates |
| `wwwroot/` | index.html, css/app.css, js/app.js | Static assets |

### → `SentenceStudio.Shared` (Enhanced — move pure .NET code here)

| Current Location | Files | Notes |
|---|---|---|
| `Services/` (pure .NET) | AiService, TeacherService, ConversationService, ClozureService, TranslationService, StorytellerService, ShadowingService, VideoWatchingService, SceneImageService, YouTubeImportService, DataExportService, NameGenerationService, VocabularyProgressService, VocabularyExampleGenerationService, VocabularyFilterService, SmartResourceService, TranscriptFormattingService, TranscriptSentenceExtractor, ScenarioService, SyncService | After abstracting platform deps |
| `Services/LanguageSegmentation/` | All 5 segmenters + factory | Already pure .NET |
| `Services/Progress/` | ProgressCacheService, ProgressService | Already pure .NET |
| `Services/PlanGeneration/` | DeterministicPlanBuilder, LlmPlanGenerationService | Already pure .NET |
| `Services/Timer/` | ActivityTimerService | Already pure .NET |
| `Services/Agents/` | ConversationAgentService, VocabularyLookupTool | Already pure .NET |
| `Services/DTOs/` | All DTOs | Already pure .NET |
| `Services/Speech/` | VoiceDiscoveryService | Needs abstraction for audio |
| `Data/` | All 12 repositories | Move alongside existing 2 repos |
| `Models/` | AppState, ConversationParticipant, FilterChip, etc. | All 8 files |
| `Common/` | Constants, LocalizationManager, ServiceExtensions | Core utilities |
| `Helpers/` | All helpers | Pure .NET |
| `Converters/` | All converters | Pure .NET |
| `Services/` (preferences) | VocabularyQuizPreferences, SpeechVoicePreferences, ThemeService | After abstracting Preferences calls |
| `ServiceCollectionExtentions.cs` | CoreSync + agent DI extensions | Shared DI setup |

### → `SentenceStudio.AppLib` (New MAUI app library)

| Current Location | Files | Notes |
|---|---|---|
| `BlazorApp.cs` | 1 file | MAUI Application subclass |
| `BlazorHostPage.cs` | 1 file | BlazorWebView host |
| `App.cs` | 1 file | If still needed (likely replaced by BlazorApp) |
| `AppShell.cs` | 1 file | Minimal shell container |
| `MauiProgram.cs` | Becomes `MauiProgramExtensions.cs` | Shared MAUI setup |
| `Resources/` | Fonts, Images, Raw, Strings, Themes | All resources |
| `Themes/` | MyTheme.cs, ApplicationTheme.Icons.cs | App theming |
| Handler customizations | ModifyEntry, ModifyPicker, ConfigureWebView | Platform handlers |
| Platform service impls | NEW — MauiFileSystemService, etc. | Implement abstractions |

### → Platform Heads (New per-platform projects)

| Current Location | Destination | Notes |
|---|---|---|
| `Platforms/Android/` | `SentenceStudio.Platforms.Android/` | MainActivity, MainApplication, AndroidManifest |
| `Platforms/iOS/` | `SentenceStudio.Platforms.iOS/` | AppDelegate, Info.plist |
| `Platforms/MacCatalyst/` | `SentenceStudio.Platforms.MacCatalyst/` | AppDelegate, Info.plist, Entitlements |
| `Platforms/Windows/` | `SentenceStudio.Platforms.Windows/` | App.xaml, Package.appxmanifest |

### Deleted / Not Migrated

| Current Location | Reason |
|---|---|
| `Pages/` (48 MauiReactor pages) | Legacy — being replaced by Blazor |
| `Components/` (3 MauiReactor components) | Legacy — replaced by Blazor equivalents |
| `Common/Layouts/`, `Common/ReactorCustomLayouts/` | Legacy MauiReactor layouts |
| `Platforms/Tizen/` | Not a target platform |

---

## 3. Shared MAUI Setup (MauiProgramExtensions)

The `MauiProgramExtensions.cs` in `SentenceStudio.AppLib` follows the maui-alltheprojects pattern:

```csharp
// SentenceStudio.AppLib/MauiProgramExtensions.cs
namespace SentenceStudio;

public static class MauiProgramExtensions
{
    public static MauiAppBuilder UseSharedMauiApp(this MauiAppBuilder builder)
    {
        builder
            .UseMauiApp<BlazorApp>()
            .UseMauiCommunityToolkit()
            .AddAudio(playbackOptions => { /* shared config */ },
                      recordingOptions => { /* shared config */ })
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("Segoe-Ui-Bold.ttf", "SegoeBold");
                fonts.AddFont("Segoe-Ui-Regular.ttf", "SegoeRegular");
                // ... all fonts
            })
            .ConfigureMauiHandlers(handlers =>
            {
                HandlerCustomizations.Apply();
            })
            .ConfigureFilePicker(100);

        // Blazor WebView
        builder.Services.AddMauiBlazorWebView();

        // Platform abstraction implementations (MAUI-specific)
        builder.Services.AddSingleton<IFileSystemService, MauiFileSystemService>();
        builder.Services.AddSingleton<IPreferencesService, MauiPreferencesService>();
        builder.Services.AddSingleton<ISecureStorageService, MauiSecureStorageService>();
        builder.Services.AddSingleton<IAudioPlaybackService, MauiAudioPlaybackService>();
        builder.Services.AddSingleton<IFilePickerService, MauiFilePickerService>();

        // All shared services (from SentenceStudio.Shared)
        builder.Services.AddSentenceStudioServices(builder.Configuration);

        // Blazor UI services (from SentenceStudio.UI)
        builder.Services.AddSentenceStudioUI();

        // CoreSync
        var dbPath = Constants.DatabasePath;
        builder.Services.AddDataServices(dbPath);
        builder.Services.AddSyncServices(dbPath, /* server URI */);

        return builder;
    }
}
```

Each platform head's `MauiProgram.cs` is minimal:

```csharp
// SentenceStudio.Platforms.MacCatalyst/MauiProgram.cs
namespace SentenceStudio;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseSharedMauiApp();

        // Mac Catalyst-specific configuration
        builder.AddAudio(playbackOptions =>
        {
            playbackOptions.Category = AVFoundation.AVAudioSessionCategory.Playback;
        }, recordingOptions =>
        {
            recordingOptions.Category = AVFoundation.AVAudioSessionCategory.Record;
            recordingOptions.Mode = AVFoundation.AVAudioSessionMode.Default;
        });

#if DEBUG
        builder.Logging.AddDebug().AddConsole().SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.AddMauiDevFlowAgent(options => { options.Port = 9223; });
#endif

        return builder.Build();
    }
}
```

---

## 4. Blazor UI Sharing (RCL Pattern)

### `SentenceStudio.UI.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <StaticWebAssetBasePath>/</StaticWebAssetBasePath>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\SentenceStudio.Shared\SentenceStudio.Shared.csproj" />
  </ItemGroup>

  <!-- No MAUI dependencies — pure Razor + Blazor -->
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.Web" />
  </ItemGroup>
</Project>
```

### How consumers reference it

**MAUI (AppLib):**
```xml
<ProjectReference Include="..\SentenceStudio.UI\SentenceStudio.UI.csproj" />
```
The `BlazorHostPage` in AppLib sets `HostPage = "wwwroot/index.html"` and the RCL's wwwroot is automatically served.

**Web (SentenceStudio.Web):**
```xml
<ProjectReference Include="..\SentenceStudio.UI\SentenceStudio.UI.csproj" />
```
In `Program.cs`, map the Blazor components:
```csharp
app.MapRazorComponents<SentenceStudio.UI.Routes>()
   .AddInteractiveServerRenderMode();
```

### DI Registration Extension in RCL

```csharp
// SentenceStudio.UI/DependencyInjection.cs
namespace SentenceStudio.UI;

public static class DependencyInjection
{
    public static IServiceCollection AddSentenceStudioUI(this IServiceCollection services)
    {
        services.AddSingleton<ToastService>();
        services.AddSingleton<ModalService>();
        services.AddSingleton<BlazorLocalizationService>();
        services.AddSingleton<BlazorNavigationService>();
        services.AddScoped<NavigationMemoryService>();
        services.AddScoped<JsInteropService>();
        return services;
    }
}
```

---

## 5. Platform Head Structure

Each platform head is a **thin shell** — minimal code, single TFM, delegates to AppLib.

### Mac Catalyst Head

```
SentenceStudio.Platforms.MacCatalyst/
├── MauiProgram.cs              # builder.UseSharedMauiApp() + Mac-specific audio config
├── AppDelegate.cs              # [Register("AppDelegate")] : MauiUIApplicationDelegate
├── Info.plist                  # Bundle ID, capabilities, permissions
├── Entitlements.plist          # App Sandbox, Keychain, etc.
└── SentenceStudio.Platforms.MacCatalyst.csproj
```

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-maccatalyst</TargetFramework>
    <OutputType>Exe</OutputType>
    <ApplicationId>com.sentencestudio.app</ApplicationId>
    <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
    <ApplicationVersion>1</ApplicationVersion>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\SentenceStudio.AppLib\SentenceStudio.AppLib.csproj" />
  </ItemGroup>
</Project>
```

### iOS Head — Same pattern, `net10.0-ios` TFM

### Android Head — Same pattern, `net10.0-android` TFM, with `MainActivity.cs` and `MainApplication.cs`

### Windows Head — Same pattern, `net10.0-windows10.0.19041.0` TFM

### Future: Native macOS (Platform.Maui.MacOS)

When ready, create `SentenceStudio.Platforms.MacOS/` targeting AppKit. This head:
- References `SentenceStudio.AppLib` (for shared setup, resources, platform service impls)
- OR references `SentenceStudio.Shared` + `SentenceStudio.UI` directly if AppKit doesn't use MAUI BlazorWebView
- Provides its own WKWebView host for the Blazor content if needed

---

## 6. SentenceStudio.Shared vs AppLib vs RCL — Boundary Rules

| Layer | TFM | Contains | Depends On |
|---|---|---|---|
| **SentenceStudio.Shared** | `net10.0` | Domain models, EF Core, repositories, pure .NET services, platform abstraction **interfaces**, DTOs, language segmenters, AI services, progress/plan services, constants, localization core | NuGet: EF Core, Microsoft.Extensions.AI, ElevenLabs SDK, SkiaSharp (if needed), CoreSync |
| **SentenceStudio.UI** | `net10.0` | Blazor pages, components, layouts, routes, Blazor UI services, wwwroot assets | `SentenceStudio.Shared`, NuGet: ASP.NET Components.Web |
| **SentenceStudio.AppLib** | `net10.0` (MAUI class lib, multi-TFM) | MAUI app class, BlazorHostPage, AppShell, MauiProgramExtensions, platform abstraction **implementations**, handler customizations, resources (fonts/images/raw/strings), themes | `SentenceStudio.Shared`, `SentenceStudio.UI`, NuGet: MAUI, CommunityToolkit.Maui, Plugin.Maui.Audio, LukeMauiFilePicker, CoreSync, Shiny |
| **Platform Heads** | Single TFM each | MauiProgram.cs (thin), platform entry points, platform-specific config (plists, manifests) | `SentenceStudio.AppLib` |
| **SentenceStudio.Web** | `net10.0` | ASP.NET host, web platform implementations of abstractions, Program.cs | `SentenceStudio.Shared`, `SentenceStudio.UI` |

### Decision Framework: "Where does this code go?"

```
Is it a Blazor .razor file or Blazor UI service?
  → SentenceStudio.UI (RCL)

Is it a domain model, repository, or pure .NET service?
  → SentenceStudio.Shared

Does it call MAUI APIs (FileSystem, Preferences, etc.)?
  → Define interface in SentenceStudio.Shared/Abstractions/
  → Implement in SentenceStudio.AppLib/Platforms/ (for MAUI)
  → Implement in SentenceStudio.Web/Services/ (for web)

Is it MAUI app infrastructure (App, Shell, handlers, resources)?
  → SentenceStudio.AppLib

Is it platform-specific entry point code?
  → Platform head project
```

---

## 7. Handling Services That Depend on MAUI Platform APIs

### Abstraction Interfaces (in `SentenceStudio.Shared/Abstractions/`)

```csharp
// IFileSystemService.cs
public interface IFileSystemService
{
    string AppDataDirectory { get; }
    string CacheDirectory { get; }
    Task<Stream> OpenAppPackageFileAsync(string filename);
    Task<bool> AppPackageFileExistsAsync(string filename);
}

// IPreferencesService.cs
public interface IPreferencesService
{
    T Get<T>(string key, T defaultValue);
    void Set<T>(string key, T value);
    bool ContainsKey(string key);
    void Remove(string key);
    void Clear();
}

// ISecureStorageService.cs
public interface ISecureStorageService
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    bool Remove(string key);
    void RemoveAll();
}

// IAudioPlaybackService.cs
public interface IAudioPlaybackService
{
    Task<IAudioPlayerHandle> CreatePlayerAsync(Stream audioStream);
    Task<IAudioPlayerHandle> CreatePlayerAsync(string filePath);
}

public interface IAudioPlayerHandle : IDisposable
{
    void Play();
    void Pause();
    void Stop();
    double Duration { get; }
    double CurrentPosition { get; }
    bool IsPlaying { get; }
    event EventHandler? PlaybackEnded;
}

// IFilePickerService.cs
public interface IFilePickerService
{
    Task<FilePickerResult?> PickFileAsync(IEnumerable<string>? allowedTypes = null);
}

public record FilePickerResult(string FileName, string FullPath, Func<Task<Stream>> OpenReadAsync);
```

### MAUI Implementations (in `SentenceStudio.AppLib/Platforms/`)

```csharp
// MauiFileSystemService.cs
public class MauiFileSystemService : IFileSystemService
{
    public string AppDataDirectory => FileSystem.AppDataDirectory;
    public string CacheDirectory => FileSystem.CacheDirectory;
    public Task<Stream> OpenAppPackageFileAsync(string filename)
        => FileSystem.OpenAppPackageFileAsync(filename);
    public Task<bool> AppPackageFileExistsAsync(string filename)
        => FileSystem.AppPackageFileExistsAsync(filename);
}

// MauiPreferencesService.cs
public class MauiPreferencesService : IPreferencesService
{
    public T Get<T>(string key, T defaultValue) => Preferences.Get(key, defaultValue);
    public void Set<T>(string key, T value) => Preferences.Set(key, value);
    public bool ContainsKey(string key) => Preferences.ContainsKey(key);
    public void Remove(string key) => Preferences.Remove(key);
    public void Clear() => Preferences.Clear();
}
```

### Web Implementations (in `SentenceStudio.Web/Services/`)

```csharp
// WebPreferencesService.cs — uses localStorage via JS interop
public class WebPreferencesService : IPreferencesService
{
    private readonly IJSRuntime _jsRuntime;
    // ... wraps localStorage.getItem/setItem
}

// WebFileSystemService.cs — uses server-side file paths
public class WebFileSystemService : IFileSystemService
{
    public string AppDataDirectory => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.ApplicationData), "SentenceStudio");
    // ...
}
```

### Migration Strategy for Existing Services

Services that currently call `FileSystem.AppDataDirectory` or `Preferences.Get()` directly get refactored to accept the abstraction interface via constructor injection:

```csharp
// BEFORE (in monolith):
public class AudioCacheManager
{
    private readonly string _cacheDir = Path.Combine(FileSystem.AppDataDirectory, "audio_cache");
    // ...
}

// AFTER (in SentenceStudio.Shared):
public class AudioCacheManager
{
    private readonly string _cacheDir;

    public AudioCacheManager(IFileSystemService fileSystem)
    {
        _cacheDir = Path.Combine(fileSystem.AppDataDirectory, "audio_cache");
    }
}
```

### Services Requiring Abstraction (Priority Order)

| Priority | Service(s) | API Used | Abstraction |
|---|---|---|---|
| **P0** | AudioCacheManager, ElevenLabsSpeechService, ~10 page code-behinds | `FileSystem.AppDataDirectory`, `OpenAppPackageFileAsync` | `IFileSystemService` |
| **P0** | ThemeService, VocabularyQuizPreferences, SpeechVoicePreferences, AppShell, ~15 pages | `Preferences.Get/Set` | `IPreferencesService` |
| **P1** | ConversationPage, VocabularyQuizPage, ReadingPage, ShadowingPage | `Plugin.Maui.Audio` | `IAudioPlaybackService` |
| **P2** | AddLearningResourcePage, EditLearningResourcePage | `LukeMauiFilePicker` | `IFilePickerService` |
| **P3** | (if any secure storage usage found) | `SecureStorage` | `ISecureStorageService` |

---

## 8. Migration Strategy (Order of Operations)

### Phase 0: Preparation (Non-Breaking)
**Goal:** Set up project scaffolding without moving any code yet.

1. Create empty `SentenceStudio.UI` RCL project, add to solution
2. Create empty `SentenceStudio.AppLib` MAUI class library, add to solution
3. Create empty platform head projects (start with MacCatalyst only), add to solution
4. Add project references between them (per dependency graph above)
5. Verify solution builds with empty projects

### Phase 1: Platform Abstractions (Non-Breaking)
**Goal:** Define and implement abstractions so services can be decoupled.

1. Create `SentenceStudio.Shared/Abstractions/` with all 5 interfaces
2. Create MAUI implementations in `SentenceStudio.AppLib/Platforms/`
3. Register MAUI implementations in a new `MauiProgramExtensions.UseSharedMauiApp()`
4. **Keep the monolith working** — register same implementations there too
5. Verify monolith still builds and runs

### Phase 2: Move Pure .NET Services to Shared (Low Risk)
**Goal:** Consolidate all non-UI, non-MAUI code in Shared.

1. Move `Services/LanguageSegmentation/` → `SentenceStudio.Shared/Services/LanguageSegmentation/`
2. Move `Services/Progress/` → `SentenceStudio.Shared/Services/Progress/`
3. Move `Services/PlanGeneration/` → `SentenceStudio.Shared/Services/PlanGeneration/`
4. Move `Services/Timer/` → `SentenceStudio.Shared/Services/Timer/`
5. Move `Services/DTOs/` → `SentenceStudio.Shared/Services/DTOs/`
6. Move `Services/Agents/` → `SentenceStudio.Shared/Services/Agents/`
7. Move `Models/` → `SentenceStudio.Shared/Models/` (merge with existing)
8. Move `Common/Constants.cs`, `Common/LocalizationManager.cs` → `SentenceStudio.Shared/Common/`
9. Move `Data/` repositories → `SentenceStudio.Shared/Repositories/` (merge with existing)
10. Move `Helpers/`, `Converters/` → `SentenceStudio.Shared/`
11. Update namespaces, fix using statements in monolith
12. **Verify monolith builds and runs after each batch of moves**

### Phase 3: Refactor MAUI-Dependent Services (Medium Risk)
**Goal:** Replace direct MAUI API calls with abstraction interfaces.

1. Refactor services one-by-one to accept `IFileSystemService` / `IPreferencesService` via DI
2. Start with lowest-risk: `ThemeService`, `VocabularyQuizPreferences`, `SpeechVoicePreferences`
3. Then: `AudioCacheManager`, `ElevenLabsSpeechService`
4. Then: remaining services with `FileSystem` calls
5. Move each refactored service to `SentenceStudio.Shared/Services/`
6. **Verify monolith builds and runs after each service migration**

### Phase 4: Extract Blazor UI to RCL (Medium Risk)
**Goal:** Move all Blazor content to the shared RCL.

1. Copy `WebUI/Pages/`, `WebUI/Shared/`, `WebUI/Layout/` → `SentenceStudio.UI/`
2. Copy `WebUI/Services/` → `SentenceStudio.UI/Services/`
3. Copy `WebUI/Routes.razor`, `_Imports.razor` → `SentenceStudio.UI/`
4. Copy `wwwroot/` → `SentenceStudio.UI/wwwroot/`
5. Update namespaces from `SentenceStudio.WebUI` → `SentenceStudio.UI`
6. Update `_Imports.razor` with new namespaces
7. Update `BlazorHostPage` to reference `SentenceStudio.UI.Routes`
8. Remove `WebUI/` from monolith, reference RCL instead
9. Update `SentenceStudio.Web` to reference RCL
10. **Verify both MAUI app and Web app build and run**

### Phase 5: Create AppLib + Platform Heads (Higher Risk)
**Goal:** Split the monolith into AppLib + per-platform heads.

1. Move `BlazorApp.cs`, `BlazorHostPage.cs`, `App.cs`, `AppShell.cs` → `SentenceStudio.AppLib/`
2. Move `Resources/` → `SentenceStudio.AppLib/Resources/`
3. Move `Themes/` → `SentenceStudio.AppLib/Themes/`
4. Create `MauiProgramExtensions.cs` from current `MauiProgram.cs`
5. Move handler customizations → `SentenceStudio.AppLib/Handlers/`
6. Move platform service implementations → `SentenceStudio.AppLib/Platforms/`
7. Create Mac Catalyst head with thin `MauiProgram.cs`
8. Move `Platforms/MacCatalyst/` contents → Mac Catalyst head
9. **Verify Mac Catalyst head builds and runs**
10. Create iOS, Android heads (copy pattern)
11. Create Windows head (when ready)
12. Delete original monolith project (or archive)

### Phase 6: Cleanup + Future Platform Prep

1. Remove legacy MauiReactor pages from solution
2. Remove legacy `Pages/`, `Components/`, `Common/Layouts/` directories
3. Update CI/CD to build platform heads instead of monolith
4. Update `copilot-instructions.md` with new project structure
5. Create `SentenceStudio.Platforms.MacOS/` stub for future native macOS

---

## 9. Risk Mitigation

| Risk | Mitigation |
|---|---|
| Breaking the working app during migration | Each phase is independently verifiable. Never delete monolith files until replacements are proven. |
| Namespace confusion | Use consistent `SentenceStudio.*` naming. Update `_Imports.razor` and `global using` carefully. |
| Resource path changes | MAUI app library auto-distributes resources. Test font loading, image display, raw asset access after each move. |
| CoreSync database path | `Constants.DatabasePath` stays in `SentenceStudio.Shared`. Database file location doesn't change. |
| Audio playback platform differences | `IAudioPlaybackService` abstraction + per-platform `AddAudio()` config in head MauiPrograms. |
| Web app breaks after RCL extraction | Test Web project at Phase 4 completion. Web needs its own implementations of platform abstractions. |
| Conditional compilation scattered across services | Replace `#if ANDROID` etc. with DI-based abstractions. Conditional compilation stays only in platform heads and AppLib. |

---

## 10. Summary: DI Registration Flow

```
Platform Head MauiProgram.cs
    └─ builder.UseSharedMauiApp()                    // From AppLib
        ├─ Registers MAUI infrastructure (fonts, handlers, BlazorWebView)
        ├─ builder.Services.AddSentenceStudioUI()    // From RCL (Blazor services)
        ├─ builder.Services.AddSentenceStudioServices(config)  // From Shared (all domain services)
        ├─ Registers platform abstractions:
        │   ├─ IFileSystemService    → MauiFileSystemService
        │   ├─ IPreferencesService   → MauiPreferencesService
        │   ├─ IAudioPlaybackService → MauiAudioPlaybackService
        │   └─ IFilePickerService    → MauiFilePickerService
        └─ CoreSync setup (AddDataServices + AddSyncServices)

SentenceStudio.Web Program.cs
    ├─ builder.Services.AddSentenceStudioUI()        // Same RCL services
    ├─ builder.Services.AddSentenceStudioServices(config)  // Same domain services
    └─ Registers web abstractions:
        ├─ IFileSystemService    → WebFileSystemService
        ├─ IPreferencesService   → WebPreferencesService (localStorage)
        └─ IAudioPlaybackService → WebAudioPlaybackService (HTML5 Audio)
```
