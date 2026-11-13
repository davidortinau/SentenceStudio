# Handoff: native forms pass (2026-11-13, evening)

Written so the work can continue on another machine. Read with
`docs/handoff-2026-11-13-remaining-work.md` (the earlier audit) and `AGENTS.md`.

## Where things stand

- **SentenceStudio:** branch `library-split-view`, pushed at the commit that adds this note. Not
  merged into `native-platform-heads`/`main`. Nothing is installed on Duotopia or DX24 since
  02235970; the forms pass has run only on simulators.
- **SwiftUI bridge** (`davidortinau/Microsoft.SwiftUI`): branch `split-view` pushed at 4dd4182
  (previews .62-.66). SentenceStudio pins **0.1.0-preview.66**.
- **Ailoha** (`davidortinau/ailoha`) and **maui-labs** (`davidortinau/maui-labs`, a fork made for
  this handoff): every local branch and stash is pushed; stashes are `stash/*` branches.

## Setting up the other machine

1. Clone SentenceStudio and check out `library-split-view`. Write your own `global.json` (it is
   ignored and developer-specific; see `.squad/skills/dotnet-sdk-detection/SKILL.md`). This
   machine builds the native iOS head with the .NET 11 SDK (11.0.100) and Xcode 27.
2. Clone the bridge to `~/work/Microsoft.SwiftUI` (branch `split-view`). The iOS head's
   `src/SentenceStudio.Native.iOS/NuGet.Config` reads the package from
   `/Users/davidortinau/work/Microsoft.SwiftUI/artifacts` (an absolute path: change it locally if
   your home folder differs, and don't commit that change).
3. Put the package in that folder. Either download it:
   `gh release download v0.1.0-preview.66 --repo davidortinau/Microsoft.SwiftUI --dir ~/work/Microsoft.SwiftUI/artifacts`
   (SHA-256 `80e8e3436cc2b15cbb27d14b7421c3fe018183573019b87251ef54169d70fb10`), or build it from
   the bridge: `bash scripts/build-xcframework.sh`, then `bash scripts/pack-and-verify.sh` (see the
   bridge's `docs/build-and-provenance.md`; it was packed with a temporary RC SDK `global.json`).
4. `scripts/ios-sim.sh <UDID>` builds, installs and launches on a simulator (local API on port
   5081 through the AppHost). Ailoha is opt-in for Debug (`AilohaAgentXCFramework`); the build
   doesn't need it.

## Done in this pass (departures 84-93)

Every iOS form is a native grouped Form (`Styling/NativeForm.cs`): Resource, Skill, word editor,
Profile, Settings, Import Content, YouTube Import, Create Minimal Pair, Sign In and the account
pages, and Onboarding (93, `5759c08f`). Redraws swap Form Sections in place
(`ApplicationScene.TrySwapFormSections`) so scroll and focus stay. Also fixed: the crash on Back
from the YouTube channel page (783bc191).

Onboarding was run end to end on the iPad simulator with throwaway local accounts
(`forms-onboarding-1113@test.local`, `forms-onboarding-1113b@test.local`); the profile saved the
chosen name, languages, 30 min and B1.

## Next

1. **Minimal Pairs native list** (started, nothing written). Plan:
   - `MinimalPairsScreen.Render`: `BrowserPage.SingleColumn("native.minimalpairs", rows, width,
     search, style)` under `Views.Toolbar` with `BrowserPage.AddButton` for Create Pair (in place
     of the inline Create button).
   - Rows: a notices row; Mode as a segmented Picker row and Start Session as an accent row; each
     pair a `Views.SwipeRow` (icon, "A vs B", contrast label, checkmark when highlighted; tap
     selects) with trailing Delete (red, not destructive: it asks first through the existing
     alert) and leading Study (`Library_StudyThis`, "graduationcap").
   - Study needs a presenter method (shared, with a test in
     `tests/SentenceStudio.Native.Tests/NativeMinimalPairsPresenterTests.cs`): set Focus, select
     the pair, start the session, under one gate.
   - Search is view-only (filter WordA/WordB/contrast in the screen). The scene's
     `UpdateLibraryPairs` uses `TryReplaceScrollContent`, which needs a "scroll"; add
     `BrowserPageUpdate.MinimalPairs` and update in place as `UpdateSkillsList` does, so the field
     keeps focus. The activity path (`ApplicationScene.Activities.UpdateActivity`) renders the
     same list.
   - Count under the title: a new key (`NativeMinimalPairs_PairCount`, "{0} pair(s)", Korean
     "최소 대립쌍 {0}개") and `WithNavigationSubtitle`, as Skills does (`SkillsSubtitle`).
   - Add a departure row and update the contract tests that read `MinimalPairsScreen.cs`.
2. The rest of the backlog: B82 parity, SwiftUIViews harness launch ("Code Signature Invalid"),
   counts after sync, Dashboard total words, dead phone filter sheet, text-matching tests, open
   backports B41, B52, B58, B67/68, B71, B73/74.

## Known issues

- iOS contract tests: 469/471, the two long-standing failures
  (`SourceButtons_KeepNativeHit...`, `SamPresentationUsesOnlyTheSwiftUIFacade`).
- Commits 783bc191 and 03f9dc9b carry an older `Claude-Session` trailer. Left as is (fixing it
  would rewrite pushed history).
- Testing on simulators: iPhone 18 Pro Max simulators (iOS 27.0) ignore mobile-canvas taps on text
  fields and the tab bar; use an iPad simulator. Korean text can't be typed by automation there
  (it goes through a paste that fails headless); tap a Korean suggestion or use the device.
- Create Account with hidden passwords: iOS's "Use Strong Password?" sheet garbles synthetic typing
  into both fields; typing with the passwords shown works. A bridge `textContentType` is the likely
  fix (backlog).
