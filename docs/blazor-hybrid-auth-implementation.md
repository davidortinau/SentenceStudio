# Blazor Hybrid Authentication Implementation Analysis

## Executive Summary

This document analyzes the official Blazor Hybrid authentication patterns and provides a concrete implementation roadmap for SentenceStudio. Our current approach uses manual auth gates in MainLayout.razor with boolean preferences, which bypasses the framework's authentication infrastructure. The official pattern uses AuthenticationStateProvider + AuthorizeRouteView + CascadingAuthenticationState, providing framework-level auth awareness and proper integration with AuthorizeView and route guards.

**Key Finding:** MainLayout.razor should NOT be an auth gate. The Router component (Routes.razor) is the official auth enforcement point via AuthorizeRouteView.

## Official Pattern: How Blazor Hybrid Auth Works

### 1. Custom AuthenticationStateProvider

**Source:** [ASP.NET Core Blazor Hybrid authentication and authorization](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/security/?view=aspnetcore-10.0&pivots=maui)

The framework provides `Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider` as the central abstraction for auth state. A custom implementation must:

1. Return the current authenticated user via `GetAuthenticationStateAsync()`
2. Signal auth changes via `NotifyAuthenticationStateChanged(Task<AuthenticationState>)`
3. Expose login/logout methods that components can call

**Official Code Pattern (Option 2 - Auth within BlazorWebView):**

```csharp
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;

public class ExternalAuthStateProvider : AuthenticationStateProvider
{
    private ClaimsPrincipal currentUser = new ClaimsPrincipal(new ClaimsIdentity());

    public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
        Task.FromResult(new AuthenticationState(currentUser));

    public Task LogInAsync()
    {
        var loginTask = LogInAsyncCore();
        NotifyAuthenticationStateChanged(loginTask);

        return loginTask;

        async Task<AuthenticationState> LogInAsyncCore()
        {
            var user = await LoginWithExternalProviderAsync();
            currentUser = user;

            return new AuthenticationState(currentUser);
        }
    }

    private Task<ClaimsPrincipal> LoginWithExternalProviderAsync()
    {
        // Integrate with IAuthService here
        // Return ClaimsPrincipal based on JWT claims
        var authenticatedUser = new ClaimsPrincipal(new ClaimsIdentity());
        return Task.FromResult(authenticatedUser);
    }

    public void Logout()
    {
        currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        NotifyAuthenticationStateChanged(
            Task.FromResult(new AuthenticationState(currentUser)));
    }
}
```

### 2. Routes.razor with AuthorizeRouteView

**Source:** [.NET MAUI Blazor Hybrid and Web App with ASP.NET Core Identity](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/security/maui-blazor-web-identity?view=aspnetcore-10.0)

The Router component uses `AuthorizeRouteView` instead of plain `RouteView` to enforce authentication at the routing level. When a user navigates to a route requiring auth (marked with `[Authorize]` attribute), the framework redirects them to the `NotAuthorized` fragment.

**Official Pattern:**

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
                <!-- Redirect to login page or render login component inline -->
                <Login />
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

Key differences:
- `AuthorizeRouteView` instead of `RouteView` — framework-level auth checking
- `<Authorizing>` fragment — loading state while checking auth
- `<NotAuthorized>` fragment — renders when user is unauthenticated for a protected route
- `[Authorize]` attributes on Razor components automatically trigger this flow

### 3. MauiProgram Registration

**Source:** [ASP.NET Core Blazor Hybrid authentication and authorization](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/security/?view=aspnetcore-10.0&pivots=maui)

Register authorization core + custom AuthenticationStateProvider in DI:

```csharp
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<AuthenticationStateProvider, ExternalAuthStateProvider>();
```

**Key Point:** AddAuthorizationCore() enables AuthorizeView, AuthorizeRouteView, and [Authorize] attributes.

### 4. Token Storage with SecureStorage

**Source:** [.NET MAUI Blazor Hybrid and Web App with ASP.NET Core Identity](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/security/maui-blazor-web-identity?view=aspnetcore-10.0)

The official sample uses a `TokenStorage` class wrapping SecureStorage API to persist tokens across app restarts:

```csharp
// From MauiBlazorWeb sample TokenStorage class
public async Task<string> SaveTokenToSecureStorageAsync(string token, string email)
{
    await SecureStorage.SetAsync("auth_token", token);
    await SecureStorage.SetAsync("auth_email", email);
    return token;
}

public async Task<string?> GetTokenAsync()
{
    return await SecureStorage.GetAsync("auth_token");
}
```

The `MauiAuthenticationStateProvider` restores tokens on startup and refreshes near expiration.

**Token Lifecycle:**
1. App starts → AuthenticationStateProvider.GetAuthenticationStateAsync() called
2. Check SecureStorage for refresh token
3. If found, attempt silent token refresh (POST /api/auth/refresh)
4. If refresh succeeds, set ClaimsPrincipal from JWT claims
5. If refresh fails or no token, return unauthenticated ClaimsPrincipal
6. NotifyAuthenticationStateChanged() triggers UI updates

### 5. CascadingAuthenticationState

**Source:** [ASP.NET Core Blazor authentication and authorization](https://learn.microsoft.com/aspnet/core/blazor/security/?view=aspnetcore-10.0#authorizeview-component)

WebApp projects use `<CascadingAuthenticationState>` in App.razor to cascade auth state to all child components. This enables AuthorizeView and [Authorize] throughout the component tree.

**WebApp App.razor pattern:**

```razor
<!DOCTYPE html>
<html lang="en">
<head>...</head>
<body>
    <CascadingAuthenticationState>
        <Routes />
    </CascadingAuthenticationState>
</body>
</html>
```

For MAUI clients, CascadingAuthenticationState is not explicitly required because the DI-registered AuthenticationStateProvider is automatically available. However, the official sample app (MauiBlazorWeb) does NOT use CascadingAuthenticationState in the native client — the DI registration is sufficient.

## Our Current Implementation

### What We Have Today

**MainLayout.razor:**
- Boolean preference `app_is_authenticated` checked in `OnInitializedAsync()`
- Manual auth gate logic: if not authenticated, render Auth page inline via `showAuthInline`
- NavigateTo() bypassed because it doesn't fire in OnInitializedAsync in Blazor Hybrid
- Custom flags: `isAuthGate`, `authCheckComplete`, `showAuthInline`

**Routes.razor:**
- Plain `RouteView` with no auth checking
- No `[Authorize]` attributes on pages (all publicly routable)

**IdentityAuthService:**
- Implements IAuthService with JWT token flow
- `IsSignedIn` checks in-memory cached token + expiration
- `SignInAsync()` (parameterless) attempts silent refresh from SecureStorage
- No integration with Blazor auth framework (no ClaimsPrincipal exposure)

**ServiceCollectionExtentions.cs:**
- Registers `IAuthService` as singleton IdentityAuthService
- No registration of AuthenticationStateProvider or AddAuthorizationCore()

**WebApp Program.cs:**
- Registers `ServerAuthService` (IAuthService implementation)
- Uses ASP.NET Core Identity cookie auth (UserManager + SignInManager)
- No AuthenticationStateProvider registration (uses Identity middleware)

### Auth.razor (Auth Landing Page)

- Lists local UserProfile records (from SQLite)
- "Sign In" / "Create Account" buttons link to /auth/login and /auth/register
- "Select a local user" calls `LoginAsAsync(profile)` which checks `AuthService.IsSignedIn` and attempts silent sign-in before granting access
- If no valid server session, shows warning and stays on auth page

## What's Wrong: Technical Reasons

### 1. NavigateTo Doesn't Work During OnInitializedAsync in Blazor Hybrid

**Problem:** MainLayout.OnInitializedAsync() calls `NavigationManager.NavigateTo("/auth")` when unauthenticated, but this navigation never fires. The WebView routing infrastructure isn't fully initialized during component initialization in Blazor Hybrid.

**Current Workaround:** We render the Auth page inline via conditional logic (`showAuthInline = true` and `<SentenceStudio.UI.Pages.Auth />` in the layout body). This bypasses routing entirely and creates state management complexity.

**Consequence:** The URL bar doesn't change, and browser back/forward buttons don't work as expected. The app is effectively in a "phantom route" state.

### 2. Layout Component Persists Across Route Changes

**Problem:** MainLayout.razor is reused for all routes. Once `authCheckComplete = true` and `showAuthInline = false`, the layout assumes authentication and renders @Body for all future navigation. If the user signs out, MainLayout doesn't re-run OnInitializedAsync() — it just keeps rendering @Body.

**Current Workaround:** LocationChanged event handler detects auth route changes and updates flags. This is brittle and requires manual state synchronization.

**Consequence:** Auth state can desync from UI state. For example, if the token expires mid-session, MainLayout doesn't know to re-gate until the user refreshes the page or triggers a navigation event.

### 3. No Framework-Level Auth Awareness

**Problem:** We don't register AuthenticationStateProvider or call AddAuthorizationCore(), so the Blazor framework has no concept of an authenticated user. AuthorizeView components and [Authorize] attributes are unavailable.

**Consequence:**
- Cannot use `<AuthorizeView>` in components (requires AuthenticationStateProvider)
- Cannot use `[Authorize]` attribute on pages (requires AuthorizeRouteView)
- Cannot react to auth state changes via `AuthenticationStateChanged` event
- Custom code everywhere: every page that needs auth must manually check `AuthService.IsSignedIn`

### 4. Boolean Preference is Not Auth State

**Problem:** The preference `app_is_authenticated` is a convenience flag, not a security boundary. It's set by Auth.razor after a successful local profile selection, but it doesn't guarantee a valid JWT token.

**Consequence:** A stale preference (leftover from a previous session after app restart) bypasses all server auth checks. This was the bug fixed in the March 15th mobile auth gate fix, which added a server validation step, but the fix is a band-aid on a fundamentally broken pattern.

### 5. Router Has No Auth Integration

**Problem:** Routes.razor uses plain RouteView, so the Router component doesn't know about auth. All routes are publicly accessible by default.

**Consequence:**
- Cannot use `[Authorize]` attribute on Razor components
- Cannot redirect unauthenticated users to a login page automatically
- Manual auth checks required on every page that needs protection

### 6. WebApp and Mobile Auth Diverge

**Problem:** WebApp uses ASP.NET Core Identity cookie auth via UserManager + SignInManager, while mobile uses IAuthService with JWT tokens. Both contexts share MainLayout.razor, but the auth patterns are fundamentally different:
- WebApp: HttpContext.User.Identity.IsAuthenticated (cookie)
- Mobile: IAuthService.IsSignedIn (in-memory JWT cache)

**Current Workaround:** ServerAuthService.IsSignedIn checks HttpContext.User.Identity.IsAuthenticated; IdentityAuthService.IsSignedIn checks cached JWT expiration. The IAuthService abstraction unifies the interface, but MainLayout's manual gate logic doesn't leverage any Blazor auth primitives.

**Consequence:** Refactoring auth for one context risks breaking the other. The shared MainLayout acts as a brittle adapter instead of a clean framework-integrated layout.

## Implementation Roadmap

### Phase 1: Create MauiAuthenticationStateProvider (Mobile Only)

**Goal:** Integrate IdentityAuthService with Blazor's authentication framework by creating a custom AuthenticationStateProvider.

**New File:** `src/SentenceStudio.AppLib/Services/MauiAuthenticationStateProvider.cs`

**Code Sketch:**

```csharp
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

public class MauiAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly IAuthService _authService;
    private readonly ILogger<MauiAuthenticationStateProvider> _logger;
    private ClaimsPrincipal _currentUser = new ClaimsPrincipal(new ClaimsIdentity());

    public MauiAuthenticationStateProvider(
        IAuthService authService,
        ILogger<MauiAuthenticationStateProvider> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        // On app startup, attempt silent sign-in from SecureStorage
        if (!_authService.IsSignedIn)
        {
            _logger.LogInformation("Not signed in, attempting silent refresh");
            await _authService.SignInAsync(); // Parameterless = silent refresh
        }

        if (_authService.IsSignedIn)
        {
            _currentUser = CreateClaimsPrincipalFromToken(
                await _authService.GetAccessTokenAsync(Array.Empty<string>())
            );
        }
        else
        {
            _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        }

        return new AuthenticationState(_currentUser);
    }

    public async Task LogInAsync(string email, string password)
    {
        var loginTask = LogInAsyncCore(email, password);
        NotifyAuthenticationStateChanged(loginTask);
        await loginTask;
    }

    private async Task<AuthenticationState> LogInAsyncCore(string email, string password)
    {
        var result = await _authService.SignInAsync(email, password);
        
        if (result is not null)
        {
            _currentUser = CreateClaimsPrincipalFromToken(result.AccessToken);
        }
        else
        {
            _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        }

        return new AuthenticationState(_currentUser);
    }

    public async Task LogOutAsync()
    {
        await _authService.SignOutAsync();
        _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        NotifyAuthenticationStateChanged(
            Task.FromResult(new AuthenticationState(_currentUser))
        );
    }

    private ClaimsPrincipal CreateClaimsPrincipalFromToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return new ClaimsPrincipal(new ClaimsIdentity());

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);
            
            var identity = new ClaimsIdentity(jwt.Claims, "jwt");
            return new ClaimsPrincipal(identity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse JWT token");
            return new ClaimsPrincipal(new ClaimsIdentity());
        }
    }
}
```

**Registration in ServiceCollectionExtentions.cs (AddAuthServices):**

```csharp
public static IServiceCollection AddAuthServices(this IServiceCollection services, IConfiguration configuration)
{
    services.AddSingleton<IAuthService, IdentityAuthService>();

    // Register authorization core + custom AuthenticationStateProvider
    services.AddAuthorizationCore();
    services.AddScoped<AuthenticationStateProvider, MauiAuthenticationStateProvider>();

    // Named HttpClient for auth endpoints (unchanged)
    var apiBaseUrl = configuration.GetValue<string>("ApiBaseUrl");
    if (!string.IsNullOrEmpty(apiBaseUrl))
    {
        services.AddHttpClient("AuthClient", client =>
        {
            client.BaseAddress = new Uri(apiBaseUrl);
        });
    }

    services.AddTransient<AuthenticatedHttpMessageHandler>();
    return services;
}
```

### Phase 2: Update Routes.razor to Use AuthorizeRouteView

**Goal:** Replace plain RouteView with AuthorizeRouteView to enforce authentication at the routing level.

**File:** `src/SentenceStudio.UI/Routes.razor`

**Before:**

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

**After:**

```razor
<Router AppAssembly="typeof(Routes).Assembly">
    <Found Context="routeData">
        <AuthorizeRouteView RouteData="routeData" DefaultLayout="typeof(Layout.MainLayout)">
            <Authorizing>
                <div class="d-flex justify-content-center align-items-center vh-100">
                    <div class="spinner-border text-primary" role="status">
                        <span class="visually-hidden">Checking authentication...</span>
                    </div>
                </div>
            </Authorizing>
            <NotAuthorized>
                @* Render Auth page when user is unauthenticated for a protected route *@
                <SentenceStudio.UI.Pages.Auth />
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

**Key Changes:**
- `<AuthorizeRouteView>` replaces `<RouteView>`
- `<Authorizing>` fragment for loading state
- `<NotAuthorized>` renders Auth page (inline, no navigation required)

### Phase 3: Strip Auth Gate Logic from MainLayout.razor

**Goal:** Remove all manual auth checking from MainLayout. Let AuthorizeRouteView handle auth enforcement.

**File:** `src/SentenceStudio.UI/Layout/MainLayout.razor`

**Remove These:**
- `authCheckComplete`, `showAuthInline`, `isAuthGate` flags
- `OnInitializedAsync()` auth checking logic
- Conditional rendering of Auth page inline
- LocationChanged auth gate updates

**Keep These:**
- Sidebar + mobile nav rendering (already conditional on `!isAuthGate && !isOnboarding`)
- Onboarding flow (separate concern, not auth-related)
- Theme service, sidebar collapse logic

**Code Changes:**

```razor
@code {
    private IJSObjectReference? jsModule;
    private bool sidebarCollapsed;
    private bool isOnboarding;

    protected override async Task OnInitializedAsync()
    {
        NavigationManager.LocationChanged += OnLocationChanged;

        var uri = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
        
        // Only check onboarding state — auth is handled by AuthorizeRouteView
        if (!Preferences.Get("is_onboarded", false))
        {
            isOnboarding = true;
            if (!uri.AbsolutePath.StartsWith("/onboarding", StringComparison.OrdinalIgnoreCase))
            {
                NavigationManager.NavigateTo("/onboarding");
            }
        }
        else
        {
            isOnboarding = false;
        }

        sidebarCollapsed = Preferences.Get("SidebarCollapsed", false);
    }

    // ... rest of component unchanged (theme, sidebar, location changed for onboarding)
}
```

**Simplified Main Content Rendering:**

```razor
<div class="d-flex flex-column flex-grow-1 overflow-hidden">
    <main class="flex-grow-1 overflow-auto p-3 main-content">
        @Body
    </main>
</div>
```

No more conditional auth checking or inline Auth page rendering. AuthorizeRouteView handles it.

### Phase 4: Add [Authorize] Attributes to Protected Pages

**Goal:** Mark pages requiring authentication with `[Authorize]` attribute.

**Examples:**

**src/SentenceStudio.UI/Pages/Index.razor (Dashboard):**

```razor
@page "/"
@attribute [Authorize]

<PageHeader Title="Dashboard" />

@* Rest of dashboard content *@
```

**src/SentenceStudio.UI/Pages/Vocabulary.razor:**

```razor
@page "/vocabulary"
@attribute [Authorize]

<PageHeader Title="Vocabulary" />

@* Rest of vocabulary content *@
```

**Public Pages (No [Authorize]):**
- `/auth` — Auth landing page
- `/auth/login` — Login page
- `/auth/register` — Register page
- `/onboarding` — Onboarding flow (separate gate)

**Result:** When an unauthenticated user navigates to `/`, AuthorizeRouteView detects the [Authorize] attribute and renders the `<NotAuthorized>` fragment (Auth page) instead of the Index component.

### Phase 5: Update Auth.razor to Use MauiAuthenticationStateProvider

**Goal:** Replace direct IAuthService calls with MauiAuthenticationStateProvider.LogInAsync() to trigger framework auth state changes.

**File:** `src/SentenceStudio.UI/Pages/Auth.razor`

**Before:**

```csharp
private async Task LoginAsAsync(UserProfile profile)
{
    loginError = null;
    Preferences.Set(ActiveProfileIdKey, profile.Id);
    AppState.CurrentUserProfile = profile;

    // Verify server authentication before granting access
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

**After:**

```csharp
[Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

private async Task LoginAsAsync(UserProfile profile)
{
    loginError = null;
    Preferences.Set(ActiveProfileIdKey, profile.Id);
    AppState.CurrentUserProfile = profile;

    // Verify server authentication via AuthenticationStateProvider
    var authState = await AuthStateProvider.GetAuthenticationStateAsync();
    if (!authState.User.Identity?.IsAuthenticated ?? false)
    {
        // Attempt silent sign-in (refresh from SecureStorage)
        await ((MauiAuthenticationStateProvider)AuthStateProvider).LogInAsync("", "");
        authState = await AuthStateProvider.GetAuthenticationStateAsync();
    }

    if (authState.User.Identity?.IsAuthenticated ?? false)
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

**Note:** The official pattern is to call LogInAsync with email/password. For local profile selection (which doesn't have credentials), we'll need to extend MauiAuthenticationStateProvider with a `LogInSilentlyAsync()` method that attempts refresh without credentials.

**Better Approach (Recommended):**

```csharp
public async Task LogInSilentlyAsync()
{
    var loginTask = LogInSilentlyAsyncCore();
    NotifyAuthenticationStateChanged(loginTask);
    await loginTask;
}

private async Task<AuthenticationState> LogInSilentlyAsyncCore()
{
    // Attempt silent sign-in from SecureStorage
    await _authService.SignInAsync();
    
    if (_authService.IsSignedIn)
    {
        _currentUser = CreateClaimsPrincipalFromToken(
            await _authService.GetAccessTokenAsync(Array.Empty<string>())
        );
    }
    else
    {
        _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
    }

    return new AuthenticationState(_currentUser);
}
```

### Phase 6: WebApp Auth Integration (Lower Risk)

**Goal:** Decide whether to create a WebAuthenticationStateProvider or rely on existing ASP.NET Core Identity middleware.

**Current State:** WebApp uses ASP.NET Core Identity with cookie authentication. ServerAuthService checks `HttpContext.User.Identity.IsAuthenticated` for auth state.

**Option A: Keep WebApp As-Is (Recommended for MVP)**

WebApp's cookie-based auth already integrates with Blazor Server's built-in authentication via `<CascadingAuthenticationState>` in the App.razor (if added). The ServerAuthService is a thin adapter that makes IAuthService work in a server context.

**Required Changes:**
- Add `<CascadingAuthenticationState>` to WebApp's App.razor (wrapping `<Routes />`)
- WebApp's Program.cs already calls `builder.Services.AddAuthorization()`, so AuthorizeRouteView will work
- No custom AuthenticationStateProvider needed — ASP.NET Core Identity middleware provides one automatically

**Option B: Create WebAuthenticationStateProvider (For Consistency)**

If we want a unified pattern across WebApp and mobile, create a `WebAuthenticationStateProvider` that wraps HttpContext.User:

```csharp
public class WebAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public WebAuthenticationStateProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var user = _httpContextAccessor.HttpContext?.User 
            ?? new ClaimsPrincipal(new ClaimsIdentity());
        return Task.FromResult(new AuthenticationState(user));
    }
}
```

Register it:

```csharp
builder.Services.AddScoped<AuthenticationStateProvider, WebAuthenticationStateProvider>();
```

**Recommendation:** Option A (keep WebApp as-is) is lower risk. Option B adds consistency but requires more testing.

### Phase 7: Remove Boolean Preferences

**Goal:** Eliminate `app_is_authenticated` preference and rely entirely on AuthenticationStateProvider.

**Files to Update:**
- Auth.razor — remove `Preferences.Set(AuthenticatedPreferenceKey, true)`
- MainLayout.razor — already removed in Phase 3

**Reasoning:** The boolean preference was a workaround for manual auth gates. With AuthorizeRouteView + AuthenticationStateProvider, the framework tracks auth state via ClaimsPrincipal. Preferences are no longer needed.

**Exception:** Keep `active_profile_id` and `is_onboarded` preferences — these are app state, not auth state.

## Code Sketches: Key Files After Refactor

### src/SentenceStudio.AppLib/Services/MauiAuthenticationStateProvider.cs

```csharp
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

public class MauiAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly IAuthService _authService;
    private readonly ILogger<MauiAuthenticationStateProvider> _logger;
    private ClaimsPrincipal _currentUser = new ClaimsPrincipal(new ClaimsIdentity());

    public MauiAuthenticationStateProvider(
        IAuthService authService,
        ILogger<MauiAuthenticationStateProvider> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (!_authService.IsSignedIn)
        {
            await _authService.SignInAsync(); // Silent refresh from SecureStorage
        }

        if (_authService.IsSignedIn)
        {
            var token = await _authService.GetAccessTokenAsync(Array.Empty<string>());
            _currentUser = CreateClaimsPrincipalFromToken(token);
        }
        else
        {
            _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        }

        return new AuthenticationState(_currentUser);
    }

    public async Task LogInAsync(string email, string password)
    {
        var loginTask = LogInAsyncCore(email, password);
        NotifyAuthenticationStateChanged(loginTask);
        await loginTask;
    }

    private async Task<AuthenticationState> LogInAsyncCore(string email, string password)
    {
        var result = await _authService.SignInAsync(email, password);
        
        if (result is not null)
        {
            _currentUser = CreateClaimsPrincipalFromToken(result.AccessToken);
        }
        else
        {
            _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        }

        return new AuthenticationState(_currentUser);
    }

    public async Task LogInSilentlyAsync()
    {
        var loginTask = LogInSilentlyAsyncCore();
        NotifyAuthenticationStateChanged(loginTask);
        await loginTask;
    }

    private async Task<AuthenticationState> LogInSilentlyAsyncCore()
    {
        await _authService.SignInAsync(); // Parameterless = silent refresh
        
        if (_authService.IsSignedIn)
        {
            var token = await _authService.GetAccessTokenAsync(Array.Empty<string>());
            _currentUser = CreateClaimsPrincipalFromToken(token);
        }
        else
        {
            _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        }

        return new AuthenticationState(_currentUser);
    }

    public async Task LogOutAsync()
    {
        await _authService.SignOutAsync();
        _currentUser = new ClaimsPrincipal(new ClaimsIdentity());
        NotifyAuthenticationStateChanged(
            Task.FromResult(new AuthenticationState(_currentUser))
        );
    }

    private ClaimsPrincipal CreateClaimsPrincipalFromToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return new ClaimsPrincipal(new ClaimsIdentity());

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);
            
            var identity = new ClaimsIdentity(jwt.Claims, "jwt");
            return new ClaimsPrincipal(identity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse JWT token");
            return new ClaimsPrincipal(new ClaimsIdentity());
        }
    }
}
```

### src/SentenceStudio.UI/Routes.razor

```razor
<Router AppAssembly="typeof(Routes).Assembly">
    <Found Context="routeData">
        <AuthorizeRouteView RouteData="routeData" DefaultLayout="typeof(Layout.MainLayout)">
            <Authorizing>
                <div class="d-flex justify-content-center align-items-center vh-100">
                    <div class="spinner-border text-primary" role="status">
                        <span class="visually-hidden">Checking authentication...</span>
                    </div>
                </div>
            </Authorizing>
            <NotAuthorized>
                <SentenceStudio.UI.Pages.Auth />
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

### src/SentenceStudio.UI/Layout/MainLayout.razor (Simplified)

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

        var uri = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
        
        // Only check onboarding state — auth is handled by AuthorizeRouteView
        if (!Preferences.Get("is_onboarded", false))
        {
            isOnboarding = true;
            if (!uri.AbsolutePath.StartsWith("/onboarding", StringComparison.OrdinalIgnoreCase))
            {
                NavigationManager.NavigateTo("/onboarding");
            }
        }
        else
        {
            isOnboarding = uri.AbsolutePath.StartsWith("/onboarding", StringComparison.OrdinalIgnoreCase);
        }

        sidebarCollapsed = Preferences.Get("SidebarCollapsed", false);
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

### src/SentenceStudio.AppLib/ServiceCollectionExtentions.cs (AddAuthServices)

```csharp
public static IServiceCollection AddAuthServices(this IServiceCollection services, IConfiguration configuration)
{
    services.AddSingleton<IAuthService, IdentityAuthService>();

    // Register authorization core + custom AuthenticationStateProvider
    services.AddAuthorizationCore();
    services.AddScoped<AuthenticationStateProvider, MauiAuthenticationStateProvider>();

    var apiBaseUrl = configuration.GetValue<string>("ApiBaseUrl");
    if (!string.IsNullOrEmpty(apiBaseUrl))
    {
        services.AddHttpClient("AuthClient", client =>
        {
            client.BaseAddress = new Uri(apiBaseUrl);
        });
    }

    services.AddTransient<AuthenticatedHttpMessageHandler>();
    return services;
}
```

### src/SentenceStudio.WebApp/Program.cs (Auth Section)

```csharp
// Add CascadingAuthenticationState for Blazor Server auth integration
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    // ... identity options unchanged
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    // ... cookie options unchanged
});

builder.Services.AddAuthorization();

// Server-side IAuthService using Identity directly (UserManager + SignInManager)
builder.Services.AddScoped<IAuthService, ServerAuthService>();
```

**Note:** No need to register a custom AuthenticationStateProvider for WebApp — ASP.NET Core Identity middleware provides one automatically.

## Risk Assessment

### Low Risk

1. **Phase 1 (MauiAuthenticationStateProvider):** New file, no existing code changes. Safe to add and test in isolation.
2. **Phase 2 (AuthorizeRouteView):** Routes.razor is simple and rarely changed. Updating it is low-risk.
3. **Phase 7 (Remove preferences):** Deleting unused preferences is safe cleanup.

### Medium Risk

4. **Phase 3 (Strip MainLayout auth logic):** MainLayout is shared across WebApp and mobile. Changes here affect both contexts. Requires thorough testing on both platforms.
5. **Phase 4 ([Authorize] attributes):** Adding attributes to pages is low-risk per page, but we need to audit all pages to ensure public pages (auth, onboarding) don't get locked.
6. **Phase 5 (Update Auth.razor):** Auth.razor has complex logic for local profile selection. Refactoring login flow requires careful testing to avoid breaking existing user workflows.

### High Risk

7. **Phase 6 (WebApp auth integration):** WebApp's cookie-based auth currently works. Adding CascadingAuthenticationState should be safe, but any changes to ServerAuthService risk breaking the WebApp entirely. Recommend Option A (minimal changes) to mitigate risk.

### Breaking Change Potential

**Mobile:** High risk of breaking mobile auth flow during migration. Mitigation: Feature flag the new auth pattern (e.g., `Auth:UseNewAuthPattern=false` in config) and test thoroughly before flipping the flag.

**WebApp:** Low risk if we use Option A (CascadingAuthenticationState only). Option B (custom AuthenticationStateProvider) increases risk.

### Rollback Strategy

1. Keep boolean preferences during initial rollout (Phase 3) as a fallback. Remove them in Phase 7 only after confirming new auth pattern works.
2. Feature flag: `Auth:UseFrameworkAuth=true/false` in appsettings.json to toggle between old and new patterns.
3. Comprehensive E2E tests for both WebApp and mobile before merging to main.

## References

All implementation patterns are sourced from official Microsoft Learn documentation:

1. **ASP.NET Core Blazor Hybrid authentication and authorization**  
   https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/security/?view=aspnetcore-10.0&pivots=maui  
   Custom AuthenticationStateProvider patterns, NotifyAuthenticationStateChanged usage

2. **.NET MAUI Blazor Hybrid and Web App with ASP.NET Core Identity**  
   https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/security/maui-blazor-web-identity?view=aspnetcore-10.0  
   Official sample app architecture, AuthorizeRouteView setup, token storage with SecureStorage

3. **ASP.NET Core Blazor Hybrid security considerations**  
   https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/security/security-considerations?view=aspnetcore-10.0  
   Security best practices, token handling, trust boundaries

4. **Microsoft Docs Search Results**  
   Custom AuthenticationStateProvider implementation examples, AuthorizeRouteView usage, SecureStorage token persistence patterns

All code sketches in this document are derived from official Microsoft documentation patterns adapted to SentenceStudio's existing IAuthService architecture.
