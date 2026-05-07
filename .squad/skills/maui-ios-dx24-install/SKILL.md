# Skill: iOS DX24 CoreDevice Installation & NWError 57

**Domain:** iOS device deployment  
**Context:** SentenceStudio publish workflow (DX24 = iPhone 15 Pro, CF4F94E3-A1C9-5617-A089-9ABB0110A09F)  
**Status:** ✅ High confidence (observed 4+ times across publishes #6–#9, 2026-05-06 / 2026-05-07)  
**Last Updated:** 2026-05-07

## Problem: NWError 57 During CoreDevice App Install

### Failure Signature

```
xcrun devicectl device install app --device CF4F94E3... SentenceStudio.iOS.app/
Error: CoreDeviceError 4000: ...
NWError 57: <network error details>
```

### Root Cause (Medium Confidence)

**Device deep sleep state** — The CoreDevice control-channel tunnel (used for device communication during install handshake) is killed when the device enters deep sleep, even if the device is also locked.

**Lock state alone is insufficient** to preserve the tunnel. The tunnel requires:
1. Device to be **unlocked**
2. Device to be **recently active** (not in deep sleep)

### Symptoms

- First install attempt fails with NWError 57 (tunnel dead)
- Device is locked
- Device has been idle for some time (deep sleep likely entered)
- Retry immediately without changes will likely fail again
- Retry AFTER physical unlock + device wake succeeds

### Recovery Path

1. **Physical unlock on device:** DX24 unlock via Face ID or passcode
2. **Physical activity:** Tap screen or interact with device (wakes from deep sleep, re-establishes tunnel)
3. **Retry install:** `xcrun devicectl device install app --device CF4F94E3... app.app/`
4. **Outcome:** Second attempt typically succeeds ✅

### Preemptive Procedure (do this BEFORE first install attempt)

**Apply this every publish.** It is faster to wake the device once than to recover from NWError 57.

1. **Wake DX24:** Ask Captain to tap the device screen (or do it yourself if physically present). If locked, unlock with Face ID / passcode.
2. **Confirm device is reachable:**
   ```bash
   xcrun devicectl list devices | grep CF4F94E3
   ```
   Expect `available (paired)`. If not, wait 5–10 seconds and recheck.
3. **Run install.** First attempt should now succeed.
4. **If first attempt still fails with NWError 57 / CoreDeviceError 4000 / ControlChannelConnectionError 1:** retry the SAME command immediately. **This is expected and not a build error.** Do NOT change build flags, do NOT rebuild, do NOT panic. Retry once. If it fails twice in a row → fall through to "Recovery Path" above (deeper sleep / paired-state issue).

> **Heuristic for publish agents (Wash, etc.):** Budget for 1 retry on every iOS install. If it succeeds first try, great. If it doesn't, retry without escalating to Captain unless the second attempt also fails.

### Evidence

- **2026-05-06 Publish #6:** Attempt 1 failed (NWError 57, device locked). Captain unlocked + tapped. Attempt 2 succeeded.
- **2026-05-07 Publish #7:** Same pattern — first attempt failed, retry succeeded.
- **2026-05-07 Publish #8:** Same pattern — first attempt failed, retry succeeded.
- **2026-05-07 Publish #9:** Same pattern — first attempt failed, retry succeeded.

Pattern is now reliable enough to treat as a known retry-once recipe rather than an investigation.

## Deployment Recipes

### Net10 SDK (Canonical)

```bash
dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj \
  -f net10.0-ios \
  -c Release \
  -p:RuntimeIdentifier=ios-arm64 \
  -p:ValidateXcodeVersion=false
```

**Why `-p:ValidateXcodeVersion=false`:** Xcode 26.3 (Captain's machine) ≠ net10 GA SDK expected version (26.2). Build succeeds with flag.

**Why NOT global.json swap to net11p3:** Triggers 31 Razor SG errors in ImportContent.razor (dogfooding debt, deferred).

### Install & Launch

```bash
xcrun devicectl device install app \
  --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F \
  src/SentenceStudio.iOS/bin/Release/net10.0-ios/ios-arm64/SentenceStudio.iOS.app

xcrun devicectl device process launch \
  --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F \
  com.simplyprofound.sentencestudio
```

### Environment

```bash
services__api__https__0=https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io \
  dotnet build ...
```

(Points to production API for release builds)

## Future Investigations

1. ~~**Pattern validation:** Does NWError 57 recur on future iOS deploys if device is in deep sleep?~~ ✅ **Validated** (publishes #6–#9).
2. ~~**Automation resilience:** Can CI/CD detect NWError 57 and auto-retry?~~ ✅ **Resolved by recipe** (retry-once is now standard).
3. **Xcode integration:** Does Xcode 26.3's devicectl have known tunnel bugs? (still open — low priority since recipe works)
4. **Upstream issue:** File with dotnet/maui or Apple — confirmed reproducible, worth a minimal-repro issue when bandwidth allows.

## References

- **Publish #6 decision:** `.squad/decisions.md` (2026-05-06 Publish #6 section)
- **Orchestration log:** `.squad/orchestration-log/2026-05-06T23:36:29Z-wash-publish-6.md`
- **Wash history:** `.squad/agents/wash/history.md`
- **Related skill:** `.squad/skills/maui-devflow-blazor-hybrid/SKILL.md` (DevFlow tunnel issues)
