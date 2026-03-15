# Blazor Hybrid Authentication Architecture Research

**Date:** 2026-03-15  
**Author:** Zoe (Lead)  
**Status:** Research Complete

## Executive Summary

Microsoft's official pattern for Blazor Hybrid authentication uses **AuthenticationStateProvider** as the core abstraction, NOT boolean preferences or custom gate logic. The prescribed pattern uses `<CascadingAuthenticationState>` + `<AuthorizeRouteView>` in the router, which handles authentication state reactively and renders unauthorized content inline rather than using `NavigateTo()` redirects. Our current implementation bypasses this entire framework, resulting in fragile auth gate logic in `MainLayout.razor` that fights against Blazor's lifecycle and navigation model.

## Official Architecture

### Source Documents

1. **Primary Guide:** [ASP.NET Core Blazor Hybrid authentication and authorization](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/security/?view=aspnetcore-10.0&pivots=maui)
2. **Security Considerations:** [Blazor Hybrid security considerations](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/security/security-considerations?view=aspnetcore-10.0)
3. **Sample Implementation:** [.NET MAUI Blazor Hybrid and Web App with ASP.NET Core Identity](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/security/maui-blazor-web-identity?view=aspnetcore-10.0)

### Core Components

#### 1. AuthenticationStateProvider

**What it is:** The official abstraction that Blazor components use to access information about the authenticated user and receive updates when authentication state changes.

**Official pattern (from docs):**

```csharp
public class ExternalAuthStateProvider : AuthenticationStateProvider
{
    private AuthenticationState currentUser;

    public ExternalAuthStateProvider(ExternalAuthService service)
    {
        currentUser = new AuthenticationState(service.CurrentUser);

        service.UserChanged += (newUser) =>
        {
            currentUser = new AuthenticationState(newUser);
            NotifyAuthenticationStateChanged(Task.FromResult(currentUser));
        };
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
        Task.FromResult(currentUser);
}
```

**Key principles:**
- Returns `AuthenticationState` containing a `ClaimsPrincipal`
- Calls `NotifyAuthenticationStateChanged()` when auth state changes
- Registered in DI as scoped service
- Components react to state changes automatically via cascading parameters

**Documentation quote (from main security page):**
> "Integrating authentication must achieve the following goals for Razor components and services: Use the abstractions in the Microsoft.AspNetCore.Components.Authorization package, such as AuthorizeView. React to changes in the authentication context. Access credentials provisioned by the app from the identity provider, such as access tokens to perform authorized API calls."

#### 2. Router Configuration

**Official pattern (from MAUI Identity sample):**

```razor
<Router AppAssembly="typeof(Program).Assembly">
    <Found Context="routeData">
        <AuthorizeRouteView RouteData="routeData" 
            DefaultLayout="typeof(Layout.MainLayout)">
            <Authorizing>
                Authorizing...
            </Authorizing>
            <NotAuthorized>
                <Login />
            </NotAuthorized>
        </AuthorizeRouteView>
        <FocusOnNavigate RouteData="routeData" Selector="h1" />
    </Found>
</Router>
```

**Key components:**
- **`AuthorizeRouteView`** instead of plain `RouteView` — honors `[Authorize]` attributes on pages
- **`<NotAuthorized>` slot** — renders inline component (not a redirect) when user is unauthorized
- **`<Authorizing>` slot** — shows loading UI during auth state checks
- **No NavigateTo() calls** — authorization is declarative, handled by the router

**Documentation quote (from sample):**
> "The Routes component uses an AuthorizeRouteView to route users based on their authentication status. If a user isn't authenticated, they're redirected to the Login page."

**CLARIFICATION:** Despite saying "redirected," the sample shows the `<Login />` component rendered **inline** in the `<NotAuthorized>` slot, NOT a `NavigateTo()` call.

#### 3. DI Registration

**Official registration (from docs):**

```csharp
// In MauiProgram.cs
builder.Services.AddAuthorizationCore();
builder.Services.TryAddScoped<AuthenticationStateProvider, ExternalAuthStateProvider>();
```

**What Microsoft does NOT do:**
- No `CascadingAuthenticationState` registration in DI (it's a Razor component)
- No separate "auth gate" boolean logic
- No manual `NavigateTo()` in layouts

#### 4. Page Protection

**Official pattern:**

```razor
@using Microsoft.AspNetCore.Authorization
@attribute [Authorize]

<h1>Counter</h1>
...
```

**How it works:**
1. `[Authorize]` attribute on page
2. `AuthorizeRouteView` in router reads attribute
3. If unauthorized, router renders `<NotAuthorized>` content inline
4. If authorized, router renders page normally

**No custom gate logic in MainLayout.**

#### 5. Navigation Gating in MAUI Hybrid

**Microsoft's explicit guidance (from security page):**

> "Avoid authentication in the context of the Web View."

**What Microsoft prescribes for MAUI:**
1. Use platform-native auth (WebAuthenticator, MSAL)
2. Perform auth **outside** the WebView
3. Signal auth state changes via `AuthenticationStateProvider.NotifyAuthenticationStateChanged()`
4. Let `AuthorizeRouteView` handle UI gating reactively

**Microsoft does NOT use NavigateTo() for auth redirects in Hybrid apps.**

**Documentation quote (from security considerations):**
> "Avoid using the platform's Web View control to perform authentication. Instead, rely on the system's browser when possible."

#### 6. Token Storage

**Official pattern (from MAUI Identity sample):**

```csharp
// TokenStorage class uses SecureStorage API
var token = await response.Content.ReadAsStringAsync();
_accessToken = await TokenStorage.SaveTokenToSecureStorageAsync(token, email);
```

**Microsoft's approach:**
- Use platform-native `SecureStorage` (MAUI)
- Store JWT access token + refresh token
- Handle token refresh automatically in `AuthenticationStateProvider`
- Don't pass tokens to JavaScript/WebView context

**Documentation quote (from security considerations):**
> "Don't store sensitive information, such as credentials, security tokens, or sensitive user data, in the context of the Web View, as it makes the information available to a cyberattacker if the Web View is compromised."

#### 7. Integration with ASP.NET Identity

**Server-side setup (from MAUI Identity sample):**

```csharp
// In WebApp Program.cs
builder.Services.AddIdentityApiEndpoints<ApplicationUser>(...)
    .AddEntityFrameworkStores<ApplicationDbContext>();

app.MapGroup("/identity").MapIdentityApi<ApplicationUser>();
```

**Mobile client pattern:**
- Call `/identity/login` via HttpClient
- Receive JWT token in response
- Store token in SecureStorage
- Use token in Authorization header for API calls

**No cookie-based auth for mobile — always token-based.**

## Gap Analysis

### What Microsoft Prescribes vs What We Built

| Component | Microsoft Pattern | Our Implementation | Gap Severity |
|-----------|-------------------|--------------------|--------------| 
| **Auth Abstraction** | `AuthenticationStateProvider` | Custom `IAuthService` interface | **HIGH** — not using Blazor's official abstraction |
| **Router** | `<AuthorizeRouteView>` | `<RouteView>` | **HIGH** — no declarative auth |
| **Auth Gate** | `<NotAuthorized>` slot in router | Custom boolean logic in `MainLayout.razor` | **HIGH** — fighting Blazor lifecycle |
| **Navigation Redirect** | Inline component render in `<NotAuthorized>` | `NavigateTo()` or inline render (inconsistent) | **MEDIUM** — works but fragile |
| **Page Protection** | `[Authorize]` attribute | None (relies on layout gate) | **MEDIUM** — no per-page auth |
| **State Change** | `NotifyAuthenticationStateChanged()` | Manual preference writes | **HIGH** — no reactive updates |
| **DI Registration** | `AddAuthorizationCore()` | Not present | **HIGH** — missing core services |
| **Token Management** | Inside `AuthenticationStateProvider` | Separate `IAuthService` | **MEDIUM** — separation OK, but not standard |
| **Cascading State** | `Task<AuthenticationState>` cascading parameter | None | **HIGH** — components can't access auth state |

### Detailed Gap Assessment

#### 1. MainLayout.razor Auth Gate

**Our implementation (lines 80-150):**

```csharp
protected override async Task OnInitializedAsync()
{
    var prefAuthenticated = Preferences.Get("app_is_authenticated", false);
    
    if (prefAuthenticated)
    {
        if (!AuthService.IsSignedIn)
        {
            await AuthService.SignInAsync(); // Silent refresh
        }
        
        if (!AuthService.IsSignedIn)
        {
            Preferences.Remove("app_is_authenticated");
            prefAuthenticated = false;
        }
    }
    
    if (!prefAuthenticated)
    {
        isAuthGate = true;
        if (isAuthRoute)
        {
            authCheckComplete = true;
        }
        else
        {
            Logger.LogInformation("Auth gate ACTIVE — rendering auth inline (bypassing router)");
            showAuthInline = true;
            authCheckComplete = true;
        }
    }
}
```

**Problems with our approach:**
1. **Boolean preference is not a security boundary** — Microsoft uses `ClaimsPrincipal` identity
2. **NavigateTo() doesn't work in OnInitializedAsync** — we discovered this the hard way, hence the `showAuthInline` workaround
3. **No reactivity** — if auth state changes elsewhere, components don't re-render
4. **Manual layout visibility logic** — `isAuthGate` boolean controls sidebar/nav, should be automatic
5. **LocationChanged handler complexity** — lines 172-208 manually track route changes

**Microsoft's approach:**
- Router handles auth gating automatically
- No boolean flags in layout
- No manual NavigateTo() logic
- Reactive updates via `AuthenticationStateProvider`

#### 2. Routes.razor

**Our implementation:**

```razor
<Router AppAssembly="typeof(Routes).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="routeData" DefaultLayout="typeof(Layout.MainLayout)" />
        <FocusOnNavigate RouteData="routeData" Selector="h1" />
    </Found>
    <NotFound>
        <LayoutView Layout="typeof(Layout.MainLayout)">
            <div class="container text-center mt-5">
                <h1>Page not found</h1>
                <p class="text-secondary">Sorry, the page you requested could not be found.</p>
            </div>
        </LayoutView>
    </NotFound>
</Router>
```

**Missing from our implementation:**
- No `AuthorizeRouteView` (using plain `RouteView`)
- No `<NotAuthorized>` slot
- No `<Authorizing>` slot
- No support for `[Authorize]` attributes on pages

**Impact:**
- Cannot use declarative `[Authorize]` on Counter.razor, Weather.razor, etc.
- All auth logic must live in MainLayout (fragile, hard to maintain)
- No way to show "Authorizing..." spinner during auth checks

#### 3. IAuthService vs AuthenticationStateProvider

**Our IAuthService (IdentityAuthService.cs):**

```csharp
public interface IAuthService
{
    bool IsSignedIn { get; }
    string? UserName { get; }
    Task<AuthResult?> SignInAsync();
    Task<AuthResult?> SignInAsync(string email, string password);
    Task<AuthResult?> RegisterAsync(string email, string password, string displayName);
    Task SignOutAsync();
    Task<bool> DeleteAccountAsync();
    Task<string?> GetAccessTokenAsync(string[] scopes);
}
```

**Microsoft's AuthenticationStateProvider:**

```csharp
public abstract class AuthenticationStateProvider
{
    public abstract Task<AuthenticationState> GetAuthenticationStateAsync();
    protected void NotifyAuthenticationStateChanged(Task<AuthenticationState> task);
}
```

**Gaps:**
1. **No ClaimsPrincipal** — we return boolean `IsSignedIn`, Microsoft returns `AuthenticationState` with user claims
2. **No change notifications** — our service can't notify components when auth state changes
3. **Not integrated with Blazor auth components** — can't use `<AuthorizeView>`, `[CascadingParameter] Task<AuthenticationState>`
4. **Missing standard Blazor abstractions** — have to inject `IAuthService` manually everywhere

**Note:** Our `IAuthService` implementation is actually **good engineering** for token management and API calls. The problem is we're not **also** implementing `AuthenticationStateProvider` to integrate with Blazor's auth system.

#### 4. Missing DI Registration

**Microsoft requires (from docs):**

```csharp
builder.Services.AddAuthorizationCore();
```

**Our MauiProgram.cs:**
- No `AddAuthorizationCore()` call
- Cannot use `[Authorize]` attributes
- Cannot use `<AuthorizeView>` component
- Policy-based auth unavailable

**Impact:** Entire Blazor authorization subsystem is missing.

#### 5. Auth.razor Profile Selection

**Our implementation (lines 121-145):**

```csharp
private async Task LoginAsAsync(UserProfile profile)
{
    Preferences.Set(ActiveProfileIdKey, profile.Id);
    AppState.CurrentUserProfile = profile;
    
    if (!AuthService.IsSignedIn)
    {
        await AuthService.SignInAsync();
    }
    
    if (AuthService.IsSignedIn)
    {
        Preferences.Set(AuthenticatedPreferenceKey, true);
        Preferences.Set("is_onboarded", true);
        NavManager.NavigateTo("/", forceLoad: true);
    }
    else
    {
        loginError = "Server authentication required. Please sign in first.";
        StateHasChanged();
    }
}
```

**Problems:**
1. **NavigateTo with forceLoad: true** — this forces a full app reload, losing in-memory state
2. **Preference-based auth flag** — not a security boundary
3. **No ClaimsPrincipal creation** — profile selection doesn't create user identity
4. **Manual StateHasChanged()** — should be automatic via AuthenticationStateProvider

**Microsoft's pattern (from sample):**
- Create `ClaimsPrincipal` with user claims
- Call `NotifyAuthenticationStateChanged()`
- Let router reactively render authorized content
- No manual NavigateTo()

## Root Cause Analysis

### Why Our Auth Gate Doesn't Work

**Primary issue:** We're trying to use `NavigateTo()` during component initialization in a Blazor Hybrid WebView, which doesn't work reliably because:

1. **WebView lifecycle timing** — the WebView may not be fully initialized when `OnInitializedAsync` runs
2. **Blazor's rendering model** — NavigateTo during init causes race conditions with the router's own initialization
3. **Missing router integration** — plain `RouteView` doesn't check auth, so MainLayout has to do it manually

**Quote from our code comments (MainLayout.razor lines 125-126):**

```csharp
// NavigateTo() doesn't work during init in Blazor Hybrid WebView,
// so we bypass routing and render the Auth page directly.
```

**This is a symptom, not a root cause.** The root cause is using the wrong architecture.

### Why Microsoft's Pattern Works

1. **Declarative, not imperative** — `AuthorizeRouteView` checks auth before rendering, no manual NavigateTo()
2. **Reactive** — `AuthenticationStateProvider` notifies components when state changes
3. **Lifecycle-aware** — router handles auth checks at the right time in Blazor's rendering lifecycle
4. **No WebView navigation issues** — content renders inline, no URL changes needed

### Additional Issues

**Token management:** Our `IdentityAuthService` is actually well-designed for API communication (JWT storage, refresh logic, HttpClient integration). The problem is it's **not connected to Blazor's auth system**.

**Multi-user support:** Our profile selection (Auth.razor) is a valid feature, but it should **create a ClaimsPrincipal** with profile claims, not just set a preference boolean.

## Recommended Architecture

### High-Level Design

```
┌─────────────────────────────────────────────────────────────┐
│ Blazor Router (Routes.razor)                                 │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │ <AuthorizeRouteView>                                     │ │
│ │   <Authorizing>Loading...</Authorizing>                  │ │
│ │   <NotAuthorized><Auth /></NotAuthorized>                │ │
│ │ </AuthorizeRouteView>                                    │ │
│ └─────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
                           ▲
                           │ reads auth state
                           │
┌──────────────────────────┴──────────────────────────────────┐
│ SentenceStudioAuthenticationStateProvider                   │
│   (implements AuthenticationStateProvider)                  │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │ - Wraps IAuthService (token management)                  │ │
│ │ - Returns AuthenticationState with ClaimsPrincipal       │ │
│ │ - Notifies on auth state changes                         │ │
│ │ - Handles profile selection → claims mapping             │ │
│ └─────────────────────────────────────────────────────────┘ │
└──────────────────────────┬──────────────────────────────────┘
                           │ uses
                           ▼
┌─────────────────────────────────────────────────────────────┐
│ IAuthService (IdentityAuthService)                          │
│ - Token storage, refresh, API calls                         │
│ - HTTP authentication (login, register, refresh)            │
│ - UNCHANGED — keep current implementation                   │
└─────────────────────────────────────────────────────────────┘
```

### Code Sketches

#### 1. New AuthenticationStateProvider

```csharp
// src/SentenceStudio.AppLib/Services/SentenceStudioAuthStateProvider.cs
using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;

namespace SentenceStudio.Services;

public class SentenceStudioAuthStateProvider : AuthenticationStateProvider
{
    private readonly IAuthService _authService;
    private readonly IPreferencesService _preferences;
    private readonly ILogger<SentenceStudioAuthStateProvider> _logger;
    private AuthenticationState _currentState;

    public SentenceStudioAuthStateProvider(
        IAuthService authService,
        IPreferencesService preferences,
        ILogger<SentenceStudioAuthStateProvider> logger)
    {
        _authService = authService;
        _preferences = preferences;
        _logger = logger;
        _currentState = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        // Try silent sign-in if preference indicates user was authenticated
        if (_preferences.Get("app_is_authenticated", false) && !_authService.IsSignedIn)
        {
            try
            {
                await _authService.SignInAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Silent sign-in failed");
            }
        }

        if (_authService.IsSignedIn)
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, _authService.UserName ?? "User"),
                new Claim(ClaimTypes.Email, _authService.UserName ?? ""),
                // Add profile claims here if available
            }, "jwt");

            _currentState = new AuthenticationState(new ClaimsPrincipal(identity));
        }
        else
        {
            _currentState = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }

        return _currentState;
    }

    public async Task SignInAsync(string email, string password)
    {
        var result = await _authService.SignInAsync(email, password);
        if (result is not null)
        {
            _preferences.Set("app_is_authenticated", true);
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }
    }

    public async Task SignOutAsync()
    {
        await _authService.SignOutAsync();
        _preferences.Remove("app_is_authenticated");
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
```

#### 2. Updated Routes.razor

```razor
<Router AppAssembly="typeof(Routes).Assembly">
    <Found Context="routeData">
        <AuthorizeRouteView RouteData="routeData" DefaultLayout="typeof(Layout.MainLayout)">
            <Authorizing>
                <div class="d-flex justify-content-center align-items-center" style="min-height: 200px;">
                    <div class="spinner-border text-primary" role="status">
                        <span class="visually-hidden">Authorizing...</span>
                    </div>
                </div>
            </Authorizing>
            <NotAuthorized>
                <LayoutView Layout="typeof(Layout.MainLayout)">
                    <SentenceStudio.UI.Pages.Auth />
                </LayoutView>
            </NotAuthorized>
        </AuthorizeRouteView>
        <FocusOnNavigate RouteData="routeData" Selector="h1" />
    </Found>
    <NotFound>
        <LayoutView Layout="typeof(Layout.MainLayout)">
            <div class="container text-center mt-5">
                <h1>Page not found</h1>
                <p class="text-secondary">Sorry, the page you requested could not be found.</p>
            </div>
        </LayoutView>
    </NotFound>
</Router>
```

#### 3. Updated MauiProgram.cs Registration

```csharp
// Add to MauiProgram.cs or SentenceStudioAppBuilder
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<AuthenticationStateProvider, SentenceStudioAuthStateProvider>();
```

#### 4. Simplified MainLayout.razor

```razor
@inherits LayoutComponentBase
@inject ThemeService ThemeService
@inject IJSRuntime JS
@inject NavigationManager NavigationManager
@inject SentenceStudio.Abstractions.IPreferencesService Preferences
@inject ILogger<MainLayout> Logger
@implements IDisposable

<div class="d-flex vh-100">
    @if (!isOnboarding)
    {
        @* Sidebar navigation (desktop only) *@
        <nav class="d-none d-md-flex flex-column flex-shrink-0 bg-surface sidebar-nav @(sidebarCollapsed ? "collapsed" : "")"
             style="border-right: 1px solid var(--bs-border-color);">
            <div class="flex-grow-1 overflow-auto">
                <NavMenu />
            </div>
            <div class="p-2 border-top" style="border-color: var(--bs-border-color) !important;">
                <button class="btn btn-sm btn-icon d-flex align-items-center @(sidebarCollapsed ? "justify-content-center w-100" : "ms-auto")"
                        title="@(sidebarCollapsed ? "Expand sidebar" : "Collapse sidebar")"
                        @onclick="ToggleSidebar">
                    <i class="bi @(sidebarCollapsed ? "bi-chevron-right" : "bi-chevron-left")"></i>
                </button>
            </div>
        </nav>
    }

    <div class="d-flex flex-column flex-grow-1 overflow-hidden">
        @* Main content — Router handles auth gating now *@
        <main class="flex-grow-1 overflow-auto p-3 main-content">
            @Body
        </main>
    </div>

    @if (!isOnboarding)
    {
        @* Mobile offcanvas navigation *@
        <div class="offcanvas offcanvas-start bg-surface" tabindex="-1" id="mobileNav"
             style="border-right: 1px solid var(--bs-border-color);">
            <div class="offcanvas-header border-bottom"
                 style="border-color: var(--bs-border-color) !important; padding-top: calc(env(safe-area-inset-top, 0px) + 12px);">
                <h5 class="offcanvas-title text-primary-ss">SentenceStudio</h5>
                <button type="button" class="btn-close" data-bs-dismiss="offcanvas"></button>
            </div>
            <div class="offcanvas-body p-0">
                <NavMenu />
            </div>
        </div>
    }

    <ToastContainer />
</div>

@code {
    private IJSObjectReference? jsModule;
    private bool sidebarCollapsed;
    private bool isOnboarding;

    protected override async Task OnInitializedAsync()
    {
        NavigationManager.LocationChanged += OnLocationChanged;
        sidebarCollapsed = Preferences.Get("SidebarCollapsed", false);
        
        // Check onboarding state
        var uri = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
        isOnboarding = !Preferences.Get("is_onboarded", false) 
            || uri.AbsolutePath.StartsWith("/onboarding", StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/SentenceStudio.UI/js/app.js");
            await jsModule.InvokeVoidAsync("applyTheme", ThemeService.CurrentTheme, ThemeService.CurrentMode);
            await jsModule.InvokeVoidAsync("setFontScale", ThemeService.FontScale);
            ThemeService.ThemeChanged += OnThemeChanged;
        }
    }

    private void ToggleSidebar()
    {
        sidebarCollapsed = !sidebarCollapsed;
        Preferences.Set("SidebarCollapsed", sidebarCollapsed);
    }

    private async void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e)
    {
        var newUri = NavigationManager.ToAbsoluteUri(e.Location);
        var path = newUri.AbsolutePath;
        
        isOnboarding = path.StartsWith("/onboarding", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (jsModule != null)
                await jsModule.InvokeVoidAsync("resetScroll");
        }
        catch { /* component may be disposed */ }
    }

    private async void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        if (jsModule != null)
        {
            await jsModule.InvokeVoidAsync("applyTheme", e.Theme, e.Mode);
            await jsModule.InvokeVoidAsync("setFontScale", e.FontScale);
        }
    }

    public void Dispose()
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        NavigationManager.LocationChanged -= OnLocationChanged;
        jsModule?.DisposeAsync();
    }
}
```

**Key changes:**
- Removed `isAuthGate`, `authCheckComplete`, `showAuthInline` — router handles this now
- Removed auth verification logic in `OnInitializedAsync` — AuthenticationStateProvider handles it
- Removed LocationChanged auth logic — router handles it
- Kept onboarding logic (separate concern)
- ~150 lines → ~90 lines

#### 5. Updated Auth.razor

```csharp
private async Task LoginAsAsync(UserProfile profile)
{
    Preferences.Set(ActiveProfileIdKey, profile.Id);
    AppState.CurrentUserProfile = profile;
    
    // Ensure server authentication
    if (!AuthService.IsSignedIn)
    {
        await AuthService.SignInAsync();
    }
    
    if (AuthService.IsSignedIn)
    {
        Preferences.Set("app_is_authenticated", true);
        Preferences.Set("is_onboarded", true);
        
        // Notify auth state provider (inject it)
        await AuthStateProvider.GetAuthenticationStateAsync();
        
        // No NavigateTo() — let router handle it
    }
    else
    {
        loginError = "Server authentication required. Please sign in first.";
    }
}
```

**Inject AuthenticationStateProvider:**

```razor
@inject AuthenticationStateProvider AuthStateProvider
```

#### 6. Add [Authorize] to Pages

```razor
@page "/counter"
@using Microsoft.AspNetCore.Authorization
@attribute [Authorize]

<PageHeader Title="Counter" />
...
```

Repeat for:
- Counter.razor
- Weather.razor
- Any other protected pages

## Migration Path

### Phase 1: Foundation (No Breaking Changes)

**Goal:** Add Blazor auth infrastructure without changing behavior.

1. **Add NuGet package** (if missing): `Microsoft.AspNetCore.Components.Authorization`
2. **Create `SentenceStudioAuthStateProvider`** (wraps existing `IAuthService`)
3. **Register in DI:**
   ```csharp
   builder.Services.AddAuthorizationCore();
   builder.Services.AddScoped<AuthenticationStateProvider, SentenceStudioAuthStateProvider>();
   ```
4. **Keep `IAuthService` unchanged** — it's still used internally
5. **Test:** Verify app still works, auth flow unchanged

**Deliverable:** Auth infrastructure present but unused.

### Phase 2: Router Replacement

**Goal:** Switch from RouteView to AuthorizeRouteView.

1. **Update Routes.razor:**
   - Replace `<RouteView>` with `<AuthorizeRouteView>`
   - Add `<Authorizing>` and `<NotAuthorized>` slots
2. **Test:** Verify Auth page renders for unauthenticated users
3. **Test:** Verify authenticated users see normal content

**Deliverable:** Router-based auth gating works.

### Phase 3: MainLayout Simplification

**Goal:** Remove manual auth gate logic.

1. **Delete from MainLayout.razor:**
   - `isAuthGate`, `authCheckComplete`, `showAuthInline` state
   - Auth verification in `OnInitializedAsync`
   - Auth logic in `OnLocationChanged`
2. **Keep only:**
   - Theme service
   - Sidebar collapse
   - Onboarding logic (separate concern)
3. **Test:** Verify auth flow still works
4. **Test:** Verify NavigateTo issues are gone (they should be)

**Deliverable:** Clean layout, no auth logic.

### Phase 4: Page Attributes

**Goal:** Add declarative auth to pages.

1. **Add `[Authorize]` to protected pages:**
   - Counter.razor
   - Weather.razor
   - Profile pages
   - Settings pages
2. **Test:** Verify pages require auth
3. **Test:** Verify Auth page renders for unauthorized access

**Deliverable:** Per-page auth protection.

### Phase 5: Enhanced Auth (Optional Future Work)

**Goal:** Leverage full Blazor auth system.

1. **Use `<AuthorizeView>` in components:**
   ```razor
   <AuthorizeView>
       <Authorized>
           Welcome, @context.User.Identity?.Name
       </Authorized>
       <NotAuthorized>
           Please log in.
       </NotAuthorized>
   </AuthorizeView>
   ```
2. **Add role/policy-based auth:**
   ```razor
   @attribute [Authorize(Roles = "Admin")]
   ```
3. **Use cascading auth state in components:**
   ```razor
   [CascadingParameter]
   private Task<AuthenticationState> AuthState { get; set; }
   ```

**Deliverable:** Full Blazor auth feature set.

## Open Questions

### 1. Profile Selection vs Claims

**Question:** How should multi-user profile selection integrate with ClaimsPrincipal?

**Options:**
- **A:** Profile ID as claim (`new Claim("profile_id", profile.Id)`)
- **B:** Profile properties as claims (name, native language, target language)
- **C:** Both — ID + key properties

**Recommendation:** Option C — profile ID is minimum, key properties improve logging/debugging.

### 2. Onboarding Integration

**Question:** Should onboarding also use the auth system?

**Current:** Onboarding is separate from auth (runs after auth succeeds).

**Options:**
- **A:** Keep separate (onboarding is a one-time setup, not an auth state)
- **B:** Use `[Authorize]` + onboarding claim

**Recommendation:** Option A — onboarding is not an authentication concern.

### 3. WebApp Cookie Auth

**Question:** Does this pattern work for both MAUI (token) and WebApp (cookie) auth?

**Answer:** Yes, with separate `AuthenticationStateProvider` implementations:
- **MAUI:** `SentenceStudioAuthStateProvider` (uses IAuthService + SecureStorage)
- **WebApp:** Use built-in `RevalidatingServerAuthenticationStateProvider` (ASP.NET Identity)

**Documentation reference:** The MAUI Identity sample shows both approaches in the same solution.

### 4. DevAuthHandler Compatibility

**Question:** Does this pattern work with our existing DevAuthHandler (Auth:UseEntraId=false)?

**Answer:** Yes — `IAuthService` (IdentityAuthService) already handles both. The AuthenticationStateProvider just wraps it.

### 5. Offline Mode

**Question:** What happens if the user is offline and tokens expired?

**Current behavior:** Silent sign-in fails, user sees Auth page.

**Recommendation:** Keep current behavior. Add toast: "Authentication required. Please connect to the internet to sign in."

### 6. Token Refresh Timing

**Question:** When does token refresh happen with this pattern?

**Answer:** Same as now — `IAuthService.GetAccessTokenAsync()` handles refresh (60s buffer). AuthenticationStateProvider doesn't change this.

### 7. Navigation After Login

**Question:** Without NavigateTo(), how does the user return to the original page after login?

**Answer:** Router handles this automatically via `returnUrl` query parameter (standard Blazor behavior). We may need to add this to our Auth page.

**Microsoft pattern (from docs):**

```razor
@page "/login"
@code {
    [SupplyParameterFromQuery]
    public string? ReturnUrl { get; set; }
    
    private async Task HandleLogin()
    {
        // Login logic
        NavigationManager.NavigateTo(ReturnUrl ?? "/");
    }
}
```

## References

### Microsoft Documentation

1. [ASP.NET Core Blazor Hybrid authentication and authorization](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/security/?view=aspnetcore-10.0&pivots=maui)
2. [ASP.NET Core Blazor Hybrid security considerations](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/security/security-considerations?view=aspnetcore-10.0)
3. [.NET MAUI Blazor Hybrid and Web App with ASP.NET Core Identity](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/security/maui-blazor-web-identity?view=aspnetcore-10.0)
4. [ASP.NET Core Blazor authentication and authorization](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/?view=aspnetcore-10.0)
5. [Customize unauthorized content with the Router component](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/?view=aspnetcore-10.0#customize-unauthorized-content-with-the-router-component)

### Code Samples

- [Microsoft Blazor samples GitHub repository (MauiBlazorWebIdentity folder)](https://github.com/dotnet/blazor-samples) — official MAUI + Blazor + Identity sample

### Internal Documentation

- `.squad/decisions.md` — Mobile Auth Guard decision (#10)
- `.squad/agents/zoe/history.md` — Auth flow implementation history

## Conclusion

Our current auth implementation bypasses Blazor's official authentication system, resulting in fragile gate logic that fights against the framework's lifecycle. The fix is not to tweak the gate logic — it's to adopt Microsoft's prescribed architecture:

1. Implement `AuthenticationStateProvider` (wraps existing `IAuthService`)
2. Replace `RouteView` with `AuthorizeRouteView`
3. Remove manual gate logic from MainLayout
4. Add `[Authorize]` attributes to pages

This is a **refactor, not a rewrite** — our existing `IAuthService` is well-designed and stays unchanged. We're adding the missing Blazor integration layer.

**Estimated effort:** 1-2 days (Phase 1-4). No data migration, no API changes, minimal risk.
