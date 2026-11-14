# Handoff to Lilliput (2026-11-13)

Start here. This is for the agent taking over SentenceStudio on the Captain's new machine,
**Lilliput**. Read `AGENTS.md` first (the rules; they all still apply), then this note, then
`docs/handoff-2026-11-13-forms-pass.md` (the last piece of work and the Minimal Pairs plan).

The previous agent's memory lived on the old machine and does not come with you. The parts you
need are written down here.

## Repositories

| Repo | Where it is on GitHub | Branch to use | Notes |
|---|---|---|---|
| SentenceStudio | `davidortinau/SentenceStudio` (public) | `library-split-view` | The current native work; not merged. `main` = `native-platform-heads`; prod deploys from `main`. Work in a worktree named `SentenceStudio-native-platform-heads`. |
| SwiftUI bridge | `davidortinau/Microsoft.SwiftUI` (public) | `split-view` (4dd4182, preview.66) | Its own repo, not maui-labs. `main` is behind `split-view`. |
| CoreSync | `davidortinau/CoreSync` (fork of `adospace/CoreSync`) | `fix/postgres-caller-owned-provisioning` | `d611359` is the source of the committed `CoreSync.PostgreSQL 0.1.129-local3` package. Only needed to read or rebuild the provider. |
| Ailoha | `davidortinau/ailoha` (private fork of `Redth/ailoha`) | see below | Debug-only UI automation agent; optional for builds. |
| maui-labs | `davidortinau/maui-labs` (fork of `dotnet/maui-labs`, made for this handoff) | `davidortinau/devflow-*`, `davidortinau/comet-next` | DevFlow and Comet work. `dotnet/maui-labs` is read-only prior art for the bridge. |

Ailoha branches with SwiftUI fixes (PRs #140-#143 upstream): `fix/swiftui-accessibility-tree`,
`fix/swiftui-menu-rows`, `fix/swift-ios-scroll-swipe-prefs` (best: menu rows plus scroll/swipe and the
`storage pref set` fix). Old stashes from both forks are pushed as `stash/*` branches.

## Setting up Lilliput

1. **SDK and Xcode.** The old machine pinned `11.0.100-rc.2.26504.105` (`rollForward: disable`)
   with Xcode 27.2. `global.json` is ignored and developer-specific: write your own, never commit
   it (`.squad/skills/dotnet-sdk-detection/SKILL.md`). The bridge packs with the same RC SDK
   through a temporary `global.json` (GA 11.0.100 packs `net11.0-ios27.2` and fails its verifier).
   Building iOS/macOS with a newer Xcode needs `-p:ValidateXcodeVersion=false` (standing OK).
2. **Clone** SentenceStudio, the bridge (to `~/work/Microsoft.SwiftUI`) and, if needed, CoreSync.
3. **SwiftUI package.** `src/SentenceStudio.Native.iOS/NuGet.Config` reads
   `/Users/davidortinau/work/Microsoft.SwiftUI/artifacts` (absolute path; if Lilliput's home
   differs, change it locally and don't commit it):
   `gh release download v0.1.0-preview.66 --repo davidortinau/Microsoft.SwiftUI --dir ~/work/Microsoft.SwiftUI/artifacts`
   (SHA-256 `80e8e3436cc2b15cbb27d14b7421c3fe018183573019b87251ef54169d70fb10`). Or build it:
   `bash scripts/build-xcframework.sh`, then `bash scripts/pack-and-verify.sh`.
4. **`~/work/LocalNuGets`.** `src/NuGet.config` also lists this machine-local feed. The native iOS
   head and the API don't need it (CoreSync's provider is committed in
   `lib/coresync-postgresql-local3`). The MAUI heads restore seven dev packages from it, which the
   Captain copies over by hand:
   `Microsoft.Maui.DevFlow.Agent`, `.Agent.Core`, `.Blazor`, `.Logging` (0.25.0-dev; Android,
   iOS, MacCatalyst, MacOS heads) and `Microsoft.Maui.Platforms.MacOS`, `.Essentials`,
   `.BlazorWebView` (0.26.0-dev; MacOS head). Without the folder, create it empty or disable that
   source locally if restore complains.
5. **Secrets:** see below.
6. **Backend for native testing:** this branch's AppHost (Aspire) with the API on
   `http://localhost:5081`. If another stack holds the ports, stop it first (the
   `aspire-recovery` skill).
7. **Run:** `scripts/ios-sim.sh <UDID>` (build, install, launch, confirm). Ailoha is opt-in for
   Debug (`AilohaAgentXCFramework` property); the build does not need it.

## Lilliput as configured (2026-11-13, over SSH from the old machine)

- Xcode 27.2, .NET SDK **11.0.100 GA** only (workloads `maui`, `macos`), no `global.json`. The
  native iOS head builds under GA after `8e7f03ba` (`ValidateSceneManifest=false`, MT7199: the
  app starts through the bridge's SwiftUI App, so it needs no scene manifest) and launches on an
  iPad simulator. The AppHost and API build.
- Worktree `~/work/SentenceStudio-native-platform-heads` on `library-split-view`, beside the
  main checkout `~/work/SentenceStudio`.
- **Package mirror:** Lilliput can't reach `api.nuget.org` directly; it reaches
  `packagefeedproxy.microsoft.io` (the user-level NuGet config already lists it). In the worktree,
  `src/NuGet.config`'s `nuget.org` entry points at the mirror and the file is marked
  `git update-index --skip-worktree`, so the local change can't be committed. Undo with
  `--no-skip-worktree` before editing that file on purpose. Repeat the same edit in any new
  worktree.
- Installed: `gh` 2.104, Azure CLI 2.92 (Homebrew); the iOS 27.2 simulator runtime; simulator
  "SS iPad" (iPad Pro 11-inch M5, `7F9CD5B1-95BE-4191-9E20-BA83EBE5D5ED`).
- Copied from the old machine (SHA-256 checked): the three user-secrets folders,
  `src/SentenceStudio.AppLib/appsettings.json` (both checkouts), the seven LocalNuGets packages,
  and `Microsoft.SwiftUI.0.1.0-preview.66.nupkg` in `~/work/Microsoft.SwiftUI/artifacts`.
- The bridge checkout `~/work/Microsoft.SwiftUI` is on Lilliput's own branch
  `fix-versioned-platform-target-validation` with an uncommitted verifier change (packing under
  the GA SDK); it was left as is. Bridge work for this branch continues on `split-view`.
- Also installed: .NET SDK 10.0.101 (the server projects target `net10.0`; the iOS head builds
  with 11.0.100, the newest SDK, since there is no `global.json`), Docker Desktop 29.9.3; `gh` and
  `az` are signed in. The HTTPS dev certificate is trusted (`~/.aspnet` had been created by root
  on 2026-11-11 and was handed back to the user).
- Agent tooling: git identity set as on the old machine and `gh` is the git credential helper;
  Node 22 (Homebrew `node@22`) and the Aspire CLI 13.6.4 (.NET global tool) for the repo's
  `.mcp.json` servers (`aspire`, `squad_state`); `~/.zprofile` puts `node@22`, `~/.dotnet/tools`,
  `~/.docker/bin` and the Android SDK tools on the PATH. The old machine's project memory notes
  are in `~/.claude/projects/-Users-davidortinau-work-SentenceStudio/memory` (linked for the
  native worktree). No Android emulator exists yet, and the MAUI DevFlow and Ailoha CLIs aren't
  installed (install them when that work starts).
- **Starting the backend:** `~/start-sentencestudio-apphost.command` (open it, or `open` it over
  SSH). It runs the AppHost in the desktop session, where the login keychain holds the dev
  certificate; an AppHost started from an SSH shell can't read the keychain and stops with
  "Unable to configure HTTPS endpoint". Log: `~/apphost.log`. Over SSH, `docker` is at
  `~/.docker/bin`.
- Proven on 2026-11-13: API health 200 on `http://localhost:5081`, Postgres/Redis/Azurite
  containers up, and the seeded `testsailor` and `e2e` accounts sign in through
  `/api/auth/login`; the Captain signed in from the app on "SS iPad" (the Study tab shows the
  getting-started choices: fixture accounts have no learning data).
- **Phone signing:** Lilliput has its own "Apple Development: Dave Ortinau (W4L3MZPMK9)"
  certificate (team NYHGX6KCDG, valid to 2027-11-13) and the regenerated `MaxProWildDev`
  profile (`com.simplyprofound.*`, 4 devices, valid to 2027-11-14) carrying it and the old
  machine's certificate. Apple's WWDR **G3** intermediate had to be added to the login keychain
  (only G5 and the expired 2023 one were there), or the identity counts as invalid. Signing reads
  the keychain, so run device builds from the desktop session (`~/devicebuild.command` runs
  `scripts/ios-build.sh device`, log `~/devicebuild.log`). Proven 2026-11-13: a Release build
  signed and verified (`codesign --verify --deep --strict`); Duotopia shows as connected.
  `scripts/ios-phone.sh` installs only after the Captain's go.

## Secrets

None are in git. Move them by hand (AirDrop or a password manager), never through git, chat,
commits, docs or artifacts.

| Where | Holds (key names) |
|---|---|
| user-secrets `d8521a4e-969b-4696-9990-45dea324bda8` (AppHost) | Parameters: openaikey, elevenlabskey, syncfusionkey, jwtkey, githubpat, githubappprivatekey, feedbackhmackey, dbUser, dbPassword, db-password, cache-password; AppHost: OtlpApiKey, McpApiKey, DashboardApiKey; Azure: SubscriptionId, Location; Coach settings; resource ports |
| user-secrets `9ae3953f-a490-41b3-a2b8-a8e2555b4615` (Api) | Jwt:SigningKey, Auth:UseEntraId |
| user-secrets `33f95f89-d495-4311-b6cb-53a47b5c34e6` (WebApp) | AzureAd (TenantId, ClientId, ClientSecret, Instance, CallbackPath), DownstreamApi scopes, Auth flags |
| `src/SentenceStudio.AppLib/appsettings.json` (git-ignored) | Settings: OpenAIKey, HuggingFaceToken, ElevenLabsKey, SyncfusionKey; service URLs |

User-secrets files are `~/.microsoft/usersecrets/<id>/secrets.json`; copy the three folders.
The Workers project has an ID but no secrets file.

- **Production Azure** is keyless (Entra): `az login` on Lilliput. Prod is on the business tenant,
  `rg-sstudio-prod-biz` in westus3; Foundry `daortin-sstudio-eus2` (Fast = gpt-5-mini,
  Reasoning = gpt-5). No Azure commands or deploys without the Captain's go
  (`scripts/prod-deploy.sh`, `scripts/prod-db-access.sh`).
- **Local database:** a fresh Aspire Postgres volume takes whatever db password the AppHost
  secrets hold; local test accounts then need seeding (`docs/local-dev-test-accounts.md`).
- **Rotate:** the old machine's AppLib `appsettings.json` holds an OpenAI project key that was
  never committed but was flagged to rotate if still live; a branch named
  `op-revoke-huggingface-token` suggests the Hugging Face token needs the same check.
- **iOS signing:** the Release app for the Captain's phones is signed with the `MaxProWildDev`
  wildcard profile; Lilliput needs the certificate and profile (Xcode account or an exported
  `.p12`).

## People, devices and accounts

- The user is **the Captain** (David Ortinau), the only user of the app. Speak like a pirate.
- **Production devices:** Duotopia (iPhone, UDID `00008160-…000A`), DX24, Pixel 5 and the
  Captain's Mac. Never uninstall, clear, reset or replace their apps without permission in that
  turn; `scripts/ios-phone.sh Duotopia|DX24 --yes` only after he approves that install. After an
  approved install, end a running SentenceStudio yourself instead of asking him to.
- The Captain's data on Duotopia is large (4,466 words, 2,908 lemmas, 92 resources, 20 skills).
  Per-item SwiftUI views tripped the 0x8BADF00D watchdog; test lists with about 3,000 rows.
- **Test accounts** (local API only): `e2e@test.local`, `testsailor@test.local`, `squad-kaylee`;
  never `captain@test.local`; `squad-jayne` is reserved. Passwords are in
  `docs/local-dev-test-accounts.md`; read them into shell variables, never into messages.
  Throwaway accounts `forms-onboarding-1113@test.local` and `...1113b@test.local` exist in the old
  machine's local database only.
- Don't sign out existing device sessions, and don't clone a signed-in simulator (clones share one
  refresh token; the first refresh signs the others out).

## Standing approvals and product direction

- **Bridge:** add views and modifiers to the local SwiftUI bridge and pack a new local version
  without asking (not new dependencies, architecture changes, upstream publishing or UIKit).
  Approved bridge additions include the web view (YouTube) and the Android photo picker.
- **Native first** (2026-11-12): system controls and containers everywhere unless the Captain
  overrides; no hand-built look-alikes. Every form follows the grouped Form pattern
  (`Styling/NativeForm.cs`, departures 84-93).
- **MAUI apps still ship:** API, sync and schema changes must keep the MAUI heads building,
  migrating and syncing. Parity covers web, MAUI, native heads and the future Flutter port.
- **Sam:** one runtime that can do anything the user can do (the Captain never approved the
  September coach freeze).
- **Words:** one owner per word; devices sync only the signed-in owner's rows.
- **Git:** one branch at a time from `native-platform-heads`, merged before switching. Push only
  after the Captain approves. Never rewrite history, force-push, amend or use a bare `git stash`.

## Tooling notes that cost time before

- **Simulators:** iPhone 18 Pro Max simulators (iOS 27.0) ignore mobile-canvas taps on text
  fields and the tab bar; use an iPad Pro 11-inch simulator for forms. Korean can't be typed by
  automation there (it pastes, and the clipboard doesn't reach headless simulators). Fresh
  simulators need the Accessibility automation defaults before SwiftUI publishes elements.
- **mobile-canvas device IDs** look like `ios:core-simulator:<UDID>`.
- **Stale outputs:** `scripts/ios-build.sh` moves aside stale generated outputs. If the app spins
  at 100% CPU at launch, the SwiftUI shim is stale; if it crashes in `ResolveTokenReference`,
  CoreCLR is stale after an SDK change; an old ReadyToRun AppLib can also ship. Move `obj` aside
  and rebuild. Repacking a bridge version needs
  `rm -rf ~/.nuget/packages/microsoft.swiftui/<version>`.
- **Ailoha:** official v0.1.13 cannot tap SwiftUI (use the fork branches above); Ailoha's
  `global.json` pins SDK 10.0.203.
- **iPhone Duo simulator:** screenshots via `simctl io <udid> screenshot --display=1`.
- **Toolbar long-press labels** show only at accessibility text sizes; the Captain dropped that.
- **SwiftUI Section IDs:** never put an automation ID on a Section (SwiftUI gives it to every row).

## State of the work

- **Done:** the native forms pass (departures 84-93), the Library split view, native lists for
  Resources, Vocabulary and Skills. See `docs/native-port-departures.md`.
- **Next:** the Minimal Pairs native list (plan in `docs/handoff-2026-11-13-forms-pass.md`), then
  `docs/native-review-backlog.md` and the open backports in `docs/native-port-backports.md`.
- **Tests:** iOS contract tests 469/471 (two long-standing failures:
  `SourceButtons_KeepNativeHit...`, `SamPresentationUsesOnlyTheSwiftUIFacade`). The SwiftUIViews
  harness (a macOS app run by hand) is killed at launch with "Code Signature Invalid" (backlog).
- **Waiting on the Captain:** merging `library-split-view`; a Duotopia install of the forms pass;
  the Korean wording of new strings (listed in `docs/handoff-2026-11-13-remaining-work.md`).

## Local-only work not yet on GitHub

The old machine's main SentenceStudio checkout also holds older local-only work: 56 branches,
20 stashes and uncommitted changes in five worktrees (the main checkout on `main`, two
`op-fix-*` worktrees, `davidortinau/investigate-local-ai-fallback`, one agent worktree). A secret
scan of all of it found nothing, but pushing it to the public repo needs the Captain's go. If he
gives it, it goes under `handoff/2026-11-13/{branch,stash,wip}/...` so nothing existing is
overwritten.
