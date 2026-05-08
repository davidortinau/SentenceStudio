# Decision: Use `AuthClaimTypes` constants for all custom JWT claim names

**Author:** Kaylee
**Date:** 2025 (Wash review remediation, commit 398a7690)
**Status:** Proposed
**Scope:** API claim handling

## Context

Wash's review of commit 398a7690 (`api-endpoint-review-checklist` SIGNIFICANT #5)
flagged that endpoints reach into `User.FindFirst("user_profile_id")` with the
claim name spelled as a magic string. Multiple endpoints (Profile, Speech,
Channel, Import, Feedback, Maintenance) and the auth pipeline
(`JwtTokenService`, `DevAuthHandler`) all repeated the same literal. A typo in
any one of them silently degrades to "anonymous user" with no compile-time
warning — the kind of bug we will only catch in production.

## Decision

Custom JWT claim names live in
`src/SentenceStudio.Api/AuthClaimTypes.cs` as `public const string`
fields on a `public static class`.

```csharp
namespace SentenceStudio.Api;

public static class AuthClaimTypes
{
    public const string UserProfileId = "user_profile_id";
}
```

All endpoints, handlers, and tests must reference these constants:

```csharp
// ✅ correct
var profileId = ctx.User.FindFirst(AuthClaimTypes.UserProfileId)?.Value;

// ❌ banned
var profileId = ctx.User.FindFirst("user_profile_id")?.Value;
```

Standard ASP.NET Core claim types (`ClaimTypes.NameIdentifier`,
`ClaimTypes.Email`, etc.) continue to come from
`System.Security.Claims.ClaimTypes` — `AuthClaimTypes` is for
SentenceStudio-specific claims only.

## Compliance status (this PR)

- ✅ `ProfileEndpoints.cs`, `SpeechEndpoints.cs` — new code uses the constant.
- ✅ `ChannelEndpoints.cs`, `ImportEndpoints.cs`, `FeedbackEndpoints.cs` —
  swept on this branch.
- ✅ `Auth/JwtTokenService.cs`, `Auth/DevAuthHandler.cs` — swept on this branch
  (with `using SentenceStudio.Api;` added because they live in
  `SentenceStudio.Api.Auth`).
- ⚠️ `MaintenanceEndpoints.cs` — Zoe's lane on a sibling branch. Sweep follows
  there; do not double-edit.

## Follow-ups

1. **Lint enforcement.** Open an issue to add a Roslyn analyzer or a unit-test
   that greps `src/SentenceStudio.Api/**/*.cs` for the literal
   `"user_profile_id"` and fails if found outside `AuthClaimTypes.cs`.
2. **New claim names.** Anyone adding a new custom claim must extend
   `AuthClaimTypes` first; PR review must reject magic-string claim names.
3. **Tests.** `tests/SentenceStudio.Api.Tests/Infrastructure/TestJwtGenerator.cs`
   already exists (Jayne's branch) — that file should also reference the
   constant once both branches land.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
