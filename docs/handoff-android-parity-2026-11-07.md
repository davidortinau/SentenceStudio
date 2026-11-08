# Handoff: bring native Android up to date with iOS (2026-11-07)

**Goal (Captain, 2026-11-07):** "make sure [Android is] up to date with the iOS changes we've
been making." Start from `native-platform-heads` at `71a571d2` (pushed up to `54b0eb30`).

Rules that matter most here (all in `AGENTS.md`): Compose only, no Android widget/AppCompat UI
without approval; shared presenters are already platform-neutral, so Android work is mostly the
Compose screen and a platform service; keep data, owner checks and sync as they are; review the
diff before the emulator round; log any WebApp/MAUI-affecting fix in
`docs/native-port-backports.md` and Android-vs-iOS differences in `docs/native-port-departures.md`.

## What iOS has that Android may not

Found by comparing `git log -- src/SentenceStudio.Native.iOS` with the Android head since
2026-11-04 and searching the Android head. "Verify" means the search was not conclusive; read
the Android screen before building anything.

| iOS change (commits) | Shared code it uses | Android today |
|---|---|---|
| Conversation: Messages-style composer, scenario as a toolbar menu (`a1c13a6a`; departures 46-47) | `Native/Activities/Conversation/` presenter | `AndroidSliceShell.Conversation.Compose.cs` exists; verify composer and scenario placement |
| Conversation realtime voice, read-aloud start, voice pace menu (`a1c13a6a`, `a5c5ecc6`, `54b0eb30`; departures 48-50) | `NativeConversationPresenter.Voice.cs`, `NativeConversationVoice.cs`, `AppLib/Services/Realtime/ConversationVoiceSession.cs` | Missing. Android has no microphone/audio-streaming port (departures doc, Conversation section). Needs a non-UI audio capture + playback service (24 kHz PCM, like the iOS one) and the Compose voice panel |
| Import Content: paste-anything import, Library quick add (`a59778e7`, `194547ef`, `48c97189`, `29d1fe54`, `a8f2a81f`) | shared import workflow | Missing (no `ImportContent` in the Android head). `QuickAdd` there is the older resource-detail quick add |
| YouTube import: single video, My Channels, import history, channel page (`c4554be6`, `527c6a50`, `40af1a7b`; departures 40-45) | shared import/channel presenters, YoutubeExplode 6.6.3 | Missing (Android `YouTube` hits are only the video-watching embed) |
| Find Duplicates from the import word editor (`88e734ca`, `1cfcf27e`) | vocabulary workflow | Word-editor Find Duplicates exists (`AndroidSliceShell.VocabularyDuplicates.Compose.cs`); the import-editor entry depends on Import Content |
| Tab shell trial: Study, Library, Settings, Sam slot; Library list of four pages (`7a09daaf`, `566817d9`, `0a8e1b16`) | none (iOS app layer) | Android has its own tabs (`AndroidRoutePolicy.cs`). **Ask the Captain** whether Android follows the iOS trial |
| Launch look, single Sam entry, tab bar minimize (`25fef602`), hide floating Sam on scroll (`bb821fae`) | Sam entry rule (shared) | Verify the Sam entry; launch look and tab minimize are iOS-specific |
| Keyboard dismissal on editor pages (`a8f2a81f`) | none | Verify on Android editors |
| App icon and launch screen matching MAUI (`bcd8b731`) | none | Verify the Android icon/splash |

iOS-only fixes that need no Android work: Playback audio category, silent-switch audio,
resource-open freeze on device, quiz run off the main thread.

`docs/native-source-fidelity-status.md` row AF-N08 says Android has no Profile, Skills,
Vocabulary or Activity Log screens; that is stale (they landed in `62dad833`, 2026-11-03).

## Questions for the Captain before starting

1. Order: suggested Conversation composer/scenario, then Import Content, then YouTube import,
   then voice (largest: needs the audio port).
2. Should Android adopt the iOS tab shell trial (Study, Library, Settings, Sam)?
3. Voice on Android: OK to add a non-UI audio capture/playback service (AudioRecord/AudioTrack)?
   It renders no UI, so it does not need the widget approval, but it is new platform code.

## Build and run

- Build: `dotnet build src/SentenceStudio.Native.Android/SentenceStudio.Native.Android.csproj -f net11.0-android -c Debug -m:1`
  (one build at a time; in `AndroidSliceShell.Activities.cs` every switch case ends with `break;`).
- Emulator `emulator-5556` (sdk_gphone16k_arm64) was attached on 2026-11-07. Backend: this
  branch's AppHost on port 5081 (memory `native-journey-backend`). Test accounts:
  `e2e@test.local`, `testsailor@test.local` or `squad-kaylee` only.
- Verify Debug and Release/R8 (Compose JNI calls can be stripped by R8).
- The Pixel 5 is production: no install without the Captain's go.
- There is no Android equivalent of `scripts/ios-build.sh` yet; add one only if the same
  friction repeats.

## Progress (2026-11-07, session 1)

Captain's answers: order is composer/scenario, then Import Content, then YouTube import, then
voice; **Android follows the iOS tab shell trial** (Study, Library, Settings, Sam); the
AudioRecord/AudioTrack voice service is approved.

Done (uncommitted at the end of the session):

- Conversation follows departures 46-47 on Android
  (`AndroidSliceShell.Conversation.Compose.cs`): scenario is a header icon menu (Material
  "theater_comedy" vector drawable, check on the current one, falls back to the first scenario);
  Messages-style composer with a round quick-phrases toggle (`OutlinedIconToggleButton`,
  "format_quote" drawable) before a capsule field; keyboard Send sends, no Send button; after a
  keyboard send the field takes focus back once the reply is in (`RememberConversationEffects`).
  Listen follows the shared `CanListen`. Drawables live in `Resources/drawable` (the binding's
  core icon set has neither icon).
- Check marks in the Conversation scenario and language menus were invisible (no tint); both now
  use `palette.Primary`, like Activity Log. The scenario's spoken label drops the source colon.
- Shared: `NativeConversationAccess.Composer` added, unused `Send` ID removed.

Verified on `emulator-5556` (Debug, squad-kaylee, local AppHost 5081): scenario menu opens with
the check, quick-phrases toggle (checkbox role) shows phrases and inserts one, keyboard Send adds
the bubble and clears the field, the toggle dims while waiting, the field is focused with the
keyboard shown after the reply ends. Debug and Release/R8 builds pass.

Not verified, or blocked for a while:

- (Resolved the same day.) For a while the API could not connect to
  `daortin-sstudio-eus2.openai.azure.com:443` (TCP timeout from the Mac too), so starts failed
  after about 100 s. It recovered without changes; the models (`gpt-5-mini`, `gpt-5`,
  `AppHost.cs`) were fine. The opening line then came back in 3.5 s, and a keyboard-sent reply got
  its answer with Listen and comprehension, with the field focused and the keyboard up after.
- Korean input: `ailoha ui fill` fails on Compose text fields and adb cannot type Korean.
- Ailoha screenshots do not include popup windows (menus); use `adb exec-out screencap`.

Next: Import Content (paste-anything import, Library quick add), then the tab shell.

## Progress (2026-11-07, session 1, continued)

Import Content landed on Android (`AndroidSliceShell.ImportContent.cs`, `.ImportContent.Compose.cs`):
the shared `ImportContentCoordinator` behind a Dashboard More menu entry (until the tab shell gives
it the Library quick-add home), the full wizard, Import in the header, results with filter, the
result word editor with the Find Duplicates handoff, View resource returning to the results, and
the success snackbar (the activity snackbar helper is now shared, `WithToast`).

Verified on `emulator-5556` (squad-kaylee, local API): paste and Preview (CSV detected), row
uncheck and selected count, the missing-title error banner, commit into a new resource (3
created), re-import (3 skipped with reasons), commit into an existing resource (1 created), result
filter, word editor and Back, Find Duplicates opening the Vocabulary duplicate panel, View resource
and back to the results, and the resources and their four words after an app restart.

Not verified: Korean input (adb types ASCII only, Ailoha `fill` fails on Compose fields, and the
emulator does not share the Mac clipboard), the auto-detect confidence gate (needs Auto plus an AI
classification), light theme and text scale.

Test-tooling notes: Ailoha taps by element id, so look it up with `ailoha ui query --automationId` first; popup menus need
`adb exec-out screencap` and coordinate taps; `adb shell input keyevent 111` (Escape) acts as Back
when no keyboard is up.

Next (done below): YouTube import.

YouTube import landed on Android (`AndroidSliceShell.YouTubeImport.cs`, `.YouTubeImport.Compose.cs`):
the shared `YouTubeImportCoordinator` and `YouTubeChannelCoordinator` behind a More menu "Media
Import" entry, and Android now registers `AddOwnerBoundNativeYouTubeApi` (channels and history
through the API; it was missing).

Verified on `emulator-5556` (squad-kaylee, local API): My Channels empty state, Single Video fetch
(tracks and transcript from YouTube on the device), Save as Resource and View resource, Add Channel
(resolved by the server, "Channel added" toast on the page), the channel page's Check for New Videos
(20 found), Select None, importing one video (the server failed it with "No Korean transcript
available for this video", shown as Failed with Retry), Import History linked to the channel, and
the monitor switch saved through the API.

Not verified: Polish with AI, a successful channel video import, Retry, Retry Resolve.

Next (done below): voice.

Conversation voice landed on Android: `AndroidVoiceAudio` (AudioRecord voice-communication source,
echo canceller and noise suppressor when present, AudioTrack streaming, PCM16 24 kHz, half duplex
with the 600 ms tail, as iOS), the microphone permission through the Activity Result API (system
dialog), RECORD_AUDIO in both manifests, and the Compose voice UI (waveform button in the field,
voice panel with status, level, pace menu and End, spoken-turn bubbles with the mic marker,
Transcribing, and tool notes). The tool-note wording moved to the shared
`NativeConversationToolNote.Format`, which iOS now uses too.

Verified on `emulator-5556` (squad-kaylee, local API): the permission dialog, client secret, audio
start (no echo canceller on the emulator), Connecting, the partner reading aloud ("최수진 is
speaking"), Listening, the pace menu with checks and a speed change applied to the running session
(log: speed 0.8), End voice back to the composer, Back during voice stopping the audio (no player
left). iOS head builds (`scripts/ios-build.sh sim`; its assembly-age check flagged
`SentenceStudio.Native.dll`, which the Android build had produced minutes before; the iOS app's copy
contains the new code).

Not verified: real speech. The emulator's virtual microphone is noisy and has no echo canceller,
so it produced a stray learner turn ("嗯"); a physical device is needed (the Pixel 5 is production:
needs the Captain's go).

Next: the tab shell (approved, not yet ordered); the smaller checks (Sam entry, editor keyboard
dismissal, icon and splash).

## Plan for the next session: the tab shell on Android (Captain: "Follow iOS trial")

Source of truth: iOS commits `7a09daaf` (Study, Library, Settings, Sam slot; More menu gone),
`566817d9` (Sam slot always shown; alert when Sam is not offered), `0a8e1b16` (Library opens on a
list of its four pages), `25fef602` (launch look, single Sam entry, Library quick add), `bb821fae`
(floating Sam hides on scroll). Read `src/SentenceStudio.Native.iOS/App/ApplicationController.Library.cs`
and `ApplicationScene.Library.cs` first.

Android today: `AndroidRoutePolicy.TopLevelDestinations` = Dashboard, Resources; a two-item
`NavigationBar` (`AndroidSliceShell.Resources.Compose.cs` `NavigationTabs`); the Dashboard More menu
(`AndroidSliceShell.Visual.cs` `DashboardAppBar`) opens Profile, Settings, Skills, Vocabulary, Import
Content, Media Import and Activity Log as `MoreScreen`s (`AndroidSliceShell.More.cs`).

Suggested steps:

1. Tabs: Study (the Dashboard content; header: History opens Activity Log, Regenerate, Refresh),
   Library, Settings, Sam. Keep `NavigationBar` (Material 3); hide it in activities, the quiz and
   editors as iOS does.
2. Library root: a list of Resources (count once loaded), Vocabulary, Skills, Minimal Pairs, then the
   quick-add grid (new resource, skill profile, minimal pair, Import content, YouTube import). Reuse
   the `MoreScreen` pages as Library pages (they already open, close and release cleanly); the
   Resources tab content becomes the Library's Resources page.
3. Minimal Pairs: iOS hosts the list outside the activity (no timer or plan item); check whether the
   shared Minimal Pairs presenter can open its list without the activity before building anything.
4. Settings tab: the profile row (opens Profile), the existing Settings and Sam settings, Sign out.
5. Sam: the tab slot opens Sam; when Sam is not offered, an alert with `Coach_SettingsUnavailable`.
   The floating button leaves the tab roots; pushed screens keep the header entry
   (`docs/sam-entry.md`).
6. Remove the More menu once every destination is reachable; keep the automation ids where pages
   are reused.

## Progress (2026-11-07, session 2): the tab shell landed on Android

Done (`AndroidSliceShell.Tabs.cs`, `AndroidSliceShell.Tabs.Compose.cs`): Study (History, Regenerate,
Refresh in the header), Library (Resources with count, Vocabulary, Skills, Minimal Pairs, then Quick
add: Add Resource, Add Skill Profile, Create Pair, Import Content, YouTube Import), Settings (profile
row, settings, Sam settings, Sign out), Sam's slot (opens Sam; a dialog when he is not offered). Each
tab keeps its page; the tab bar hides in editors, activities and the quiz; Sam's header entry shows
only where the tab bar is hidden. The More menu is gone. Differences from iOS: departures 51-57.

Verified on `emulator-5556` (Debug, squad-kaylee, local API): Study header and four tabs; the Library
list (Resources count 12) and quick-add grid; Vocabulary kept across a switch to Study, re-tapping
Library returns to the list; the Resources row and Back; Create Pair opens the create page, Back twice
returns to the Library list; Settings profile row ("Kaylee", "English → Korean"), Profile and Back;
system Back from Settings to Study; History; Sam's slot opens his panel; Import Content and YouTube
Import from quick add with the tab bar; restart opens on Study. Android tests 357/357; Debug and
Release/R8 builds; iOS app builds with the shared `NativeProfileSummaryText`.

Not verified: Sam's "not available" and "checking" dialogs (Sam is offered on the local API), the
new-learner Add a Resource path, Sign out, light theme, text scale, Korean UI. iOS tests have two
failures that predate this work (`NativeHeadContractTests` expects bridge preview.43, the app pins
preview.44; `NativeSourceStylingContractTests` counts 6 `WithButtonBorderWidth(` calls, expects 5).

## Progress (2026-11-07, session 2, continued): the remaining handoff checks

- Sam entry: with the tab shell, screens that show the tab bar reach Sam through his slot only;
  editors, activities and the quiz keep the header icon (seen on the Minimal Pairs create page).
  The floating button is not used (backlog).
- Keyboard on editors: system Back hides the keyboard and keeps the page on Import Content, the
  new-resource form and the skill form (departure 59); no drag-to-dismiss needed.
- Icon and splash (departure 58): the MAUI artwork as an adaptive icon and the Android 12+ splash,
  the launch look while the session restores (splash, then Study, no Sign In frame; logo size
  measured so the handoff does not jump), and system-bar icons that follow the app theme
  (departure 60). Checked: cold launch frames, app drawer icon, Release APK badging, light and
  dark app themes on a light device.

All rows of the handoff table are done. Open: real-speech voice test on a phone (Pixel 5 is
production; the Captain's call), the items in docs/native-review-backlog.md.

