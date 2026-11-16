# SentenceStudio Engineering Guide

Call the user **Captain**.

This is the authoritative guide for SentenceStudio. The background behind these rules
(decision history, quotes, longer explanations, the review checklist) is in
`docs/engineering-guide-full.md`. Instructions that describe the app as MauiReactor are
historical.

## Product direction

SentenceStudio ships three ways, and all three are current products:

- **Native heads:** platform UI written in C#.
  - **Android:** .NET for Android + Jetpack Compose (`jonathanpeppers/Microsoft.AndroidX.Compose`).
  - **iOS:** .NET for iOS + SwiftUI (`davidortinau/Microsoft.SwiftUI`, a separate repo at
    `~/work/Microsoft.SwiftUI`).
  - **macOS:** planned, not started. There is no native macOS project yet. The plan is a
    .NET for macOS + SwiftUI head that reuses the iOS SwiftUI feature views, so keep those
    views free of iOS-only assumptions where that costs nothing. Do not start it until the
    Captain asks.
- **MAUI apps:** .NET MAUI Blazor Hybrid heads that share one Razor UI across Android, iOS,
  Mac Catalyst, macOS and Windows. They were the first clients and they are not being retired.
  MAUI gets features through the shared Razor UI, not through MAUI-only screens. API, sync,
  schema, shared-service and shared Razor UI changes must keep the MAUI heads building,
  migrating existing device databases without losing rows, and syncing with the same owner
  checks.
- **Web:** the Blazor WebApp, using the same shared Razor UI.

Windows and Linux native heads are out of scope until the Captain adds them. Parity covers
every client (web, MAUI, native heads, the future Flutter port). Never write "both apps".

## Project map

All under `src/`. The project files are the source of truth for target frameworks.

| Project | What it is |
|---|---|
| `SentenceStudio.Native` | Shared native layer (`net11.0`): presenters, state, workflows, navigation destinations. No platform UI types. |
| `SentenceStudio.Native.iOS` | Native iOS head (SwiftUI). |
| `SentenceStudio.Native.Android` | Native Android head (Compose). |
| `SentenceStudio.UI` | Shared Razor UI for the WebApp and the MAUI heads. |
| `SentenceStudio.Android`, `.iOS`, `.MacCatalyst`, `.MacOS`, `.Windows` | MAUI Blazor Hybrid heads. |
| `SentenceStudio.ShareExtension`, `SentenceStudio.Sharing` | iOS share extension for the MAUI iOS app and its shared code. |
| `SentenceStudio.MauiCompatibility`, `SentenceStudio.MauiServiceDefaults` | MAUI-head support only. Never a dependency of the WebApp, API, Workers, shared libraries, tests or native heads. |
| `SentenceStudio.WebApp` | Blazor WebApp. |
| `SentenceStudio.Api`, `SentenceStudio.Workers` | Web API and background workers (`net10.0`). |
| `SentenceStudio.Shared` | Data, EF migrations for both providers, repositories, sync and services used by every client and the server (`net10.0`). |
| `SentenceStudio.AppLib` | Client application services used by the native heads, the MAUI heads and the WebApp (`net11.0`). |
| `SentenceStudio.Domain`, `.Contracts`, `.Application`, `.Infrastructure` | Domain types, API contracts and server-side layers (`net10.0`). |
| `SentenceStudio.AppHost`, `.ServiceDefaults`, `.WebServiceDefaults` | Aspire AppHost and service defaults. |
| `SentenceStudio.Marketing` | Marketing site. |

Tests are under `tests/`; the native ones are `SentenceStudio.Native.Tests`,
`.Native.iOS.Tests`, `.Native.Android.Tests` and `.Native.SwiftUIViews.Tests`.

## Native UI rules

- All iOS feature UI and navigation is authored **and rendered** in SwiftUI; all Android UI in
  Compose (including dialogs, selectors, pickers that render UI). Native navigation means
  SwiftUI `NavigationStack` or Compose navigation, never `UINavigationController`.
- **Approval gate:** any app-authored UIKit on iOS, or Android widget/AppCompat/View UI, needs
  the Captain's explicit approval for that exact use first (show necessity, alternatives,
  sources, data impact). Non-UI platform services are fine.
- Do not build a custom renderer that translates declarations into UIKit (like
  `NativeVisualViewController`).
- **Bridge standing approval:** when an approved feature is blocked by the SwiftUI bridge, add
  the view or modifier to the local bridge and publish a new local package version without
  asking. Keep defaults for existing callers, read the framework source and prior art
  (Comet in `~/work/maui-labs`, read-only), add tests, keep the build/review/running-app
  gates. This does not cover new dependencies, architecture changes, upstream publishing or
  UIKit. When the pinned bridge version changes, change it in
  `src/SentenceStudio.Native.iOS/SentenceStudio.Native.iOS.csproj` and in both
  `microsoft.swiftui/<version>` inputs of `.github/validation.json` (the check list the
  `verify-work` skill runs) in the same change.
- No MAUI UI stack in native code (`Microsoft.Maui.Controls*`, XAML, Shell, handlers,
  MauiReactor, MAUI Community Toolkit UI, BlazorWebView, Comet). Non-UI `Microsoft.Maui.*`
  packages such as Essentials are allowed after checking their dependency closure.
- Native code lives in the three `src/SentenceStudio.Native*` projects (see the project map).
  The shared one holds state, workflows, navigation destinations, validation, text keys and
  service interfaces, with no platform UI types. Reuse the existing domain, sync, grading,
  progress, localization and AI logic; never copy business rules per platform.
- Ailoha stays out of reusable libraries and Release builds; Debug app heads only.
- Compose: Material 3, stable test tags, verify Debug and Release/R8.
- iPhone/iPad layouts adapt. When the macOS head exists it behaves like a desktop app.
- Every control has a role, accessible name, state and stable automation ID. Do not use color
  alone. Lists virtualize. Safe areas are handled once. No emoji in UI, logs or text.

## Scope

- Build the smallest real user journey first, with real logic and storage, then review it
  running on every target platform. Expand shared foundations only for a demonstrated blocker.
- Reuse the proven Web API, auth, plan generation and CoreSync paths; change the API only for
  an observed failure.
- Every created screen keeps its full source UI and behavior until the Captain approves a
  change. Do not redesign unrequested behavior.
- Before infrastructure, abstractions or tooling work, name the failing acceptance step it
  unblocks; otherwise defer it. If more than half the work since the last checkpoint is
  tooling, return to product behavior. Progress reports start with the user-visible change.
- Tooling friction preempts product work only if it blocks the current work or has happened
  twice. Otherwise record it (error, repro, versions) and continue. After 30 minutes on a
  blocker without progress, report status and switch to independent work.

## Working style

- Keep sessions to one piece of work. Hand off through a short note under `docs/` or memory.
- **Read before writing against a library or provider.** Before calling a bridge view,
  provider API or serializer for the first time, read its source or docs and check its
  constraints (allowed children, wire formats). Test through the real adapter or serializer,
  not only a fake.
- **Review before device testing.** Run the code review on the diff before the simulator or
  phone round, fix its findings, then test once.
- Use the scripts below instead of rebuilding their commands from the runbook.
- Do the work directly. Fan out to subagents or parallel worktrees only when the Captain asks
  for it. When planning, say where parallel work would help (independent pieces with separate
  file ownership and their own devices) and let him approve it.
- When a tool blocks the work (Ailoha, MAUI DevFlow, the bridge), fixing the tool is part of
  the task: find the cause, fix it locally, and open a PR to the tool's repo when the fix is
  general. Ask before opening the PR.
- Always pass the active user ID to progress and owner-scoped queries.
- Add regression tests when a bug recurs. Reuse existing helpers before adding abstractions.
- Check prior sessions and docs before asking the Captain to repeat context.
- Record product decisions through Squad state, not by editing `.squad/decisions.md`.
- Documentation goes under `docs/` (except control files like this one). Cite real paths,
  versions and observed results.
- Log WebApp/MAUI-affecting fixes from native work in `docs/native-port-backports.md`.
  Review correctness findings get fixed; cleanup findings go to `docs/native-review-backlog.md`.
- For a guess with a feedback loop over five seconds, read first. After three failed
  evidence-based attempts, stop and reassess.

## Scripts

- `scripts/ios-build.sh sim|device`: builds the native iOS app, moving aside the stale
  generated outputs incremental builds have reused, and checks the .app (bundle, API URL,
  fresh assemblies, shim, Release has no Ailoha, profile).
- `scripts/ios-sim.sh [UDID]`: build, install, launch and confirm on a simulator.
- `scripts/ios-phone.sh Duotopia|DX24 [--yes]`: Release build against production and install
  over the existing app. Pass `--yes` only after the Captain approves that install. Once he
  has, finish it without further questions: the script ends a running SentenceStudio on the
  phone and installs.
- `scripts/prod-db-access.sh status|open|close|backup`: production database access and the
  Step 1 backup, with the server lock restored.
- `scripts/prod-deploy.sh check|deploy|after`: Option A deploy with the Sam settings and
  feedback key, sticky sessions, settings comparison and validation.

## Git

- The Captain is a solo developer. `main` is the trunk and production deploys from it. Work on
  one branch at a time, cut from `main`, and merge it before starting the next. Run code review
  before every push and fix its findings. Push only after the Captain approves.
- "Merge to main" or "ship it" means merge directly to `main`, no PR unless asked.
- Autonomous cloud issue work still delivers through a PR.
- Never rewrite history, force-push, amend, discard uncommitted changes, or use a bare
  `git stash`.

## Build and run

- Check the SDK once per session (`global.json`, `dotnet --list-sdks`, `dotnet workload list`)
  and read `.squad/skills/dotnet-sdk-detection/SKILL.md` if anything is unexpected. The local
  `global.json` is developer-specific: never commit or replace it.
- API and server shared code is `net10.0`; WebApp and native follow their project files.
  Minimums: Android API 36, iOS 26, macOS 26.
- Name the real project and target framework in builds. Do not run concurrent builds that
  share outputs.
- Native heads: simulator/emulator before a device, Ailoha Debug only, verify app ID,
  process, binary and visible state, act through stable IDs, and confirm state changed.
- WebApp: run through Aspire with isolated database and storage; drive it with Playwright.
- MAUI heads: build, run and inspect through the `maui-devflow-debug` skill (MAUI DevFlow).
  For desktop MAUI work use the macOS (AppKit) head, `net11.0-macos`, not Mac Catalyst, unless
  the Captain names Catalyst. A MAUI result does not verify a native head, and the reverse.
- The `e2e-testing` skill says which tool to use for each kind of project.
- **Devices:** DX24, Pixel 5, the Captain's Mac and Duotopia are production. Never uninstall,
  clear, reset or replace their apps without permission in that turn. Any future native macOS
  Debug build uses a separate bundle ID and sandbox from the production app.

## Validation

- A build is not verification. For UI changes: run the current binary, reach the feature,
  interact through Ailoha, capture the tree/screenshot, check logs and persistence, and cover
  the applicable error, cancel, offline, restart, theme, text-scale, locale and accessibility
  states.
- Shared/data changes: focused tests, the real path on isolated SQLite or PostgreSQL, owner
  scoping and restart.
- Every closing response ends with a `Verified:` line naming the checks that ran, or saying
  what was blocked.

## Data preservation

- Never delete or lose user data. Before anything that can reset, overwrite or destroy data
  (uninstall, data clearing, database reset/drop/truncate/destructive migration, keychain or
  preferences cleanup, deleting files outside task scratch, history rewriting, cloud
  deletion): make and verify a backup, name the step and the data at risk, ask the Captain in
  that turn, and prefer a non-destructive option.
- Cloud sync is not a backup. Preserve unsynced rows, CoreSync state, preferences, media,
  keychain entries, migration history and WAL/SHM files.
- Azure: no commands or deploys without the Captain's go. Production database queries are
  read-only unless he approves a write. Schema changes need approval.

## Owner scoping

- Every read, write, sync, export, media, preference, history, Sam and background operation
  resolves the owner from the trusted user scope. An absent owner means no data and no write,
  logged; never an unfiltered query, never a guessed owner. Prove two-user isolation with real
  database tests.
- Do not run automatic orphan recovery or enable `enable_automatic_data_recovery`. It once
  retagged the Captain's data to a test account that signed in on his device. The service
  (`src/SentenceStudio.Shared/Data/DataRecoveryService.cs`) stays off by default and must keep
  its safeguards: email match, temporal sanity, one run per user, masked logging. Turning it
  on needs the Captain's approval after a confirmed server wipe.

## Migrations and sync

- Shared schema changes need both PostgreSQL and SQLite migrations; follow
  `.squad/skills/ef-dual-provider-migrations/SKILL.md` and run
  `scripts/validate-migration-attributes.sh` and `scripts/validate-mobile-migrations.sh`.
  Prove the migration in `__EFMigrationsHistory` on scratch SQLite. Never use raw `ALTER
  TABLE`, suppress `PendingModelChangesWarning`, reset a database, or backfill ownership by
  guessing. Never validate against the Captain's live database.
- Keep CoreSync owner-scoped. API startup migrations, backfills, seeding and CoreSync
  provisioning stay fenced by the same lock-owning PostgreSQL session.

## Product rules

- **Learning Value Gate:** changes to an activity's modes, prompts, responses, toggles,
  defaults, hints, audio, photos or empty states pass `.squad/skills/learning-value-gate/SKILL.md`.
  No state may be native-language prompt plus native-language response.
- **Localization:** use existing resources; test English and Korean (including Korean input);
  keep user text out of platform code where it can be shared.
- **Microsoft.Extensions.AI:** `[Description]` on structured-output DTOs, let MEAI serialize,
  no hand-written JSON schemas in prompts, test without live network calls.
- **Errors and logging:** `ILogger<T>` with templates; never catch and return success or empty
  data; surface user-actionable errors; a cleanup failure must not block an independent data
  operation; no `Debug.WriteLine` in commits.
- **Async:** follow `.squad/skills/single-flight-async/SKILL.md`; no `async void` outside event
  handlers, no fire-and-forget persistence, no singleton EF contexts.

## Publishing

`docs/deploy-runbook.md` is authoritative, through the scripts above. Prove native Release
builds on a simulator first and get the Captain's approval before any production-identity
install.
