# SentenceStudio Engineering Guide

Call the user **Captain** and speak like a pirate.

This is the authoritative guide for the `native-platform-heads` work. The background behind
these rules (decision history, quotes, longer explanations, the review checklist) is in
`docs/engineering-guide-full.md`. Older MAUI/MauiReactor instructions are historical.

## Product direction

- **Android:** .NET for Android + Jetpack Compose (`jonathanpeppers/Microsoft.AndroidX.Compose`).
- **iOS / macOS:** .NET for iOS/macOS + SwiftUI (`davidortinau/Microsoft.SwiftUI`, a separate
  repo at `~/work/Microsoft.SwiftUI`). Share feature views between iOS and macOS.
- **Web:** the Blazor WebApp stays.
- **MAUI apps still ship** (2026-11-04). API, sync, schema, shared-service and shared Razor UI
  changes must keep the MAUI heads building, migrating existing device databases without
  losing rows, and syncing with the same owner checks. No new MAUI-only product UI: MAUI gets
  features through the shared Razor UI. `SentenceStudio.MauiCompatibility` and
  `MauiServiceDefaults` must never become dependencies of the WebApp, API, Workers, shared
  libraries, tests or native heads.
- Windows and Linux native heads are out of scope until the Captain adds them.
- Parity covers every client (web, MAUI, native heads, the future Flutter port). Never write
  "both apps".

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
  UIKit.
- No MAUI UI stack in native code (`Microsoft.Maui.Controls*`, XAML, Shell, handlers,
  MauiReactor, MAUI Community Toolkit UI, BlazorWebView, Comet). Non-UI `Microsoft.Maui.*`
  packages such as Essentials are allowed after checking their dependency closure.
- Native code lives under `src/SentenceStudio.Native/`. Shared native projects hold state,
  workflows, navigation destinations, validation, text keys and service interfaces, with no
  platform UI types. Reuse the existing domain, sync, grading, progress, localization and AI
  logic; never copy business rules per platform.
- Ailoha stays out of reusable libraries and Release builds; Debug app heads only.
- Compose: Material 3, stable test tags, verify Debug and Release/R8.
- iPhone/iPad layouts adapt; macOS behaves like a desktop app.
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
- Prefer doing focused work directly over spawning subagents; fan out only for large,
  independent work with separate file ownership.
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
  over the existing app. Pass `--yes` only after the Captain approves that install.
- `scripts/prod-db-access.sh status|open|close|backup`: production database access and the
  Step 1 backup, with the server lock restored.
- `scripts/prod-deploy.sh check|deploy|after`: Option A deploy with the Sam settings and
  feedback key, sticky sessions, settings comparison and validation.

## Git

- The Captain is a solo developer. Work on a branch. Run code review before every push and fix
  its findings. Push only after the Captain approves.
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
- Old MAUI heads: reference capture only, through the `maui-devflow-debug` skill.
- **Devices:** DX24, Pixel 5, the Captain's Mac and Duotopia are production. Never uninstall,
  clear, reset or replace their apps without permission in that turn. The macOS proof uses a
  separate Debug bundle ID and sandbox.

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
- Do not run automatic orphan recovery or enable `enable_automatic_data_recovery` without
  reading `.squad/decisions/inbox/captain-rca-datarecoveryservice-cross-tenant-corruption.md`.

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
