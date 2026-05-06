# Skill: iOS DX24 CoreDevice Installation & NWError 57

**Domain:** iOS device deployment  
**Context:** SentenceStudio publish workflow (DX24 = iPhone 15 Pro, CF4F94E3-A1C9-5617-A089-9ABB0110A09F)  
**Status:** ⚠️ Medium confidence (observed once; pattern needs validation across future deploys)  
**Last Updated:** 2026-05-06 23:36:29Z

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

### Evidence (2026-05-06 Publish #6)

- **Attempt 1:** 23:31 UTC, device locked → CoreDeviceError 4000 + NWError 57 ❌
- **Unlock:** 23:35 UTC, Captain unlocked DX24 (physical unlock + tap)
- **Attempt 2:** Immediately after → Install succeeded ✅
- **App launch:** Successful ✅

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

1. **Pattern validation:** Does NWError 57 recur on future iOS deploys if device is in deep sleep?
2. **Automation resilience:** Can CI/CD detect NWError 57 and auto-retry?
3. **Xcode integration:** Does Xcode 26.3's devicectl have known tunnel bugs?
4. **Upstream issue:** File with dotnet/maui or Apple if pattern confirmed across many deploys.

## References

- **Publish #6 decision:** `.squad/decisions.md` (2026-05-06 Publish #6 section)
- **Orchestration log:** `.squad/orchestration-log/2026-05-06T23:36:29Z-wash-publish-6.md`
- **Wash history:** `.squad/agents/wash/history.md`
- **Related skill:** `.squad/skills/maui-devflow-blazor-hybrid/SKILL.md` (DevFlow tunnel issues)
