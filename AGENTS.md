# SentenceStudio Engineering Guide

Call the user **Captain** and speak like a pirate.

This file is the authoritative engineering guide for the current
`native-platform-heads` effort. Older instructions that describe SentenceStudio
as a .NET MAUI or MauiReactor application are historical and do not govern new
work.

## Current Product Direction

SentenceStudio is moving from .NET MAUI Blazor Hybrid native clients to:

- **Android:** .NET for Android with Jetpack Compose.
- **iOS:** .NET for iOS with SwiftUI.
- **macOS:** .NET for native macOS/AppKit with SwiftUI.
- **Web:** the existing Blazor WebApp remains supported.

The native platform heads use platform-native UI stacks. New native code must
not depend directly or transitively on:

- `Microsoft.Maui.*`
- `Microsoft.Maui.Controls`
- MauiReactor / Reactor.Maui
- MAUI Community Toolkit UI
- MAUI BlazorWebView
- Comet

Windows and Linux native heads are out of scope until Captain explicitly adds
them.

The durable, repository-local product and acceptance guidance is in:

- `docs/native-shared-foundation.md`
- `.claude/skills/e2e-testing/references/`
- `.squad/skills/learning-value-gate/SKILL.md`
- `docs/deploy-runbook.md`

The current vertical-slice acceptance criteria are stated in this file under
`YAGNI and Vertical-Slice-First Gate`. Session rebuild records and evidence may
add detail, but an agent must not require an undiscoverable external record to
understand or implement the current slice.

## Transition Boundary

The existing projects `SentenceStudio.Android`, `SentenceStudio.iOS`,
`SentenceStudio.MacOS`, `SentenceStudio.MacCatalyst`, and
`SentenceStudio.Windows` are **temporary MAUI reference heads**.

Use them to:

- observe current behavior and visual design;
- capture source-side screenshots and runtime evidence;
- verify data compatibility during the transition;
- compare old and new implementations.

Do not:

- add new product UI to the old MAUI heads;
- convert additional pages to MauiReactor;
- create new Shell routes or MAUI handlers for the replacement app;
- treat DevFlow success in an old head as verification of a new native head;
- allow transitional MAUI projects to become dependencies of shared, server,
  web, or new native projects.

`SentenceStudio.MauiCompatibility` and `SentenceStudio.MauiServiceDefaults`
exist only to keep the old reference heads buildable. They are forbidden
dependencies for the active WebApp, API, Workers, shared libraries, tests, and
new native heads.

New native application and presentation projects live under
`src/SentenceStudio.Native/`. Platform project names and target frameworks are
defined by the `.csproj` files in that directory. If a platform project has not
been created yet, create it there; do not put replacement UI into the temporary
MAUI heads.

## Native UI Dependencies

### Android

Use the standalone `Microsoft.AndroidX.Compose` package from
`jonathanpeppers/Microsoft.AndroidX.Compose`.

- Author Compose UI in C#.
- Use Material 3 and native Compose semantics.
- Keep UI state and platform-independent workflows outside the Android head.
- Add stable semantic/test tags for Ailoha and accessibility.
- Verify both Debug and Release/R8. Compose JNI calls can work in Debug and be
  stripped by R8 unless package retention contracts are correct.
- Do not vendor the Compose facade into SentenceStudio.
- Do not consume Comet. Comet source may be consulted as prior art only.

### iOS and macOS

Use the standalone `Microsoft.SwiftUI` package from
`davidortinau/Microsoft.SwiftUI`.

- Author feature declarations in C# through the SwiftUI facade.
- Share feature views and design tokens between iOS and macOS.
- Keep UIKit and AppKit hosts, scenes, windows, menus, navigation shells,
  permissions, and lifecycle behavior platform-specific.
- iPhone and iPad must have adaptive layouts; do not stretch a phone layout.
- macOS must behave like a desktop application with resizable windows,
  keyboard navigation, menus, pointer interaction, and desktop density.
- Keep Ailoha out of the reusable SwiftUI library. Agent integration belongs
  only in Debug application heads.

### Shared native application layer

Shared native projects may contain:

- screen state and presentation models;
- application workflows and orchestration;
- navigation destinations and launch context;
- validation and localized text keys;
- platform-neutral service interfaces;
- commands, events, loading, empty, error, retry, and cancellation states.

They must not contain Android, UIKit, SwiftUI, AppKit, MAUI, or browser UI
types.

Reuse existing SentenceStudio domain, application, repository, grading,
progress, timer, activity-session, synchronization, localization, and AI
logic. Do not create platform copies of business rules.

## YAGNI and Vertical-Slice-First Gate

The first delivery objective is the smallest real end-to-end user journey, not
generalized infrastructure.

The current risk-first slice is:

`sign in -> Dashboard -> Today's Plan -> assigned Vocabulary Quiz -> complete
the activity -> return -> completed plan item -> restart with state preserved`

Required sequence:

1. Read only enough source and framework documentation to implement the slice
   safely.
2. Build and run the slice with real application logic and storage.
3. Review the running slice on every target platform.
4. Expand shared foundations only in response to a demonstrated blocker or the
   next approved slice.

Before starting infrastructure, abstractions, CI guards, reusable libraries,
upstream work, or broad refactoring, answer:

- Which current acceptance step cannot work without this?
- What observed failure proves it is blocking?
- What is the smallest change that unblocks the slice?
- Can it wait until integration, pre-merge, or release?

If there is no currently failing acceptance step, defer the work.

Classify work as:

- **Slice-blocking:** required now.
- **Integration-blocking:** required before completed slices are combined.
- **Release-blocking:** required before shipping.
- **Optional/generalization:** backlog unless Captain explicitly requests it.

Only slice-blocking work may delay the first running slice. Thoroughness means
completely satisfying the current phase, not solving future phases early.
Unlimited budget does not authorize unlimited scope.

If more than half the work since the last checkpoint is infrastructure,
verification machinery, or generalized tooling, stop and return to product
behavior. Every progress report must begin with the user-visible capability
added since the previous checkpoint.

This section overrides eager downstream fan-out during the first slice.

## Tooling Friction and Dogfooding

SentenceStudio continues to dogfood the .NET mobile workloads, Aspire, Ailoha,
Compose bindings, SwiftUI bindings, Hot Reload, and related tooling.

Tooling friction preempts product work only when:

1. it directly blocks the currently approved vertical slice; or
2. the same friction has occurred at least twice and continuing requires
   another workaround.

If friction does not block the slice:

- capture the error, reproduction, logs, versions, and binary identity;
- search the relevant upstream repository;
- record follow-up work;
- continue the product slice.

Do not turn incidental friction into a new framework, generalized diagnostic
system, or comprehensive test harness. First make the smallest evidence-based
change required to continue.

If a tooling investigation exceeds 30 minutes without removing the current
blocker:

- report its status and evidence;
- switch to another independent part of the current slice when one exists;
- keep the blocker investigation bounded and parallel;
- ask Captain only when no independent slice work can proceed or a product/
  permission decision is required.

Do not pretend a directly blocked acceptance step can proceed, but do not let
one blocked platform stop independent slice work on other platforms.

When an upstream change is genuinely required, prefer:

1. root cause and verified local fix;
2. upstream PR with a minimal generic change;
3. otherwise a deduplicated upstream issue with a minimal reproduction.

## Working Style

- Use phase-appropriate thoroughness.
- Always pass the active user ID to progress and owner-scoped queries.
- Empty user identity must fail closed, never broaden a query.
- Add regression tests when a bug recurs.
- Reuse existing helpers and patterns before introducing abstractions.
- Follow the source-led app rebuild contract; do not redesign unrequested
  product behavior.
- Consult relevant active or prior Copilot sessions and their artifacts before
  asking Captain to repeat prior context.
- Record real product decisions through Squad state. Do not write directly to
  `.squad/decisions.md`.
- Keep documentation under `docs/`, except authoritative repository control
  files such as `AGENTS.md`.

## Git Workflow

Captain is a solo developer.

For interactive sessions:

- Work on a branch.
- Run code review before every push.
- Fix review findings before pushing.
- Push only after Captain approves.
- When Captain says "merge to main" or "ship it," merge directly to `main`;
  do not create a PR unless he explicitly asks for one.

Autonomous cloud issue work still uses a PR as its delivery channel.

Do not:

- rewrite history without explicit permission;
- force-push over another person's work;
- discard uncommitted changes;
- amend commits unless requested;
- create a PR as the default interactive workflow.

## SDK and Workload Selection

Before any `dotnet` command:

1. `find . -maxdepth 4 -name global.json`
2. `dotnet --list-sdks`
3. `dotnet --info | head -20`
4. `dotnet workload list`

Read `.squad/skills/dotnet-sdk-detection/SKILL.md` if the selected SDK,
workload band, target framework, or Xcode compatibility is unexpected.

Current architecture:

- API and server-side shared slices remain `net10.0`.
- WebApp and new native work use .NET 11 where their project files specify it.
- Android minimum: API 36.
- iOS minimum: iOS 26.
- macOS minimum: macOS 26.

The local `global.json`, if present, is developer-specific and gitignored.
Never commit or silently replace it.

Build commands must name the actual project under `src/SentenceStudio.Native/`
and its target framework. Discover project names from the current `.csproj`
files; do not infer a target from an old MAUI example in historical
documentation.

Do not run concurrent `dotnet` builds against projects that share generated
outputs. Build the solution once or sequence project builds in one shell.

## Build, Run, and Inspection

### New native heads

- Build with the platform's .NET SDK and explicit target framework.
- Run on a simulator/emulator before a physical device.
- Use official Ailoha binaries in Debug only.
- Verify app ID, process, device, binary, agent endpoint, and visible state.
- Use stable semantic/accessibility IDs for actions.
- Confirm actions changed state; a successful response alone is not proof.
- Inspect Release output to prove Ailoha, NanoHTTPD, Debug permissions, and
  listeners are absent.

### Temporary MAUI reference heads

- Use MAUI DevFlow only for source/reference capture.
- Follow the `maui-devflow-debug` skill for those old heads.
- Do not use MAUI DevFlow as the target automation system for Compose/SwiftUI.

### WebApp

- Run through Aspire with isolated PostgreSQL and storage volumes.
- Use Playwright for deterministic browser interaction and screenshots.
- Preserve the authenticated browser context across navigation.
- Inspect real owner-scoped PostgreSQL rows, logs, cookies, preferences, media,
  and Data Protection behavior.

### Physical devices

Treat DX24, Pixel 5, and Captain's Mac as production environments.

- Prove changes on simulators/emulators first.
- Never uninstall, clear data, reset, wipe, or replace a production-identity
  app without explicit permission in that turn.
- Assume no backup exists unless Captain confirms one.

Native macOS has no ordinary simulator. Use a separate Debug bundle identifier,
isolated sandbox/database, and non-production signing for the first local macOS
proof. This permits safe iteration on Captain's Mac without touching the
production-identity app or its data. Production-identity replacement remains a
separate, explicitly approved operation.

## Validation and Completion

A build is a prerequisite, not end-to-end verification.

For a native UI or behavior change:

1. build the exact target;
2. launch the current binary;
3. navigate to the changed feature;
4. interact with it through Ailoha;
5. capture before/after tree and screenshots;
6. inspect logs and persistence;
7. test applicable error, cancellation, permission, offline, restart, theme,
   text-scale, locale, keyboard, and accessibility states.

For shared/data changes:

- run focused tests;
- exercise the real path against isolated SQLite or PostgreSQL;
- verify owner scoping and restart behavior;
- run the smallest relevant native/web journey.

For migrations:

- run both migration validation scripts;
- prove discovery and application on real scratch SQLite;
- validate PostgreSQL on isolated storage;
- verify Up and Down on a verified backup when the operation is safe and
  explicitly authorized;
- never use Captain's live database as a validator.

Every closing response must end with a `Verified:` line naming actual checks.
If verification is blocked, say so explicitly.

## Data Preservation

Never delete or lose user data.

Before any action that can reset, overwrite, or destroy data:

1. create and verify a feasible backup or clone;
2. name the destructive step and data at risk;
3. ask Captain for explicit permission in that turn;
4. prefer a non-destructive alternative.

This includes:

- app uninstall or data clearing;
- database reset, drop, truncate, destructive delete, or destructive migration;
- SecureStorage, Keychain, Preferences, or app-group cleanup;
- filesystem deletion outside task-owned scratch;
- Git history rewriting;
- cloud resource deletion.

Cloud sync is not a backup. Preserve unsynced SQLite rows, CoreSync state,
preferences, media, keychain entries, migration history, WAL/SHM sidecars, and
local-only tables.

Replacement-install testing must use a simulator/clone first. A real
production-identity install requires fresh backup evidence and per-turn
permission.

## Multi-Tenant and Owner Scoping

The WebApp and API are multi-tenant.

Every read, write, sync, export, media, preference, history, Sam, and background
operation must:

- resolve the active owner from the trusted user scope;
- log and return an empty/null/false result when the owner is absent;
- never fall through to an unfiltered query;
- never guess ownership from language, display name, row count, or "first
  profile";
- prove two-user isolation with real database tests.

Forbidden pattern:

```csharp
var query = db.Items.AsQueryable();
if (!string.IsNullOrEmpty(userId))
    query = query.Where(item => item.UserProfileId == userId);
return await query.ToListAsync();
```

Required behavior: absent user ID means no data and no write.

The Development-only Sam operator surface is owner-scoped. Rollup, list,
detail, review, evidence, notices, and export must not cross owners.

## DataRecoveryService

Do not invoke automatic orphan recovery or enable
`enable_automatic_data_recovery` without reading:

`.squad/decisions/inbox/captain-rca-datarecoveryservice-cross-tenant-corruption.md`

All safeguards must pass:

- email match;
- temporal sanity;
- first-run per-user gate;
- masked sensitive logging.

Recovery remains disabled by default.

## EF Core and Dual-Provider Migrations

SentenceStudio uses:

- PostgreSQL for API/WebApp;
- SQLite for native clients.

Every shared schema change requires both provider migrations unless genuinely
provider-specific.

Workflow:

1. Scaffold the PostgreSQL migration with `dotnet ef`.
2. Review effective table names from `OnModelCreating`.
3. Create the SQLite counterpart under `Migrations/Sqlite/`.
4. Put `[DbContext(typeof(ApplicationDbContext))]` and
   `[Migration("<id>")]` directly on hand-written SQLite migration classes.
5. Update both snapshots.
6. Run:

```bash
bash scripts/validate-migration-attributes.sh
bash scripts/validate-mobile-migrations.sh
```

7. Prove the migration is present in `__EFMigrationsHistory` and the resulting
   schema on real scratch SQLite.

Never:

- use raw `ALTER TABLE` as a migration substitute;
- suppress `PendingModelChangesWarning`;
- assume valid SQL means EF discovered the migration;
- reset a database to make a migration pass;
- backfill ambiguous ownership by guessing.

Use `.squad/skills/ef-dual-provider-migrations/SKILL.md`.

## CoreSync and Startup Safety

- Keep synchronization owner-scoped.
- Do not treat server sync as recovery for unsynced local-only data.
- Preserve tracking tables, anchors, pending operations, and conflict state.
- API startup migrations, backfills, seeding, and CoreSync provisioning must be
  fenced by the same lock-owning PostgreSQL session.
- Workers and WebApp must validate API/schema readiness against the same
  database identity before database work.
- Startup fault tests must use attested disposable PostgreSQL storage and
  server-side write ordering, not client exception timing.

## Learning Value Gate

Any change to an activity's modes, directions, prompts, response types,
toggles, defaults, hints, audio, photos, or empty states must pass:

`.squad/skills/learning-value-gate/SKILL.md`

Required:

1. State the learning objective.
2. Enumerate direction x prompt modality x response modality x toggle.
3. Identify the target-language exposure or retrieval in every reachable row.
4. Trace the first-run default path.
5. Check answer leakage through labels, accessibility, audio, images, filenames,
   hints, and distractors.
6. Update E2E acceptance cases.

A state containing only native-language prompt and native-language response is
not acceptable. A photo must not make the target-language artifact disappear.

## Localization

- Use existing resources and strongly typed enums where available.
- Do not invent localization keys from AI-generated strings.
- Keep user-facing text out of platform-specific code when it can be shared.
- Test English and Korean, including Korean input composition.
- Set correct accessibility language and platform metadata.
- Do not preserve hard-coded English defects from the reference UI.

The Blazor-specific `LocalizationManager` interpolation rule applies only to
the retained WebApp/reference Razor UI, not to Compose or SwiftUI.

## Microsoft.Extensions.AI

- Use `[Description]` attributes on structured-output DTO properties.
- Let Microsoft.Extensions.AI handle serialization and deserialization.
- Do not hand-author JSON schemas in Scriban prompts.
- Do not add `[JsonPropertyName]` unless the wire name truly must differ.
- Keep prompts focused on business constraints and pedagogy.
- Preserve provider abstraction and test without live network calls.

## Error Handling and Logging

- Use `ILogger<T>` and structured message templates.
- Do not catch exceptions and return success or empty data.
- Keep expected domain failures distinct from unexpected technical failures.
- Surface user-actionable errors honestly.
- A cleanup failure must not silently prevent an independent data operation;
  log cleanup separately and preserve the primary operation's result.
- Use `Debug.WriteLine` only for temporary local diagnosis and remove it before
  commit.

## Async and Concurrency

Use established skills:

- `.squad/skills/single-flight-async/SKILL.md`
- `.squad/skills/async-single-flight-testing/SKILL.md`

Do not:

- use `async void` except framework event boundaries;
- fire-and-forget persistence without an explicit durability contract;
- make EF contexts singleton;
- allow concurrent startup writers outside the PostgreSQL startup fence;
- duplicate an in-flight token refresh, sync, or cache operation.

## Accessibility and Native UI Quality

- Every interactive control needs a semantic role, accessible name, state, and
  stable automation ID.
- Do not use color alone to convey meaning.
- Support screen readers, keyboard/focus order, dynamic text scaling, and
  platform-standard activation.
- Lists must use native virtualization.
- Respect safe areas and system insets exactly once at the boundary-owning
  container.
- Use native platform navigation and modal conventions while preserving the
  source information hierarchy and outcomes.
- Keep iOS/macOS shared feature UI adaptive rather than platform-identical.
- Do not put emoji in UI, logs, code output, or user-facing text. Use native
  symbols/icons or plain text.

## Troubleshooting

Before a slow build/deploy experiment:

1. reproduce the smallest failure;
2. read the named framework concept's current source/docs;
3. search current project issues;
4. search the owning dependency repository;
5. compare working and failing binaries/configuration;
6. state the source-backed reason the change should fix the issue.

Check related active/prior Copilot sessions and handoff artifacts before asking
Captain to repeat operational context.

For guesses with a feedback loop longer than five seconds, read first. After
three evidence-based failed attempts, stop editing and reassess the model.

## Documentation

- Put developer documentation under `docs/`.
- Keep current guidance separate from historical plans and archived specs.
- Cite real file paths, versions, commands, and observed results.
- Do not claim a package, runtime, device, or behavior was verified unless it
  was just read or executed.
- Update this file when the architectural transition materially changes.

## Publishing

`docs/deploy-runbook.md` is authoritative.

During the transition:

- do not publish or replace a production native app merely because a reference
  MAUI head builds;
- validate Azure independently from native packaging;
- validate native Release binaries, signing, entitlements, Ailoha exclusion,
  storage compatibility, and replacement behavior;
- prove on simulator/emulator before DX24 or Pixel 5;
- require Captain's explicit approval before a production-identity install.

When Captain says "publish," follow the runbook's current Azure and native
delivery matrix. Do not reuse historical Mac Catalyst commands or obsolete SDK
swap procedures.

## Review Checklist

Before commit:

- current phase and slice are explicit;
- change is required by a current acceptance case;
- active code contains no new MAUI/MauiReactor/Comet dependency;
- owner scoping fails closed;
- data-preservation rules are respected;
- tests cover recurring defects and public API changes;
- target build passes;
- end-to-end behavior is observed on the correct surface;
- no secrets, generated scratch, or external binaries are committed;
- documentation reflects the current native architecture.

Before push:

- staged diff review is clean;
- tests and runtime evidence match the staged commit;
- Captain approved the push.
