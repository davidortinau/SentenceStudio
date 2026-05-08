# Decision: Use `AuthClaimTypes` constants for all custom JWT claim names

**Author:** Kaylee
**Date:** 2026-05-08 (Wash review remediation, commit 398a7690; finalized post-Flutter-endpoints deploy)
**Status:** ✅ ACCEPTED
**Scope:** All claim issuers and consumers across the solution

## Context

Wash's review of commit 398a7690 (`api-endpoint-review-checklist` SIGNIFICANT #5)
flagged that endpoints reach into `User.FindFirst("user_profile_id")` with the
claim name spelled as a magic string. Multiple endpoints (Profile, Speech,
Channel, Import, Feedback, Maintenance) and the auth pipeline
(`JwtTokenService`, `DevAuthHandler`) all repeated the same literal. A typo in
any one of them silently degrades to "anonymous user" with no compile-time
warning — the kind of bug we will only catch in production.

The original draft of this decision placed the constants in
`SentenceStudio.Api`, but during finalization we found that
`SentenceStudio.WebApp/Auth/ServerAuthService.cs:219` ALSO mints the
`user_profile_id` claim (Blazor Server-side identity flow) and could not
reference an `Api`-scoped constant. Because peer Aspire services should not
project-reference each other, the constants were moved to
`SentenceStudio.Contracts` — both Api and WebApp already reference Contracts,
and claim names ARE wire contract.

## Decision

Custom JWT claim names live in
`src/SentenceStudio.Contracts/AuthClaimTypes.cs` as `public const string`
fields on a `public static class` in namespace `SentenceStudio.Contracts`.

```csharp
namespace SentenceStudio.Contracts;

public static class AuthClaimTypes
{
    public const string UserProfileId = "user_profile_id";
}
```

All endpoints, handlers, token issuers, and tests must reference these
constants:

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

## Compliance status (final)

- ✅ `ProfileEndpoints.cs`, `SpeechEndpoints.cs` — uses the constant.
- ✅ `ChannelEndpoints.cs`, `ImportEndpoints.cs`, `FeedbackEndpoints.cs` — uses the constant.
- ✅ `Auth/JwtTokenService.cs`, `Auth/DevAuthHandler.cs` — uses the constant.
- ✅ `WebApp/Auth/ServerAuthService.cs` — swept post-deploy (was the only remaining magic-string producer; its existence is what motivated the move to `SentenceStudio.Contracts`).
- ✅ `tests/SentenceStudio.Api.Tests/Infrastructure/TestJwtGenerator.cs` — swept (follow-up #3 closed).
- N/A `MaintenanceEndpoints.cs` — file no longer carries a `user_profile_id` claim consumer (verified via repo-wide grep on `main` 2026-05-08).

## Follow-ups

1. **Lint enforcement.** Open an issue to add a Roslyn analyzer or a unit-test
   that greps `src/**/*.cs` for the literal `"user_profile_id"` and fails if
   found outside `AuthClaimTypes.cs`. *(Tracked as [#202](https://github.com/davidortinau/SentenceStudio/issues/202) — 2026-05-08.)*
2. **New claim names.** Anyone adding a new custom claim must extend
   `AuthClaimTypes` first; PR review must reject magic-string claim names.
3. ~~`tests/SentenceStudio.Api.Tests/Infrastructure/TestJwtGenerator.cs`
   should reference the constant.~~ ✅ DONE 2026-05-08.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
