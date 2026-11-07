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
