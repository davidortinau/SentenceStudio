## Active Decisions

(Most recent decisions below. Archived decisions in `decisions-archive-2026-04-25.md`)

---


---

### 2026-06-01: Local dev test accounts — durable fixtures for stable E2E testing

**By:** Copilot (Coding Agent)
**Date:** 2026-06-01
**Status:** ✅ IMPLEMENTED
**Context:** E2E testing and local development tooling friction reduction

#### Decision

Development AppHost startup seeds three canonical local-only Identity accounts:

- `captain@test.local`
- `testsailor@test.local`
- `e2e@test.local`

Each account is email-confirmed and linked to a default English-to-Korean A1 `UserProfile`.

#### Rationale

Stable local fixtures prevent agents and E2E scripts from creating many one-off accounts, reduce auth confusion across worktrees, and give Captain a durable daily-driver account for local testing.

#### Source of truth

Keep `src/SentenceStudio.Api/Auth/DevTestAccountSeeder.cs`, `docs/local-dev-test-accounts.md`, and `.github/copilot-instructions.md` in sync when fixture accounts change.

#### Implementation

- ✅ `DevTestAccountSeeder` added to AppHost (Development environment only)
- ✅ Durable local fixtures seeded at AppHost startup
- ✅ Documentation in `docs/local-dev-test-accounts.md`
- ✅ Seeding behavior validated with tests
- ✅ Instruction table added to `.github/copilot-instructions.md`
- ✅ WebApp Development auto-confirm patch included

#### Related files

- `src/SentenceStudio.Api/Auth/DevTestAccountSeeder.cs` — seeding logic
- `docs/local-dev-test-accounts.md` — usage guide
- `.github/copilot-instructions.md` — team reference table
- Tests added to `SentenceStudio.Api.Tests` for seeding behavior
