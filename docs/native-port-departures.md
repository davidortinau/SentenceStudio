# Native activity port: departures from the original app

Places where the native iOS and Android activities behave differently from the
original Blazor/MAUI app (`src/SentenceStudio.UI`). Most are bug fixes. The
recommendation is to keep all of them.

Captain: mark each row **Keep** or **Revert**. Rows left blank are kept.

Related: `docs/native-port-backports.md` lists the same bug fixes as work items
for the WebApp and MAUI app.

## Errors no longer save fake results

| # | Departure | Original behavior | Decision |
|---|---|---|---|
| 1 | Translation, Describe a Scene and Word Association: when AI grading fails, show an error and save nothing | Saved a 0% score or a wrong answer | Keep |
| 2 | Cloze: when the AI fails, show "could not generate" | Showed "No cloze sentences" | Keep |
| 3 | Conversation: errors are shown, and no empty reply bubbles appear | Failures were silent; an empty AI reply added an empty bubble | Keep |

## Data fixes

| # | Departure | Original behavior | Decision |
|---|---|---|---|
| 4 | Minimal Pairs uses the signed-in learner | Used user "1" for everyone, so a real learner's pairs never showed | Keep |
| 5 | Reading does not save the dictionary lookup word | Saved it reversed (English and Korean swapped) and with no owner | Keep |
| 6 | Diary checks the owner when updating an entry and saving feedback | No owner check | Keep |
| 7 | How Do You Say replays saved audio | Re-generated audio every time and added a duplicate history row | Keep |
| 27 | Vocabulary word editor: Delete and Save follow what the server's owner-checked sync accepts. Delete is offered only for a word the server would delete too (recorded to the learner; no link, progress, sentence, minimal pair or phrase of another learner or of nobody; no Minimal Pairs answer), otherwise it is disabled and the editor says why. Save needs what the server applies to a word update (the word recorded to the learner, one of their resources linking it) and keeps the 2026-10-20 rule (`a09596c3`) that no one else links, practises or wrote a sentence for it; others' minimal pairs and phrases do not stop an edit. A sentence is the learner's only when it is recorded to them, in the list, the strength badge and both rules Review 4 (`922076f6`): whose the word is follows the one ownership rule (docs/native-port-backports.md, below B69), so a learner's own legacy word is editable and deletable and shows its memory aids; a sentence recorded to nobody is its resource's, else its word owner's; Save may remove every link while progress remains and the word stays editable; Delete calls the shared delete once and maps its answer, and a refused delete refreshes the editor | Delete removed every account's links and the word, hid failures and always said "Vocabulary word deleted!" (B66); Save had no owner rule | Keep (the Captain approved the matching web/MAUI fix, B66, 2026-11-05) |
| 28 | Find Duplicates does not merge a group that holds a copy not recorded to the learner (another learner's, or an older word recorded to nobody). The group says it can't be merged here, that each copy can be opened but only the learner's own can be edited, and any other reason the source gives; it gives no "Edit before merge" advice, since editing cannot unblock it Review 4: "not the learner's" follows the one ownership rule, and the device shows copies exactly as the shared scan masks them (memory aids and audio only), the same as the web | Merged it: copied that copy's memory aid, image and audio into the keeper and deleted the copy (B69) | Keep (the Captain approved the matching web/MAUI fix, B69, 2026-11-05) |

## Behavior changes

| # | Departure | Original behavior | Decision |
|---|---|---|---|
| 8 | Number Drill shows a message for non-Korean target languages | Silently ran the Korean drill | Keep |
| 9 | Number Drill counts "Listen & Place" answers and shows feedback | Ignored them (0/0 summary) | Keep |
| 10 | Number Drill shows Disambiguate hints | A nesting bug hid them | Keep |
| 11 | Number Drill: Next during the 1.5-second pause no longer skips an item | Skipped an item | Keep |
| 12 | Number Drill summary fills in median time and "Patterns to Review" from your answers | Always blank | Keep |
| 13 | Cloze drops sentences where the word can't be blanked out | Showed them with the answer visible | Keep |
| 14 | Reading keeps the "audio generation failed" message visible | Hid it instantly | Keep |
| 15 | Conversation sends its opening instructions as system instructions | Combined prompt that Azure's content filter rejects (chat opened empty) | Keep |
| 16 | Shadowing applies Slow speed in the audio player | Slow was ignored (speech service has no speed) | Keep |
| 17 | Describe a Scene accepts https image links only | Accepted any link | Keep |
| 18 | Translation and Writing block moving to another sentence while a grade is pending | A late grade could land on the wrong sentence | Keep |

## Presentation

| # | Departure | Original behavior | Decision |
|---|---|---|---|
| 19 | Pop-up messages in several activities are inline banners | Toasts/pop-ups | Split by kind: errors banner, success/info toast (implemented 81c2cede) |
| 20 | Android Translation puts the answer box above the buttons | Answer box and buttons in one row | Keep |
| 21 | iOS Minimal Pairs delete dialog is titled "Delete A vs B?" | "Confirm Delete" | Keep |
| 22 | Video Watching transcript scrolls with the page | Fixed 300-pt scrolling box | Keep |
| 23 | Describe a Scene shows accuracy color as a badge | Colored left border | Keep |
| 24 | Hard-coded English strings have Korean translations | English only | Keep |
| 29 | ~~Vocabulary Quiz "Use in a sentence" shows the target word without its translation~~ **No longer a departure**: the WebApp/MAUI quiz now shows the target word only too (B70; Captain, 2026-11-06: "I shouldn't see the translation") | Showed the native-language term under the target word | Keep (Captain); all clients match |

## Import Content (iOS, 2026-11-06)

Source: `src/SentenceStudio.UI/Pages/ImportContent.razor`. The native page runs the same
shared `ContentImportService`; these are the presentation differences.

| # | Departure | Original behavior | Decision |
|---|---|---|---|
| 30 | The Import (commit) action is in the toolbar at every width (a plain toolbar button on a phone, the primary button on iPad) | `PageHeader` shows `PrimaryActions` only at 768 px and wider, so a phone-width web page or the MAUI phone app has no way to commit an import (B71) | |
| 31 | Errors and warnings are pinned over the top of the page until dismissed or the next action; success is a toast | Toasts fixed over the page | |
| 32 | The preview table is a list of row cards: the row checkbox, the status and duplicate badges, and the two term fields (side by side from 680 pt); a row's error is read by VoiceOver with its status badge | Table; the row error is the badge's hover tooltip | |
| 33 | Hard-coded English (the auto-detect gate, the Sentences option, OK/Warning/Error, the type and status badges) uses resources, and so do the service's detected format, warnings, row errors and result reasons (B74); new keys have English and draft Korean | English only (B73) | |
| 34 | A result row opens the word editor above the results, and Back returns to them. That editor's Find Duplicates goes where the source goes: the Library returns to its list and opens Vocabulary with the duplicate panel focused on the word. Back from there returns to the Library list, not to the import results (the import page closed when the Library returned to its list) | `/vocabulary/edit/{id}`; Find Duplicates navigates to the Vocabulary page, and browser Back returns to the import page | Captain 2026-11-06 ("ok"): route Find Duplicates to the Vocabulary page; Back to the Library list is the simpler correct return |
| 35 | When the owner's resources cannot be read for "Add to existing resource", the page says so with Retry. Typed or previewed content is kept while the page is open: Study's Add resource (which would return the Library to its list) shows the page instead; other entries (Sam's review quiz, a Study insight link) are not blocked and leave the page as it is under them. After a commit the resource list is read again, so Import more then Add to existing offers the new resource | The select is silently empty; leaving the page drops the draft | |

## YouTube Import (iOS, 2026-11-06)

Source: `src/SentenceStudio.UI/Pages/MediaImport.razor` (`/media-import`, `/import`). The video's
metadata and captions, the transcript cleanup and Polish with AI run on the phone through the same
shared services (`YouTubeImportService`, `TranscriptFormattingService`, whose AI goes through the
API gateway); Save as Resource writes through `LearningResourceRepository` as the source does.
Errors and toasts follow row 31.

| # | Departure | Original behavior | Decision |
|---|---|---|---|
| 36 | Hard-coded English (the fetch, download and save errors, the toasts, "Language:", "(auto)", "(auto-generated)", "Unnamed Channel", "Channel", "Untitled", the stuck hint) uses resources; the new keys have English and draft Korean (B78) | English only | |
| 37 | The three tabs share the page width equally and their labels wrap, as the web page's flex buttons do on a phone; a one-line label's button is shorter than a wrapped one, and neighbouring borders meet instead of overlapping (the SwiftUI bridge has no equal-height or negative-spacing layout) | Bootstrap `btn-group w-100`: equal-height buttons, overlapping borders | |
| 38 | Each caption track is its own choice, so of two tracks with the same language code (for example English and English auto-generated) the second can be chosen | The select's values are language codes, so choosing the second same-code track loads the first (B77) | |
| 39 | Save as Resource locks the page until it finishes (Polish, the link and the transcript wait; Back waits) | Only the Save button is disabled while saving | |
| 40 | My Channels, Import History and the channel page read and write the channels and imports through the API for the signed-in owner; finding a channel's videos and Retry Resolve run on the phone with the shared `ChannelMonitorService`, and the resolved channel is saved through the API. Offline, these show their load or save error | The page uses the services on its own database | |
| 41 | Imports started from a channel page go through the API's import endpoint, which takes only the link and the language, so they are not linked to the channel (from the md width the history shows "Manual import" for them, and the channel's discovery list still finds their status by video) | Each import records its channel (`MonitoredChannelId`) | Open: the endpoint could accept the channel, checked against the owner (an additive API change) |
| 42 | Opening the page does not mark the owner's stale imports failed; the server's worker does that on its schedule. Until then a stale row shows Stuck with Retry, as the source shows it before its cleanup | Opening the page marks imports stuck for over 10 minutes failed | |
| 43 | A video's selection is a switch (iOS has no checkbox), and the found videos are a lazy list in the page's scroll | A checkbox per video, in a box 50% of the window high that scrolls on its own | |
| 44 | Below 420 points Check for New Videos and Import Selected stack instead of sharing a row; the status badges, Retry and Select All New keep their width and a video's title truncates (bridge 0.1.0-preview.43 `WithFixedSize`) | One row of buttons that shrink; badges do not wrap and the title truncates | |
| 45 | Hard-coded English on the channel page (the resolution, import and retry errors and toasts) uses resources; the new `YouTubeChannel_*` keys have English and draft Korean (B78) | English only | |

## Kept exactly like the original (also your call)

| # | Behavior | Possible change | Decision |
|---|---|---|---|
| 25 | Writing clears text you typed while the previous sentence was being graded | Keep the typed text | Keep the clearing |
| 26 | Number Drill's "I was correct" fixes the session score but not long-term mastery | Also correct mastery | Yes, also correct mastery. Implemented (`c3836877`): the stored attempt becomes correct and SM-2 is recomputed as a correct grade; WebApp backport B37 |
