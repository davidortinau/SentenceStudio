# Blazor Localization in SentenceStudio (Hybrid + WebApp)

Add localized strings to a Razor component that works identically in MAUI Blazor Hybrid and the WebApp, with correct per-user isolation on the server.

## Golden rules

1. **Never read `AppResources.*` directly.** `AppResources` is internal to `SentenceStudio.Shared`. Go through `LocalizationManager.GetString(key, culture?)` or the injected `BlazorLocalizationService`.
2. **Never `AddSingleton<BlazorLocalizationService>`.** It must be **scoped** so each Blazor circuit holds its own `CultureInfo`. A singleton leaks culture between users on the server.
3. **Never mutate `CultureInfo.DefaultThreadCurrentUICulture` from Blazor code.** The `LocalizationManager.Instance.SetCulture` call is reserved for the MAUI path (single-user process). On WebApp, use the cookie endpoint.
4. **Never write cookies directly from a Blazor Server component.** Circuits run over SignalR, the `HttpResponse` is unavailable. Redirect through an endpoint.

## Pattern: adding localized strings to a Razor page

### 1. Add keys to both resx files

`src/SentenceStudio.Shared/Resources/Strings/AppResources.resx` (en) and `AppResources.ko-KR.resx` (ko). Use a stable prefix for the page (e.g., `Profile_Save`, `Nav_Dashboard`). Keep the `<comment>` human-readable — translators will thank you.

```xml
<data name="MyPage_Title" xml:space="preserve">
  <value>My Page</value>
  <comment>MyPage: page header title</comment>
</data>
```

### 2. Inject + consume in the Razor component

```razor
@implements IDisposable
@inject SentenceStudio.WebUI.Services.BlazorLocalizationService Localize

<h1>@Localize["MyPage_Title"]</h1>
<button>@Localize["MyPage_Save"]</button>
<p>@Localize.Get("MyPage_Greeting", userName)</p>  @* formatted with args *@

@code {
    protected override void OnInitialized()
    {
        Localize.CultureChanged += OnCultureChanged;
    }

    private void OnCultureChanged() => InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        Localize.CultureChanged -= OnCultureChanged;
    }
}
```

The subscription is how you get live re-render when the user saves a new language without reloading the page.

### 3. (Rarely) changing the culture from a component

If your component is the one that lets the user pick the language:

```csharp
var cultureInfo = new System.Globalization.CultureInfo("ko");
Localize.SetCulture(cultureInfo);  // flips THIS circuit, raises CultureChanged

var isWeb = !NavManager.BaseUri.StartsWith("app://") && !NavManager.BaseUri.Contains("0.0.0.0");
if (isWeb)
{
    // Persist via the account endpoint — circuits can't write cookies.
    NavManager.NavigateTo(
        $"/account-action/SetCulture?culture=ko&returnUrl=/some/page",
        forceLoad: true);
}
else
{
    // MAUI: safe to flip the process-wide culture in a single-user client.
    SentenceStudio.LocalizationManager.Instance.SetCulture(cultureInfo);
}
```

## Wire-up checklist (already done; reference only)

- `WebApp/Program.cs` has `AddLocalization()` + `Configure<RequestLocalizationOptions>` (cookie + accept-language providers) + `app.UseRequestLocalization()` before auth.
- `BlazorUIServiceExtensions.AddBlazorUIServices` registers `BlazorLocalizationService` as **scoped**.
- `AccountEndpoints.MapAccountEndpoints` exposes `GET /account-action/SetCulture`.
- MAUI `SentenceStudioAppBuilder` registers `LocalizationInitializer` (`IMauiInitializeService`) to apply `UserProfile.DisplayLanguage` at launch.

## Supported cultures

Currently `en` and `ko`. To add a new culture:

1. Create `AppResources.<tag>.resx` with all keys translated.
2. Add the tag to `supportedCultures` array in `WebApp/Program.cs`.
3. Add the tag to the whitelist in `AccountEndpoints.SetCulture`.
4. Add the option to Profile's `displayLanguage` dropdown + `UserProfile.DisplayCulture` switch expression.

## Gotchas

- **`AppResources` is `internal`.** Use `LocalizationManager.GetString`. If you genuinely need direct access from another assembly, add a targeted public helper rather than `InternalsVisibleTo` the world.
- **Don't forget `Dispose`.** If a component subscribes to `CultureChanged` without unsubscribing, the scoped service will hold a reference to the disposed component and the GC can't collect it. Always implement `IDisposable`.
- **Don't re-read strings into static fields.** `private static readonly string Title = Localize["..."];` won't update on culture change. Use properties or inline `@Localize["..."]` in markup.
- **Not every string should be a resource.** Debug logs, log categories, exception messages to developers, and audit trails should stay in English. Localize only user-facing UI copy.
