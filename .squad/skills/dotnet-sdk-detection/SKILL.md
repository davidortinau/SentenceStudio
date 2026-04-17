# Skill: Determining Which .NET SDK Is In Use

**Confidence:** high
**Domain:** .NET build, agent diagnostics
**Applies to:** ANY .NET project (this is a 100-level fundamental, not project-specific)

## When this skill applies

Before you touch a `dotnet` command in an unfamiliar repo — or any time you are about to:

- Run `dotnet build`, `restore`, `test`, `run`, `publish`, or `ef migrations`
- Diagnose "missing SDK", "missing workload", or "TFM not recognized" errors
- Assert "X is not installed locally" / "the build is using the wrong version"
- Plan a multi-target migration, TFM bump, or workaround for a perceived SDK gap
- Reproduce a CI failure locally (or vice versa)

**Run the diagnostic order below FIRST. It takes seconds and prevents whole categories of wrong work.**

## Mental model

There are FOUR independent layers. Conflating them is the #1 cause of wrong diagnoses:

| Layer | What it is | How to inspect |
|---|---|---|
| **1. Installed SDKs** | What's physically on disk under `<dotnet-root>/sdk/<version>/` | `dotnet --list-sdks` |
| **2. Selected SDK** | Which installed SDK the CLI picks for THIS directory (controlled by `global.json` walk + roll-forward + prerelease policy) | `dotnet --info` |
| **3. Workload manifests** | iOS / Android / MAUI / WASI tooling — pinned PER SDK band, NOT shared | `dotnet workload list` |
| **4. Project TFMs** | What the csproj actually targets — `<TargetFramework>` / `<TargetFrameworks>` | `grep -h '<TargetFramework' **/*.csproj` |

A build can fail because of any one of these. Knowing which layer is responsible determines the fix.

## Required diagnostic order

Run these in order. Stop at the first one that explains the problem.

```bash
# 1. Is there a global.json controlling SDK selection? (walks UP from CWD)
find . -maxdepth 4 -name global.json -not -path '*/node_modules/*' 2>/dev/null
cat global.json 2>/dev/null

# 2. What SDKs are actually installed on this machine?
dotnet --list-sdks

# 3. What is the CLI selecting RIGHT NOW in this directory? (the truth)
dotnet --info | head -20

# 4. What workloads are installed for the SELECTED SDK?
dotnet workload list

# 5. What does the project actually want? (TFMs)
grep -rh '<TargetFramework' --include='*.csproj' . | sort -u
```

Step 3 is the ground truth. If `dotnet --info` says the CLI is using `10.0.101`, that IS what builds will use, regardless of what's in `--list-sdks`.

## global.json — the SDK selector

`global.json` at (or above) the repo root **pins which installed SDK** the `dotnet` CLI selects. The CLI walks UP from CWD to find the nearest one; first match wins.

```jsonc
{
  "sdk": {
    "version": "10.0.101",          // requested floor
    "rollForward": "latestFeature",  // how to pick from installed SDKs
    "allowPrerelease": false         // whether previews count (default false)
  }
}
```

### `rollForward` policies

| Policy | Behavior |
|---|---|
| `disable` | EXACT match required. Fail if absent. |
| `patch` | Same major.minor.feature; allow higher patch. |
| `feature` | Same major.minor; allow higher feature band. |
| `minor` | Same major; allow higher minor. |
| `major` | Allow higher major. |
| `latestPatch` | Highest patch in same feature band. |
| `latestFeature` | Highest feature band in same major.minor. |
| `latestMinor` | Highest minor in same major. |
| `latestMajor` | Highest installed. |

`allowPrerelease: false` (the default if omitted) means preview SDKs are SKIPPED during selection — even if they appear in `dotnet --list-sdks`.

### Three things `global.json` is NOT

- **Not always committed.** It is frequently **gitignored** (per-machine SDK pin for one developer). Run `git check-ignore global.json` to confirm. If gitignored, it's a local artifact and you cannot reason about other contributors from it.
- **Not the only SDK selector.** Environment variables (`DOTNET_ROOT`, `DOTNET_ROLL_FORWARD`, `DOTNET_ROLL_FORWARD_TO_PRERELEASE`) and the `--sdk-version`-equivalent build properties also influence selection.
- **Not a workload manifest selector.** Workloads are pinned per-SDK. Switching SDKs changes available workloads silently.

## Newer SDKs CAN build older TFMs

A net11 SDK can build a `net10.0` TFM project. This is by design — the .NET runtime is forward-compatible at the SDK level. The build artifact still targets net10 and runs on the net10 runtime.

**The wrinkles** that make this not always seamless:

1. **MAUI / iOS / Android workloads** are pinned per SDK band. A net11 SDK has the net11 MAUI workload, which may or may not still know how to target `net10.0-ios`. If the project's TFM is `net10.0-ios` and only the net11 MAUI workload is installed, you can hit `NETSDK1139: target platform ios not recognized` even though the SDK technically supports the framework.
2. **Xcode coupling for iOS/MacCatalyst.** Each .NET iOS SDK band ships expecting a specific Xcode version. A newer Xcode may require a newer .NET SDK (preview or otherwise) to build. This is the most common reason a repo pins to a preview SDK on a developer's machine.
3. **Aspire & ASP.NET Core** generally don't have these wrinkles — newer SDK builds older TFMs cleanly. Container deploy artifacts target the project's TFM, so a net11-SDK-built net10 container runs on net10 in Azure.

If a build fails with "missing workload" but `dotnet --list-sdks` shows the right SDK, the issue is layer 3 (workload manifests), not layer 1 or 2.

## Override patterns

### Temporary `global.json` swap (when you need a different SDK for one operation)

```bash
cp global.json global.json.bak
echo '{"sdk":{"version":"X.Y.Z","rollForward":"latestFeature","allowPrerelease":true}}' > global.json
# do the work
cp global.json.bak global.json && rm global.json.bak
```

If you ever see `global.json.bak` left over in `git status`, a swap workflow was interrupted — restore it before doing anything else.

### Per-invocation overrides (no file edit)

```bash
# Allow rolling forward across major versions, including prereleases:
DOTNET_ROLL_FORWARD=LatestMajor DOTNET_ROLL_FORWARD_TO_PRERELEASE=1 dotnet build

# Run dotnet from a different directory to escape the global.json walk:
cd /tmp && dotnet build /path/to/proj.csproj
```

### Workload installation is independent

```bash
dotnet workload install maui                            # for the currently selected SDK
dotnet workload install maui --version 10.0.100         # specific manifest
dotnet workload list                                    # what's installed FOR THIS SDK
```

If `dotnet workload list` is empty for the selected SDK band, the workloads are genuinely missing — that's separate from the SDK question.

## Anti-patterns this skill prevents

- ❌ "The build uses net10, so net11 must not be installed." → Check `dotnet --list-sdks` and `global.json` first.
- ❌ "I'll multi-target the library to net10 because net11 isn't available." → Verify with the diagnostic order before changing the project.
- ❌ "`dotnet workload list` shows no net11 workloads, so the net11 SDK isn't installed." → Workloads ≠ SDKs. Run `dotnet --list-sdks`.
- ❌ "I'll edit the csproj's `<TargetFrameworks>` to work around an SDK problem." → Almost always wrong; first determine which of the 4 layers is failing.
- ❌ "Let me commit this `global.json` so everyone uses the same SDK." → Often the existing one is intentionally **gitignored** for per-developer machine reasons. Check `.gitignore` and `git check-ignore global.json` before committing one.

## A real-world miss (worked example)

A coding agent reported "no net11 SDK is installed locally" when restore failed with `NETSDK1139: target platform android not recognized`. Reality:

- `dotnet --list-sdks` showed net11 previews 1, 2, AND 3 installed.
- `global.json` (gitignored, per-machine) pinned `10.0.101` with `allowPrerelease: false`.
- `dotnet --info` confirmed selection was net10.
- The `NETSDK1139` was actually a workload manifest gap (net11 MAUI workload not installed), not an SDK gap.

The agent shipped a multi-target workaround that compiled fine but rested on a wrong premise. Three of the diagnostic steps above would have caught it in seconds.

## Related references

- Microsoft Learn: <https://learn.microsoft.com/dotnet/core/tools/global-json>
- Microsoft Learn: <https://learn.microsoft.com/dotnet/core/versions/selection>
- Microsoft Learn: <https://learn.microsoft.com/dotnet/core/install/upgrade> (forward compat)
- `AGENTS.md` (this repo) — project-specific note on why this repo's `global.json` is gitignored
