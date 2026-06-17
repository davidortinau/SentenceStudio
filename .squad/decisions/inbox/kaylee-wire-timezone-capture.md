### 2026-06-17: Wire TimeZoneCapture into webapp render tree (Blocker #1)

By: Kaylee (Full-stack Dev / Blazor / UI)
Branch: squad/per-user-timezone-plan-dates
Resolves: Zoe review blocker #1 (zoe-review-per-user-timezone.md)

---

#### What changed

Added `<TimeZoneCapture />` to `src/SentenceStudio.WebApp/Components/AppRoutes.razor`,
immediately before the `<Router>` element.

#### Why AppRoutes.razor (not the shared MainLayout)

1. WEBAPP-ONLY: AppRoutes.razor lives in SentenceStudio.WebApp/Components/ and is
   not referenced by any MAUI head project. The shared MainLayout at
   src/SentenceStudio.UI/Layout/MainLayout.razor is compiled into MAUI heads --
   placing a WebApp-project component there would break MAUI-head compilation
   because those projects do not reference TimeZoneCapture or TimeZoneCaptureService.

2. INTERACTIVE: App.razor applies `@rendermode="InteractiveServer"` to the
   `<AppRoutes>` tag (App.razor line 23). Everything inside AppRoutes -- including
   TimeZoneCapture -- runs in an interactive Blazor Server circuit, so
   OnAfterRenderAsync(firstRender) fires and JS interop (Intl.DateTimeFormat)
   works correctly.

3. AUTHENTICATED CONTEXT: App.razor wraps AppRoutes in `<CascadingAuthenticationState>`
   (App.razor line 22). The TimeZoneCapture component reads AuthenticationStateProvider
   and no-ops when the user is not authenticated (lines 30-33 of the component).
   TimeZoneCaptureService also refuses writes on empty userId (multi-tenant guard).

4. ONE-SHOT: The component guards with `if (!firstRender || _captured) return;` and
   sets `_captured = true` immediately. It runs once per circuit, not on every render.

5. NO VISIBLE UI: The component is headless (no markup). It does not affect layout.

#### Component review

TimeZoneCapture.razor was reviewed as-is and found sound for InteractiveServer:
- firstRender guard prevents repeat JS calls.
- _captured flag prevents re-entry.
- JSDisconnectedException is caught (circuit disconnect during JS call).
- General Exception is caught (non-critical capture should never crash the circuit).
- Auth check runs before JS interop (no wasted JS call for anonymous visitors).
- UserProfileId claim is read via AuthClaimTypes.UserProfileId (matches the
  established claim type used throughout the webapp).
- No @rendermode attribute needed on the component itself -- it inherits
  InteractiveServer from the parent AppRoutes component.

No changes were needed to the component file itself.

#### Build verification

- Built: `dotnet build src/SentenceStudio.WebApp/SentenceStudio.WebApp.csproj -f net11.0`
- Result: 0 errors, warnings are pre-existing (unrelated to this change).
- MAUI heads unaffected: only AppRoutes.razor (webapp-only) was edited. No shared
  project files were touched.

#### What this fix enables

With TimeZoneCapture rendered in every interactive circuit:
- On first interactive render for an authenticated user, the browser's IANA timezone
  (e.g. "America/Chicago") is read via JS interop and persisted to
  UserProfile.IanaTimeZoneId via TimeZoneCaptureService.
- WebAppPlanDateContext then resolves a non-null IanaTimeZoneId on subsequent requests,
  using the user's local timezone instead of falling back to UTC.
- The production stale-pin bug (evening CDT = next-day UTC plan pre-generation) is
  resolved because UserLocalDate now reflects the user's actual local date.

#### Limitations

- Cannot verify end-to-end JS interop execution without a running browser session.
  Compilation confirms the component is in the render tree and will be instantiated.
- The timezone is captured once per circuit (not once ever). If a user changes
  timezones, the next circuit connect will update it. This is by design.
