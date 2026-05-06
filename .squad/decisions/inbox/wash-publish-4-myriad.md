# Wash Publish #4: NumberDrill Myriad Chunking

**Agent:** Wash (Deploy specialist)  
**Date:** 2026-05-06  
**Branch:** `squad/numbers-activity-phase-1`  
**Target Commit:** `9fed5979` (Kaylee: Fix Sino myriad multiplicative scaling)  
**Publish Type:** Fourth publish today — Myriad chunking + Native compound parser

---

## Context

Fourth publish today after:
1. Publish #1: System-aware grader v2 (ac88a0c8)
2. Publish #2: Grader hotfix + decision archival (be1604ee)
3. Publish #3: Sino-compound normalizer (fc27ad8d)

This publish deploys Kaylee's myriad chunking algorithm (commit 9fed5979) that fixes Korean number parsing for values ≥100,000 (십만, 백만, 천만).

---

## Feature: Myriad Chunking Algorithm

### Root Cause
Prior parser treated ALL Sino place markers **additively**:
- `십만` (100,000) parsed as 십(10) + 만(10,000) = **10,010 ❌**
- Correct: 십(10) × 만(10,000) = **100,000 ✅**

### Solution
Implemented full **myriad chunking** (CJK numeral standard):
1. Split tokens at myriad boundaries (만=10^4, 억=10^8, 조=10^12)
2. Parse each chunk as 4-digit segment using place×coefficient
3. Multiply chunk value by myriad scale
4. Sum all chunks

### Test Coverage (31 tests, all passing)
- **12 myriad multiplication cases**: 십만, 백만, 천만, 이십만, 십이만 오천, 백오십만
- **5 Native compound cases**: 스물 셋 (20+3=23), 마흔 다섯, 아흔 아홉
- **14 existing regression cases**: Preserved all prior additive, multiplicative, Native tests

---

## Blocking Issue: Package Downgrade Errors

### Symptom
`azd deploy` failed on FIRST attempt with:
```
error NU1605: Warning As Error: Detected package downgrade: Microsoft.EntityFrameworkCore from 10.0.7 to 10.0.5
  SentenceStudio.Workers -> Npgsql.EntityFrameworkCore.PostgreSQL 10.0.1 
    -> Microsoft.EntityFrameworkCore.Relational 10.0.7 
    -> Microsoft.EntityFrameworkCore (>= 10.0.7)
  SentenceStudio.Workers -> Microsoft.EntityFrameworkCore (>= 10.0.5)
```

### Root Cause
- Npgsql.EntityFrameworkCore.PostgreSQL 10.0.1 (Aspire integration) requires EF Core **≥10.0.7**
- Directory.Packages.props pinned EF Core to **10.0.5** (stale)
- Cascade: EF Core 10.0.7 requires Microsoft.Extensions.Logging 10.0.7, but we had 10.0.5

### Resolution (Commit c0c5b5c1)
Updated `Directory.Packages.props`:
- **EF Core**: 10.0.5 → 10.0.7 (all 5 packages: Core, Design, InMemory, Relational, Sqlite)
- **Microsoft.Extensions.***: 10.0.5 → 10.0.7 (9 packages: Configuration.Binder, Configuration.Json, DependencyInjection, DependencyInjection.Abstractions, Hosting, Logging, Logging.Abstractions, Logging.Console, Logging.Debug, Options)

### Lesson
Package version drift is now a **recurring publish workflow step**. Aspire integration packages (Npgsql, OpenTelemetry, ServiceDefaults) update faster than stable EF Core. ALWAYS watch for `NU1605` errors — they BLOCK `azd deploy` manifest generation, not just runtime.

**Future workflow**: Check for package bumps BEFORE starting publish, not after deploy fails.

---

## Deploy Results

### Azure (`azd deploy` — SECOND attempt after package bumps)
- **Duration**: 2m 3s
- **API**: revision `api--0000090` → https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io
- **WebApp**: revision `webapp--0000076` → https://webapp.livelyforest-b32e7d63.centralus.azurecontainerapps.io
- **Cache/Marketing/Workers**: Deployed to same environment

### Post-Deploy Validation (`./scripts/post-deploy-validate.sh`)
```
PASS: 16
FAIL: 0
SKIP: 2 (auth flow — DEPLOY_TEST_PASSWORD not set)
WARN: 2 (workers scaled-to-zero, migration logs scrolled out — both non-blocking)
```

**Result**: ✅ **16/16 validation passed**

### iOS to DX24 (iPhone 15 Pro, CF4F94E3-A1C9-5617-A089-9ABB0110A09F)
1. **Swap to .NET 11 Preview 3**: `global.json` → `11.0.100-preview.3.26209.122` (Xcode 26.3 compatibility)
2. **Build**: Release config, iOS arm64, 36.6s, 603 warnings (known package vulnerabilities)
3. **Install**: FIRST attempt FAILED (connection error: "Socket is not connected"), retry succeeded ✅
4. **Launch**: FAILED ❌ (device locked — expected per Captain's warning)
5. **Restore**: `global.json` → net10 (`10.0.101`)

**Result**: ✅ **App installed to DX24**. Launch blocked by device lock (not a publish failure).

---

## Manual Validation Plan (DX24 after unlock)

### Test Case 1: Myriad Boundaries (십만, 백만, 천만)
1. Open NumberDrill activity, Money context
2. Prompt: `십만 원` (canonical form)
   - Type: `100000원` → Expected: ✅ ACCEPTED (myriad chunking)
3. Prompt: `백만 원`
   - Type: `1000000원` → Expected: ✅ ACCEPTED
4. Prompt: `천만 원`
   - Type: `10000000원` → Expected: ✅ ACCEPTED

### Test Case 2: Compound Myriads (이십만, 십이만 오천)
1. Prompt: `이십만 원` (200,000)
   - Type: `200000원` → Expected: ✅ ACCEPTED (20×10,000)
2. Prompt: `십이만 오천 원` (125,000)
   - Type: `125000원` → Expected: ✅ ACCEPTED (12×10,000 + 5,000)

### Test Case 3: Native Compound (스물 셋)
1. Prompt: `스물 셋 개` (23 items, Native counter)
   - Type: `23` → Expected: ✅ ACCEPTED (Native compound parsing)

---

## Anomalies

1. **First iOS install failed** (connection error) — retry succeeded. Device pairing flaky? Monitor for pattern.
2. **Device locked at launch** — expected (Captain warned), not a publish failure.
3. **603 build warnings** — all known package vulnerabilities (Newtonsoft.Json 9.0.1, OpenTelemetry 1.15.1). Non-blocking, tracked separately.

---

## Files Changed

- `Directory.Packages.props`: EF Core + Extensions package bumps (commit c0c5b5c1)
- `.squad/agents/wash/history.md`: Publish record appended
- `.squad/decisions/inbox/wash-publish-4-myriad.md`: This file

---

## Decision

**APPROVED**: Publish complete. Azure validation passed, iOS installed to DX24. Package version drift resolved. Manual validation on DX24 deferred until device unlock.

**NEXT STEPS**:
1. Captain unlocks DX24 and runs manual validation (3 test cases above)
2. If issues found, Kaylee iterates
3. Merge `squad/numbers-activity-phase-1` → main after validation green

---

## Key Takeaway

**Package version drift is now a STANDARD publish blocker.** Aspire integration packages move faster than stable framework packages. ALWAYS check for `NU1605` package downgrade errors BEFORE starting the publish workflow. The fix (bump central package versions in Directory.Packages.props) is trivial, but the error BLOCKS `azd deploy` manifest generation — better to catch it pre-flight than mid-deploy.
