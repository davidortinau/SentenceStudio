# Training Log: E2E Testing Skill

## Session: 2025-07-16 — Post-Deploy Validation & Data Integrity

**Trainer:** Skill Trainer (automated)
**Trigger:** User reported deployment and data recovery learnings

### Assessment

The e2e-testing skill covered local testing well but had critical gaps:
1. ❌ No post-deploy validation — skill ended at "it compiles is not sufficient" but had no guidance for deployed environments
2. ❌ No reference to existing `scripts/post-deploy-validate.sh` in the repo
3. ❌ No proxy health check pattern (POST /api/auth/login with bad creds since no /health endpoint exists)
4. ❌ No data integrity verification patterns for EF Core + SQLite gotchas
5. ❌ No sync/retagging recovery patterns

### Changes Made

1. **Added "Post-Deploy Validation" section** immediately after the "Marking a Task Complete" checklist:
   - Explicit warning that exit code 0 ≠ working
   - Reference to `scripts/post-deploy-validate.sh`
   - Proxy health check pattern using POST with bad credentials
   - "Verify the SPECIFIC change" guidance (migration → query DB, bug fix → reproduce, feature → exercise)

2. **Added "Data Integrity Patterns" section** covering:
   - NULL in non-nullable columns from unapplied migrations
   - `[ObservableProperty]` stripping nullability with `IsRequired(false)` fix
   - `.AsSplitQuery()` requirement for multi-Include SQLite queries
   - CoreSync tracking table cleanup for user ID retagging

### Evidence

- `scripts/post-deploy-validate.sh` exists and uses the proxy health check pattern (line 166)
- AppHost.cs confirms PostgreSQL is the production database
- User reported these patterns from actual data recovery session

### Rationale

The skill's strength was "test what you change." But the deployment gap meant agents would deploy to production, see exit code 0, and move on — never verifying the deployment actually worked. The data integrity patterns prevent recurring issues with SQLite databases that accumulate schema drift over time.
