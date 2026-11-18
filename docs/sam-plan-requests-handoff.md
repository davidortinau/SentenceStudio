# Sam: Today's Plan requests and app actions (handoff)

Plan: `~/.claude/plans/soft-wibbling-shell.md` (approved 2026-11-17, revised after the Fable review).
Each phase is its own branch, stacked on the one before. None is pushed yet: pushing and merging
wait for the Captain.

| Phase | Branch | State |
|---|---|---|
| 0: Sam knows the screen | `feat/sam-screen-context` | Committed 25c46be1. Verified on iOS sim, Android emulator (Pixel_9_Pro) and web. |
| 1: plan routing, plan reads, word lists | `feat/sam-plan-reads` | Committed. Verified on web, Android and iOS (below). |
| 2: start an activity | `feat/sam-start-activity` | Committed. Verified on web, Android and iOS (below). |
| 3: builder keeps content, fills > 34 min | | Not started |
| 4: default duration | | Not started |
| 5: topic focus from tags | | Not started |
| 6: another language today | | Not started |
| 7: find or create, then plan | | Not started |

## Phase 1: what changed

- **Plan routing** (`CoachWriteAuthority.ClassifyPlanRouting(text, planOnScreen)`): with plan items on
  screen, these count as plan requests:
  - an offer of time: "I have 45 minutes", "increase to 45 minutes", "30분만 할게";
  - a wish to study: "I want to study food words", "I'd prefer to study German today";
  - "the plan", "this plan", "current plan".

  Statements ("I practiced 30 minutes"), questions, activity launches and other days ("tomorrow")
  do not. Without a screen, nothing changes.
- **get_current_plan_summary**: reads `IPlanService.GetTodayAsync` through `CoachTodayPlanReader` (once
  per request), so archived and Choose My Own rows no longer count. It now returns:
  - per item: the item id, resource and title, skill and word count;
  - for the plan: total minutes, word count and the dashboard mastery figure.
- **get_plan_vocabulary** (new read): today's plan words in plan order or weakest first, with meaning
  and mastery. Words hidden on the learner's screen are left out and counted.
- **Word list**: the server builds `CoachTurnResponse.WordList` from that read, never the model.
  - **Where it shows:** under Sam's reply on web/MAUI (`CoachWordList.razor`), iOS (`SamView.WordList`)
    and Android (`SamWordList`).
  - **Rows:** each one opens the word's editor. On iOS and Android, Sam closes only after the editor
    opens; if it can't, Sam shows a notice instead.
- **Due-word embargo per turn** (`ICoachValidationDataSource.GetTurnEmbargoAsync`):
  - **Exempt:** due words the list showed, but only when the learner's own words asked to see words
    (`IsWordListRequest`).
  - **Hidden-answer quiz word:** only its hidden side stays withheld, whether or not it is due.
- **Route ids**: `CoachRouteCatalog` accepts canonical GUIDs, so word links reach the word.

## Phase 2: what changed

- **Model output:** a routing marker `CoachTurnIntent.ActivityLaunch` (the activity, a language, the
  situation in the learner's words, a resource or skill id).
- **Server checks** (`CoachSessionService.AttachActivityLaunchAsync`). Anything unchecked is dropped:
  - the activity is one of `CoachActivityKind`;
  - a language is accepted only for Conversation, from `LearnerProfileOptions.TargetLanguages`, and
    never the learner's native or current target language;
  - the scenario is matched to a built-in by whole words (`BuiltInConversationScenarios`);
  - the resource and skill must be the learner's own.
- **Opening by itself:** `AutoStart` is set only when the learner's own words say start, open, launch
  or begin with an activity named, and the message is not a question (`IsExplicitActivityStart`).
- **Clients** (`CoachActivityLaunchCard.razor`; iOS `SamView.ActivityLaunch`; Android `SamActivityLaunch`):
  - **The card:** "Opening Conversation · German · Asking for Directions" with Cancel. On web, Cancel's
    fill drains right to left over 3 s; on iOS and Android, a bar under Cancel steps down once a
    second.
  - **When it opens:** when the time runs out, as its Choose My Own tile would, and Sam closes.
  - **What stops the countdown:** Cancel, typing to Sam, closing Sam, switching or starting a
    conversation, an account change, or (web) focusing Cancel.
  - **Never auto-opens:** under VoiceOver or TalkBack, over another activity or quiz, for a reply read
    back from history, or for a recovered operation. Start and Not now show instead.
- **Native launch:** `NativeActivityLaunch` carries `ScenarioId` and `Language`, and the native
  conversation opens on them.

## Known limits

- The word list isn't kept in durable history. Reloading a conversation shows Sam's sentence ("the
  list appears below") without the list.
- The iOS and Android countdown bar steps down once a second (the SwiftUI bridge has no smooth
  animation yet); the web fill animates smoothly.
- The web can't detect a screen reader. Instead, reaching Cancel by keyboard or screen reader stops
  the countdown, and the countdown is announced in a polite live region.
- Scenarios are matched to built-ins only. A learner's own custom scenarios are not matched yet.
- `IsWordListRequest` and the on-screen routing use English and Korean phrase patterns. Other
  wording falls back to the old behavior (clarify, or no list).

## Test recipe

- Web: Playwright with the cached Chromium at
  `~/Library/Caches/ms-playwright/chromium-1252/...`. Dismiss "What's New" first. On the quiz page,
  click Resume.
- Android: memory `android-emulator-recipe`.
- iOS: `scripts/ios-sim.sh` with the Ailoha properties (memory `ios-ailoha-sim-recipe`). On
  2026-11-17 the Ailoha agent did not connect, so mobile-canvas taps by points did the work.
