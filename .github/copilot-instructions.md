# Copilot Coding Agent — Squad Instructions

You are working on a project that uses **Squad**, an AI team framework. When picking up issues autonomously, follow these guidelines.

## Required Reading: AGENTS.md

**Before any code work**, read `AGENTS.md` at the repo root. It is the authoritative source for this project's conventions and is NOT auto-loaded into cloud Copilot Coding Agent sessions. It covers (non-exhaustive):

- **Project purpose**: SentenceStudio's primary purpose is dogfooding the .NET MAUI SDK — tooling friction takes priority over app features.
- **Data preservation rules**: NEVER uninstall apps, drop databases, or wipe SecureStorage without explicit per-turn confirmation.
- **Build & run commands**: `dotnet run -f net11.0-maccatalyst` for MAUI heads (NOT `dotnet build -t:Run` anymore as of .NET 11 Preview 4); MAUI heads target `net11.0-*`, Azure/server/Shared stay on `net10.0`.
- **EF Core migrations**: use `dotnet ef migrations add` against `SentenceStudio.Shared.csproj` — never hand-write migrations, never raw SQL ALTER TABLE.
- **MauiReactor conventions**: `.HEnd()` / `.VCenter()` / `.Center()` instead of `HorizontalOptions(LayoutOptions.End)`; `ThemeKey(MyTheme.*)` over inline styles; icons in `ApplicationTheme.Icons.cs`, never inline `FontImageSource`.
- **Shell navigation**: `MauiControls.Shell.Current.GoToAsync(...)`, never `Navigation.PushAsync`.
- **Validation gate**: every UI/behavior change must be validated by running the app end-to-end via the `maui-ai-debugging` or `e2e-testing` skill — "it builds" is not sufficient.
- **No emoji in UI / code output / logs** — use Bootstrap icons or plain text.

If anything below conflicts with AGENTS.md, **AGENTS.md wins**.

## Coordinator Canary Check

**IMPORTANT — run this check on EVERY session start, before doing any work:**

Look for the exact token `SQUAD_COORDINATOR_CANARY_a8f3` in your `agent_instructions`. This token is placed at the end of `squad.agent.md`. If it is missing, the coordinator file is missing or truncated.

**When the token is missing, you MUST:**
1. **STOP** — do not proceed with standard Squad behavior.
2. **WARN the user** with this exact message:
   ```
   ⚠️ Squad coordinator (squad.agent.md) appears to be missing or truncated. The canary token was not found. Do NOT proceed with standard Squad behavior — Squad's safety rails are not loaded. Please restart your session.
   ```
3. Do not continue with normal Squad routing, spawning, PR, or branch-protection behavior after emitting the warning.

## Team Context

Before starting work on any issue:

1. Read `.squad/team.md` for the team roster, member roles, and your capability profile.
2. Read `.squad/routing.md` for work routing rules.
3. If the issue has a `squad:{member}` label, read that member's charter at `.squad/agents/{member}/charter.md` to understand their domain expertise and coding style — work in their voice.

## Capability Self-Check

Before starting work, check your capability profile in `.squad/team.md` under the **Coding Agent → Capabilities** section.

- **🟢 Good fit** — proceed autonomously.
- **🟡 Needs review** — proceed, but note in the PR description that a squad member should review.
- **🔴 Not suitable** — do NOT start work. Instead, comment on the issue:
  ```
  🤖 This issue doesn't match my capability profile (reason: {why}). Suggesting reassignment to a squad member.
  ```

## Branch Naming

Use the squad branch convention:
```
squad/{issue-number}-{kebab-case-slug}
```
Example: `squad/42-fix-login-validation`

## PR Guidelines

When opening a PR:
- Reference the issue: `Closes #{issue-number}`
- If the issue had a `squad:{member}` label, mention the member: `Working as {member} ({role})`
- If this is a 🟡 needs-review task, add to the PR description: `⚠️ This task was flagged as "needs review" — please have a squad member review before merging.`
- Follow any project conventions in `.squad/decisions.md`

## Decisions

If you make a decision that affects other team members, write it to:
```
.squad/decisions/inbox/copilot-{brief-slug}.md
```
The Scribe will merge it into the shared decisions file.

## Clarify the surface before investigating

This solution spans Mac Catalyst, iOS, Android, Mac AppKit, the Blazor WebApp, and the ASP.NET Core API. Same C# code, very different runtime context — per-device SQLite vs. shared Postgres, `MauiPreferencesService` vs. singleton `WebPreferencesService`, single-user device vs. multi-tenant server.

Before deep investigation of any reported bug, **confirm which surface the user actually hit** (Mac Catalyst Debug? iOS on DX24? Production WebApp? Aspire-local API?). Asking one clarifying question up front is cheaper than reproducing in the wrong environment for 30 turns. If the user already named the surface (e.g. "production"), do not start by reproducing locally on the device head — the failure modes don't overlap.

## Build parallelism foot-gun

Do not run `dotnet build` against multiple projects in this solution back-to-back in parallel shells. Projects share generated ref assemblies (e.g. `ServiceDefaults.dll`) and concurrent writes trigger `MSB3026` / `MSB3883` file-lock errors that look like real build failures but are pure contention.

Build options, in order of preference:
1. Build the whole solution once: `dotnet build SentenceStudio.sln` (with the appropriate `-f` per the AGENTS.md TFM matrix when needed).
2. Sequence per-project builds in a single shell with `&&`, never in parallel.
3. If you must run parallel `dotnet` invocations, target unrelated solutions only.

## Known-failing test baseline

The test suite has **one intentionally-failing test** documenting a known bug:

- `tests/SentenceStudio.UnitTests/PlanGeneration/DeterministicPlanBuilderResourceSelectionTests.cs::ResourceUsed15DaysAgo_ShouldNotBeTreatedAsNeverUsed` — source comment line 117: `// Assert — THIS WILL LIKELY FAIL`. The deterministic plan builder's 14-day lookback window treats anything older as "never used" (`DaysSinceLastUse = 999`).

**Baseline: 534/535 passing.** A 534-pass / 1-fail result is the expected baseline, not a regression. If you fix the underlying bug, also delete the `THIS WILL LIKELY FAIL` comment so the next agent doesn't get confused.

## Multi-tenant data scoping rule (NON-NEGOTIABLE)

The WebApp and API are multi-tenant. Every repository method that filters by `UserProfileId` / `ActiveUserId` / equivalent **must** treat an empty user identifier as "no data", not as "give me everything".

**Pattern (established in `src/SentenceStudio.Shared/Data/LearningResourceRepository.cs` after the May 2026 cross-tenant leak hotfix):**

```csharp
public async Task<List<Foo>> GetFoosAsync(string? userId = null)
{
    userId ??= _userScope.ActiveUserId;
    if (string.IsNullOrEmpty(userId))
    {
        _logger.LogWarning("GetFoosAsync called with no active userId — returning empty");
        return new List<Foo>();
    }
    return await _db.Foos.Where(f => f.UserProfileId == userId).ToListAsync();
}
```

**Forbidden anti-pattern:**

```csharp
// NEVER do this — leaks cross-tenant data
var q = _db.Foos.AsQueryable();
if (!string.IsNullOrEmpty(userId)) q = q.Where(f => f.UserProfileId == userId);
return await q.ToListAsync();
```

Rules:
- Empty userId → log a warning + return empty/null/false/zero (matching the method's return type).
- Never throw — would 500 the Blazor circuit.
- Never fall through to an unfiltered query.
- Applies to read AND write paths: write methods with no active user should refuse, not create orphan rows.

**DataRecoveryService gates (NON-NEGOTIABLE — see RCA `.squad/decisions/inbox/captain-rca-datarecoveryservice-cross-tenant-corruption.md`):**
- `DataRecoveryService.RecoverOrphanedDataAsync` must pass three safeguards before any UPDATE/DELETE runs: (1) email mismatch abort — orphan `UserProfile.Email` != new user's email means a different human, not a server-wipe recovery; (2) temporal sanity abort — orphan data older than the new account's `CreatedAt` by >1 day is temporally impossible (and if the new user's profile has not yet synced locally but orphan timestamps exist, this is also an ABORT — defense in depth); (3) first-run gate — per-user preference `_data_recovery_complete_{userId}="true"` makes the service one-shot per account. Email addresses in the abort warning log are masked (`dav***@ortinau.com`). If any safeguard fails, abort and log `[OrphanRecovery] ABORTED` at Warning.
- Calling `RecoverOrphanedDataAsync` is additionally gated by the `enable_automatic_data_recovery` preference flag (default `false`) in `IdentityAuthService.StoreTokens`. Flip to `true` only after a confirmed server wipe.

This rule applies to every repository, every method, every PR. If you're touching a repository, audit it before declaring the task done.
