# Native-heads shared foundation (transition)

This phase leaves the existing MAUI Android, iOS and macOS apps as **temporary
reference heads**. It does not introduce Compose, SwiftUI, Ailoha, native feature
UI, data migration or an in-place device replacement.

## Dependency ownership

| Consumer | Allowed dependency direction |
| --- | --- |
| AppHost | WebApp, API, Workers, Marketing; no MAUI hosting package or `AddMauiProject` |
| WebApp and shared Razor UI | MAUI-free `SentenceStudio.AppLib` and `SentenceStudio.Shared`; browser media, preferences, files, connectivity, authentication and audio adapters are WebApp-owned |
| API, Workers, Marketing | MAUI-free `SentenceStudio.ServiceDefaults`, Shared and their existing server/data contracts |
| Frozen Android/iOS/macOS heads (plus Catalyst/Windows) | MAUI-free AppLib/UI/Shared **and** transitional `SentenceStudio.MauiCompatibility` and `SentenceStudio.MauiServiceDefaults`; these two projects must never be referenced by a server, shared library or shared test |

`SentenceStudio.MauiCompatibility` owns the former AppLib MAUI builder,
localization startup, handlers/layouts, device adapters, exception hooks,
embedded native configuration and MAUI resource build actions. The underlying
raw files and fonts stay in AppLib so the WebApp's existing content paths remain
unchanged. `SentenceStudio.MauiServiceDefaults` retains the MAUI-specific
telemetry/service-discovery initializer; server service defaults remain
independent. The AppLib audio-player and auth-notification contracts carry no
MAUI types. Browser audio uses JS interop; the Web adapter explicitly declines
local playback instead of returning a success-shaped proxy.

HelpKit is no longer registered or navigable in the retained WebApp or the
reference heads. The separate `lib/Plugin.Maui.HelpKit` source is not in the
active graph. Profile/Settings data export has no UI entry or DI registration;
direct legacy `DataExportService` calls throw `NotSupportedException` before
accessing data. The Sam operator `GET /export` is a **different** gated API;
Cycle 2 scopes its reads and export to the authenticated operator's owner ID
and rejects an absent owner. No migrations, app data or installations are
deleted.

## Repeatable closure gate

After checking the selected SDK/workloads, run the guard directly:

```bash
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s scripts/tests -p test_platform_neutral_dependencies.py
python3 scripts/validate-platform-neutral-dependencies.py --list-matrix
PYTHONDONTWRITEBYTECODE=1 python3 scripts/validate-platform-neutral-dependencies.py
```

The validator declares 15 active roots in the script: AppHost, WebApp, API,
Workers, Marketing, WebServiceDefaults, AppLib, UI, Shared, Infrastructure,
and the API/AppLib/UI/Unit/WebApp test projects. Each root gets a normal
recursive **Debug** and **Release** restore with an explicitly empty RID
(unaffected by an ambient `RuntimeIdentifier`); the 10 server/shared roots
also get **Release + linux-x64**. Every entry sets
`IncludeMobileTargets=false`, so Shared contributes its server `net10.0`
target. Root target frameworks are checked explicitly, but no global
`TargetFramework` is passed: the net10.0 AppHost references a net11.0 WebApp.
`--list-matrix` prints the 40 root entries; no additional property contexts
are predicted from `ProjectReference` metadata.

The guard runs ordinary recursive `dotnet restore --force --no-http-cache`
with `RestoreForce=true` and `RestoreNoCache=true`, followed by
`dotnet build --no-restore`, for each root. Both commands use `-m:1`;
the one-node build still observes SDK-prepared reference properties while
avoiding parallel restore-graph contention inside the write sandbox.
Its checked-in `scripts/platform-neutral-guard/DependencyGuard.csproj` builds
a small logger and target task against the selected SDK's MSBuild assemblies
(no external logger package). The **independent logger** records every
`ProjectStarted` request's actual global properties, evaluation/instance ID,
and the evaluated `ProjectAssetsFile`. The injected
`Observe.targets` records the effective project properties,
`PackageReference`, `PackageDownload` and `ProjectReference` items and marks
each instance. The logger independently copies the
**actual SDK `ResolvePackageAssets` task input** when MSBuild logs its
structured `ProjectAssetsFile` parameter. Both that input and the imported
target's earlier asset snapshot must match the fresh restore; a target
that replaces the file after the marker cannot supply an unchecked closure.
MSBuild can reuse one evaluated instance for multiple target requests: a
skipped marker is accepted only for the **same** observed project, globals,
evaluation ID and instance ID, with a matching evaluated assets path. A child
that loses the target import, including via task-level global-property
removal, remains visible to the logger and fails closed.

NuGet's real restore graph, generated assets and success cache cover its
restore-time queries; the build log captures build-only and nested references
that NuGet omits from that graph. For **each actual build request**, the guard
forces a normal restore of that project using the *logged child globals*,
never a reconstructed `Properties`/`AdditionalProperties`/SDK
`UndefineProperties` model. It checks every restored project and all
TFM/RID-specific resolved packages and download-only dependencies. Both
observed `PackageDownload` items and assets `downloadDependencies` reject
MAUI/Comet; each exact download must exist in the isolated package cache.
Project, package-reference, package-download and project-reference `NoWarn`
metadata cannot hide `NU1603`/`NU1605`, even if NuGet suppresses the warning
before `-warnaserror` can see it. The checked-in task uses the SDK's
`NuGet.Versioning` assembly to compare requested ranges with resolved target
packages wherever the lock file identifies both; stable `CoreSync/0.1.129`
satisfies the provider's prerelease minimum. Build-time asset snapshots must match
the freshly restored **package** closure for their actual TFM/RID; project
reference framework metadata can differ without changing that package
closure. The matrix's root TFM must equal the evaluated root and freshly
restored graph TFM. Repository-local non-C# MSBuild projects and projects
outside the checkout (other than targets shipped by the selected SDK) are
rejected rather than silently disappearing from the build inventory. Any
build request without an attributed fresh closure fails.
Real-build fixtures cover prepared TargetFramework/RID/SelfContained removal,
task-level removals, nested build-only references, metadata and configuration/
platform setters, conditional imports, stale assets, output overrides,
post-marker asset tampering, instrumentation stripping, package caches,
fallback folders and NuGet downgrade/approximate-version warnings.

A unique guard-owned scratch child contains a read-only copy of the active
source directories and a separate writable runtime with per-entry,
per-project `obj` and `bin` output. Source files and directories in the
copy are read-only; original repo `obj`/`bin`, `.git`, symlinks and local
`.env` files are not copied. macOS runs every dotnet process inside a
scratch-only Seatbelt write policy (including native child processes).
The policy permits cloning **from** read-only source **to** runtime for
normal MSBuild file copies but rejects clones or writes **into** source.
Linux requires Bubblewrap: it mounts the host tree read-only, binds only
guard runtime writable and maps `/tmp` and `/var/tmp` to runtime-backed
directories for SDK mutexes and temporary files. CI installs Bubblewrap;
unavailable or restricted user namespaces fail closed rather than silently
falling back to an unsandboxed build. A preflight proves that content,
permission, timestamp and extended-attribute writes to a host-writable
file outside runtime are denied before any restore or build.
`SentenceStudio.Shared` generates a strongly typed `.resx` designer into
the source tree on an ordinary build; in the isolated copy, its generated
designer is instead emitted to scratch `obj` through the injected target.
Neither this redirection nor the omission of that generated file edits the
original project.

The shared scratch package cache is reused across entries while space permits.
If less than 3 GiB remains **between roots**, the guard removes only its
own `runtime/packages` cache, after all child MSBuild processes have exited,
and re-downloads needed packages on subsequent restores. It never deletes
a user cache or source output to make room. The
scratch-only config preserves the repository's CI package source mapping
while excluding the developer-only `localnugets` feed and clearing inherited
fallback settings. `NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH`,
`NUGET_PLUGINS_CACHE_PATH`, `NUGET_SCRATCH`, `TMPDIR`, `HOME`,
`DOTNET_CLI_HOME` and XDG cache/data/config paths are forced inside scratch;
the observed process paths and assets must agree. `--no-http-cache` prevents
NuGet from reading an ambient HTTP cache even when a path was configured.
The SDK's own `library-packs` source is permitted only at the path derived
from the selected MSBuild installation; arbitrary additional sources,
fallback folders, global caches and escaped output paths are rejected.
The target rejects unsafe paths/cache properties before its project writes
output; if a child strips the target, the OS boundary still prevents source
or checkout writes and the independent logger rejects the build. The guard
removes **only its own scratch child**; it never
recursively deletes or cleans user or repository files. A local
`--scratch-parent` must exist outside the checkout; CI uses
`--scratch-parent "$RUNNER_TEMP"`. On Linux, that parent must also be
outside `/tmp` and `/var/tmp` so those paths can be remounted from scratch.

The reviewed `CoreSync.PostgreSQL/0.1.129-local3` archive in
`lib/coresync-postgresql-local3` must match its exact SHA-256 both before
restore and in the isolated package cache. The exact central pin
`[0.1.129-local3]` and exclusive provider-only source mapping remain
required; stable `CoreSync/0.1.129` remains mapped to nuget.org. Resolving
the public `CoreSync.PostgreSQL/0.1.129`, any MAUI/Comet package, `NU1603`
or `NU1605` fails the gate. Each dotnet invocation is capped at 300 seconds,
the guard at 90 minutes, and each root at 160 distinct observed build
contexts. Below 512 MiB free scratch space, the guard stops its own build
or restore. CI's job limit is 105 minutes, including SDK setup and fixtures.
Metrics report **initial root restores** and **observed-context restores**
separately: 40 plus the measured child-context count, never an ambiguous
single restore total. The previous 351-total-restore measurement predates this
isolation cutover and must not be reported as a cycle-2 result.

**Cycle-2 local evidence (September 28, 2026 UTC):** the 42 real-build
adversarial fixtures passed from 04:15:36Z to 04:20:41Z (304.661 seconds),
including read-only source and metadata-write denial, downloads, per-item
suppression, external caches, and scratch-only eviction. The full 40-root
matrix was attempted from 03:51:31Z to 04:08:31Z. It completed **22/40**
roots, with 22 initial root restores and 158 observed-context restores
reported at that point; the next restore stopped when free scratch space
fell below the 512 MiB reserve. The guard cleaned its scratch child and
returned failure, not a false pass. An unrelated original-checkout build
also changed files during that run, so the earlier global source-byte
comparison cannot establish an unchanged entire worktree. The full cycle-2
matrix still requires a run with enough free disk and no concurrent writer.
A later isolated Infrastructure/Release root attempted from 04:24:31Z to
04:24:36Z stopped at the same scratch reserve. A fresh
04:24:21Z-to-04:25:15Z byte inventory of 35,388 original checkout files
and 74 pre-existing `obj`/`bin` directories found **zero changes** across
that attempt; guard scratch was removed. The filesystem then had only
696 MiB available. Do not delete unrelated build outputs or user data to
force this gate through.

The frozen heads, Share Extension and temporary MAUI projects remain named
transition exceptions, never hidden server dependencies.

**Mixed CoreSync policy (2026-09-27):** the provider archive requires
`CoreSync >= 0.1.129-local3`, while the stable HTTP and SQLite packages require
`CoreSync >= 0.1.129`. A fresh Shared server restore resolves the exact
repo-local provider and stable base together without `NU1603` or `NU1605`.
Do not require the prerelease base or rebuild the stable dependents to satisfy
this provider-specific provenance requirement.

`tests/SentenceStudio.IntegrationTests` is separately excluded from the
active test roots because its pre-existing reference to the missing
`src/SentenceStudio/SentenceStudio.csproj` prevents restore; this phase does
not silently substitute a different test suite for it.

For a direct local build, **do not** pass `-f net10.0` to the mixed-target
AppHost: that MSBuild global property propagates to its `net11.0` WebApp
reference and produces `NETSDK1005`. Build the AppHost without `-f`; build
individual native reference heads with their explicit `net11.0-*` TFM. The
ordinary Aspire Web/API/Workers/Marketing run requires its own isolated local
database volume and must not reuse Captain's running stack or data.
The API alone runs application database migrations and shared startup backfills.
The WebApp waits for the API's startup-readiness contract and checks its own
pending migrations before serving; it does not perform those shared writes.
Local Aspire also waits for the API's HTTP health check before starting the
WebApp. Production rollout remains on hold pending the verification described
in `docs/deploy-runbook.md`.

## Standalone reference-head API tunnel (opt-in only)

The frozen heads no longer launch under AppHost. Start the **separate
target-worktree** AppHost with `LocalDevTunnel__Enabled=true` only when a
reference head needs the API. Use that worktree's isolated database and
Azurite volumes, not Captain's existing stack or data. The launcher below
**does not start, stop, or reconfigure AppHost**. The opt-in exposes the API
through an anonymous public dev tunnel; keep it off for normal server runs.

Aspire's `WithReference(api.GetEndpoint("http"))` creates the **port resource**
`devtunnel-public-api-http` under a dynamically named `devtunnel-public-*`
parent. In the observed Aspire resource snapshot, both resources are running
but have `urls: []`. The launcher reads the **whole** target AppHost resource
snapshot so it can join the port's `TargetResource=api` /
`TargetEndpoint=http` to the API project's actual local HTTP port:

```bash
aspire describe --apphost "$APPHOST" --format Json --non-interactive --nologo
```

When the port has no URL, the launcher passes the single **Running** parent
resource's `TunnelId` only to `devtunnel show <TunnelId> --json` (the CLI must
already be installed and able to access this tunnel). From `tunnel.ports[]`,
it accepts **exactly one** entry whose numeric `portNumber` equals the API
project's local HTTP port and whose `protocol` is `http`; the entry's `portUri`
must be a public root HTTPS `*.devtunnels.ms` URL. The child port must be
Running/Healthy and target `api/http`. The parent may report Unhealthy despite
the child port and public API working; its health indicator alone cannot prove
the endpoint is unusable. The launcher instead GETs that exact public URL's
`/health` with normal certificate/hostname validation, no redirects, and a
10-second timeout. **Only HTTP 200** allows a config to be written or a launch
command to be emitted. TLS failures, warnings, timeouts and other status codes
fail closed. It does not construct a tunnel URL, use the API's localhost URL,
or use an inspector link. Missing, ambiguous or mismatched ports and absent
CLI/auth fail closed. For older snapshots with a unique `tunnel` entry in the
port's `urls`, the launcher continues to validate that entry and requires the
same public HTTPS `/health` proof before writing a config.

Do not paste the full `describe` or `devtunnel show` response into logs:
resource snapshots can contain environment settings and tunnel metadata
includes access controls. The script keeps the opaque tunnel ID in memory
only, does not print CLI JSON or subprocess errors, and writes **only**
`{"Services":{"api":{"https":["<actual tunnel URL>"]}}}` to a unique
mode-0600 JSON file. Missing/disabled/stopped tunnels, unhealthy child ports,
failed public health checks, localhost
(including Android's `10.0.2.2` direct-host alias), production URLs, extra
paths/credentials, and invalid JSON fail closed. The tunnel URL is passed
**unchanged** to Android emulator, iOS simulator, and macOS; Android's
`10.0.2.2` is useful only for direct emulator-to-host loopback, never for a
public tunnel.

From the target worktree root, provide the **existing, absolute session
scratch directory outside the checkout and outside OS temporary folders**.
Replace `<session-id>` with this session's real ID; do not use `/tmp` or the
native app's data directory:

```bash
APPHOST="$PWD/src/SentenceStudio.AppHost/SentenceStudio.AppHost.csproj"
SESSION_SCRATCH="$HOME/.copilot/session-state/<session-id>/files"
python3 scripts/run-reference-head.py --apphost "$APPHOST" \
  --output-dir "$SESSION_SCRATCH"
```

This **config-only** command (or a platform command without `--launch`) needs
no speech/AI credentials; its generated mode-0600 JSON contains **only**
`Services:api:https:0`, the verified public API tunnel URL. Do not put a
credential, token, or extra setting in that JSON. It is not a replacement for
the private native configuration.

Before Android/iOS `--launch`, the launcher checks the actual native direct
client requirements **before starting any adb, simctl, Aspire, devtunnel, or
dotnet process**:

| Direct native client | Required private configuration key | Source |
| --- | --- | --- |
| OpenAI chat and the OpenAI audio fallback in `AiService` | `Settings:OpenAIKey` | `src/SentenceStudio.AppLib/appsettings.json` |
| `ElevenLabsSpeechService` (including shadowing/timestamped audio) | `Settings:ElevenLabsKey` | Same private file |

Use `src/SentenceStudio.AppLib/appsettings.template.json` for the **names and
structure only**; the actual `appsettings.json` is gitignored and embedded
in the local native Debug build, so do not share that file or the built app.
The Debug `appsettings.Development.json` can override either key and is
checked as part of the effective configuration; do not put secret values in
that tracked file. A missing file, absent key, non-string, whitespace-only,
short, or recognizable placeholder key fails without a launch or tunnel
probe. This checks presence and basic shape, **not** whether the provider
accepts the credential. Exporting `AI__OpenAI__ApiKey`, `ElevenLabsKey`, or
`Settings__OpenAIKey` in the build shell is **not** a substitute for mobile
configuration: the installed app does not reliably inherit shell variables.
The API speech gateway is registered, but direct ElevenLabs calls remain in
use and `AiService` has an OpenAI audio fallback; the API tunnel alone cannot
authenticate those clients.

To **build and launch** on a selected Android **emulator** or already-booted
iOS **simulator**, use one of the following. Do not run simultaneous `dotnet`
builds against the same solution. The launcher verifies the Android target's
`ro.kernel.qemu=1` and the iOS UDID in `simctl`'s booted simulator list
before querying the tunnel or generating a file:

```bash
adb devices -l
python3 scripts/run-reference-head.py --apphost "$APPHOST" \
  --output-dir "$SESSION_SCRATCH" --platform android \
  --device emulator-5554 --launch

xcrun simctl list devices booted
python3 scripts/run-reference-head.py --apphost "$APPHOST" \
  --output-dir "$SESSION_SCRATCH" --platform ios \
  --device <BOOTED_SIMULATOR_UDID> --launch
```

Those commands use `dotnet build -t:Run -f net11.0-android` with
`-p:Device=emulator-5554`, or `dotnet build -t:Run -f net11.0-ios` with an
explicit `iossimulator-*` RID and `-p:_DeviceName=:v2:udid=<UDID>`. Neither
selects a physical device by default. Without `--launch`, the script prints
the exact build/run command without executing it. **Do not use
`maui devflow run`: that CLI has no `run` subcommand.**

For the macOS AppKit head, request a **command only**:

```bash
python3 scripts/run-reference-head.py --apphost "$APPHOST" \
  --output-dir "$SESSION_SCRATCH" --platform macos
```

It prints `dotnet run --project ... -f net11.0-macos -c Debug
--no-launch-profile --property:ReferenceHeadConfig=<generated JSON>` but
never executes it; `--platform macos --launch` is refused. The existing
AppKit bundle identity can reach Captain's keychain/preferences/SQLite.
Unlike mobile, the desktop direct OpenAI chat and ElevenLabs registrations
read the app process's `AI__OpenAI__ApiKey` and `ElevenLabsKey` environment
variables. `AiService` separately reads `Settings:OpenAIKey` for its audio
client: supply it via the private native `appsettings.json` or the macOS
process's `Settings__OpenAIKey`. The printed command is **not** credential
preflighted and must not be executed without these values and an isolated
test identity.
**Do not execute the printed command on his normal macOS identity without
explicit per-turn authorization and an isolated test identity/data plan.**
No app IDs, app data, or stored credentials are changed by this launcher.

`ReferenceHeadConfig` is an MSBuild **build-time** property: the referenced
`SentenceStudio.MauiCompatibility` project embeds the absolute JSON path
with logical name `SentenceStudio.reference-head.generated.json`. Mobile
installed-app processes cannot depend on shell environment inheritance.
At startup, native configuration loads developer-local base `appsettings`
when present, the Debug/Release environment config, environment variables,
and **finally the generated tunnel overlay**. Thus a stale
`services__api__https__0` from a host environment cannot redirect a
reference head to production. Extra HTTPS endpoint entries or a scalar
HTTPS override cause a startup error instead of nondeterministically
selecting another host; `https+http://api` uses HTTPS when present. If the
base file is absent, the valid generated overlay explicitly supplies the
minimum API endpoint. If **both** base and generated config are absent, the
app still throws; a specified-but-missing build-time JSON path fails in
MSBuild before compilation. Nothing writes to or overwrites AppLib's
gitignored developer-local `appsettings.json`.

The owner of the sequential native build/verification can inspect the
actual MSBuild resource item before launch:

```bash
CONFIG="<absolute generated reference-head JSON printed by the launcher>"
dotnet msbuild src/SentenceStudio.MauiCompatibility/SentenceStudio.MauiCompatibility.csproj \
  -p:TargetFramework=net11.0-android -p:ReferenceHeadConfig="$CONFIG" \
  -getItem:EmbeddedResource -nologo
```

Confirm the output includes the config's full path with logical name
`SentenceStudio.reference-head.generated.json`. Build the head with the
**same property** (the launch script does this) so the generated JSON is
packaged into the compatibility assembly. Do not use `--no-build` with a
new config. Run `maui devflow diagnose`, `maui devflow wait`, verify the
connected agent is the selected app, then `maui devflow ui tree --depth 1`,
`maui devflow ui screenshot --output "$SESSION_SCRATCH/<new-reference-head-check>.png"`,
and `maui devflow logs --limit 20`. Interact with an API-backed screen and
confirm it uses the tunnel; a build alone does not verify the mobile path.
Offline launcher/packaging fixtures run with:

```bash
python3 -m unittest discover -s scripts/tests -p test_reference_head_launch.py
```

For an isolated **local-only** Aspire smoke in this separate worktree, set
fresh named `LocalDb__DataVolume` and `LocalAzurite__DataVolume` values, an
absolute session-scratch `WebApp__AppDataRoot`, and a session-scratch
`SENTENCESTUDIO_APPDATA_ROOT` before starting `aspire run --isolated`.
The AppHost forwards the WebApp path only to WebApp and the API path only to
API/Workers. WebApp's isolated path also contains its own Data Protection
key ring; the API's protected Coach key ring uses the isolated Azurite volume
when its blob connection is present. Unset overrides retain the existing
stores. `LocalDevTunnel__Enabled` defaults to `false`; set it to `true`
only when intentionally running an excluded MAUI reference head that needs
the public API tunnel. Do not delete either test volume: it holds the smoke
account and is not a throwaway substitute for Captain's data.

The frozen Android reference head declares minimum API 24 for the installed
.NET 11 Android workload; API 21 fails with `XA4216`. Its manifest has no
separate `minSdkVersion`. This is a temporary build compatibility floor, not
the approved API 36 target for the future Compose head.

## Verification boundaries observed on September 27, 2026 (UTC)

The approved plan's baseline gate still passes; the full cross-platform
slice/release gates have **not** been attempted in this foundation phase.
Sequential builds passed for WebApp, API, Workers, Marketing, AppHost and the
frozen Android/iOS/macOS heads. The active dependency gate found zero
MAUI/Comet packages across 20 projects. Directly affected .NET tests, the
isolated SQLite quiz-persistence tests, and 190 JS tests pass. On an isolated
Aspire stack, the canonical test account registered and signed in, returned
from Dashboard -> Vocabulary Quiz's truthful empty state, and kept its cookie
and saved preference across a WebApp restart. Profile/Settings offered no
data export; the API used the isolated Azurite Coach key ring.

Do **not** treat other suites as passing: full `SentenceStudio.UI.Tests` had
150 failures in Coach render/test-harness cases (106 lacked a registered
`NavigationManager`), despite its directly affected cases passing. Retargeting
`SentenceStudio.AppLib.Tests` to the compatible `net11.0` TFM exposed 19
existing SM2/Korean-number expectation failures in otherwise untouched code.
`SentenceStudio.UnitTests` passed 2129/2129 on rerun after one intermittent
concurrent-confirmation failure. Its older documented 534/535 baseline is
stale here: the named 15-day resource-selection case passed in isolation.
None of these unrelated failures has been silently excluded from its suite.

Browser automation in a shared developer environment needs a fresh
Playwright CLI session name and a browser profile under session scratch;
start the CLI **from scratch** or it creates `.playwright-cli` evidence in
the caller's repository. The MCP browser's default profile can be in use by
another session. Aspire console-log filtering accepts the DCP resource name
(`api-...`), whereas structured-log filtering uses the OpenTelemetry
resource name (`SentenceStudio.Api-...`); an unfiltered structured-log query
reveals the latter. Neither mismatch is an application failure.

The Squad state MCP backend in this session was scoped to the source checkout,
not this target worktree, and exposes no team-root selector. Do not write
target decisions/history through it until its scope is corrected. No Squad
state was merged or committed as part of this foundation.
