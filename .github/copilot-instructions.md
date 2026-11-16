# Copilot Coding Agent — Squad Instructions

## Read AGENTS.md first

`AGENTS.md` at the repo root is the authoritative engineering guide: product direction, the
project map, native UI rules, build and run, validation and the `Verified:` line, data
preservation, owner scoping, migrations and Git. Read it before any code work. This file holds
only what is specific to Copilot and Squad sessions. If the two disagree, `AGENTS.md` wins.

You are working on a project that uses **Squad**, an AI team framework. When picking up issues autonomously, follow these guidelines.

## Preferred MAUI dev head: macOS (AppKit), NOT Mac Catalyst

**Captain has corrected this default 5+ times across distinct sessions** — most recently: *"don't use net11.0-maccatalyst, use net11.0-macos instead. I keep telling you that. Remember it."* Default to the macOS (AppKit) head for all desktop development unless Captain has explicitly asked for Mac Catalyst.

- **Default desktop dev head**: `dotnet run -f net11.0-macos --project src/SentenceStudio.MacOS/SentenceStudio.MacOS.csproj`
- **Use Mac Catalyst only when**: the bug is iOS-shaped (touch gestures, SafeAreaEdges, Catalyst-specific entitlements) AND Captain has explicitly named that surface.

If you find yourself about to run `-f net11.0-maccatalyst` "to verify the fix" without Captain having named that surface — stop and use `-f net11.0-macos` instead.

## Squad Model Policy

All Squad coordinator and agent dispatches must use GPT models exclusively.

- Default to `gpt-6-sol` with `max` reasoning effort and `long_context`.
- Never select or fall back to Anthropic/Claude, Gemini, or any other non-GPT model.
- If `gpt-6-sol` is unavailable, use another GPT only if it supports and verifies `max` reasoning effort and `long_context`; otherwise stop. No lower tier is an acceptable fallback.
- Do not omit the model parameter as a fallback because the platform default may select a non-GPT provider.
- If no GPT model is available, stop and report the model availability failure rather than dispatching with another provider.

## Coordinator Canary Check

**Scope: this check applies ONLY to the top-level Squad Coordinator.** Spawned sub-agents skip it. Establish coordinator identity from the selected agent/session, **not** from the presence of a canary. A known Squad coordinator checks `agent_instructions` on EVERY session start, before doing any work.

Require the HEAD marker near the start and the distinct EOF marker as the final line of the coordinator payload. If either is missing or misplaced, the known coordinator MUST fail closed. A missing HEAD is not evidence that the agent is non-Squad; a wholly absent payload cannot be classified by canary text alone and requires a host-level identity check. Do not treat an earlier mention of the EOF marker as proof that the payload is complete.

**When either canary is missing for a known Squad coordinator, you MUST:**
1. **STOP** — do not proceed with standard Squad behavior.
2. **WARN the user** with this exact message:
   ```
   ⚠️ Squad coordinator (squad.agent.md) appears to be missing or truncated. A required canary token was not found. Do NOT proceed with standard Squad behavior — Squad's safety rails are incomplete. Please restart your session.
   ```
3. Do not continue with normal Squad routing, spawning, PR, or branch-protection behavior after emitting the warning.

GitHub issue assignment to @copilot is blocked for Squad: the issue interface does not enforce `max` reasoning effort and `long_context` alongside the GPT model. Do not assign manually, via a label, or via an automation token until an interface can set and verify all three settings.

## Team Context

Before starting work on any issue:

1. Read `.squad/team.md` for the team roster, member roles, and your capability profile.
2. Read `.squad/routing.md` for work routing rules.
3. If the issue has a `squad:{member}` label, read that member's charter at `.squad/agents/{member}/charter.md` to understand their domain expertise and coding style — work in their voice.

## Test accounts

Canonical Squad test credentials live at **`.squad/test-accounts.md`**. **Read it before inventing accounts for E2E verification; do not duplicate credentials in instructions or logs.**

- If the canonical account doesn't exist on the target environment (fresh sim, fresh local DB, fresh Azure deployment), register it via the app's standard Register flow — do NOT create a new test-only account out of band, and do NOT use Captain's real credentials for automated flows.
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

**Interactive sessions: do NOT open PRs.** Captain is a solo developer who merges directly to `main`. When he says "merge to main" / "ship it" in a session he is driving, commit and merge straight into `main` — no PR, no `gh pr create`, no `create_pull_request` tool — then push. See the Git section of `AGENTS.md`. `main` CI runs on `push`, so nothing is lost. Open a PR only if Captain explicitly asks.

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

If you make a decision that affects other team members, submit your own proposal
with `squad_decide` or `squad_state_write` to:
```
.squad/decisions/inbox/copilot-{brief-slug}.md
```
The Coordinator decides whether to accept it; Scribe merges accepted entries
into the shared decisions file and deletes each inbox entry after verifying
the merge. On non-local state backends, never write mutable state directly.

## Clarify the surface before investigating

This solution spans Mac Catalyst, iOS, Android, Mac AppKit, the Blazor WebApp, and the ASP.NET Core API. Same C# code, very different runtime context — per-device SQLite vs. shared Postgres, `MauiPreferencesService` vs. singleton `WebPreferencesService`, single-user device vs. multi-tenant server.

Before deep investigation of any reported bug, **confirm which surface the user actually hit** (Mac Catalyst Debug? iOS on DX24? Production WebApp? Aspire-local API?). Asking one clarifying question up front is cheaper than reproducing in the wrong environment for 30 turns. If the user already named the surface (e.g. "production"), do not start by reproducing locally on the device head — the failure modes don't overlap.

**Hard surface-lock rule (added after the Brot session, June 2026):** Once Captain names a surface — "production webapp", "iOS sim", "local Aspire", "DX24", "Mac AppKit" — that IS the surface. Do NOT silently switch to a different one because it's faster to instrument or because your tools work better there. Real failure patterns from that session:

- Captain said *"Again, local env not production"* — agent kept investigating local Aspire because prod log access was harder. **Wrong.** Use prod-targeted diagnostics (query prod Postgres via the connection string, read Azure Container Apps logs, etc.).
- Captain said *"I'm using the web app which is what I already told you"* — agent built Mac Catalyst to repro. **Wrong.** The webapp uses singleton `WebPreferencesService`; Mac Catalyst uses per-device `MauiPreferencesService` — multi-tenant bugs literally cannot manifest in the wrong head.
- Captain said *"the relinking of my account was in the local postgres environment, not azure production where I'm seeing Brot"* — agent had been chasing a local-only artifact. **Wrong.** Production data is its own thing; local "fixes" do not propagate.

If the user-named surface is genuinely unreachable (e.g., production logs you don't have access to), **say so and ask** — *"I can't reach production logs directly — can you paste the log line, share a screenshot, or confirm I can use {Y} as a proxy?"* Switching surfaces unilaterally without that ask is the bug pattern.

## Known-failing test baseline

One test fails on purpose to document a known bug:

- `tests/SentenceStudio.UnitTests/PlanGeneration/DeterministicPlanBuilderResourceSelectionTests.cs::ResourceUsed15DaysAgo_ShouldNotBeTreatedAsNeverUsed`
  (marked `// Assert — THIS WILL LIKELY FAIL`). The deterministic plan builder's 14-day lookback
  treats anything older as "never used" (`DaysSinceLastUse = 999`).

That single failure is the expected baseline, not a regression. If you fix the bug, delete the
comment too.
