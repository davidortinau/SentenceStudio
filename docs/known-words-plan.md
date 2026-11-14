# Known words: Learner-led and Practice-led learning (plan, revision 3)

Captain, 2026-11-14: "As a user I will come to the app knowing some vocabulary and do not want to
regularly be tested over it. I should be able to tell the app if a word is known and have that
override the acquired status. I should also be able to admit 'ok I'm just learning and need to
practice something I thought I knew' and then have the word grow back to known. I should not lose
declared mastery/known status based on performance, but I should be able to improve from a
declared state of unknown or learning to known." And: "I'm not happy with trust but verify."

Revision 2 followed an independent review of revision 1 against the code. Revision 3 takes the
Captain's point that a mode switch should leave the marks alone ("Why not leave it be"), and ships
everything in one release. See "History of this plan" at the end.

## Decisions (Captain, 2026-11-14)

1. A **Learning mode** setting per learner:
   - **Learner-led** (default, for everyone): you and your practice decide what's Known. A word you
     mark Known, and a word that reaches Known through practice, stays Known until you choose
     Practice again. A later miss is saved in its history but does not change its status. Known
     words are **never scheduled** (no reviews, quizzes or plan focus); they may still appear as
     context inside reading or sentences.
   - **Practice-led**: only practice decides. Your Known marks are set aside, not deleted: Mark
     Known is hidden, a marked word is judged by its practice score, and a miss on a Known word
     can send it back to practice. Switching back to Learner-led brings the marks back.
2. **Switching modes changes the setting and nothing else.** The marks stay exactly as they are;
   the mode decides whether they are read.
3. **Practice again** reopens a word: it becomes Learning at typing level and grows back to Known
   through practice. **Start over** does the same from zero.
4. **Trust but verify goes away** (the Familiar status, the grace period, the probes). Existing
   Familiar words, pending or confirmed (293 on Duotopia), are read as marked Known.
5. Entry points: the **word editor** and **bulk select** on every client, and in **Vocab Quiz only**
   a secondary toolbar item **Mark Known**, always enabled. Mark Known shows only in Learner-led.
6. A word that was Known once but had already slipped back to Learning before this ships stays
   Learning. Nothing tells you when you miss a Known word in the first release (the word's history
   shows it).
7. **Everything ships in one release**, the setting and Practice-led included.

**What Practice-led keeps from today.** A word Known by score is already left out of the quiz, the
due lists and the review plan today (`VocabQuizSourcePolicy.cs:42-44`,
`VocabularyProgressRepository.cs:394,430`, `SmartResourceService.cs:279`); the 60-day date is used
only by Cloze's due mode and Sam's focus. So Practice-led differs from Learner-led in two things:
your marks are not read, and a miss on a Known word, met inside another activity, can drop it to
Learning.

## The model: the mark, the score and the mode are separate

- **The score** (`MasteryScore`, streaks, counts, schedule) always holds what practice earned. No
  mark and no mode changes how it is calculated. A miss on a Known word still lowers the score
  underneath, so the history stays honest.
- **The mark** is the learner's own state for the word, kept in the existing `VerificationState`
  column (an integer; no new progress column):

| Mark | Value | Meaning |
|---|---|---|
| None | 0 | no mark |
| Pending | 1 | old Familiar: read as Marked Known |
| Confirmed | 2 | Marked Known by the learner |
| Demoted | 3 | old failed probe: read as no mark |
| KnownByPractice | 4 (new) | was Known in practice while Learner-led: stays Known |
| Reopened | 5 (new) | Practice again, at `UserDeclaredAt` |
| StartedOver | 6 (new) | Start over, at `UserDeclaredAt` |

- **The mode** is the learner's setting. It is read, never written into the rows.
- **One rule for Known**, in shared code, used everywhere and always given the learner's mode: a
  word is Known when, in Learner-led, it carries a Known mark (Confirmed, Pending,
  KnownByPractice), **or**, in either mode, its score meets today's rule
  (`VocabularyProgress.IsKnown`, with its high-confidence case). The rule exists once for in-memory
  use and once as a query expression for both databases. The row's own `IsKnown` and `Status`
  properties, which cannot know the mode, are replaced by it; with about 165 references in 38
  source files and 25 test files, the compiler finds every reader. A guard test fails if any other
  file tests `MasteryScore >= 0.85` or the old Familiar flags on its own.
- **Never scheduled** comes from every selector skipping Known words through that rule, not from
  changing review dates.

### What each action writes

- **Mark Known** (Learner-led only): mark = Confirmed, `UserDeclaredAt` = now. Nothing else.
- **Practice again**: mark = Reopened, `UserDeclaredAt` = now; score set to 0.5 with streaks at zero
  and the review interval and ease reset (interval 1, ease 2.5), so the word is due at once and is
  asked by typing first (0.5 is the quiz's typing threshold, `VocabularyQuizItem.cs:46-48,128-130`;
  a miss drops it to multiple choice). History and counts are kept.
- **Start over**: mark = StartedOver, `UserDeclaredAt` = now; score, streaks and schedule to zero.
  History and counts are kept.
- **An answer in practice** (live scoring only): the score is calculated as today. Then, in
  Learner-led, a word that was Known before the answer or is Known by score after it, and has no
  Known mark, gets KnownByPractice. This is how earned Known becomes lasting, and how the 382 words
  Known by score today are covered, each the first time practice meets it. In Practice-led nothing
  is stamped. The shared scoring entry point (`ApplyAttemptScoring`) takes the mode as a required
  argument, so a call site that forgets it does not compile.
- **Replays** (`RecalculateInto`, in the sync merge, duplicate merges and repairs) never need the
  mode. They apply one more event: a Reopened or StartedOver mark resets the score at
  `UserDeclaredAt` with the same numbers as the live action, so a replay and the live path agree.
  Once the word is Known again and stamped, the reset is spent.
- **Switching the mode**: the profile's setting. No progress row is written.

A consequence of leaving marks alone: a word stamped Known by practice in Learner-led is Known
again when you return to Learner-led, whatever happened to it in Practice-led. Practice again
reopens any you want to work on.

### Sync and merge

- The learner's own actions (Mark Known, Practice again, Start over) merge newest-first by
  `UserDeclaredAt`, as declarations do today (`VocabularyProgressConcurrentMerge.MergeDeclaration`).
  One side having no mark never erases the other's.
- The automatic stamp never beats a learner's action it did not know about: stamped on an unmarked
  row it carries no `UserDeclaredAt`, so any learner action is newer; stamped over Reopened or
  StartedOver (the word grew back) it takes the answer's time and competes like any action.
- Fix needed in the merge: when an upload's counts already cover the stored row, the code keeps the
  uploaded score whole and merges only the flags (`VocabularyProgressConcurrentMerge.cs:71-87`).
  With a newer Reopened or StartedOver mark on the stored row, that would bring back the old score
  and undo the reopen. Such an upload takes the replay path.
- A Known mark needs no score change, so a miss recorded on another device cannot undo it.
- The contract (mark values, the Known rule, the reset numbers, the merge rules, the mode field)
  goes in `docs/sync-all-tables-plan.md` for every client, the future Flutter port included.

### The mode setting

- `UserProfile.LearningMode` (`LearnerLed` | `PracticeLed`, stored as text, default `LearnerLed`):
  one new profile column, the only schema change (PostgreSQL and SQLite migrations).
- The native apps receive the profile by download only and edit it through the profile API
  (`SharedSyncRegistration.cs:28-29`, `ProfileEndpoints.cs`), so the field is added to that API;
  the web and MAUI save it with the profile as they save the other settings.
- Each place that decides Known reads the mode from the learner's profile: once per page or
  screen, once per query on the server. A device that has not yet received a mode change keeps
  reading the old mode until it syncs; nothing it writes meanwhile can damage a mark.

## Selectors and counts to move onto the Known rule

Every reader takes the rule and the mode. These decide by score, zero attempts or review date on
their own today, and a word marked Known with no practice is the case to test in all of them:

- Dashboard and list counts, where such a word would count as New and Known at once
  (`VocabularyProgressRepository.cs:322-337`, `VocabularyProgressRepository.Queries.cs:356-372`);
  the web list's filters and stats (`Vocabulary.razor`).
- Due lists and smart resources (`VocabularyProgressRepository.cs:394,430`;
  `SmartResourceService.cs:279,306`).
- Plan focus: the never-practised tier, the weak tier and the least-recently-practised fallback
  (`VocabularyFocusResolver.cs:117-118,209-232`); plan freshness (`ProgressService.cs:1380-1391`).
- Vocab Quiz: the source policy, the focus quiz's fixed set, and a resumed session's saved batch
  (`VocabQuizSourcePolicy.cs`, `VocabQuiz.razor`, `NativeQuizPresenter.Session.cs`,
  `NativeQuizPresenter.cs`): a word marked Known since the set was built is dropped at start and
  on resume.
- Matching's focus set, Cloze's due mode, and the word pools of Word Association, Shadowing,
  Translation and Writing, on the web/MAUI services and the native presenters. A Known word may be
  context in a sentence; it is not chosen as the tested word.
- Sam's counts and status filter (`VocabularyDueSummaryTool.cs`, `SamReadToolResults.cs`,
  `CoachToolFactory.cs`); `VocabularyWordDetailTool.cs:62` reports `IsUserDeclared` as "the
  learner added this word", which is wrong today and is corrected.
- Every activity's "nothing left to practise" state is checked with most words Known.

## Removing trust but verify

- `LearningStatus.Familiar` is no longer produced; Familiar counts and badges fold into Known.
- The probes go: `HandleVerificationProbeResultAsync` and its callers in the web VocabQuiz,
  WordAssociation, Cloze, VocabMatching and Writing pages, the native Matching, WordAssociation,
  Cloze and Writing presenters, the native quiz sentence committer and `ExtractAndScoreVocabulary`.
- The grace-period filters go (the Cloze, Word Association, Shadowing, Translation, Writing and
  Matching pools on web/MAUI and native, `VocabQuizSourcePolicy`, `VocabularyProgressService`).
- The web quiz's separate 14-day requalification write (`VocabQuiz.razor:2484-2489`) is reviewed
  with the quiz change.
- Strings (English and Korean), the test files, and Sam's Familiar wording.
- The status endpoint (`POST /api/v1/vocabulary/{wordId}/status`) has no caller; it is kept in step
  with the service (Known, Practice again, Start over) and nothing more.

## Older app versions

Every client ships together (web and API, MAUI, native). An older build still running cannot
destroy a mark: the server's merge never lets an unmarked row erase one, and a Known mark does not
depend on the score an old build might lower. An old build would show a marked word by its score.

## Where the learner sets it

1. **Word editor**, every client: Mark Known (Learner-led only), Practice again, Start over, and a
   line saying who decided ("You marked this Known", "Known through practice"), in words, never
   colour alone. SwiftUI Picker or Menu on iOS, Compose on Android, the existing select on the web
   and MAUI.
2. **Bulk select**: the web's bulk status gets the new choices and writes in one batch (today it
   makes one call per word). iOS and Android have no multi-select in the vocabulary list today;
   they need it (SwiftUI List selection; Compose selection mode).
3. **Vocab Quiz**, every client: the secondary toolbar item **Mark Known**, always enabled in
   Learner-led. One command: an answer already submitted is saved first (an answer being graded
   finishes), the current word is marked Known, it leaves the session, the session is saved, and
   the next item shows. If nothing was submitted, no answer is recorded.
4. **Settings > Learning**, every client: Learning mode, with one true line for each choice.

## Learning Value Gate

`.squad/skills/learning-value-gate` applies twice: to the shared rules, which change the word
pools and empty states of every activity, and to the quiz toolbar item. No prompt or response mode
changes, so no native-to-native state can arise; the gate's six artifacts cover the pools, the
defaults and the empty states.

## Tests

- The Known rule in memory and in SQL on SQLite and PostgreSQL, for every mark in both modes, with
  and without practice; the guard test.
- Mark Known, Practice again, Start over and growing back; live scoring against a replay of the
  same history (they must agree); a marked word with a miss stays Known in Learner-led and follows
  its score in Practice-led; switching modes writes no progress row.
- Merges: a mark made on one device and a miss on another; Practice again on one device and a stale
  Known row on another; an automatic stamp against a learner's action.
- Every selector with a word marked Known and never practised, and with most words Known.
- Two learners on SQLite and PostgreSQL: marks and bulk marks act only on the learner's own rows,
  and each learner's mode is read for their own words only.
- The profile column on both providers, EF discovery on scratch SQLite, the MAUI upgrade, the
  validation scripts; the profile API field.
- Clients: presenter tests, SwiftUI view tests, web UI tests; then the running apps (simulator,
  emulator, Playwright).

## Phases (one release)

| # | Work |
|---|---|
| 1 | The mode and the shared rules: the profile column and API field, the marks, the one Known rule and every reader on it, live stamping, Practice again and Start over, the merge fix, every selector, trust but verify removed, the gate for pools and empty states |
| 2 | Word editor on every client |
| 3 | Bulk select: the web's choices and batch write; multi-select on iOS and Android |
| 4 | Vocab Quiz Mark Known toolbar item, with the gate |
| 5 | Settings > Learning on every client |
| 6 | Release: review, the running apps on every client, production backup (the migration), deploy, installs, each with the Captain's go |
| Later | Sam ("mark these Known", "practise 달 again"), Mark Known at import, a "missed while Known" filter |

Every phase is reviewed before any device round.

## History of this plan

Revision 1 wrote the mark into the score fields (mastery 1.0 for a marked word), made the score
calculation and replays depend on the mode, and rebuilt words from history when the mode changed.
An independent review (2026-11-14) showed, with the code:

- The sync merge can keep another device's score while keeping the mark, so a marked word could
  end up marked Known with a Learning score, or a reopened word could come back Known.
- A mode-aware replay would promote words that are Learning today (Known once, missed since).
- Rebuilding from history loses data for words whose history is partial, and cannot reproduce
  scores that were set outside the history (the old probe's 1.0).
- "Reopened" shared one stored state with the existing Learning and Unknown choices.
- Words marked Known with no practice would still be picked as New, never practised or due.
- Plan-focus quiz and matching sets ask every word they were built with.
- The native apps cannot upload a profile change; the mode needs the profile API.
- The 60-day description of today's behaviour was wrong.

Revision 2 kept the mark and the score separate, which removed the first three, but still rewrote
marks on every device at a mode switch so that readers would not need the mode. Revision 3 drops
that rewrite: the mode is read wherever Known is decided, and a switch writes nothing.
