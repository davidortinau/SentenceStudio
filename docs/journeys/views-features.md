# SentenceStudio original app: views and features journey audit (non-activity, non-Sam)

Scope: every view and feature except the learning activities themselves and the Sam/learning coach.
Method: read-only source review. Nothing was built, run, edited or launched.

Source roots (all file paths below are relative to `src/` unless they start with `docs/` or `.claude/`):

- FR = frozen reference: a frozen copy of `main` at commit `59c72262` (the approved native-rebuild reference snapshot) (pinned to commit 59c72262). Every `file:line` citation is from FR.
- LIVE = the developer's `main` checkout (main, uncommitted). Compared with `diff -rq` on `src/`, `docs/`, `.claude/`. See section 17.

Terms: "Reference head" = a temporary MAUI Blazor Hybrid head (`SentenceStudio.iOS`, `.Android`, `.MacCatalyst`, `.MacOS`, `.Windows`). "Web" = `SentenceStudio.WebApp` (Blazor Web App, Interactive Server) hosting the same shared Razor from `SentenceStudio.UI`. Localization keys are shown as `Key_Name`; the English/Korean text lives in `SentenceStudio.Shared/Resources/Strings/AppResources.resx` (2179 entries) and `AppResources.ko.resx` (2117 entries).

---------------------------------------------------------------------------------------------------

## 0. Summary table

Platform legend: W = Web (WebApp), iOS / And / MCat / MacOS / Win = MAUI reference heads. "All" = W + all MAUI heads. Everything rendered by `SentenceStudio.UI` Razor is available on every head that hosts a BlazorWebView; exceptions are listed.

| # | Area | Views / components | Routes | Platforms |
|---|------|--------------------|--------|-----------|
| 1 | App launch, shell, navigation | `Routes.razor`, `Layout/MainLayout.razor`, `Layout/NavMenu.razor`, `Shared/PageHeader.razor`, `NavigationMemoryService` | (layout, no route); `/not-found` style fallback inside Routes | All |
| 2 | Sign in / sign up / reset | `LoginPage`, `RegisterPage`, `ForgotPasswordPage`, `Auth` (dev picker), WebApp `Account/*` pages and `/account-action/*` endpoints | `/auth/login`, `/auth/register`, `/auth/forgot-password`, `/auth`, `/Account/Login`, `/Account/Register`, `/Account/ForgotPassword`, `/Account/ResetPassword`, `/Account/ConfirmEmail`, `/Account/AccessDenied`, `/account-action/*` | Login/Register/Forgot: All. `/Account/*` and `/account-action/*`: W only. `/auth` picker: dev only |
| 3 | Onboarding / first run | `Onboarding` (6 steps) | `/onboarding` | All |
| 4 | Dashboard and Today's Plan | `Index` (new-user, Today's Plan mode, Choose My Own mode, vocab stats, number stats), `PlanActivityPresentation` | `/` | All |
| 5 | Learning resources | `Resources`, `ResourceAdd`, `ResourceEdit`, `ReferenceSentencesSection` (word-level) | `/resources`, `/resources/add`, `/resources/edit/{Id}` | All |
| 6 | Vocabulary | `Vocabulary`, `VocabularyWordEdit`, duplicate manager, bulk edit, cleanup tools | `/vocabulary`, `/vocabulary/edit/{Id}` | All |
| 7 | Skills | `Skills`, `SkillAdd`, `SkillEdit` | `/skills`, `/skills/add`, `/skills/edit/{Id}` | All |
| 8 | Import | `ImportContent` (paste wizard), `MediaImport` (YouTube: channels, single video, history), `ChannelDetail`, share extension, `SharedIngestIndicator` | `/import-content`, `/media-import`, `/import`, `/media-import/channel/{Id?}`, `/import/channel/{Id?}` | Pages: All. Share extension and share drain: iOS only. Channel worker: server only |
| 9 | Activity log / progress | `ActivityLog`, `ActivityDot`, `PlanSummaryCard`; dashboard vocab/number/streak widgets | `/activity-log` | All |
| 10 | Profile, languages, account | `Profile` | `/profile` | All (account delete path differs on W vs MAUI) |
| 11 | Settings, theme, localization | `Settings`, `ThemeSwatch`, `WhatsNewModal`, culture switch (in `Profile`) | `/settings` | All (culture persistence differs W vs MAUI) |
| 12 | Sync, offline, updates | `SyncService`, sync overlay, `UpdateAvailableBanner`, `EnvironmentBadge`, `WhatsNewModal` | none | Sync: MAUI only (`NoOpSyncService` on W). Update banner: MAUI only. Env badge: All |
| 13 | Help, feedback, about | `Feedback`, HelpKit `Help` menu item, Settings About, `DebugHealth` | `/feedback`, `/debug/health` | Feedback: All. HelpKit: iOS and MacCatalyst only. DebugHealth: Debug builds only |
| 14 | Operator / admin | `Operator/SamOpportunities` | `/operator/sam-opportunities` | W only, Development only (Sam; listed, not audited) |
| 15 | Adjacent (owned by other agents, referenced only) | Diary (`/diary`, `/diary/today`, `/diary/{Id}`), `VideoWatching`, `FlashcardActivity`, 14 dashboard activities, Minimal Pairs, Coach/Sam overlay | various | n/a |

Counts: 22 in-scope UI page components carrying 24 route templates (`MediaImport` and `ChannelDetail` each have an alias); 8 WebApp account pages plus 1 operator page; 8 `/account-action/*` endpoints; 9 `/api/auth/*` endpoints; 30+ distinct modal, sheet, toast and confirmation surfaces (section 14).

---------------------------------------------------------------------------------------------------

## 1. Global shell, launch and navigation

### 1.1 Entry points and launch

Files: `SentenceStudio.UI/Routes.razor:1-27`, `SentenceStudio.UI/Layout/MainLayout.razor`, `SentenceStudio.iOS/BlazorApp.cs`, `SentenceStudio.iOS/BlazorHostPage.cs`, `SentenceStudio.AppLib/Setup/SentenceStudioAppBuilder.cs:200-350`, `SentenceStudio.AppLib/Services/MauiAuthenticationStateProvider.cs:36-125`.

MAUI head cold launch journey:
1. `MauiProgram.CreateMauiApp` builds services (`SentenceStudio.iOS/MauiProgram.cs`), then `SentenceStudioAppBuilder.InitializeApp` runs (`SentenceStudioAppBuilder.cs:200`).
2. `syncService.InitializeDatabaseAsync()` runs synchronously and blocks startup; a failure throws (fatal) (`SentenceStudioAppBuilder.cs:226-243`).
3. Fire-and-forget: silent `IAuthService.SignInAsync()` to warm the token cache (`:263-275`); then DB init, scenario seeding, Korean NumberDrill content seed (`NumberContentSeeder.SeedAsync("ko")`), then `TriggerSyncAsync` (`:277-321`).
4. `ConnectivityChanged` to Internet triggers a sync (`:323-343`).
5. `BlazorApp` creates `BlazorHostPage` (MAUI `ContentPage` with one `BlazorWebView`, `HostPage = wwwroot/index.html`, root component `Routes`) (`SentenceStudio.iOS/BlazorHostPage.cs`). Safe area edges set to None; CSS owns safe areas (`app-safe-area-top`, `app-safe-area-inline` in `MainLayout.razor:20,22`).
6. `Router` renders `AuthorizeRouteView` with `MainLayout` as default layout. `Authorizing` shows a spinner "Checking authentication..." (`Routes.razor:5-11`); `NotAuthorized` renders `RedirectToLogin` (`Routes.razor:12-14`, `Shared/RedirectToLogin.razor`) which does `NavigateTo("/auth/login", replace: true)`.
7. `MauiAuthenticationStateProvider.GetAuthenticationStateAsync` (`:36-80`): if a valid cached token exists, authenticated principal; else if a refresh token exists in secure storage, an optimistic "refresh_token_pending" principal is returned immediately and a background refresh starts (this makes launch usable offline); else anonymous.
8. Background refresh: success republishes the real principal; explicit rejection (token store cleared) republishes anonymous, forcing `/auth/login`; transient failure keeps the optimistic state (`:82-120`).
9. `OnResume` of `BlazorApp` re-runs silent sign-in if `!IsSignedIn` (iOS/Android/MacCatalyst/Windows; `BlazorApp.cs`, 5 `OnResume` references each). The MacOS head (`MacOSBlazorApp.cs`) has no `OnResume` hook (unconfirmed whether it compensates elsewhere).

Web launch: `App.razor` (WebApp) server-renders with the per-browser `ss_appearance` cookie so the theme paints on first byte; `AppRoutes.razor` reuses `SentenceStudio.WebUI.Routes` assembly plus `TimeZoneCapture` (headless, stores browser IANA zone on the profile; `WebApp/Components/TimeZoneCapture.razor`). `NotAuthorized` redirects with `forceLoad` to `/auth/login` (`WebApp/Components/RedirectToLogin.razor`). Cookie auth: 90-day sliding expiry (`WebApp/Program.cs:124-125`), logout path `/account-action/SignOut` (`:99`).

States: Authorizing spinner; NotAuthorized redirect; DB init fatal error at startup (unconfirmed what the user sees; exception propagates from `InitializeApp`).

### 1.2 Post-login routing (first screen after authentication)

`MainLayout.OnInitializedAsync` (`Layout/MainLayout.razor:135-181`) and `OnAuthStateChanged` (`:183-235`) call `IPostLoginRouter.DecideRouteAsync(profileIdClaim)` (`SentenceStudio.Shared/Services/PostLoginRouter.cs:54-100`):
1. If an initial sync is in flight: defer; show full-screen sync overlay ("MainLayout_SyncingProfile", spinner) and wait for `InitialSyncCompleted` (`MainLayout.razor:101-112`, `:542-561`).
2. Else load the profile (by claim id, else active-profile preference). If the profile has non-empty TargetLanguage and NativeLanguage: route `/`, set preference `is_onboarded = true`.
3. Else route `/onboarding`.
4. Navigation only happens if not already on the target (`PostLoginRouteApplication.ShouldNavigate`).

Layout rules: sidebar and offcanvas nav are hidden while `isOnboarding`, unauthenticated, or syncing (`MainLayout.razor:23,53`).

### 1.3 Navigation shell

There are no tabs, no Shell flyout and no native navigation bar. The reference heads render a single full-window Blazor web view.

- Desktop and wide (Bootstrap `md` breakpoint, 768 px and up): left sidebar (`d-none d-md-flex`), collapsible via chevron button; collapsed state persisted in preference `SidebarCollapsed` (default false) (`MainLayout.razor:26-38`, `:173`, `:413-417`).
- Narrow: hamburger in `PageHeader` opens a Bootstrap offcanvas `#mobileNav` containing the same `NavMenu` (`MainLayout.razor:56-66`, `Shared/PageHeader.razor:14-19`). Navigation closes it via eval of `bootstrap.Offcanvas...hide()` (`NavMenu.razor:119-126`).
- `NavMenu` items (`Layout/NavMenu.razor:52-69`): Dashboard `/`, Activity `/activity-log`, Learning Resources `/resources`, Vocabulary `/vocabulary`, Minimal Pairs `/minimal-pairs`, Skills `/skills`, Import Content `/import-content`, Media Import `/media-import`; divider; Profile `/profile`, Settings `/settings`, Feedback `/feedback`; Log out (danger style); Help (only when HelpKit is registered).
- Section memory: `NavigationMemoryService` (`UI/Services/NavigationMemoryService.cs:15-33`) remembers the last URL per section and restores it when the sidebar item is tapped. The Dashboard section also owns `/conversation`, `/reading`, `/translation`, `/scene`, `/cloze`, `/vocab-quiz`, `/vocab-matching`, `/how-do-you-say`, `/shadowing`, `/video-watching`. Query keys `coach` and `pane` are never remembered (`:41`). Note: `/writing`, `/word-association`, `/numberdrill`, `/diary*`, `/flashcard-activity`, `/minimal-pairs/*` activity routes other than the listed ones are not in the Dashboard prefix list, so the Dashboard item is not highlighted there (observed from the table; behavioural impact unconfirmed).
- Back: each detail/edit page uses `PageHeader ShowBack OnBack` (chevron-left, explicit route such as `/resources`). `BlazorNavigationService.NavigateBack()` uses `javascript:history.back()` (`UI/Services/BlazorNavigationService.cs:23-26`). No Android hardware-back handling in the head code (`MainActivity.cs` is an empty `MauiAppCompatActivity`); behaviour is whatever BlazorWebView does (unconfirmed).
- Scroll reset on every location change (`MainLayout.razor:419-434`, JS `resetScroll`). Focus moves to `h1` on navigation (`Routes.razor:16`).
- `PageHeader` (`Shared/PageHeader.razor`): title, optional back, `ToolbarActions` (icon buttons, all sizes), `PrimaryActions` (desktop only), `SecondaryActions` (three-dots overflow dropdown). Many pages duplicate primary actions into the overflow so mobile can reach them.
- Not found: inline "Page not found" with `MainLayout` (`Routes.razor:18-25`); WebApp also has `/not-found`, `/Error`.

### 1.4 Layout-level surfaces (present on every authenticated screen)

| Surface | Trigger | Behaviour | Source |
|---------|---------|-----------|--------|
| Toasts | `ToastService` Show{Success,Error,Warning,Info} | stack at `.toast-container-ss`, durations 3000/5000/4000/3000 ms, manual close | `Shared/ToastContainer.razor`, `Services/ToastService.cs:9-26` |
| What's New modal | After sign-in render when preference `last_seen_version` != assembly major.minor and a release note exists | markdown to HTML, "Got it" saves `last_seen_version` | `MainLayout.razor:308-339`, `Shared/WhatsNewModal.razor`, `docs/release-notes/v1.0.md`, `v1.1.md` (embedded) |
| Update available banner | `VersionCheckService` (GET `/api/version/latest`) reports newer version | dismiss saves `dismissed_update_version`; no link to a store | `MainLayout.razor:341-364`, `Components/UpdateAvailableBanner.razor`, registered `SentenceStudioAppBuilder.cs:600` (MAUI only) |
| Environment badge | Config/env indicates non-production | bar shows LOCAL / DEV TUNNEL / DEV, API host, app version; hidden for production Azure | `Components/EnvironmentBadge.razor` |
| Shared ingest toast | Share-extension drain | processing, completed (auto-dismiss 6 s), "View" action route | `Components/SharedIngestIndicator.razor` (section 8.4) |
| Sam/Coach overlay | gate `CoachSurfaceGate` | out of scope | `MainLayout.razor:83-92` |
| Reconnect dialog | Blazor Server circuit loss | W only: "Rejoining the server...", retry, resume | `WebApp/Components/Layout/ReconnectModal.razor` |

---------------------------------------------------------------------------------------------------

## 2. Authentication, account lifecycle, token refresh

Backend (API): `SentenceStudio.Api/Auth/AuthEndpoints.cs:19-30` maps `/api/auth/{register,login,refresh,confirm-email,forgot-password,reset-password}` (anonymous), `DELETE /api/auth/account` and `POST /api/auth/change-password` (authorized). Policy: email confirmation required, password at least 8 chars with digit and uppercase (`SentenceStudio.Api/Program.cs:83-86`). Access token lifetime 1440 min default (`Auth/JwtTokenService.cs:34,72`); refresh token 90 days default (`:81`).

### 2.1 Sign in (`/auth/login`)

File: `UI/Pages/LoginPage.razor` (226 lines), `[AllowAnonymous]`.
Entry points: `RedirectToLogin`, NavMenu logout, onboarding logout, WebApp `/Account/Login` redirect, `/auth` page buttons.
Fields: Email, Password (show/hide toggle), "Remember me" checkbox. Links: Forgot password, Create one.
Journey:
1. App opens -> not authenticated -> `/auth/login`.
2. Enter email and password; button enabled only when both non-blank (`:98-100`).
3. `AuthService.SignInAsync(email, password)` -> POST `/api/auth/login` (`IdentityAuthService.cs:225`). Server returns 401 for unknown user, wrong password, OR unconfirmed email (same response, `AuthEndpoints.cs:145-162`), so the UI shows `Login_InvalidCredentials` in all three cases.
4. On success: preference `app_is_authenticated = true`; if Remember me, preferences `remember_me` and `remember_email` are WRITTEN but never READ anywhere in the codebase (grep: only `LoginPage.razor:123-124`), so the email is never prefilled (dead feature).
5. `MauiAuthenticationStateProvider.NotifySignedIn()` publishes the new principal (`:151-154`).
6. Web only: if the access token is a `userId|token` auto-sign-in pair, a full-page navigation to `/account-action/AutoSignIn` writes the cookie. MAUI: in-app `PostSignInNavigation.RouteAfterSignIn(returnUrl "/")` with `forceLoad` only on the web host (`:127-194`).
7. `MainLayout` runs the post-login router (section 1.2). Side effect on token store: `IdentityAuthService.StoreTokens` persists JWT/refresh/expiry to secure storage, sets `active_profile_id` from `UserProfileId`, backfills Name/Email from JWT claims, applies saved locale, calls `SyncService.BeginInitialSync()` synchronously then `TriggerSyncAsync()` in the background (`IdentityAuthService.cs:640-690`).
States: busy spinner "Login_SigningIn"; error alert for invalid credentials (`Login_InvalidCredentials`), `NotSupportedException` (`Login_NotAvailable`), `HttpRequestException` (`Login_ConnectionError`), other (`Login_UnexpectedError`) (`:196-214`).
Offline: sign in requires network (connection error message).

### 2.2 Sign up (`/auth/register`)

File: `UI/Pages/RegisterPage.razor` (222 lines), anonymous.
Fields: Display name, Email, Password with live checklist (8+ chars, one uppercase, one number), Confirm password; mismatch warning. Submit needs all valid (`:145-155`).
Journey:
1. Fill form -> `AuthService.RegisterAsync(email, password, displayName)` -> POST `/api/auth/register`.
2. Server creates ApplicationUser plus linked UserProfile (`AuthEndpoints.cs:71-84`). In Development it auto-confirms and returns tokens (`:86-125`); in production it emails a confirmation link and returns a message (`:127-135`). Duplicate email returns the same generic success (anti-enumeration, `:50-67`).
3. Client: if a token came back -> `app_is_authenticated = true` -> navigate `/onboarding` (`RegisterPage.razor:167-190`). If null -> `registrationComplete = true` shows "Check your email" with Go to Sign In (`:17-27`, `:192-196`).
4. Email confirmation (production): the emailed link is `{apiHost}/api/auth/confirm-email?userId&token` (`AuthEndpoints.cs:127-133`), which returns raw JSON `{ "message": "Email confirmed." }` to the browser (`:335-354`) (no friendly landing page; observed). A separate WebApp endpoint `/account-action/ConfirmEmail` redirects to `/auth/login?message=EmailConfirmed` (`WebApp/Auth/AccountEndpoints.cs:195-210`) but is not the link the API emails (unconfirmed which is used in production).
5. `/auth/login?message=EmailConfirmed|PasswordReset` and `?error=InvalidLink|ConfirmFailed` query parameters are produced by WebApp redirects, but `LoginPage.razor` declares no `SupplyParameterFromQuery` for them, so no banner is shown (observed: no such parameter in the file).

### 2.3 Forgot / reset password

- `/auth/forgot-password` (`UI/Pages/ForgotPasswordPage.razor`, 126 lines): email field; POST `/api/auth/forgot-password` via the `AuthClient` HttpClient; ALWAYS shows `ForgotPassword_ResetSent` (even on error) to avoid enumeration (`:84-110`). Back to sign in link.
- API builds the reset URL as `{scheme}://{api host}/Account/ResetPassword?email&token` (`AuthEndpoints.cs:357-388`) and emails it; that page exists only in the WebApp (`WebApp/Components/Pages/Account/ResetPassword.razor`). Whether the production API host and WebApp host coincide is unconfirmed.
- `/Account/ResetPassword` (W only): form POST to `/account-action/ResetPassword` with new and confirm password (min 8; hint "8 characters, uppercase, digit"); error `ResetFailed` banner; success redirect `/auth/login?message=PasswordReset` (`AccountEndpoints.cs:249-266`). Native clients have no in-app reset-token entry screen: the reset must be completed in a browser (no deep link handling found).
- Dev only: reset link is written to the server log (`AccountEndpoints.cs:212-247`).

### 2.4 Sign out

Entry points: NavMenu "Log out" (`NavMenu.razor:35-38`, `:92-112`), Onboarding "Logout" link (`Onboarding.razor:201-206`, `:290-309`), MacOS Account menu "Log Out/Log In" Cmd+Shift+L (`SentenceStudio.MacOS/MacOSAppMenu.cs:61-70`), WebApp top-row "Sign out" (template layout, `Layout/LoginDisplay.razor`).
Steps: `ProgressCacheService.InvalidateAll()`; `MauiAuthenticationStateProvider.LogOutAsync()` -> `IdentityAuthService.SignOutAsync` clears memory, removes `active_profile_id`, retires legacy keychain adoption, clears token store (throws if removal cannot be verified; outcome reported but the session is anonymous regardless) (`IdentityAuthService.cs:346-380`, `MauiAuthenticationStateProvider.cs:233-262`); preferences `app_is_authenticated=false`, remove `active_profile_id`; `AppState.CurrentUserProfile = null`; navigate to `/auth/login` (MAUI) or `/account-action/SignOut` full load (W) which signs out the cookie and redirects `/auth/login`.
Local data (SQLite rows, preferences other than the above) is NOT deleted on sign-out (observed: nothing removes it).

### 2.5 Token refresh and session persistence

`IdentityAuthService` (`AppLib/Services/IdentityAuthService.cs`):
- Silent `SignInAsync()` first restores JWT from secure storage if it has more than 2 minutes left (`:145-162`), else refreshes with the refresh token using a single-flight task (`:165-197`, `:481-494`).
- Refresh: POST `/api/auth/refresh`, 15 s timeout. Transient errors (network, timeout, 5xx) keep the token; 401/403 increments a counter and clears all three stored credentials only after 2 consecutive rejections (`:506-585`).
- `GetAccessTokenAsync` returns the cached token if it has more than 60 s left (`:443-450`); `AuthenticatedHttpMessageHandler` attaches it to API calls.
- Web uses cookies (`ServerAuthService`) not JWT storage.

### 2.6 Change password, delete account (Profile page; details in section 10)

- Change password: `IAuthService.ChangePasswordAsync` -> POST `/api/auth/change-password`.
- Delete account: see the discrepancy in section 10.4.

### 2.7 Local profile picker (`/auth`, dev only)

File: `UI/Pages/Auth.razor` (316 lines), anonymous. Landing with Sign in and Create account buttons. If environment is Development AND API URL does not contain `azurecontainerapps.io`, it lists local `UserProfile` rows (name, native -> target) with a "choose local user" button and a delete (trash) button (`:128-137`). Selecting a profile sets `active_profile_id`, `AppState.CurrentUserProfile`, requires `IAuthService.IsSignedIn` (else error `Auth_ServerAuthRequired`), sets `is_onboarded` if profile is complete, navigates `/` or `/onboarding` (`:140-179`). Delete uses a confirm modal (`Auth_ConfirmDelete`), removes the Identity user via reflection (WebApp only), the local profile, and clears active preferences (`:181-305`). Calls `ProfileRepo.EnsureMultiUserBackfillAsync()` on load (`:136`), which assigns unowned rows to the first profile (`UserProfileRepository.cs` `BackfillUserProfileIdsAsync`, fallback "first profile by Id"): a known cross-owner risk in the original; native must not copy it.
Multi-profile / user switching in production: there is no in-app profile switcher. Switching accounts = sign out then sign in as another account. Each account maps to exactly one `UserProfile` (`ApplicationUser.UserProfileId`); `UserProfile.TargetLanguages` (comma list) supports several target languages per profile but no Razor page edits it (only `TargetLanguage` is editable; `Profile.razor:47-53`).

### 2.8 WebApp-only account endpoints (`WebApp/Auth/AccountEndpoints.cs`)

| Endpoint | Purpose | Lines |
|----------|---------|-------|
| POST `/account-action/Logout` | form sign-out, redirect `/` | `:28-35` |
| GET `/account-action/SignOut` | forceLoad sign-out, redirect `/auth/login` | `:37-45` |
| GET `/account-action/SetCulture?culture&returnUrl` | writes `.AspNetCore.Culture` cookie (1 year, only en/ko), local redirect only, default `/profile` | `:47-74` |
| GET `/account-action/AutoSignIn` | one-time userId+token to cookie sign-in | `:76-193` |
| GET `/account-action/ConfirmEmail` | confirm email, redirect with message | `:195-210` |
| POST `/account-action/ForgotPassword` | form variant of forgot flow | `:212-247` |
| POST `/account-action/ResetPassword` | apply reset | `:249-266` |
| GET `/account-action/DeleteAccount?profileId` | delete UserProfile + Identity user for the signed-in cookie (or an explicit profileId if not signed in), then redirect `/auth` | `:268-` |

Other WebApp account pages: `/Account/Login`, `/Account/Register`, `/Account/ForgotPassword` redirect to the `/auth/*` pages; `/Account/AccessDenied` ("Access Denied", Go Home); `/Account/ConfirmEmail` static "Processing..." with Go to Sign In (never calls the endpoint itself); `/not-found`, `/Error`. Template leftovers exist and appear unused: `WebApp/Components/Layout/MainLayout.razor`, `NavMenu.razor` (Home/Counter/Weather links), `LoginDisplay.razor`, `AuthLayout.razor` (used by Account pages).

---------------------------------------------------------------------------------------------------

## 3. Onboarding / first run (`/onboarding`)

File: `UI/Pages/Onboarding.razor` (422 lines), `[Authorize]`. Layout nav hidden on this route.
Entry: post-login router when profile lacks languages; after registration; dashboard "starter" when target language empty (`Index.razor:800-810`).
Journey (step indicator of 6 dots; Back/Next on steps 1-4):
1. Step 0 Welcome -> "Get started".
2. Step 1 Native language: buttons English, Korean, French, German, Spanish, Japanese, Chinese (`:236`); tapping a language selects and advances.
3. Step 2 Target language: Korean, English, French, German, Spanish, Japanese, Chinese (`:237`); tapping advances and starts AI name suggestions (`NameGenerationService.GenerateNamesAsync(targetLanguage)`, `:277-283`, `:311-329`).
4. Step 3 Name: text input (required to proceed, `CanProceed` `:241-247`) plus suggested-name buttons (loading state while generating; failure silently logged).
5. Step 4 Learning preferences: session length buttons 5/10/15/20/30/45 min (default 15), CEFR level A1..C2 (default A2).
6. Step 5 Finish: "Create starter content" or "Skip setup".
   - Create: `SaveProfile`, then `ResourceRepo.GetStarterVocabulary(native, target)` (AI-generated "Sentence Studio Starter Vocabulary" resource), then a `SkillProfile` titled `Beginner {target}`; three progress rows with checkmarks; 500 ms pause; `FinishOnboarding` (`:331-369`). On error: toast "Failed to create content. You can set it up later." then still finishes.
   - Skip: `FinishOnboarding`.
7. `FinishOnboarding`: saves profile if none, sets `is_onboarded=true` and `app_is_authenticated=true`, navigates `/` (`:406-416`).
Side effects: updates the existing server-linked profile if one exists (created at registration) else creates a new local profile; `DisplayLanguage` is forced to "English" regardless of native language (`:381,396`); `active_profile_id` set; sync triggered via repository saves.
Escape hatch: "Logout" link (see 2.4).
Edge: AI name generation and starter vocabulary require network/AI; offline just yields empty suggestions or the error toast.
Note: Onboarding sets `PreferredSessionMinutes` default 15 while the `UserProfile` model default is 20 (`Shared/Models/UserProfile.cs`), and `Profile.razor` falls back to 15.

---------------------------------------------------------------------------------------------------

## 4. Dashboard and Today's Plan (`/`)

File: `UI/Pages/Index.razor` (1423 lines), `[Authorize]`. Companion: `UI/Services/PlanActivityPresentation.cs` (icons, labels, routes), `Shared.Services.Progress.ProgressService` (`SentenceStudio.Shared/Services/Progress/ProgressService.cs`), API `/api/v1/plans/*` (`Api/Plans/PlanEndpoints.cs:27-47`) used by `ApiPlanGenerationService` on MAUI.

### 4.1 Load and modes

1. If an initial sync is in progress, loading is deferred until `InitialSyncCompleted` (`:656-666`, `:711-736`).
2. `CheckNewUserAsync`: new user = zero resources AND zero vocabulary words (`:774-789`). If new and no sync in flight, one `TriggerSyncAsync` then re-check (`:677-695`).
3. Else read preference `DashboardMode` (default `TodaysPlan`; other value `ChooseOwn`) (`:701-702`), load vocab stats (30-day window, `GetVocabSummaryAsync`), number-mastery stats, and the plan if in plan mode (`:704-708`).
4. Header: new user "Dashboard_Welcome"; otherwise title "Dashboard_Title" with refresh icon; in Plan mode an overflow menu with Regenerate Plan (`:10-38`). On md+ a greeting "Dashboard_WelcomeGreeting" with the profile name (`:98-103`).
5. Mode switcher (button group): Today's Plan / Choose My Own (`:105-117`, `SetMode` `:751-770` persists the preference).

### 4.2 New-user state (`:41-94`)

Two cards. (a) "Quick start": create starter pack -> `CreateStarterResourceAsync` (`:791-893`): requires profile target language (else navigates `/onboarding`); if a starter exists for the language, just transitions; else creates a SkillProfile `{lang} Basics` if the user has none, saves 20 hard-coded Korean words (annyeonghaseyo ... boda) tagged with the user's target language (`:896-925`; comment admits non-Korean learners get Korean placeholder terms), and a resource `{lang} Starter Pack` (`MediaType "Vocabulary List"`, tags `starter,beginner,{lang}`). Errors: duplicate (unique/23505) -> `Dashboard_StarterAlreadyExists` and exit new-user state; other -> `Dashboard_StarterGenericError`; null id -> `Dashboard_StarterCreateFailed`. (b) "Create your own": link to `/resources/add`. Two different starter-content paths exist: this one (static Korean list) and `ResourceRepo.GetStarterVocabulary` (AI prompt) used by Onboarding and the Resources empty state.

### 4.3 Today's Plan mode (`:119-428`)

Components, top to bottom:
1. Streak badge "N day streak" and "best streak" (`:130-142`). `GetStreakInfoAsync` (`ProgressService.cs:1501-1540`): 90-day lookback of `UserActivity` rows; counts distinct UTC dates walking back from today with gaps at most 1 day.
2. Progress card: completed/total, minutes spent of estimated total, green progress bar (`CompletionPercentage`) (`:145-160`).
3. "Today's insight" collapsible narrative (collapsed by default) (`:162-330`): story text; resource chips linking to `/resources/{id}` (media type icon, selection reason); vocab insight (new count, review count, total due, average mastery %); button "Preview plan vocabulary" (opens `/flashcard-activity?date=yyyy-MM-dd`, `:226-230`, `:1092-1100`) shown when preview words exist; struggling / new / ok category badges with accuracy; pattern insight alert; sample struggling words ("words to review/discover"); focus areas list. Fallback: legacy rationale with resource names (`:331-350`).
   - DEFECT in original: the resource chip href is `/resources/@resource.Id` (`:187`), but no `/resources/{id}` route exists (only `/resources`, `/resources/add`, `/resources/edit/{Id}`), so tapping it hits the Not Found page.
4. Plan item list (`:352-407`): per item icon (check-circle when complete), activity label, resource title, minutes ("N of M min" when started else "~M min"), vocab-due count, partial-progress bar when `MinutesSpent > 0 && !IsCompleted`. Tapping launches the item.
5. Empty state: "Dashboard_NoPlanYet", error text, "Generate plan" button (`:408-427`).

Plan generation (`ProgressService.GenerateTodaysPlanAsync`, `:230-260`): returns cached plan if present; otherwise single-flight per user, LLM plan with deterministic fallback plan (`GenerateFallbackPlanAsync`); persists `DailyPlanCompletion` rows per item. `GetCachedPlanAsync` (`:451`) checks memory cache, then reconstructs from DB rows. On MAUI the plan is generated via `ApiPlanGenerationService` -> `IPlansApiClient` (`AppLib/ServiceCollectionExtentions.cs:90`).
Activity types and route mapping (`PlanActivityPresentation.Route`, `:89-103`): VocabularyReview -> `vocab-quiz`; Reading `reading`; Listening and Shadowing -> `shadowing`; Cloze `cloze`; Translation `translation`; Writing `writing`; SceneDescription `scene`; Conversation `conversation`; VocabularyGame -> `vocab-matching`; VideoWatching `video-watching`; NumberDrill `numberdrill`; default `vocab-quiz`.
Item launch (`LaunchPlanItem`, `:1048-1090`): URL `/{route}` with query `resourceId` (or `resourceIds` for vocab-quiz and vocab-matching) unless activity is VocabularyReview/VocabularyGame/Cloze/NumberDrill; `skillId`; `planItemId`; `FocusVocabularyIds` (comma list); plus any extra `RouteParameters` that do not collide. Completion is written by activities via `UpdatePlanItemProgressAsync`/`MarkPlanItemCompleteAsync`: item completes when `minutesSpent >= EstimatedMinutes`; creates missing completion row if needed; triggers sync (`ProgressService.cs:840-935`, `:565-600`).
Journeys:
- Generate: empty state -> Generate plan -> spinner -> plan renders; error strings `Dashboard_UnableToGeneratePlan`, `Dashboard_NetworkErrorTryAgain`, `Dashboard_UnableToGeneratePlanLater` (`:957-985`).
- Regenerate: header overflow -> Regenerate Plan -> `ClearCachedPlanAsync` DELETES today's `DailyPlanCompletion` rows for the user (including completed items and minutes) and syncs, then generates a new plan (`:987-1009`, `ProgressService.cs:529-563`). No confirmation dialog and failures are only logged. Worth preserving or deliberately changing.
- Refresh: toolbar icon -> on MAUI (`DeviceInfo.Platform != Unknown`) triggers CoreSync, then reloads vocab stats and plan (`:1011-1046`). On W the sync call is skipped.
- Launch -> do activity -> return (browser/back) -> list shows completion; restart persists via DB rows.
States: plan loading spinner; HttpRequestException -> `Common_NetworkError`; generic `Dashboard_UnableToLoadPlan`.

### 4.4 Choose My Own mode (`:430-472`, code `:1102-1210`, `:1273-1320`)

Two Tom Select (JS) pickers: multi-select Learning Resources and single-select Skill Profile, then an Activities grid of 14 cards (`:634-650`): Conversation, Describe Scene, Translate, Cloze, Writing, Diary, Reading, Vocab Quiz, Vocab Matching, Shadowing, How Do You Say, Minimal Pairs, Word Association, Number Drill. On mobile only the first 6 show until "See all" (`showAllActivities`, `:447-456`, `:462`).
Selection persistence: per-profile preferences `SelectedResourceIds{profile}` / `SelectedSkillProfileId{profile}` via `ChooseOwnSelectionPreferences` (`Shared/Services/ChooseOwnSelectionState.cs:58-90`); legacy unscoped keys exist. Selections are reconciled against owned resources/skills; unavailable ones are dropped and navigation is refused with warning toast `Dashboard_ChooseOwnSelectionUnavailable` (`:1163-1202`, `:1273-1320`, `:1363-1369`). Empty active profile id refuses navigation.
Launch: `/{route}?resourceIds=a,b` for vocab-quiz, vocab-matching, word-association; otherwise `resourceId=<first>`; plus `skillId` (`:1303-1319`).

### 4.5 Progress widgets

- Vocabulary stats: New / Learning / Familiar / Review / Known cards with tooltips, total words and 7-day accuracy (`:474-534`, `ProgressService.GetVocabSummaryAsync :177-195`).
- Numbers mastery (Korean only): per context (`Time`, `Age`, `Counting`...) correct/total, mastery %, "N due" badge, from `NumberMasteryProgresses` where `LanguageCode == "ko"` (`:536-583`, `:1225-1263`). Hard-coded English strings ("Numbers - Mastery", "Start mastering Korean numbers") are not localized (`:537,578-580`).
- Service methods `GetRecentResourceProgressAsync`, `GetRecentSkillProgressAsync`, `GetSkillProgressAsync`, `GetPracticeHeatAsync` (`ProgressService.cs:71-226`) have NO Razor consumer; `docs/dashboard-progress-feature.md` describes resource/skill/practice-heat cards from the retired MauiReactor dashboard. The Blazor dashboard does not show them (observed via grep).

---------------------------------------------------------------------------------------------------

## 5. Learning resources

### 5.1 List (`/resources`)

File: `UI/Pages/Resources.razor` (290 lines). Header primary action "Add resource".
Controls: search box (calls `SearchResourcesAsync` then client-side type/language filters), media type select (`Video, Podcast, Image, Vocabulary List, Article, Other`, `AppLib/Common/Constants.cs:10`), language select (`English, Korean, Spanish, Japanese, Chinese, French, German, Italian, Portuguese, Russian`, `:11`), grid/list toggle persisted in preference `resources-view-mode` (default `grid`) (`:28-58`, `:151-155`, `:271-275`). Row/card: media-type icon (Video, Podcast, Image, Vocabulary List card-text, Article, default folder), title, "type - language", created date; whole card taps to `/resources/edit/{id}`. Count line. Both views use Blazor `<Virtualize>`.
Empty state: "No resources found" with "Add first resource" and "Create starter" (`CreateStarterResource`: needs native language else error toast; guards duplicates; calls `GetStarterVocabulary`; full-screen "Creating starter" overlay) (`:60-75`, `:120-130`, `:221-259`).
Smart resources: resources with `IsSmartResource` (types DailyReview, NewWords, Struggling, Phrases, Sentences; `SmartResourceService.cs:19-24`) appear in this list alongside normal ones, seeded per profile (`UserProfileRepository.EnsureSmartResourcesAsync :75-108`).
Data: `LearningResourceRepository.GetAllResourcesLightweightAsync(mediaType, languages)` (owner-scoped by repository).
Errors: `Resources_LoadFailed`, `Resources_SearchFailed` toasts.

### 5.2 Add (`/resources/add`)

File: `ResourceAdd.razor` (388 lines). Back confirms nothing (just returns to `/resources`; unsaved changes are lost silently).
Fields: Title (required), Description, Media type (default Video), Language (default English), Tags; when type is not "Vocabulary List": Media URL, Transcript, Translation; when "Vocabulary List": paste box with comma/tab delimiter radio and Import button, or `.txt/.csv` file import (`InputFile`, 1 MB cap), and a preview table with per-row Lexical Unit Type select (Word/Phrase/Sentence) and remove (`:66-180`, `:320-382`).
Rules: duplicate target terms skipped (case-insensitive trim); heuristic classification `VocabularyClassificationBackfillService.ClassifyHeuristic`; leftover pasted text is appended on Save (`:225-259`, `:288-313`).
Save -> `SaveResourceAsync` -> toast `ResourceAdd_SavedSuccess` -> `/resources`. Sync triggered by the repository.
Notes: the media/transcript fields exist but there is no audio/video player, upload, or YouTube fetch on this page (media files are not attached; `MediaUrl` is a text field). Image resources have no image upload UI here (unconfirmed elsewhere).

### 5.3 Edit / detail / delete (`/resources/edit/{Id}`)

File: `ResourceEdit.razor` (1075 lines). There is no read-only detail page; edit is the detail view.
States: loading; not found (message plus Back, toast `ResourceEdit_NotFound`); smart-resource mode (info banner "smart resource, auto-updated"; all inputs disabled; no Save/Delete; delete refused with log only `:444-453`).
Sections: Basic info (Title, Description, Media type, Language, Media URL, Tags, Created/Updated timestamps), Transcript, Translation, Vocabulary.
Vocabulary section:
- "Generate vocabulary" (AI, when transcript exists and not smart): `AiService.SendPrompt<List<VocabularyWord>>`, merges with existing words (`GetOrCreateWordAsync`), saves, toast with counts (`:863-990`).
- Quick add: typeahead search of the user's vocabulary for this resource's language (disabled when language is "Other"), keyboard navigation, "Add new word 'x'" inline create with native term, associates via `AddVocabularyToResourceAsync` (`:139-205`, `:502-720`).
- Bulk import expander: paste with delimiter and `.txt/.csv` file (`:206-258`, `:732-860`).
- Word chips with remove (disassociate only; the word itself is kept) (`:260-285`, `:677-703`).
Save: title required (`ResourceEdit_TitleRequired`); `SaveResourceAsync`; mutation outcome check; toast; stays on page? (it navigates only on delete; Save keeps the page, unconfirmed after `:432`). Delete: overflow -> confirm modal -> `DeleteResourceAsync` -> toast -> `/resources` (cascades to `ResourceVocabularyMapping` per `.claude/skills/e2e-testing/references/management-pages.md` section 3).
Side effects: repository writes trigger CoreSync.

### 5.4 Reference sentences (word-level, on vocabulary edit)

`Components/ReferenceSentencesSection.razor` (398 lines): generate AI suggestions, keep or discard each, add manual target/native sentence, edit, set core, flag/unflag, delete via per-row menu; badges Core/Flagged; speech register labels (`FormalPolite`, `InformalPolite`, `Casual`, `PlainWritten`). Embedded in `VocabularyWordEdit.razor:158`.

### 5.5 Resource-related media and transcripts

- YouTube transcripts: section 8.2. Transcript formatting/polish: `TranscriptFormattingService`. Transcript-to-example-sentence harvest: Settings "Harvest example sentences" (section 11.4). No standalone transcript viewer page; transcripts are consumed by Reading/Shadowing/VideoWatching (activities).
- Photo/mnemonic images: URL field on vocabulary word (section 6.2); photo-viewer JS assets exist (`UI/wwwroot/js/photo-viewer-*.js`), used by activities (not audited).

---------------------------------------------------------------------------------------------------

## 6. Vocabulary

### 6.1 Browse, search, filter (`/vocabulary`)

File: `Vocabulary.razor` (2044 lines). Query parameters: `q` (search), `duplicateTerm`, `focusWordId` (`:696-705`).
Header: Add (primary), mobile filter button opens bottom offcanvas `#vocabFilterPanel` (`:12-14`, `:276`), overflow (`:24-34`): view toggle, Find duplicates, Populate lemmas, Fix swapped languages, Assign orphaned words, Bulk edit.
Stats bar (md+): `filtered of total`, Unknown, Learning, Familiar, Known counts (`:48-54`); sort buttons Added / Updated with direction toggle; grid/list toggle.
View mode and sort are stored in the browser WebView `localStorage` keys `vocab-view-mode` and `vocab-sort` (`:1337-1366`, `:1368-1400`), not in IPreferencesService (so they follow the WebView storage, not the profile).
Search: unified box with filter syntax parsed by `ISearchQueryParser`; autocomplete suggestions with counts; removable filter chips; "clear filters" (`:104-133`, `:863-940`); URL is kept in sync (`SyncUrlWithFilters`, replace navigation `:972-978`). Syntax: `docs/search-syntax-guide.md`. Filter types: association (associated/orphaned), status (known/familiar/learning/unknown), tag, resource (title), lemma, language, encoding (strong/good/basic), type (word/phrase/sentence). Free text matches target, native, tags, lemma (`ApplyFilters :980-1058`). Dropdown shortcuts mirror each filter (desktop row and mobile panel).
Cards (grid) and rows (list): target and native term, status badge, encoding badge, known/orphan/lemma/tag indicators; tap opens editor with the current query in `returnQuery`. Data load: `GetAllVocabularyWordsWithResourcesAsync` + `ProgressService.GetAllProgressDictionaryAsync` + resources, in parallel (`:790-860`); words shown only if associated with the user's resources or orphaned (association restricted to `userResourceIds`).
Empty states: no words yet (Add first word); no matches (`Vocabulary_NoMatchFilter`).

### 6.2 Bulk edit (`:75-102`, `:1096-1330`)

Enter via overflow: checkboxes on cards/rows, Select all / none, action select: Set status (Unknown/Learning/Familiar; uses `SetUserDeclaredStatusAsync` per word with `active_profile_id`), Set language, Add tags, Delete (confirm modal `showBulkDeleteConfirm`, `:657-690`, `BulkDeleteVocabularyWordsAsync`). Partial success shows a warning toast; the vocab summary cache is invalidated (`CacheService.InvalidateVocabSummary()`).

### 6.3 Cleanup tools (`:406-570`, `:1501-1975`)

- Find duplicates / duplicate manager (also from word editor): groups with recommended keeper, memory-aid summary, per-group Merge (`MergeVocabularyWordsAsync`), Auto-merge all, show more, per-word Open editor; groups that cannot be merged automatically show a blocked reason.
- Populate lemmas: AI-assisted batches (`AiService`), results listed in the cleanup panel; `BulkUpdateLemmasAsync`.
- Fix swapped languages: detects English/Korean swapped terms, merges with an existing duplicate or rewrites terms (`:1805-1892`).
- Assign orphaned words: creates/reuses a "General Vocabulary" Vocabulary List resource and associates all orphans (`:1894-1975`).
All five are destructive-ish bulk writes with progress/result list; no confirmation except bulk delete.

### 6.4 Add / edit word (`/vocabulary/edit/{Id}`, `Id = 0` adds)

File: `VocabularyWordEdit.razor` (1181 lines). Query: `returnQuery`, `returnDuplicateTerm`, `returnFocusWordId`, `initialTargetTerm` (prefill from a no-match search).
Fields: Target term (+ speaker button), Native term, Lemma, Language (or Not set), Lexical unit type (Word/Phrase/Sentence), Tags, Mnemonic story (text) and Mnemonic image URL (preview image), encoding-strength indicator (computed), Resource associations (checkbox list, max-height scroll).
Phrase/Sentence: constituent-word typeahead; changing type shows a warning (`typeChangeWarning`) (`:793-866`).
Existing word only: Progress card (status badge; "earned through practice" note when Known; select to declare Unknown/Learning/Familiar, `OnStatusSelectChanged :1088`, Familiar shows info toast), details text, next review date, Practice History with attempt/exposure counts, an inline SVG mastery-over-time chart with known threshold line (`:270-330`, `:677-746`), per-context rows (activity, resource, passive vs active), and Reference sentences (5.4).
Actions: Save/Add (disabled until both terms present; duplicate (target,native) pair blocks with `VocabWordEdit_DuplicateExists` (`:867-990`)), Delete (confirm modal), Find duplicates, Play audio.
Audio (`PlayTermAudio :1110-1181`): looks up `StreamHistory` cache for phrase+voice, else TTS via `ElevenLabsSpeechService` using `SpeechVoicePreferences.VoiceId`, caches to `AppData/AudioCache`; offline/failure shows `VocabWordEdit_AudioOffline` warning.
Memory aids: Mnemonic text/image are stored on the word (`word.MnemonicText`, `MnemonicImageUri`). Quiz shows them when Settings "Show mnemonics" is on (activity-owned).
Save side effects: `SaveWordAsync` + add/remove resource mappings, then back via `returnQuery` (`GoBack :1024-1041`).
Vocabulary import/export: import via `ResourceAdd`/`ResourceEdit` paste/file, `ImportContent` wizard and share extension; export via the broken data-export (section 10.3). No per-vocabulary export UI.

---------------------------------------------------------------------------------------------------

## 7. Skills / topics

Files: `Skills.razor` (91), `SkillAdd.razor` (94), `SkillEdit.razor` (232). Repository `SkillProfileRepository`.
- `/skills` list: card per skill (title, truncated description), Add; empty state with "Add first profile"; error toast hard-coded English (`Skills.razor:80`).
- `/skills/add`: Title (required) and Description; save -> `/skills`.
- `/skills/edit/{Id}`: edit fields, overflow Save/Delete, confirm modal (`SkillEdit_DeleteMessage`), not found -> toast and redirect `/skills`.
Skills are consumed by Choose My Own (picker) and plan items (`skillId`). There is no skill progress screen in the Blazor UI (service exists, section 4.5). Starter skills: `{lang} Basics` (dashboard starter) or `Beginner {lang}` (onboarding).

---------------------------------------------------------------------------------------------------

## 8. Import

### 8.1 Paste import wizard (`/import-content`)

File: `ImportContent.razor` (1150 lines), service `ContentImportService`.
Steps: (1) paste text (disabled once parsed); (2) format hints: content type select Vocabulary / Phrases / Transcript / Auto, delimiter auto/comma/tab/pipe, has-header checkbox; "Preview" -> `ClassifyContentAsync` (auto-detect with confidence band and confirm / change-type chooser: Vocabulary, Phrases, Sentences, Transcript) (`:225-330`, `:742-805`); (3) harvest options: transcript, sentences, phrases, words with validation error if none (`:332-390`); (4) preview table: per-row select, editable target/native terms, status badge, duplicate badge with reason, AI-translated badge, selected count, select all (`:392-470`); (5) target: create new resource (title required, description, target/native language) or add to an existing resource (`:471-560`); (6) duplicate handling: Skip, Update (warns), Import all (`:560-620`). Commit button in header -> `CommitImportAsync`; result summary with filters All / Created / Updated / Skipped / Failed, tap an item to open `/vocabulary/edit/{id}`, "View resource" -> `/resources/edit/{id}`, "Start new import" (`:27-150`). Result stored under `?completed={storeKey}` so a reload keeps the summary (`:1053-1060`).
Source: paste only. No file picker or clipboard button on this page (grep `InputFile`/clipboard returned nothing in `ImportContent.razor`). Reference E2E: `.claude/skills/e2e-testing/references/import-content.md` (475 lines), `import-and-resources.md`.

### 8.2 YouTube import hub (`/media-import`, alias `/import`)

File: `MediaImport.razor` (788 lines). Three tabs: Channels (default), Single video, History. Header: Add channel, Refresh imports.
- Channels tab: monitored channel rows (name/handle, language, last checked or Never, active toggle switch to pause/resume monitoring) -> tap opens `/media-import/channel/{id}`; empty state with Add channel (`:40-100`, `:416-432`).
- Single video tab: paste YouTube URL (Enter key supported) -> Fetch: metadata + available transcript tracks (preferred track by target language code derived from profile, e.g. Korean -> `ko`), track select when several, editable transcript text, "Polish transcript" (AI formatting), "Save as resource" (creates `Video` resource with `MediaUrl`, transcript, language from track) -> View resource / Import another; errors inline (`:103-205`, `:617-760`).
- History tab: import rows with status badges (Pending ... Completed/Failed), tap completed -> resource edit; tap failed -> error toast; Retry button for failed/stuck imports (runs `RetryImportAsync` detached and polls); show more (page size 50) (`:209-270`, `:479-540`).
- Polling: timer every 5 s refreshes statuses of active imports while the page is open (`StartPolling :544-590`); on load `CleanupStaleImportsAsync` marks imports stuck for more than 10 minutes so retry appears (`:350-372`).
Server API mirrors: `/api/imports` (list/get/start/retry) and `/api/channels` (list/add/update/delete/check) (`Api/ImportEndpoints.cs:13-18`, `Api/ChannelEndpoints.cs:13-19`); the Razor pages call services directly (not these endpoints) (observed). DELETE `/api/channels/{id}` exists but no UI deletes a channel (grep: no Delete in `MediaImport.razor`/`ChannelDetail.razor`).

### 8.3 Channel detail (`/media-import/channel/{Id?}`, `/import/channel/{Id?}`)

File: `ChannelDetail.razor` (660 lines). New (no Id) or edit. Fields: channel URL (`https://www.youtube.com/@handle`), language, check interval (6/12/24/48 h), monitor toggle. Save resolves YouTube metadata; if unresolved, channel is still saved with error toast and stays on page for retry (`:311-410`). Edit-only: "Check for new videos" (up to 20 recent, with per-video import status), select all/none, "Import selected" (runs `VideoImportPipelineService.RunPipelineAsync` per video), retry failed video, "Retry resolve metadata" (`:448-650`).
Background monitoring runs in `SentenceStudio.Workers/Worker.cs`: first check after 10 s, then every 30 min; on each start it DELETES all failed `VideoImports` and resets `LastCheckedAt` for active channels (`Worker.cs:20-66`) (server only; operator/infrastructure behaviour).

### 8.4 Share extension and shared ingest (iOS only)

Files: `SentenceStudio.ShareExtension/ShareViewController.cs`, `SentenceStudio.Sharing/*`, `SentenceStudio.Shared/Services/SharedIngestProcessor.cs`, `SentenceStudio.iOS/MauiProgram.cs:84-140`, `docs/share-to-vocabulary.md`.
Journey: select text or share a web page/URL in another iOS app -> Share -> SentenceStudio (compose-sheet style) -> extension writes a JSON item (`Text` or `Url`) to the App Group queue `group.com.simplyprofound.sentencestudio` (no auth/AI/DB) -> CompleteRequest. Next app activation (`OnActivated`): background task assertion (about 60 s cap) then `ISharedIngestProcessor.DrainAsync`: requires an active profile (else defers and leaves queue intact); YouTube URLs start the video import pipeline detached and notify "video started" with route `/media-import`; other URLs fetch readable article text (`WebArticleFetcher`, 8000-char cap) and create a new Transcript resource; text items are classified (Auto) and appended to a single "Shared Inbox" resource with Skip-duplicates. Notifier drives `SharedIngestIndicator` (processing/complete toast with View action to `/vocabulary` or `/media-import`, 6 s auto-dismiss). Failed URL fetches stay queued for retry. Android share intent and Siri/App Intents are out of scope in the doc (design only). Android manifest has no intent filters (`SentenceStudio.Android/Platforms/Android/AndroidManifest.xml`).
Other ingest modes not found: no clipboard-import button, no web page picker in-app beyond share, no file import for Import Content, no Android/Mac share targets.

---------------------------------------------------------------------------------------------------

## 9. Activity log, history, progress

### 9.1 Activity log (`/activity-log`)

File: `ActivityLog.razor` (205 lines). Header: refresh icon; overflow filter All / Input / Output (`ActivityCategory`). Loads 8 weeks (`_weeksLoaded = 8`), "Load more" adds 4 weeks, `_hasMore` is true while returned weeks >= requested (`:117-176`). Legend (Input/Output). Each week: header "MMM d - d" range plus total minutes; 7-day grid (M T W T F S S mobile; Mon..Sun desktop); days with minutes show an `ActivityDot` colored input/output/both with a "complete" ring when all plans completed (`Shared/ActivityDot.razor:18-26`); tapping toggles an expanded `PlanSummaryCard` (completed/total badge, plan index or "Freeform" for `adhoc-` items with time, per-item completion icon, category dot, activity name, resource/skill, minutes) (`Shared/PlanSummaryCard.razor`).
Data: `ProgressService.GetActivityLogAsync` (`:1971-`) reads `DailyPlanCompletions` for the active profile, clusters rows into plans by 60-second `CreatedAt` gaps, enriches with resource/skill titles, drops days emptied by the filter. API twin: GET `/api/v1/activity-log` (`Api/Plans/ActivityLogEndpoints.cs:16`).
Empty state `ActivityLog_EmptyState`; loading spinner.

### 9.2 Other progress surfaces

Dashboard widgets (4.3 and 4.5); per-word practice history and mastery chart (6.4); streak badge. No dedicated stats/charts page and no resource-level progress page exist in the Blazor UI. `docs/dashboard-progress-feature.md` documents retired MauiReactor cards.

---------------------------------------------------------------------------------------------------

## 10. Profile, languages, account data

### 10.1 Profile (`/profile`)

File: `Profile.razor` (424 lines). Sections (all in one scroll):
1. Personal info: Name, Email (editable local copy; account email on server is not changed here).
2. Language settings: Native language, Target language (each from English, Korean, French, German, Spanish, Japanese, Chinese), Display language (English or Korean).
3. Learning preferences: session minutes 5/10/15/20/30/45, CEFR A1..C2.
4. Save Profile.
5. Change password (current/new/confirm; client checks match and 8+ chars; server enforces digit and uppercase).
6. Data export.
7. Danger zone: Delete my account (confirm modal).
Save (`:272-329`): writes `UserProfile` via `ProfileRepo.SaveAsync` (sync), updates `AppState`, renames the coach persona to the target language, applies display culture. Culture: W flips the circuit culture then navigates to `/account-action/SetCulture?culture&returnUrl=/profile` with forceLoad; MAUI calls `LocalizationManager.Instance.SetCulture`. MAUI persists via `UserProfile.DisplayLanguage` and re-applies on boot/login (`LocalizationInitializer`).

### 10.2 Change password

`ChangePassword` (`:351-390`): validates, `AuthService.ChangePasswordAsync` -> POST `/api/auth/change-password`; clears fields; toast success/failure.

### 10.3 Data export (Profile and Settings)

`DataExportService.ExportAllDataAsZipAsync` (`SentenceStudio.Shared/Services/DataExportService.cs:16-80`) builds a ZIP in memory with CSVs: UserProfiles (including an `OpenAI_APIKey` column), LearningResources, VocabularyWords, VocabularyLists, UserActivities, StreamHistory, Challenges, Stories, GradeResponses, SkillProfiles, ResourceVocabularyMappings, plus README.txt.
Two observed defects in the original:
- Both callers discard the returned stream (`Profile.razor:337`, `Settings.razor:427`) and then show a success toast. No file is saved, shared or downloaded (`IFileSaver` is registered at `SentenceStudioAppBuilder.cs:593` and a JS `downloadFileFromStream` exists in `UI/wwwroot/js/app.js:542`, but neither is wired to this export).
- Queries are unfiltered by owner (`db.UserProfiles.ToListAsync()`, `db.LearningResources.ToListAsync()`, `db.VocabularyWords.ToListAsync()` ...) and the model has no global query filters (grep `HasQueryFilter` returned nothing). On the multi-tenant web database this would export other users' rows (not verified at runtime).
Native must define the real behaviour before parity is claimed.

### 10.4 Delete account

UI: Profile -> Danger zone -> confirm modal -> `ConfirmDelete` (`Profile.razor:402-423`): reads `active_profile_id`, sets `AppState.CurrentUserProfile = null`, shows "account deleted" toast, then navigates (full load) to `/account-action/DeleteAccount?profileId=...`.
- Web: that endpoint deletes the `UserProfile` row and the Identity user and redirects `/auth` (`AccountEndpoints.cs:268-`). It does not go through the API's `DELETE /api/auth/account`, which additionally erases coach data and feedback records and fails closed (`AuthEndpoints.cs:423-531`).
- MAUI heads: `/account-action/DeleteAccount` is a WebApp server route, not a Blazor route. Navigating to it inside BlazorWebView will not reach the server (no such route in `UI`), so the account is not deleted although the toast says so (code inspection; runtime behaviour unconfirmed). `IAuthService.DeleteAccountAsync()` (`IdentityAuthService.cs:382-415`, calls `DELETE /api/auth/account` then signs out) exists but nothing in `UI` calls it.
This is a critical journey to define explicitly for native (App Store policy requires in-app account deletion).

### 10.5 Target-language setup and multi-language

Target language is chosen in Onboarding (6 steps, section 3) and Profile. `UserProfile.TargetLanguages` (comma list) and `TargetLanguagesList` exist for multi-language study (`Shared/Models/UserProfile.cs`), but the UI exposes only the single `TargetLanguage`; resources carry their own `Language`. NumberDrill content is Korean-only (`LanguageCode == "ko"`).

---------------------------------------------------------------------------------------------------

## 11. Settings, appearance, localization, preferences

### 11.1 Settings page (`/settings`)

File: `Settings.razor` (592 lines).

Appearance (applied and persisted immediately, no Save): 
- Theme: 10 swatches in a radiogroup (`ThemeCatalog.All`: seoul-pop (default), ocean, forest, sunset, monochrome, flatly, sketchy, slate, vapor, brite; `Contracts/Theme/ThemeCatalog.cs:26-100`). Bootswatch-family themes swap the Bootstrap stylesheet (`App.razor`).
- Mode: Light / Dark (default Dark, `ThemeCatalog.DefaultMode`). No "system" option.
- Text size slider 0.85 to 1.50 step 0.05 (default 1.0, `AppearanceSelection.cs:29-35`).
- Storage: MAUI device preference key `AppAppearance` (plus legacy `AppTheme`, `AppThemeMode`, `AppFontScale`; `AppLib/Services/Theme/DevicePreferencesThemeStore.cs:34-43`); Web per-browser cookie `ss_appearance`. `ThemeService` raises `ThemeChanged`; `MainLayout` applies via JS `applyTheme` and `setFontScale` (`MainLayout.razor:270-306`, `:455-471`).

Voice and Quiz (draft; committed by "Save preferences"):
- Language select (Korean, English, French, German, Spanish) -> reloads voices from `IVoiceDiscoveryService`; Voice select. Stored per language in `speech_voice_{Language}` (default Korean voice `Voices.JiYoung`, key `global_speech_voice_id`) (`AppLib/Services/SpeechVoicePreferences.cs:15-18`). The language select always starts as Korean (`Settings.razor:372`).
- Prompt language / quiz direction: Target / Native / Mixed -> `vocab_quiz_display_direction` (default `TargetToNative`).
- Autoplay audio -> `vocab_quiz_autoplay_vocab` (default true). (`vocab_quiz_autoplay_sample` default false has no Settings control.)
- Show mnemonics -> `vocab_quiz_show_mnemonic` (default true).
- Prompt modalities Text / Audio / Photo -> `vocab_quiz_use_text_prompt` (true), `..._audio_prompt` (true), `..._photo_prompt` (false); at least one must remain (toast `Settings_ModalityRequired`, forces Text on) (`:398-415`).
- Auto-advance duration slider 1 to 10 s step 0.5 -> `vocab_quiz_auto_advance_duration` ms (default 2000; page default shows 3.0 until loaded) (`AppLib/Services/VocabularyQuizPreferences.cs:16-33`).
Learning-value-gate rules apply to these toggles (AGENTS.md); not audited here.

Data management: Export all data (see 10.3 defect); "Harvest example sentences" -> `TranscriptSentenceHarvestService.HarvestForUserAsync(activeProfileId)` with per-resource progress text and a summary toast (added / skipped duplicate / skipped no match) (`:440-473`).

Learning Coach block: availability text; "Delete coach history" with an accessible confirm dialog (`CoachDialog`) -> `CoachWorkspace.DeleteSessionAsync()`; plus `CoachMemoryPanel` (what Sam remembers) (`:176-236`). Belongs to the Sam audit; noted for completeness.

About: version string (informational version), "What's New" button -> `ReleaseNotesService.GetNotesForVersionAsync` -> `WhatsNewModal` or info toast `Settings_NoReleaseNotes` (`:238-252`, `:510-531`).

### 11.2 Preference keys discovered (IPreferencesService unless noted)

`active_profile_id`, `app_is_authenticated`, `is_onboarded`, `SidebarCollapsed`, `resources-view-mode`, `DashboardMode`, `SelectedResourceIds{profile}`, `SelectedSkillProfileId{profile}`, `last_seen_version`, `dismissed_update_version`, `remember_me` and `remember_email` (write-only), `AppAppearance` (+ legacy), `vocab_quiz_*` (8 keys), `speech_voice_{Language}`, `global_speech_voice_id`, `global_voice_migration_complete`, `per_language_voice_migration_complete`, `ReadingActivity_*` (activity), `enable_automatic_data_recovery` (see AGENTS.md; keep disabled), `AppLanguage` (read only by HelpKit, `Shared/HelpKitIntegration.cs:52`; no writer found). WebView `localStorage`: `vocab-view-mode`, `vocab-sort`.

### 11.3 Localization switch

- Languages: English and Korean only (`LocalizationInitializer.SupportedCultures`, `AccountEndpoints.cs:20`). Switch lives in Profile "Display language", not in Settings.
- Components subscribe to `BlazorLocalizationService.CultureChanged` and re-render; text via `Localize["Key"]`.
- Known hard-coded English strings in the original: dashboard Numbers block (`Index.razor:537,578-580`), `Skills.razor:80` toast, `MediaImport.razor` several toasts ("Failed to load channels", "Resource saved successfully!", ...), `ChannelDetail.razor` error toasts and URL hint, `Onboarding.razor:362` toast, `Vocabulary.razor` merge toasts ("Merged N duplicate...") and filter dropdown labels ("All Words", "All Status", "Associated", "Orphaned", "All Encoding", "Strong/Good/Basic"), `Auth.razor:184` message, "Sign In" link in `RegisterPage.razor:113`. AGENTS.md says not to preserve hard-coded English defects.

### 11.4 Notifications / reminders

None found: no local notification, push, reminder or scheduled-job code in the heads, AppLib or UI (greps for notification APIs matched only a macOS NSApplication observer). The only "notification-like" surfaces are in-app toasts, update banner, What's New modal and shared-ingest toast. Android manifest declares only INTERNET and ACCESS_NETWORK_STATE; iOS Info.plist has no usage descriptions (so microphone/camera/photo permission flows for activities are unconfirmed in these heads).

---------------------------------------------------------------------------------------------------

## 12. Sync, offline, data protection

- Local store: SQLite on MAUI via EF + CoreSync; PostgreSQL on API/Web. Web uses `NoOpSyncService` (`SyncService.cs:25-32`).
- Sync triggers (MAUI): startup (background), successful sign-in (`BeginInitialSync` + `TriggerSyncAsync`), connectivity regained (`SentenceStudioAppBuilder.cs:323-343`), after nearly every repository mutation (fire-and-forget `_syncService?.TriggerSyncAsync()` in ~45 call sites across `Shared/Data/*Repository.cs`, `ProgressService.cs`), dashboard refresh icon and the dashboard new-user fallback. Single-flight via semaphore with a 100 ms wait; if busy the call is skipped (`SyncService.cs:294-300`).
- Conflict rules: INSERT conflicts skipped; UPDATE/DELETE force-write (remote and local) (`SyncService.cs:306-326`).
- UI: no sync-status indicator, last-synced time, or manual-sync button besides the dashboard refresh icon and the blocking "Syncing profile" overlay used only for the first sync after login (`MainLayout.razor:101-112`; failure also clears the overlay, `SyncService.cs:337-346`). Errors are logged, not shown.
- Offline: auth tolerates offline via optimistic principal; plan generation, AI features, speech, YouTube, feedback, version check, sign in/up/reset need network and show localized network error text or silent failure; local CRUD works and queues for sync. `VocabWordEdit_AudioOffline` is the explicit offline audio message. No global offline banner (observed).
- Backup: `docs/` and recent commits mention SQLite pre-migration backup (`ece849b6`), not part of the user-facing UI.

---------------------------------------------------------------------------------------------------

## 13. Help, feedback, about, diagnostics, operator

### 13.1 Feedback (`/feedback`)

File: `Feedback.razor` (454 lines); API `/api/v1/feedback/preview` and `/submit` (`Api/FeedbackEndpoints.cs:30-33`), client `IFeedbackApiClient`.
Journey (3 states): Form -> Preview -> Success.
1. Choose type: Auto-detect (default), Bug, Feature (enhancement); describe (required); notice that a GitHub issue is created (`Feedback_GitHubNotice`).
2. "Preview" -> server returns title, labels, formatted body and a signed preview token (AI enrichment optional; missing token = `Feedback_AIUnavailable` and submit is blocked).
3. Preview shows labels and rendered markdown; Back to edit, or Submit (disabled while submitting or after the token is spent).
4. Success: issue title, link to the GitHub issue (opens externally), replay note if the server returned an idempotent replay; Reset form.
Failure handling is deliberate: RateLimited (toast with server `Retry-After` seconds), TokenRejected / InDoubt / Closed spend the token and block resubmission with a message; there is no retry because submitting creates a public GitHub issue (`:246-342`). Client metadata sent: app version, platform (Native if webview host else Web), a closed route category (never raw URL) and UTC timestamp (`:366-389`).

### 13.2 In-app help (HelpKit)

NavMenu "Help" appears only when `Plugin.Maui.HelpKit.IHelpKit` resolves from DI (`NavMenu.razor:71-81`). Registered on `SentenceStudio.iOS` and `SentenceStudio.MacCatalyst` (`UseHelpKit()` in their `MauiProgram.cs`); not on Android, MacOS or Web. Opens a chat-style help modal over bundled markdown copied to `AppData/sentencestudio-help` (files: getting-started, dashboard, settings, user-profile, sync-and-offline, activities/{translation,word-association,cloze,vocabulary,writing,conversation}) in `AppLib/Resources/Raw/sentencestudio-help/`. Uses the app's AI/embedding clients; per-user history, 30-day retention, 10 questions per minute (`Shared/HelpKitIntegration.cs:36-70`). Ingest runs at startup in the background. E2E reference: `.claude/skills/e2e-testing/references/helpkit-flows.md`. Native replacement must decide whether to keep this.

### 13.3 About

Settings About section (version, What's New). No separate About/Legal/Privacy/Terms/Support page or link exists in the UI (grep of NavMenu and Settings). Unconfirmed: store listing text.

### 13.4 Debug health (`/debug/health`)

File: `DebugHealth.razor` (319 lines). Not in navigation. Gated by `#if DEBUG` constant `IsDebugBuild` (`:112-114`); Release shows "debug only". It has no `[Authorize]` attribute (observed). Cards: Database (can connect, provider, table count), Migrations (applied, pending, show applied), CoreSync (sync service registered), User profile (signed in, user name, active profile id, profile count), Vocabulary and Resources counts, API connectivity (base URL, reachable), refresh.

### 13.5 Operator pages

- `/operator/sam-opportunities` (`WebApp/Components/Pages/Operator/SamOpportunities.razor`, 634 lines): W only, `[Authorize]`, InteractiveServer, Development-only by three independent guards per its header comment. Sam opportunity ledger reviewer. Belongs to the Sam audit.
- API operator: `MapCoachOpportunityOperator(app.Environment)` (`Api/Program.cs:730`), Development only.
- Server health: `/health` and `/alive` (`Api/Program.cs:695-696`).
- Server worker: `SentenceStudio.Workers` channel monitor (8.3) and `PartOfSpeechBackfillWorker`.

---------------------------------------------------------------------------------------------------

## 14. Modal dialogs, sheets, confirmations and toasts index

| Surface | Where | Trigger and outcomes |
|---------|-------|----------------------|
| Delete resource confirm | `ResourceEdit.razor:288-316` | overflow Delete; Cancel / Delete |
| Delete vocabulary word confirm | `VocabularyWordEdit.razor:394-` | overflow Delete |
| Bulk delete confirm | `Vocabulary.razor:657-690` | bulk Delete action |
| Delete skill confirm | `SkillEdit.razor:59-` | overflow Delete |
| Delete account confirm | `Profile.razor:159-187` | Danger zone |
| Delete local profile confirm | `Auth.razor:67-95` | dev picker |
| Delete coach history confirm | `Settings.razor:214-236` | Sam; accessible `CoachDialog` |
| What's New modal | `Shared/WhatsNewModal.razor` | auto on new version; Settings button |
| Mobile nav offcanvas | `MainLayout.razor:56-66` | hamburger |
| Mobile vocabulary filter offcanvas (bottom sheet) | `Vocabulary.razor:276-` | header filter button |
| Duplicate manager / cleanup panel (inline panel, not a modal) | `Vocabulary.razor:406-` | overflow actions |
| Autocomplete dropdowns | Vocabulary search, ResourceEdit quick add, VocabularyWordEdit constituents | typing |
| Tom Select pickers | Dashboard Choose My Own | JS |
| Overflow menus | `PageHeader` three-dots on most pages | tap |
| Type chooser (content type) | `ImportContent.razor:305-330` | change content type |
| Full-screen "creating starter" overlay | `Resources.razor:120-130` | create starter |
| Full-screen sync overlay | `MainLayout.razor:101-112` | first sync |
| Reconnect dialog | WebApp `ReconnectModal.razor` | circuit loss (W) |
| Toast kinds | everywhere | see `ToastService` |
No `window.confirm/alert` or native dialogs are used in these views (grep not exhaustive; unconfirmed for activities).

---------------------------------------------------------------------------------------------------

## 15. Platform availability matrix (reference heads)

| Capability | Web | iOS | Android | MacCatalyst | MacOS | Windows |
|------------|-----|-----|---------|-------------|-------|---------|
| All `SentenceStudio.UI` pages in this audit | yes | yes | yes | yes | yes | yes (temporary head) |
| Cookie auth / `/account-action/*` / `/Account/*` | yes | no | no | no | no | no |
| JWT + refresh in secure storage, silent restore | no | yes | yes | yes | yes (keychain gate `Platform/MacOSKeychainGate.cs`) | yes |
| CoreSync (SQLite local DB) | no (NoOp) | yes | yes | yes | yes | yes |
| Share extension and shared-ingest drain | no | yes | no | no | no | no |
| HelpKit help chat | no | yes | no | yes | no | no |
| Update-available banner | no | yes | yes | yes | yes | yes |
| OnResume token refresh | n/a | yes | yes | yes | no (unconfirmed) | yes |
| Native app menu (Account, Log Out Cmd+Shift+L) | no | no | no | no | yes (`MacOSAppMenu.cs`) | no |
| Custom URL scheme | no | none | none | `msal68d5abeb-...` (MSAL leftover; no handler found) | same MSAL leftover | none |
| Deep links / universal links / Android app links | none | none | none | none | none | none (grep of Info.plist, manifests, `OpenUrl`) |
| Notifications / reminders | none | none | none | none | none | none |
| Report-a-problem (Feedback) | yes | yes | yes | yes | yes | yes |
| Env badge | yes | yes | yes | yes | yes | yes |
| MAUI DevFlow agent (Debug) | n/a | 9224 | 9225 | yes | 9225 | n/a |
Android manifest: INTERNET, ACCESS_NETWORK_STATE; min SDK 21 in csproj (the target for native is API 36). iOS min 15.0 in csproj (target 26).

---------------------------------------------------------------------------------------------------

## 16. Cross-references to activities (not audited here)

Dashboard and Today's Plan launch these routes: `/conversation`, `/scene`, `/translation`, `/cloze`, `/writing`, `/diary` (list; `/diary/today` editor; `/diary/{Id}` viewer), `/reading`, `/vocab-quiz`, `/vocab-matching`, `/shadowing`, `/how-do-you-say`, `/minimal-pairs` (+ `/minimal-pairs/create`, `/minimal-pairs/session/{Id}`), `/word-association`, `/numberdrill`, `/video-watching`, `/flashcard-activity` (reachable only from the dashboard "Preview plan vocabulary" button and not in the 14-card Choose My Own list). Shared UI helpers used by those pages: `ActivityTimer`, `WaveformDisplay`, `InteractiveText`, `AudioPlayer` (no consumer found), `PracticeBalanceBar` (coach plan canvas only).

---------------------------------------------------------------------------------------------------

## 17. Live-only differences (LIVE vs FR)

`diff -rq` of `src/`, `docs/`, `.claude/`, `AGENTS.md`, ignoring build artefacts:

1. `src/SentenceStudio.UI/Pages/Resources.razor` (LIVE, uncommitted): replaces Bootstrap `row/col` grid and flex list rows with `resources-virtualized-grid` / `resources-virtualized-grid-row` / `resources-virtualized-list` wrappers so `<Virtualize>` spacers stack correctly; list row content wrapped in an inner span; adds structured debug/info/error logging on init, load, search failure and view-mode change. No change to the user-visible journey, filters or navigation.
2. `src/SentenceStudio.UI/wwwroot/css/app.css` (+18 lines): the three `.resources-virtualized-*` rules.
3. `src/SentenceStudio.iOS/SentenceStudio.iOS.csproj` and `Directory.Packages.props`: new `Microsoft.AspNetCore.Components.WebView` RC1 package pin because MAUI's WebView asset still targets preview 7 and the Virtualize JS/.NET callbacks disagree (comment in the csproj). Indicates the FR Resources page's `<Virtualize>` is broken on the iOS reference head (and `scripts/reference-resources-repair/`, `tests/SentenceStudio.Reference.Resources.Tests/` are build-only workarounds/tests for that). Reference captures of Resources on iOS from FR may therefore differ from LIVE.
4. `docs/native-platform-claude-handoff.md` (new): handoff notes, not original app behaviour.
5. `.claude/skills/e2e-testing/SKILL.md` and `references/management-pages.md`: LIVE adds native-head test instructions (Settings owner isolation and a "Practice Log" read-only table, section 12) that describe the NEW native screens, not original behaviour. They do reveal the native Settings and Activity (Practice Log) scope: appearance applied immediately, Voice and Quiz as an unsaved draft committed by Save, activity categories All/Input/Output, Monday-anchored weeks, Load More.
6. `AGENTS.md`: differs (project guidance, not product).
7. Only-in-LIVE untracked folders: `.agent-scratch-ios/`, `.task-scratch/`, `report.20260928...json`, `.squad/memory/*`: not product.
No live-only routes, pages, settings or services were found; all 22 in-scope pages are byte-identical except `Resources.razor`.

Documentation drift inside FR itself (code is authoritative): `.claude/skills/e2e-testing/references/management-pages.md` section 4 describes vocabulary stat badges "Total, Associated, Orphaned" and a "Reset Profile" danger-zone action; the code shows Total/Unknown/Learning/Familiar/Known badges and "Delete my account" (`Vocabulary.razor:48-54`, `Profile.razor:149-187`). Section 10 of that doc says export "downloads a ZIP"; code discards the file (10.3).

---------------------------------------------------------------------------------------------------

## 18. Findings and risks to carry into the native rebuild

Defects observed in the original (decide per item whether native preserves, fixes, or drops; AGENTS.md says do not preserve hard-coded English defects):
1. Data export produces no file and is not owner-filtered (10.3).
2. Delete account from the UI does not use the API deletion path; on MAUI heads it likely does nothing (10.4).
3. Regenerate Plan silently deletes today's completed progress (4.3).
4. Dashboard narrative resource link `/resources/{id}` has no route (4.3).
5. "Remember me" is never read (2.1).
6. Login treats unconfirmed email as invalid credentials (2.1); email confirm link returns raw JSON (2.2); `?message=`/`?error=` query results are not displayed (2.2); reset link host may not serve `/Account/ResetPassword` (2.3).
7. Dashboard starter pack uses static Korean words for any target language (4.2).
8. `EnsureMultiUserBackfillAsync` assigns unowned rows to the first profile (2.7); LIVE rule is to fail closed.
9. Vocabulary view/sort stored in WebView localStorage, not profile preferences (6.1).
10. No sync status/error UI; sync failures are silent (12).
11. Several hard-coded English strings (11.3).
12. No channel delete in UI although API supports it (8.2); worker clears failed imports on every start (8.3).
13. Resource progress, skill progress and practice-heat service methods have no UI (4.5).
14. Onboarding forces `DisplayLanguage = English` even for Korean-native learners (3).

Items not confirmable from code (marked unconfirmed above): MacOS `OnResume` equivalent; Android hardware back behaviour; whether `ResourceEdit` Save navigates away; what the user sees on fatal DB init failure; production host for emailed confirm/reset links; whether MAUI heads expose iOS/Android permission prompts for mic/camera/photos in these heads; runtime effect of `/account-action/DeleteAccount` inside BlazorWebView; `Dashboard` sidebar highlighting on activity routes missing from the section prefix list; contents of `Shared/Coach/*` and `Sam/*` (excluded).

Files most worth reading first when planning native parity: `UI/Pages/Index.razor`, `UI/Layout/MainLayout.razor`, `Shared/Services/PostLoginRouter.cs`, `AppLib/Services/IdentityAuthService.cs`, `Shared/Services/Progress/ProgressService.cs`, `UI/Pages/Vocabulary.razor`, `UI/Pages/VocabularyWordEdit.razor`, `UI/Pages/Settings.razor`, `UI/Pages/Profile.razor`.
