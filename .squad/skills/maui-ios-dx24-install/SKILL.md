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

### App-extension restore gotcha (NETSDK1047 — validated publish 2026-07-02)

**Symptom:** the Release build fails with
`NETSDK1047: Assets file '.../SentenceStudio.ShareExtension/obj/project.assets.json'
doesn't have a target for 'net11.0-ios/ios-arm64'`.

**Cause:** `SentenceStudio.ShareExtension` is an app extension (`<IsAppExtension>true</IsAppExtension>`,
TFM `net11.0-ios`, no `<RuntimeIdentifiers>`). When the iOS app builds with
`-p:RuntimeIdentifier=ios-arm64`, that RID flows to the extension, but the build's
*implicit restore* does not produce the RID-specific asset target for the extension —
and it CLOBBERS any RID-specific assets you restored beforehand. Seen on SDK
`11.0.100-preview.5` (may differ on other previews).

**Fix (restore the extension + app with the RID, then build with `--no-restore`):**
```bash
dotnet restore src/SentenceStudio.ShareExtension/SentenceStudio.ShareExtension.csproj -r ios-arm64
dotnet restore src/SentenceStudio.iOS/SentenceStudio.iOS.csproj -r ios-arm64
# confirm: grep -o '"net11.0-ios/ios-arm64"' src/SentenceStudio.ShareExtension/obj/project.assets.json
services__api__https__0=https://api.agreeablesky-76d2f81f.westus3.azurecontainerapps.io \
  dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj \
  -f net11.0-ios -c Release -p:RuntimeIdentifier=ios-arm64 --no-restore
```
`--no-restore` is the key — it stops the build from re-restoring the extension without
the RID and clobbering the assets. (If `obj` is in a weird state, `rm -rf
src/SentenceStudio.ShareExtension/obj` first, then the two restores.)

> **Permanent fix (TODO):** add `<RuntimeIdentifiers>ios-arm64</RuntimeIdentifiers>` to
> `SentenceStudio.ShareExtension.csproj` so a normal `-p:RuntimeIdentifier` build restores
> it correctly and `--no-restore` is no longer required.

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
services__api__https__0=https://api.agreeablesky-76d2f81f.westus3.azurecontainerapps.io \
  dotnet build ...
```

(Points to production API for release builds)

## Verifying a migration actually applied on-device (WAL gotcha)

To confirm an EF migration applied on DX24, pull the app's SQLite DB and inspect it:

```bash
xcrun devicectl device copy from --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F \
  --domain-type appDataContainer --domain-identifier com.simplyprofound.sentencestudio \
  --source Library/sstudio.db3 --destination /tmp/dx24.db3
```

**🔴 WAL gotcha (validated 2026-07-02):** the app runs SQLite in **WAL mode**. While the
app is running, recent writes — including a just-applied migration — live in the
uncheckpointed `sstudio.db3-wal` file, NOT the main `sstudio.db3`. Pulling only the main
file shows STALE data and can make a working migration look like it FAILED. Pull all
three parts so SQLite replays the WAL on open:

```bash
for suf in "" "-wal" "-shm"; do
  xcrun devicectl device copy from --device CF4F94E3-A1C9-5617-A089-9ABB0110A09F \
    --domain-type appDataContainer --domain-identifier com.simplyprofound.sentencestudio \
    --source "Library/sstudio.db3${suf}" --destination "/tmp/dx24.db3${suf}"; done
sqlite3 /tmp/dx24.db3 "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY 1 DESC LIMIT 3;"
sqlite3 /tmp/dx24.db3 "SELECT name FROM sqlite_master WHERE name='YourNewTable';"
```

(Or terminate the app first — a clean shutdown checkpoints the WAL into the main file —
then pull just `sstudio.db3`.)

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
