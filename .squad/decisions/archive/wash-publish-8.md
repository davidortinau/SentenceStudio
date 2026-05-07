# Publish #8 — NumberDrill Layout Fixes (Footer Pin + Card Wrapper Removal)

**Date:** 2026-05-07  
**Requested by:** David (Captain)  
**Published by:** Wash (DevOps/Release Engineering)

## Change Summary

**Component:** NumberDrill.razor (CSS + HTML layout fixes)  
**Commits Shipped:**
- `577852ff` — fix(css): PageHeader `flex-shrink: 0` so footer pins to bottom edge
- `d09c233c` — fix(numberdrill): Remove card wrapper to match VocabQuiz flat layout
- `28aaca6e` — bookkeeping

**What Changed:** 
1. Fixed progress footer positioning (now pinned to bottom edge via CSS)
2. Removed card-ss wrapper from active session content for flat layout parity with VocabQuiz
3. **Build issue discovered & fixed:** Kaylee's card wrapper removal left unbalanced HTML tag (extra `</div>`). Fixed before deploy.

**Motivation:** Captain validated #7 on DX24: footer wasn't at bottom AND card wrapper shouldn't be there. Both now corrected.

## Deployment Execution

### Azure Deploy

- **Command:** `azd deploy` (net10 SDK)
- **Duration:** 1m 57s
- **Result:** ✅ SUCCESS

**API Revision:** `api--0000094` (new)  
**WebApp Revision:** `webapp--0000080` (new)  

### Post-Deploy Validation

**Script:** `./scripts/post-deploy-validate.sh`  
**Result:** ✅ ALL CHECKS PASSED

- PASS: 16/16
- FAIL: 0
- SKIP: 2 (auth flow — expected)
- WARN: 2 (workers scaled-to-zero, migration logs scrolled — both non-blocking)

**Validation Details:**
- Infrastructure: ✅ All services Running, api--0000094 + webapp--0000080 active & latest
- Database: ✅ Connected (auth returned expected 401)
- Endpoints: ✅ HTTP 200 on webapp homepage, API bootstrap healthy
- No crash indicators ✅

### iOS Build + Install to DX24

**Build Command:**
```bash
services__api__https__0=https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io \
dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj \
-f net10.0-ios -c Release -p:RuntimeIdentifier=ios-arm64 \
-p:ValidateXcodeVersion=false
```

**Build Result:** ✅ SUCCESS (net10 SDK, 0 errors, canonical recipe)  
**App Bundle:** `/Users/davidortinau/work/SentenceStudio/src/SentenceStudio.iOS/bin/Release/net10.0-ios/ios-arm64/SentenceStudio.iOS.app` created ✅

**Install to DX24 (CF4F94E3-A1C9-5617-A089-9ABB0110A09F):**
- **Attempt #1 (before wake):** ❌ CoreDeviceError 4000 + NWError 57 (device deep sleep)
- **Attempt #2 (after device wake):** ✅ SUCCESS
  - Tunnel acquired ✅
  - Developer disk image enabled ✅
  - App installed to `file:///private/var/containers/Bundle/Application/A775B3B6-704C-430B-9DD7-9FCCACE4DB71/SentenceStudio.iOS.app/` ✅
  - Database UUID: `BFB39C79-6089-4E5D-AD37-0B84FF06BDA3` ✅

**Launch on DX24:**
- **Result:** ✅ SUCCESS
- Process launched with bundle ID `com.simplyprofound.sentencestudio` ✅

## Build Issue & Fix

**Error:** RZ9981 + RZ1026 (unbalanced HTML tags in NumberDrill.razor:417)

**Root Cause:** Kaylee's card wrapper removal removed the opening `<div class="card card-ss p-4">` but left the corresponding closing `</div>`. HTML parser caught 33 closing divs vs 32 opening divs.

**Fix:** Removed extra `</div>` at line 416, verified div count now balanced (32 open = 32 close).

**Retry:** Azure deploy succeeded after fix.

## Final Status

| Component | Status | Notes |
|-----------|--------|-------|
| Azure API Deploy | ✅ PASS | api--0000094 active |
| WebApp Deploy | ✅ PASS | webapp--0000080 active |
| Infrastructure Validation | ✅ PASS | 16/16 checks |
| HTML/Build Fix | ✅ PASS | Unbalanced divs corrected |
| iOS Build | ✅ PASS | net10 + ValidateXcodeVersion=false |
| iOS Install | ✅ PASS | Installed + launched on DX24 |
| **Overall** | **✅ PUBLISHED** | Ready for manual layout validation |

## Next Steps — Manual Validation

**Captain to verify on DX24:**
1. App already running (launched successfully)
2. Navigate to NumberDrill activity
3. **Confirm footer:** Progress bar pinned to bottom edge (not floating)
4. **Confirm layout:** Flat card-free layout matching VocabQuiz (no card wrapper around session content)
5. Interact with activity (type answers) and verify both fixes persist

## Bookkeeping

- **History:** Appended to `.squad/agents/wash/history.md`
- **Decision file:** This file (`.squad/decisions/inbox/wash-publish-8.md`)
- **Decision commit:** Will be committed after all steps complete
