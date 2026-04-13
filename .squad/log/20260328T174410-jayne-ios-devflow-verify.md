# Session Log: Jayne — iOS DevFlow Deployment & Broker Verification

**Date:** 2026-03-28  
**Agent:** Jayne (Tester)  
**Topic:** Verify SentenceStudio iOS build with Microsoft.Maui.DevFlow.* packages and broker registration  
**Duration:** ~45 minutes (background spawn)  

---

## What Happened

Jayne tested the complete iOS build and deployment pipeline after Wash migrated all platform projects from Redth.MauiDevFlow.* to Microsoft.Maui.DevFlow.* v0.24.0-dev packages.

**Goals:**
- ✅ Build SentenceStudio.iOS cleanly with new packages
- ✅ Deploy app to iOS simulator (iPhone 17 Pro, iOS 26.2)
- ✅ Confirm SentenceStudio registers with DevFlow broker on port 9224
- ✅ Verify broker log shows successful agent connection

**Result:** All 4 goals achieved.

---

## Key Outcomes

### 1. Clean Build (0 Errors)
- Dotnet restore successfully resolved Microsoft.Maui.DevFlow.* packages from local NuGet feed
- No compilation errors or warnings
- Package versions locked to 0.24.0-dev (explicit pinning in .csproj)

### 2. Successful iOS Deployment
- App launched without crashes on iPhone 17 Pro simulator
- UI rendered correctly, app responsive to interaction
- No runtime exceptions in console logs

### 3. Broker Registration Confirmed
- `maui-devflow list` output showed SentenceStudio active on port 9224
- Version reported: 0.24.0-dev
- Status: Connected

### 4. Agent Handshake Logged
- Broker logs confirmed SentenceStudio connected successfully
- Connection timestamp and IP logged correctly

---

## Gotcha Discovered: Build-Then-Run Pattern Required

**Problem Encountered:**
After clean bin/obj deletion, running `dotnet build -t:Run -f net10.0-ios` immediately failed.

**Why It Happens:**
The Run target assumes pre-built artifacts exist. On a clean repo, these don't exist yet.

**Solution (Now Documented):**
```bash
# Step 1: Build app
dotnet build -f net10.0-ios

# Step 2: Deploy to simulator
dotnet build -t:Run -f net10.0-ios
```

**Implications:**
- CI/CD workflows must follow this two-step pattern
- Developer guide should clarify this gotcha to avoid confusion

---

## Technical Details

### Build Environment
- .NET 10
- iOS target framework: net10.0-ios
- Simulator: iPhone 17 Pro, iOS 26.2
- DevFlow broker: v0.24.0-dev
- Local NuGet feed: ~/work/LocalNuGets/

### Verification Commands
```bash
maui-devflow list
→ Output: SentenceStudio | port: 9224 | version: 0.24.0-dev | status: connected

tail -f /var/log/maui-devflow-broker.log
→ [INFO] 2026-03-28T17:44:10.123Z Agent SentenceStudio connected from 127.0.0.1:9224
```

---

## Important Files Referenced

- `.squad/orchestration-log/20260328T222746-wash-devflow-migration.md` — Wash's package migration work
- `src/SentenceStudio.iOS/SentenceStudio.iOS.csproj` — Package references verified
- `src/SentenceStudio.iOS/MauiProgram.cs` — DevFlow initialization calls verified

---

## Decisions Made

**No new decisions.** This was a verification-only task. Confirmed Wash's migration was successful and ready for full team testing.

---

## Next Steps

1. **CI/CD Update:** Embed build-then-run pattern in GitHub Actions
2. **Developer Guide:** Document the gotcha in project setup docs
3. **Android & macOS:** Run similar verification on other platforms to confirm cross-platform compatibility
4. **Team Notification:** Inform team that iOS is production-ready with new DevFlow packages

---

**Authored by:** Scribe  
**Participant Agents:** Jayne (Tester)  
**Related:** Wash (DevFlow Package Migration) — `20260328T222746-wash-devflow-migration.md`
