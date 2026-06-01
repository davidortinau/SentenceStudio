# Scribe History Summary

**Generated:** 2026-06-01  
**Original history size:** 16,076 bytes → Archival triggered at 15,360 byte threshold

## Recent Work Summary (Last 10 Sessions)

### Authentication & Security

- **2026-05-03:** Auth persistence fix cycle shipped (concurrency race, JWT lifetime alignment, Mac Catalyst entitlements, pre-load, SemaphoreSlim leak guard). New test project `SentenceStudio.AppLib.Tests` created for regression testing. Convention: 2-401 gate for token refresh.

### UI & Content Patterns

- **2026-04-29:** net11p3 Razor RenderFragment regression isolated. Trigger: switch-expression-of-RenderFragment-with-inline-markup. Workaround: tuple-meta helpers (GetTypeBadgeMeta, GetStatusBadgeMeta). Upstream issue filed at github.com/dotnet/razor/issues/13117.

- **2026-05-03:** Vocab Quiz bug cluster shipped (#189–#194). PR #196 (Kaylee) + PR #198 (Wash) merged. Convention: When inbox artifacts are cited from public issues/PRs, preserve their path — do not move to archive.

### Infrastructure & Package Management

- **2026-04-27 to 2026-04-28:** M.E.AI 10.5 debt-paydown shipped. CPM (Directory.Packages.props) centralized ~95 packages. Polly resilience added to all OpenAI HTTP. Config extraction (gpt-4o-mini, tts-1, voice IDs) → appsettings.json.

### Data Import MVP

- **2026-04-24 to 2026-05-01:** Multi-agent data import architecture planned and shipped. Wave 3 final merge complete; 11/11 todos shipped. IAiService extraction unblocked unit testing. 18 unit tests + 15-scenario E2E script, 0 bugs, ~95% coverage.

### Local Dev & Testing

- **2026-06-01:** Durable local dev test accounts implemented. Three canonical fixtures: captain@test.local, testsailor@test.local, e2e@test.local. Seeded at AppHost startup (Development only). Reduces auth flakiness in multi-agent E2E runs.

## Key Learnings

1. **SDK regression triage:** When multiple errors appear in a file, check line numbers first. Empty type/member names + CS9348 = source generator bailout = pattern-specific bug, not SDK-wide regression.

2. **Bookkeeping correction pattern:** Add a new dated entry with corrected facts + surgically soften obsolete claims in original (with forward pointer). Preserves diagnostic trail.

3. **Inbox artifact stability:** Path stability for publicly-referenced artifacts (in issues/PRs) > inbox cleanliness. Keep them at their original location even after decision is merged.

4. **SemaphoreSlim in async loops:** Guard Release with bool sentinel to prevent leak on early failure.

5. **OAuth grace window:** 60s window balances platform I/O delays vs tight attacker window.

6. **Archival strategy:** Decisions.md file size drives archival:
   - >= 20,480 bytes: archive entries > 30 days old
   - >= 51,200 bytes: archive entries > 7 days old

## Current Conventions

- **Custom JWT claims:** Defined in `SentenceStudio.Contracts.AuthClaimTypes` (not magic strings)
- **Inbox decision artifact paths:** Preserve when cited from public issues/PRs
- **Local dev fixtures:** Three durable accounts at startup; stable for E2E automation
- **Auth token refresh:** 2-401 gate before clearing refresh token; 60s grace window for successive refresh
- **App entitlements (Mac Catalyst Debug):** Use `$(AppIdentifierPrefix)` macro; omit keychain-access-groups for ad-hoc signing

## Files & Locations

- `.squad/decisions.md` — Active decisions (header only after archival)
- `.squad/decisions-archive-2026-05-25.md` — Archived May 5-8 entries (78KB+)
- `.squad/log/` — Session logs (timestamped)
- `.squad/orchestration-log/` — Agent orchestration logs
- `.squad/agents/{agent}/history.md` — Per-agent memory (this file summarized)

## Next Session Checklist

1. Monitor `.squad/decisions.md` size; re-archive if >= 51,200 bytes (cutoff: 7 days old)
2. Merge any pending inbox decisions monthly
3. Update agent histories with recent orchestration outcomes
4. Preserve paths for publicly-referenced inbox artifacts
5. Commit `.squad/` changes with proper trailers after bookkeeping
