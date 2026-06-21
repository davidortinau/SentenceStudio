# Copilot Coding Agent — Squad Instructions

You are working on a project that uses **Squad**, an AI team framework. When picking up issues autonomously, follow these guidelines.

## Required Reading: AGENTS.md

**Before any code work**, read `AGENTS.md` at the repo root. It is the authoritative source for this project's conventions and is NOT auto-loaded into cloud Copilot Coding Agent sessions. It covers (non-exhaustive):

- **Project purpose**: SentenceStudio's primary purpose is dogfooding the .NET MAUI SDK — tooling friction takes priority over app features.
- **Data preservation rules**: NEVER uninstall apps, drop databases, or wipe SecureStorage without explicit per-turn confirmation.
- **Build & run commands**: `dotnet run -f net11.0-macos` for the desktop dev head (Captain's default — see "Preferred MAUI dev head" below); Mac Catalyst only when iOS-shaped behavior is being tested. MAUI heads target `net11.0-*`, Azure/server/Shared stay on `net10.0`. (Note: `dotnet build -t:Run` is obsolete as of .NET 11 Preview 4 — use `dotnet run`.)
- **EF Core migrations**: use `dotnet ef migrations add` against `SentenceStudio.Shared.csproj` — never hand-write migrations, never raw SQL ALTER TABLE.
- **MauiReactor conventions**: `.HEnd()` / `.VCenter()` / `.Center()` instead of `HorizontalOptions(LayoutOptions.End)`; `ThemeKey(MyTheme.*)` over inline styles; icons in `ApplicationTheme.Icons.cs`, never inline `FontImageSource`.
- **Shell navigation**: `MauiControls.Shell.Current.GoToAsync(...)`, never `Navigation.PushAsync`.
- **Validation gate**: every UI/behavior change must be validated by running the app end-to-end via the MAUI DevFlow skills (`maui-devflow-debug` for build/deploy/inspect/fix loops, `maui-devflow-onboard` for first-time setup, `maui-devflow-session-review` for friction reports) or the `e2e-testing` skill — "it builds" is not sufficient.
- **No emoji in UI / code output / logs** — use Bootstrap icons or plain text.

If anything below conflicts with AGENTS.md, **AGENTS.md wins** — EXCEPT for the macOS-vs-Mac-Catalyst default; the section immediately below overrides AGENTS.md examples on that one point until AGENTS.md is updated.

## Preferred MAUI dev head: macOS (AppKit), NOT Mac Catalyst

**Captain has corrected this default 5+ times across distinct sessions** — most recently: *"don't use net11.0-maccatalyst, use net11.0-macos instead. I keep telling you that. Remember it."* Default to the macOS (AppKit) head for all desktop development unless Captain has explicitly asked for Mac Catalyst.

- **Default desktop dev head**: `dotnet run -f net11.0-macos --project src/SentenceStudio.macOS/SentenceStudio.macOS.csproj`
- **Use Mac Catalyst only when**: the bug is iOS-shaped (touch gestures, SafeAreaEdges, Catalyst-specific entitlements) AND Captain has explicitly named that surface.
- **AGENTS.md drift**: many examples in `AGENTS.md` still show `net10.0-maccatalyst` / `net11.0-maccatalyst` as the daily-dev surface. **This instruction overrides those examples** until AGENTS.md is updated.

If you find yourself about to run `-f net11.0-maccatalyst` "to verify the fix" without Captain having named that surface — stop and use `-f net11.0-macos` instead.

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

## Test accounts

Canonical Squad test credentials live at **`.squad/test-accounts.md`**. **Read it before inventing accounts for E2E verification.**

- Primary account: `squad-jayne@sentencestudio.test` / `SquadTest!2026` (Korean target language).
- If the account doesn't exist on the target environment (fresh sim, fresh local DB, fresh Azure deployment), register it via the app's standard Register flow — do NOT create a new test-only account out of band, and do NOT use Captain's real credentials for automated flows.
- Applies to webapp E2E (Playwright), Mac Catalyst, macOS, iOS Sim, and any other surface that needs auth.

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

**Interactive sessions: do NOT open PRs.** Captain is a solo developer who merges directly to `main`. When he says "merge to main" / "ship it" in a session he is driving, commit and merge straight into `main` — no PR, no `gh pr create`, no `create_pull_request` tool — then push. See AGENTS.md › "Git Workflow: Direct Merge to Main — No PRs". `main` CI runs on `push`, so nothing is lost. Open a PR only if Captain explicitly asks.

**The guidance below applies ONLY to autonomous cloud Coding Agent work on an assigned GitHub issue**, where a PR is the async delivery + review channel:

When opening a PR:
- Reference the issue: `Closes #{issue-number}`
- If the issue had a `squad:{member}` label, mention the member: `Working as {member} ({role})`
- If this is a 🟡 needs-review task, add to the PR description: `⚠️ This task was flagged as "needs review" — please have a squad member review before merging.`
- Follow any project conventions in `.squad/decisions.md`

## Cite, don't invent — for PR bodies, issue bodies, and commit messages

Any factual claim that ends up in a PR description, GitHub issue body, commit message, changelog entry, or release note **must be verifiable from something you just read or ran**. This includes:

- **Version numbers** — SDK versions, package versions, TFMs. Read `global.json`, the `.csproj` `<TargetFramework>`, or run `dotnet --info`. Do NOT name a version because it "sounds right" or "is probably the current one".
- **File paths and line numbers** — open the file and confirm before citing in a PR body.
- **Behavior descriptions** — "this fixes X" claims must come from a test run, a logged repro, or a code-read; not a hunch.
- **CI / workflow names** — confirm `.github/workflows/{name}.yml` exists before referencing it.

If you cannot verify a claim, **omit it** or mark it explicitly: `_(unverified — Captain to confirm)_`. The Brot upstream-issue incident (June 2026) — Captain caught a fabricated SDK version with *"why did you note 11 preview 4 in the issue filed?"* — is the failure mode this rule prevents.

## Decisions

If you make a decision that affects other team members, write it to:
```
.squad/decisions/inbox/copilot-{brief-slug}.md
```
The Scribe will merge it into the shared decisions file.

## Clarify the surface before investigating

This solution spans Mac Catalyst, iOS, Android, Mac AppKit, the Blazor WebApp, and the ASP.NET Core API. Same C# code, very different runtime context — per-device SQLite vs. shared Postgres, `MauiPreferencesService` vs. singleton `WebPreferencesService`, single-user device vs. multi-tenant server.

Before deep investigation of any reported bug, **confirm which surface the user actually hit** (Mac Catalyst Debug? iOS on DX24? Production WebApp? Aspire-local API?). Asking one clarifying question up front is cheaper than reproducing in the wrong environment for 30 turns. If the user already named the surface (e.g. "production"), do not start by reproducing locally on the device head — the failure modes don't overlap.

**Hard surface-lock rule (added after the Brot session, June 2026):** Once Captain names a surface — "production webapp", "iOS sim", "local Aspire", "DX24", "Mac AppKit" — that IS the surface. Do NOT silently switch to a different one because it's faster to instrument or because your tools work better there. Real failure patterns from that session:

- Captain said *"Again, local env not production"* — agent kept investigating local Aspire because prod log access was harder. **Wrong.** Use prod-targeted diagnostics (query prod Postgres via the connection string, read Azure Container Apps logs, etc.).
- Captain said *"I'm using the web app which is what I already told you"* — agent built Mac Catalyst to repro. **Wrong.** The webapp uses singleton `WebPreferencesService`; Mac Catalyst uses per-device `MauiPreferencesService` — multi-tenant bugs literally cannot manifest in the wrong head.
- Captain said *"the relinking of my account was in the local postgres environment, not azure production where I'm seeing Brot"* — agent had been chasing a local-only artifact. **Wrong.** Production data is its own thing; local "fixes" do not propagate.

If the user-named surface is genuinely unreachable (e.g., production logs you don't have access to), **say so and ask** — *"I can't reach production logs directly — can you paste the log line, share a screenshot, or confirm I can use {Y} as a proxy?"* Switching surfaces unilaterally without that ask is the bug pattern.

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

## Pre-completion checklist — the `Verified:` line

The global Copilot instructions already require a `Verified:` line on every task-closing response. For SentenceStudio specifically, "verified" means one of these — pick the row matching the change type, and **say it explicitly in your closing message**:

| Change type | What the `Verified:` line must name |
|------------|--------------------------------------|
| MAUI UI/behavior change | `Verified: built + ran on net11.0-macos, navigated to {page}, took screenshot, behavior matches expectation.` |
| Webapp change | `Verified: built webapp, ran via aspire, opened https://localhost:{port}/{path}, behavior matches expectation.` |
| API / data / repository change | `Verified: unit tests pass ({n}/{n}), and {specific repro of the code path against a real DB row}.` |
| EF Core migration | `Verified: Up + Down ran clean on a DB backup; row counts and schema look right.` |
| Doc-only / internal refactor | `Verified: not applicable — {one-line reason}.` |

A closing message that ends with *"want me to deploy?"*, *"ready for review?"*, *"let me know if you want me to smoke-test it next"* — **without a `Verified:` line above it — is malformed by definition.** The verification must run BEFORE the closing message, not be offered as an optional next step.

Captain has caught this skipped-gate pattern at least 3 times across sessions (*"did you actually run e2e tests?"*, *"I cannot believe you did since the first thing you have to do is navigate..."*, *"Did you verify it with any e2e testing? devflow etc?"*). The gate exists to prevent exactly that. If you genuinely cannot verify (no simulator handy, no prod access), say so explicitly and ask Captain to verify — but do NOT skip the `Verified:` line.
