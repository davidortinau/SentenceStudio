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
| 27 | Vocabulary word editor: Delete and Save follow what the server's owner-checked sync accepts. Delete is offered only for a word the server would delete too (recorded to the learner; no link, progress, sentence, minimal pair or phrase of another learner or of nobody; no Minimal Pairs answer), otherwise it is disabled and the editor says why. Save needs what the server applies to a word update (the word recorded to the learner, one of their resources linking it) and keeps the 2026-10-20 rule (`a09596c3`) that no one else links, practises or wrote a sentence for it; others' minimal pairs and phrases do not stop an edit. A sentence is the learner's only when it is recorded to them, in the list, the strength badge and both rules Review 4 (`922076f6`) and the one runtime rule (`b88a4dd4`, Captain 2026-11-06): whose the word is follows the recorded owner only (docs/native-port-backports.md, below B69), so a word recorded to no one, even one only this learner uses, opens read-only with its memory aids hidden and cannot be deleted until the server backfill records it and a sync brings the owner down, and a sentence is the learner's only when it is recorded to them; Save may remove every link while progress remains and the word stays editable; Delete calls the shared delete once and maps its answer, and a refused delete refreshes the editor | Delete removed every account's links and the word, hid failures and always said "Vocabulary word deleted!" (B66); Save had no owner rule | Keep (the Captain approved the matching web/MAUI fix, B66, 2026-11-05) |
| 28 | Find Duplicates does not merge a group that holds a copy not recorded to the learner (another learner's, or an older word recorded to nobody). The group says it can't be merged here, that each copy can be opened but only the learner's own can be edited, and any other reason the source gives; it gives no "Edit before merge" advice, since editing cannot unblock it Review 4 and `b88a4dd4`: "not the learner's" means not recorded to them (a copy recorded to no one blocks the group for everyone), and the device shows copies exactly as the shared scan masks them (memory aids and audio only), the same as the web | Merged it: copied that copy's memory aid, image and audio into the keeper and deleted the copy (B69) | Keep (the Captain approved the matching web/MAUI fix, B69, 2026-11-05) |

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
| 34 | A result row opens the word editor above the results, and Back returns to them. That editor's Find Duplicates goes where the source goes: the Library returns to its list and opens Vocabulary with the duplicate panel focused on the word. Back from there returns to the Library list, not to the import results (the import page closed when the Library returned to its list). The editor and results stay until the Vocabulary page has opened; when it cannot open (another Library action running, an untrusted owner, a failed open) the editor stays and says so (review fix 2026-11-06). While the Vocabulary page opens, the editor's Save, Delete and Close wait, so the release cannot cut them short | `/vocabulary/edit/{id}`; Find Duplicates navigates to the Vocabulary page, and browser Back returns to the import page | Captain 2026-11-06 ("ok"): route Find Duplicates to the Vocabulary page; Back to the Library list is the simpler correct return |
| 35 | When the owner's resources cannot be read for "Add to existing resource", the page says so with Retry. Typed or previewed content is kept while the page is open: Study's Add resource (which would return the Library to its list) shows the page instead; other entries (Sam's review quiz, a Study insight link) are not blocked and leave the page as it is under them. After a commit the resource list is read again, so Import more then Add to existing offers the new resource | The select is silently empty; leaving the page drops the draft | |

Android (2026-11-07) runs the same coordinator and follows 30-35. Since the Android tab shell (below)
the page opens from the Library's quick-add grid, as on iOS, and after Find Duplicates, Back from the
Vocabulary screen returns to the Library list. The page is one column at every width (a lazy list),
and View resource opens the resource editor; closing it returns to the results.

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
| 41 | ~~Imports started from a channel page go through the API's import endpoint, which takes only the link and the language, so they are not linked to the channel (from the md width the history shows "Manual import" for them, and the channel's discovery list still finds their status by video)~~ **No longer a departure** (Captain, 2026-11-06: "Address the channel linking"): Import Selected sends the channel's id, and the API links the import to it only when it is the signed-in owner's channel (a missing or another owner's channel is refused and nothing is created); Retry keeps the import's stored link (B79) | Each import records its channel (`MonitoredChannelId`) | Resolved; matches the source |
| 42 | Opening the page does not mark the owner's stale imports failed; the server's worker does that on its schedule. Until then a stale row shows Stuck with Retry, as the source shows it before its cleanup | Opening the page marks imports stuck for over 10 minutes failed | |
| 43 | A video's selection is a switch (iOS has no checkbox), and the found videos are a lazy list in the page's scroll | A checkbox per video, in a box 50% of the window high that scrolls on its own | |
| 44 | Below 420 points Check for New Videos and Import Selected stack instead of sharing a row; the status badges, Retry and Select All New keep their width and a video's title truncates (bridge 0.1.0-preview.43 `WithFixedSize`) | One row of buttons that shrink; badges do not wrap and the title truncates | |
| 45 | Hard-coded English on the channel page (the resolution, import and retry errors and toasts) uses resources; the new `YouTubeChannel_*` keys have English and draft Korean (B78) | English only | |

Android (2026-11-07) runs the same coordinators and follows 36-45; since the Android tab shell the page
opens from the Library's quick-add grid ("YouTube Import"), as on iOS. Other differences: Add Channel
and Refresh are in the header's overflow menu at every width; the three tabs
are equal buttons whose labels wrap; a video's selection is a checkbox (as in the source), and the
channel column of the history shows at every width; View resource opens the Resources tab's editor
and closing it returns to the page.

## Conversation (iOS, 2026-11-06)

Source: `src/SentenceStudio.UI/Pages/Conversation.razor` (`/conversation`). Voice mode comes from
the Techorama demo branch (`techorama/next-gen-demos`, `Conversation.Voice.razor.cs`), which was
never merged; on this branch it exists only in the native heads (Captain, 2026-11-06: "I want the
voice realtime but with native UI on iOS. Don't bring in web ui"). The shared presenter, session
and API endpoint are platform-neutral. Android offers voice since 2026-11-07 (`AndroidVoiceAudio`:
AudioRecord with the voice-communication source and the echo canceller when the device has one,
AudioTrack playback, PCM16 24 kHz, half duplex as on iOS; the system microphone dialog on first
use; departures 48-50 as on iOS).

Android follows 46 and 47 since 2026-11-07: the scenario is a header icon menu (Material
"theater_comedy" vector drawable; check on the current one) beside the language menu, and the
composer is a round quick-phrases button (Material "format_quote") before a capsule
`OutlinedTextField` whose keyboard Send action sends. Material's core icon set in the Compose
binding has neither icon, so both ship as vector drawables in `Resources/drawable`.

| # | Departure | Original behavior | Decision |
|---|---|---|---|
| 46 | Scenario is a toolbar menu (theater masks; the current scenario has a checkmark) beside the language menu | A "Scenario:" picker above the chat | Captain 2026-11-06: "the Scenario should be a toolbar option and kept out of the way" |
| 47 | Messages-style composer: a round quick-phrases button, then a capsule text field. Return sends; there is no Send button and no card around the input. The quick phrases open above the composer | A card with the field, the quick-phrase toggle and a Send button | Captain 2026-11-06: "Let's not use the Send button at all ... depend on the keyboard return"; phrases "before the input field similar to the + icon in the native Messages app" |
| 48 | Voice mode: a waveform button inside the field starts a live spoken conversation with the scenario's partner. While it runs a voice panel (status, input level, End voice) replaces the composer; spoken turns join the chat with a microphone marker and stay after voice ends; the partner's vocabulary lookups show on its turn. Voice turns are not graded or scored and nothing is saved, as in the demo | Not in the source page (the demo page had a Voice button beside Send) | Captain 2026-11-06: voice button "like the Messages app as well, with the sound wave icon button inline with the message field" |
| 49 | Voice starts by having the partner read its last message aloud, word for word; that message keeps its bubble and no new one is added. After a learner turn (a reply that failed), the partner answers in its own words | The demo's partner spoke first with a new greeting | Captain 2026-11-06: "when I enable speech I want ai to read the initial message not add a new message right away" |
| 50 | A voice pace menu (sliders icon) in the voice panel: speaking speed 100%, 90%, 80% or 70%, and the pause before the partner answers, 0.5 s, 1 s, 1.5 s or 2.5 s (the silence that ends the learner's turn). Kept per learner on the device; a change applies to the running session from the partner's next turn. Defaults are the provider's (100%, 0.5 s) | No pace setting | Captain 2026-11-06: "Is there an option to slow the speech rate and increase the delay between me speaking and when ai speaks?" (bridge 0.1.0-preview.44: menu sections) |

## Tab shell trial (Android, 2026-11-07)

The Captain's navigation trial (iOS 2026-11-05: `7a09daaf`, `566817d9`, `0a8e1b16`, `25fef602`) on
Android (Captain 2026-11-07: "Follow iOS trial"). The Dashboard More menu is gone; every destination
it held stays reachable: Study (the Dashboard; its header has History, Regenerate while a plan is
shown, and Refresh), Library (a list of Resources with its count, Vocabulary, Skills and Minimal
Pairs, then Quick add: a new resource, skill profile or minimal pair, Import Content, YouTube
Import), Settings (the profile row with name and languages first, the settings and Sam's settings,
Sign out last) and Sam's slot. Sam's slot shows for every signed-in owner; when Sam is not offered
it reads availability again and says why in a dialog (not offered, offline, not reachable). On
screens with the tab bar Sam is reached through his slot only; screens without it (editors,
activities, the quiz) keep the header entry, and the floating button is not used. Each tab keeps its
open page while another tab is shown, and the tab bar hides in editors (the resource, word, skill
and channel forms, the import word editor), activities and the quiz. Code:
`AndroidSliceShell.Tabs.cs`, `AndroidSliceShell.Tabs.Compose.cs`.

Differences from iOS:

| # | Android | iOS | Why |
|---|---|---|---|
| 51 | Material 3 navigation bar with four items; Sam's slot is the fourth, shown with his face icon and name, never selected | A tab bar with Sam as the trailing search-role slot (iOS 26) | Material navigation; Compose has no search-role tab |
| 52 | System Back on the Library or Settings root shows Study; Back on Study's root leaves the app | iOS has no system Back; each tab stays where it is | Material navigation guidance for bottom bars |
| 53 | Re-tapping the shown tab returns it to its root, on every tab | Re-tapping the Library tab pops to its list | One rule for all tabs |
| 54 | The Minimal Pairs list opens as the Minimal Pairs activity screen (no timer or plan item), so the tab bar hides on it; Back returns to the Library list | The list is a Library page that keeps the tab bar | Android hosts the presenter as an activity; no second host was added |
| 55 | Sam's "checking" state is a dialog with Cancel; the answer is a dialog with Close | A busy overlay with Cancel, then an alert | Compose dialog; same texts, same 300 ms delay and 10 s limit |
| 56 | The Library reads the device's resource list again on every visit (the Resources row's count) | Read when never read, after a failed read, or when a sync came after the last read | The Android coordinator has no freshness check; the read is local |
| 57 | New-learner "Add a Resource" on Study opens the Library tab on Resources with the new-resource form; when the Library is keeping a page open (it may hold a pasted import), that page shows instead and nothing is discarded | Opens the resource form from Study | The Resources page belongs to the Library tab on Android |
| 58 | App icon and launch screen are the MAUI head's (studio_icon.png on #0c0b10; the splash draws it the size MAUI's Android splash does). While a remembered session is restored the launch look stays (no Sign In or "Syncing Your Profile" frame), then Study shows; after one second a progress indicator appears under the logo. The adaptive icon has no monochrome layer, so Android 13+ themed icons show the full-color icon | MAUI also uses the opaque artwork as the monochrome layer (a blank themed icon) | iOS `bcd8b731`, `25fef602`; a transparent glyph would allow a themed icon |
| 59 | Editors put the keyboard away with system Back (or the keyboard's own hide key); Back with the keyboard down leaves the page as before | iOS added drag-into-the-keyboard dismissal (`a8f2a81f`) because a multi-line field's Return adds a line and iOS has no Back | Android's Back always dismisses the keyboard first; checked on Import Content, the resource form and the skill form |
| 60 | The status-bar and navigation-bar icons follow the app's theme (Settings Light or Dark), not only the device mode; the launch look always uses light icons | The window theme followed the device mode only, so a Dark app on a light device had dark icons on a dark page | Found during the launch work, 2026-11-07 |

## Vocabulary list layout (iOS, 2026-11-09)

Source: `src/SentenceStudio.UI/Pages/Vocabulary.razor`. The Captain asked for these on the
Duotopia (wide) after the source's desktop controls stacked into one tall column there
(Captain 2026-11-09: "this looks completely wrong on my Duotopia device when viewed wide").
Code: `src/SentenceStudio.Native.iOS/Features/Vocabulary/VocabularyListView.cs`, the in-place
update in `VocabularyListUpdate.cs`, and the shared sort draft and column flag in
`src/SentenceStudio.Native/Features/Vocabulary/VocabularyBrowser.cs`. Bridge
`Microsoft.SwiftUI` 0.1.0-preview.45 added `WithFixedWidth` for the column and preview.46 the
accessors that let a toolbar button change its symbol, label and selected state in place.

| # | Departure | Original behavior | Decision |
|---|---|---|---|
| 61 | Filters is a toolbar button at every width: a phone opens the filter sheet; from 768 pt it shows or hides the filter column (selected while shown; the choice is remembered per learner). Its icon is filled while a search or filter is on, and now updates as soon as one is | The filter button was phone-only; wide pages had the filters inline. The phone icon's fill did not change until the page was rebuilt | Captain 2026-11-09 |
| 62 | Grid/list is one toolbar button from 768 pt, showing the layout a tap switches to ("List view" or "Grid view"). A phone has no switch (it always shows one column) | Two-button group above the filters (wide only) | Captain 2026-11-09 |
| 63 | The total and status counts are one row of badges above the results that scrolls sideways when it does not fit (large text, a narrow window). This returns to the source for the counts; the active-filter chips also use one sideways row | The web row is one sideways-scrolling row (`flex-nowrap overflow-auto`) with the sort and view buttons at its end, and its chips wrap. The earlier native port stacked counts, sort and view buttons into one column under 940 pt or at large text | Captain 2026-11-09 |
| 64 | Added/Updated sorting is one of the filter options: first in the wide filter column (applied at once) and first in the phone sheet, where it is applied with the filters by Apply and dropped by Cancel. A phone can sort for the first time | Sort buttons beside the counts (wide only); a phone could not sort | Captain 2026-11-09 |
| 65 | From 768 pt (window width) the page is two columns: a fixed filter column (a third of the page, 280 to 400 pt, wider with larger text; sort, every filter, Clear All Filters while one is on) that scrolls on its own, a divider, then the results: the search field stays put above their own scroll, which holds the counts, the active-filter chips (one sideways row) and the word cards in a lazily built grid. The page measures its own width, so an iPad sidebar narrows the column and the cards rather than squeezing them. Cards are two or three across by the results' width and text size, one in list view; changing the sort or grid/list starts the results at the top | Search, counts, sort, view buttons, a two- or four-column grid of filter menus and chips stacked above the cards, all in one page scroll | Captain 2026-11-09 |

The phone layout is otherwise unchanged. Android's Compose screen still has the earlier wide
controls; matching it is a separate change.

## Vocabulary word editor and every editor's Save (iOS and Android, 2026-11-10)

Source: `src/SentenceStudio.UI/Pages/VocabularyWordEdit.razor` and
`src/SentenceStudio.UI/Components/ReferenceSentencesSection.razor`. The Captain reviewed the
editor on Duotopia (2026-11-10): "the Save button in the toolbar appears as a primary and
secondary option", "we don't need duplicate cancel/save buttons at the bottom of the page",
"the resource associations list is long", and "we are completely missing the sample/example
sentences feature of this page". Code: `src/SentenceStudio.Native.iOS/Features/Vocabulary/VocabularyWordEditView.cs`,
`VocabularyWordEditView.Examples.cs`, `VocabularyCoordinator.cs`; shared state in
`src/SentenceStudio.Native/Features/Vocabulary/VocabularyBrowser.Examples.cs`; owner-scoped
writes in `src/SentenceStudio.Native/Adapters/NativeResourceStore.VocabularyExamples.cs`.

| # | Departure | Original behavior | Decision |
|---|---|---|---|
| 66 | Every editor (the word, resource and skill forms, on iOS and Android) has one Save, a plain text toolbar action at every width (iOS: last; Android: before the overflow menu); the More menu keeps only the other actions (the word's Find Duplicates and Delete, a resource's or skill's Delete) and is not shown when it would be empty (adding a resource or skill). There is no Save or Cancel under the form; Back leaves without saving | The page header shows a filled primary Save from 768 pt and a Save in its More menu at every width; the add pages also end with a full-width "Save Resource" / "Save Skill Profile". The earlier native ports also put Cancel (word, skill) and Save (word) under the form | Captain 2026-11-10 ("other editors should also get the same Save treatment") |
| 67 | Resource Associations lists only the word's own resources, each with a Remove button, and Add to Resource (a menu) lists the learner's other resources. A smart resource is shown without Remove and is never offered. Adding and removing wait for Save, as before | Every resource of the learner as a checkbox list (scrolling at 400 px) | Captain 2026-11-10. The source add form is unchanged: native has no add-word form |
| 68 | Reference Sentences are back, as the source has them: Generate (four AI suggestions for the stored word in its language, English translations; each can be edited, then kept or discarded), an add row (sentence, translation, register; the word's first sentence becomes its core one), and the learner's sentences, core first, with Set as core example, Edit, Flag and Delete in a menu and their Core, source, register, level and Flagged badges. Each change is saved at once, apart from the word's Save, and the strength label follows the count. Results show as a line in the card instead of a toast. The core sentence's menu reads "Remove as core example" | The source labels both states "Set as core example" (its star icon shows which). The earlier native port only listed the sentences, read-only, and only when the word had some | Captain 2026-11-10 |

Android follows rows 66-68 (Captain 2026-11-10: "Android should also get these changes") on the
same shared browser: `src/SentenceStudio.Native.Android/AndroidSliceShell.VocabularyExamples.Compose.cs`,
the resources card in `AndroidSliceShell.Vocabulary.Compose.cs`, and the app-bar actions in
`AndroidSliceShell.VocabularyDuplicates.Compose.cs`, `AndroidSliceShell.Skills.Compose.cs` and
`AndroidSliceShell.ResourceForm.Compose.cs`. Its badges wrap onto more lines (`FlowRow`) where
iOS scrolls one row sideways.

## Library browse pages: Add (+) and the Resources layout (iOS, 2026-11-10)

The Captain (2026-11-10): "resources and skills lists pages have add buttons with button styling
in the toolbar/navbar space, and vocabulary has no add button. I want to use the toolbar/sidebar
space we have for things like the filter icon on vocabulary list page to include an add option
(plus icon). That should be consistent across these pages. And I want to revise the resources
list page to use the same layout and filter/sort option UX as we have on the vocabulary list
page." Sources: `src/SentenceStudio.UI/Pages/Resources.razor`, `Vocabulary.razor`,
`VocabularyWordEdit.razor` (add mode, `Id == "0"`). Code: the shared layout
`src/SentenceStudio.Native.iOS/Features/Browse/BrowserPage.cs` and its in-place updater
`BrowserPageUpdate.cs`; `Features/Vocabulary/VocabularyListView.cs`,
`Features/LearningResources/LearningResourcesView.cs`,
`LearningResourcesCoordinator.ListOptions.cs`, `Features/Skills/SkillsView.cs`; shared rules in
`src/SentenceStudio.Native/NativeResourcePresenter.ListOptions.cs` and
`Features/Vocabulary/VocabularyBrowser.cs` (`OpenNewWordAsync`).

| # | Departure | Original behavior | Decision |
|---|---|---|---|
| 69 | Resources, Skills and Vocabulary each have Add as the same plain "+" toolbar icon, last in the toolbar, at every width (no tint, no filled button) | Each page header has a filled primary Add button and an Add item in its More menu; the earlier native ports showed a tinted "+" on a phone and a filled button from 768 pt on Resources and Skills, and nothing on Vocabulary | Captain 2026-11-10 |
| 70 | Vocabulary's "+" (and "Add your first word" on an empty page) opens the source's add-word form: title "Add Word", one plain "Add" toolbar action, no More menu, no Find Duplicates, encoding strength, Reference Sentences, progress or history; every resource of the learner as a checkbox (smart resources shown but not choosable); a new word starts in the learner's target language (Korean when none is set) as a Word, and when the search found nothing its words start the target term (as the source). The "+" waits while the list loads (it reads the resources to choose from), and an open form follows a reload's resources. Add refuses a duplicate of the learner's own word and keeps the form | Same form; the source's header also has an Add item in its More menu, and its add form lists the resources as checkboxes | Captain 2026-11-10 (row 67's selected-only list stays the edit form's) |
| 71 | The Resources list uses the Vocabulary layout: phone, one column with the search row, then the count, sync line and cards or list; Filters opens a sheet with Sort (Added/Updated), Type and Language, applied together by Apply and dropped by Cancel. From 768 pt, a fixed filter column (Sort, Type, Language, Clear All Filters while one is on; applied at once) beside the results, whose search stays put above their own scroll. The toolbar holds grid/list (at every width, since the phone list differs from its one-column grid), Filters (filled while a type or language filter is on) and "+". Smart resources stay first; the rest sort by Updated, newest first, by default (the source's order); the sort, the column's shown or hidden state and grid/list are remembered on the device. The search row's clear button clears the search and the filters | Search, then a row with two filter menus and the grid/list button group, all in one page scroll; no sort | Captain 2026-11-10 |

## Library as a split view (iOS, 2026-11-12)

The Captain asked to restructure the Library tab as a split view for the iPhone Duo and iPad while
slab iPhones keep working as before ("yes, restructure the library tab as split view. Keep in mind
we also run this on traditional slab iPhones so it needs to continue working well there"). He chose
the layouts from the mockups at https://claude.ai/artifact/VhbBTqawm9DysM2CwHWfeP: an adaptive
sidebar (C), tabs kept in the tab bar with a Library-only drawer, and a resource's details first
leading into its words (G, then H). Code: `src/SentenceStudio.Native.iOS/App/SceneNavigation.cs`
(`LibrarySplitNavigation`), `App/ApplicationScene.Library.cs`; bridge preview.47
(`NavigationSplitView`, `SwiftTabViewStyle.TabBarOnly`) and preview.48
(`WithTabBarHiddenInCompactWidth`).

| # | Departure | Original behavior | Decision |
|---|---|---|---|
| 72 | The Library tab is a three-column split view: its sidebar lists Resources, Vocabulary, Skills and Minimal Pairs (and quick add), the open place's page fills the middle column, and the open item (a word, resource or skill editor) fills the right column; deeper pages push on the right column. The sidebar shows while nothing is open and tucks away once a place is open; its button brings it back over the screen. A compact width (slab iPhone, folded iPhone Duo) collapses it to the original stack: the Library list, then the page, then the editor, with Back as before | One stack: the Library list pushes a page, the page pushes its editor | Captain 2026-11-12 |
| 73 | The app's tabs use the tab-bar-only style: no app sidebar on iPad; on the iPhone Duo the system places the tab bar at the side | Sidebar-adaptable tabs (an app sidebar on iPad) | Captain 2026-11-12 |
| 74 | Beside the list in a regular width, an editor keeps the tab bar; it still hides the tab bar in a compact width | Editors hide the tab bar | Captain 2026-11-12 (tabs stay in the tab bar) |
| 75 | With nothing open the Library's sidebar is shown (beside the columns where the width allows, otherwise over them); once a page opens it becomes a drawer behind its button. An open item has a Full screen button (beside the list only) that hides the sidebar and list so the item fills the window, and brings them back | No split view | Captain 2026-11-12 ("I expected the drawer to initially be a fixed sidebar and then be an offscreen drawer once something was selected"; "a full screen button to focus on the form") |

## Kept exactly like the original (also your call)

| # | Behavior | Possible change | Decision |
|---|---|---|---|
| 25 | Writing clears text you typed while the previous sentence was being graded | Keep the typed text | Keep the clearing |
| 26 | Number Drill's "I was correct" fixes the session score but not long-term mastery | Also correct mastery | Yes, also correct mastery. Implemented (`c3836877`): the stored attempt becomes correct and SM-2 is recomputed as a correct grade; WebApp backport B37 |
