# Squad Decisions Archive

This file contains archived decisions older than 30 days. Current active decisions are in `decisions.md`.


---

## Decision: Onboarding Gate Requires All Three Profile Fields

**Author:** Kaylee (Full-stack Dev)  
**Date:** 2025-07-15  
**Status:** Implemented


### Context
The `is_onboarded` preference controls whether users are redirected to `/onboarding`. Multiple code paths were setting this flag too eagerly — before the user's profile had all required fields (TargetLanguage, NativeLanguage, Name).


### Decision
Any code path that sets `is_onboarded = true` MUST first verify the profile has all three fields populated:
- `TargetLanguage`
- `NativeLanguage`  
- `Name`

If any are missing, the user should be redirected to `/onboarding` rather than granted dashboard access.


### Affected Files
- `Layout/MainLayout.razor` — existing-profile bypass check
- `Pages/Auth.razor` — local profile login
- `Pages/LoginPage.razor` — post-login redirect
- `Pages/Index.razor` — starter content creation uses user's language


### Rationale
Onboarding collects these three fields in a guided flow. Bypassing it with incomplete data leads to broken language selection, hardcoded defaults, and a confusing UX.

---

## Decision: WebFilePickerService Uses JS Interop with Scoped DI

**Date:** 2025-07-18  
**Author:** Kaylee (Full-stack Dev)  
**Status:** Implemented


### Context

`WebFilePickerService` threw `NotSupportedException`. The `IFilePickerService` interface needed a working implementation for any code that calls it programmatically in the WebApp.


### Decision

- Implemented via JS interop (`filePickerInterop.pickFile`) that creates a hidden `<input type="file">`, reads the selected file as a byte array, and returns it to C#.
- Changed DI registration from `AddSingleton` to `AddScoped` because `IJSRuntime` is circuit-scoped in Blazor Server — a singleton cannot hold a scoped dependency.
- Follows the existing `window.*` global object pattern used by `audioInterop.js`.


### Files Changed

- `src/SentenceStudio.WebApp/wwwroot/js/filePicker.js` — new JS interop module
- `src/SentenceStudio.WebApp/Platform/WebFilePickerService.cs` — injects IJSRuntime, calls JS
- `src/SentenceStudio.WebApp/Program.cs` — `AddSingleton` → `AddScoped`
- `src/SentenceStudio.WebApp/Components/App.razor` — added `<script>` tag


### Impact

Any service or page that injects `IFilePickerService` will now get a working implementation on web. Existing Blazor pages using `InputFile` directly are unaffected.

---

# Decision: Fix broken UnitTests compilation

**Author:** Kaylee (Full-stack Dev)  
**Date:** 2025-07-22  
**Status:** Done

## Problem
The `SentenceStudio.UnitTests` project failed to compile due to three categories of drift between tests and source:

## Fixes Applied

### 1. SearchQueryParserTests — wrong namespace
`SearchQueryParser` lives in `SentenceStudio.Services`, but the test imported `SentenceStudio.Shared.Services`. Fixed the `using` directive.

### 2. ParsedQuery — missing convenience properties
Tests referenced `HasContent`, `TagFilters`, `ResourceFilters`, `LemmaFilters`, `StatusFilters`, `CombinedFreeText`, and `IsValid` which didn't exist on `ParsedQuery`. These are natural computed properties (simple LINQ projections over `Filters`), so I added them to the model rather than gutting the tests. This makes the API richer and keeps the tests clean.

### 3. VocabularyProgressTests — two type/logic mismatches
- `UserId` changed from `int` (default 1) to `string` (default `string.Empty`). Updated assertion.
- `Status` computation now requires both `MasteryScore >= 0.85` AND `ProductionInStreak >= 2` for `Known`. Updated the `Status` theory to pass `ProductionInStreak` and adjusted expected values to match the current dual-requirement logic.

## Result
- **Build:** 0 errors ✅
- **SearchQueryParserTests + VocabularyProgressTests:** 99/99 passing ✅
- **Pre-existing failures in FuzzyAnswerMatcherTests:** 17 failures (not in scope, not touched)

---

# Decision: Add `user_profile_id` claim to all JWT token paths

**Author:** Wash (Backend Dev)  
**Date:** 2025-07-25  
**Status:** Implemented  
**Relates to:** #139 (Feedback feature 401 Unauthorized)

## Context

Multiple API endpoints (Feedback, Channel, Import) require a `user_profile_id` claim in the JWT to identify the user's profile. However, **no JWT generation path** ever included this claim:

- `JwtTokenService.GenerateToken()` — used for mobile login tokens
- `ServerAuthService.GetAccessTokenAsync()` — used by the webapp to mint JWTs for API calls
- `DevAuthHandler` — dev-mode fallback

All three omitted `user_profile_id`, making every endpoint that checks for it return 401.

## Fix

1. **`JwtTokenService`** — includes `user_profile_id` from `ApplicationUser.UserProfileId` when present
2. **`ServerAuthService`** — looks up the `ApplicationUser` via `UserManager` to retrieve `UserProfileId` and adds it to the JWT (method changed from sync `Task.FromResult` to async)
3. **`DevAuthHandler`** — adds a static `user_profile_id` claim for dev mode

## Risk

Low. The DB lookup in `ServerAuthService` adds one query per token mint, but tokens are cached by the handler and the webapp's request rate is low.
