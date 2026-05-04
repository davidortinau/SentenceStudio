# Training Log: ef-dual-provider-migrations

## Session: 2026-05-03 — Initial Assessment

**Trainer:** Skill Trainer (post-ship review)
**Trigger:** Skill created during auth-persistence fix. First validation.

### Assessment

Strong skill. Covers the `dotnet ef` tooling failure mode for multi-TFM projects, the
PG↔SQLite type mapping table, the timestamp-must-match constraint, and the mandatory
`scripts/validate-mobile-migrations.sh` gate. Used successfully this session to ship
`AddRefreshTokenReplacedBy` (PG + SQLite pair) — both applied cleanly, validation script
green, no schema drift on first run.

Confidence: **medium → high** after this cycle.

No gaps observed. The skill matches the actual workflow and outputs that shipped.

### Changes Made

None. Skill was followed verbatim and worked.

### Suggested Eval Scenarios

1. **"Add a nullable `LastUsedAt` DateTime column to RefreshToken"** — give the model the
   skill. Verify the produced migrations: (a) both files exist, (b) timestamps match,
   (c) PG uses `timestamp with time zone` and SQLite uses `TEXT`, (d) namespaces differ.
   Score on all 4 invariants.
2. **"Diagnose: mobile app crashes with `SQLite Error 1: 'no such column'` after deploy"** —
   verify the model jumps to the dual-migration check (rather than chasing data corruption
   or model-config issues).

### Verdict

**KEEP-AS-IS.** Production-validated.
