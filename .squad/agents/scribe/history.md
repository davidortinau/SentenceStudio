# Project Context

- **Project:** SentenceStudio
- **Created:** 2026-03-07

## Core Context

Agent Scribe initialized and ready for work.

## Recent Updates

📌 Team initialized on 2026-03-07
📌 Squad — Word/Phrase Plan Review checkpoint logged 2026-07-26
📌 **2026-06-01** — Decision archive + local dev test accounts logging complete

For detailed work history, see:
- `.squad/agents/scribe/history-summary-2026-06-01.md` — Condensed summary of recent 10 sessions (learnings, conventions, files)
- `.squad/log/` — Full session logs (timestamped)
- `.squad/orchestration-log/` — Orchestration details per agent

## Current Conventions (6 Key Patterns)

1. **Custom JWT claims:** `SentenceStudio.Contracts.AuthClaimTypes` (no magic strings)
2. **Inbox artifact paths:** Preserve when cited from public issues/PRs (path stability > cleanliness)
3. **Local dev fixtures:** Three durable accounts at startup (captain@test.local, testsailor@test.local, e2e@test.local)
4. **Auth token refresh:** 2-401 gate before clearing token; 60s grace window
5. **App entitlements (Mac Catalyst):** Use `$(AppIdentifierPrefix)` macro; no keychain-access-groups for ad-hoc signing
6. **Decisions archival:** File size triggers archival:
   - >= 20,480 bytes → archive entries > 30 days old
   - >= 51,200 bytes → archive entries > 7 days old

## 2026-06-01 — Decision Archive + Local Dev Test Accounts Logging

**Status:** ✅ Complete

**Archival executed:**
1. ✅ `decisions.md` 80,180 bytes (>= 51,200 threshold) → archive entries older than 7 days
2. ✅ All dated entries (May 5-8) moved to `decisions-archive-2026-05-25.md`
3. ✅ Inbox merge: `copilot-local-dev-test-accounts.md` → decisions.md (1 file processed)
4. ✅ Session log written: `.squad/log/2026-06-01T00-14-49Z-local-dev-test-accounts.md`
5. ✅ Orchestration log: `.squad/orchestration-log/2026-06-01T00-14-49Z-scribe.md`

**Decision logged:**
- Local dev test accounts convention documented
- Fixture accounts seeded at AppHost startup (Development only)
- Reduces auth flakiness for multi-agent E2E runs

**Scope boundary:** This run restricted to `.squad/` work only — no domain changes.

---

## 2026-06-21 — Vocabulary Duplicate Merge Criteria Logging

**Status:** ✅ Complete

**Session logged:**
1. ✅ Session log created: `.squad/log/2026-06-21T15-44-06Z-duplicate-cleanup.md`
2. ✅ Inbox merge: `copilot-duplicate-merge-criteria.md` → decisions.md (1 file processed)
3. ✅ Orchestration log: `.squad/orchestration-log/2026-06-21T15-44-06Z-scribe.md`

**Decision captured:**
- Vocabulary duplicate detection focuses on semantic safety (target term, native term, language, lexical unit type match)
- Keeper selection prioritizes encoding strength + memory aids, NOT resource count
- Duplicate review UI hides raw IDs, resource counts, and internal implementation details
- Batch merge and single-group merge use unified recommendation logic
- Learning progress and resource associations are combined during merge (not user-facing criteria)

**Scope boundary:** This run restricted to `.squad/` work only — no domain changes. Duplicate cleanup is a focused UX refinement with code verification (unit tests, E2E on Mac Catalyst webapp).
