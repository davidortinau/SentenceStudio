# Management Pages E2E Tests

## 1. Learning Resources List (`/resources`)

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/resources` | Resource cards displayed with title, media type, language |
| 2 | Type in search box | List filters by title, description, tags and language as implemented by `LearningResourceRepository.SearchResourcesAsync` |
| 3 | Select media type filter | List filters by type |
| 4 | Click a resource card | Navigates to `/resources/edit/{id}` |
| 5 | Click "Add Resource" | Navigates to `/resources/add` |

### Required native source-fidelity Resources journeys (not yet accepted)

Use only owner-scoped disposable data for destructive/conflict cases. Capture
the original mapping IDs, transcript, translation, other-owner rows, and sync
revision before an edit; compare them after restart. A shared presenter test
does not replace these running-head checks.

| State | Action | Required observation |
|-------|--------|----------------------|
| Grid/list | Switch both list modes, filter against catalog choices with zero owned rows, restart | Grid is the default; `resources-view-mode` restores the chosen mode; all source type/language choices remain available; no-match retains Add and Starter actions |
| Full Add | Add an Article with nonempty title, description, type, language, URL, tags and long transcript/translation; save, navigate away/back, reopen | Every field round-trips exactly, including empty vs null untouched values; one owned row appears in sorted list, pending sync never claims remote confirmation early |
| Add vocabulary | Stage comma and tab rows; change one lexical type, remove another; import valid `.txt`/`.csv` and reject oversized/empty file; save | Only selected unique target/native rows and mappings persist; 1,048,576-byte file limit, busy/errors and cancel are observable |
| Full Edit | Edit all scalar fields and long transcript/translation on a resource with existing mappings, then save/restart; separately Cancel and simulate an overlapping downloaded change | Mappings/progress and other owner remain unchanged; cancel writes nothing; conflict preserves unsaved draft until explicit reload |
| Quick-add | Search existing owned same-language words; attach one; create another with native gloss; navigate/reopen | Current resource lists both full rows once; other-owner, `Other` language, smart and already-attached rows refuse mutation; keyboard focus/dropdown states are usable |
| Bulk/generation | Paste/file import and generate from an owned transcript through authenticated AI API | Duplicate terms skipped, real target-language words and mappings persist; unavailable/failed AI is an error, never a success toast |
| Unlink | Remove one vocabulary row on a non-smart resource | Mapping disappears, but shared word and its other-resource association remain; smart rows have no Remove |
| Starter | From an empty/no-match view create starter after profile languages are configured | Real target-language vocabulary is generated for the active owner once; progress and failures are visible; never insert source Korean placeholders for a different target language |
| Delete confirmation | Request deletion, Cancel, then confirm only on a backed-up disposable row | Cancel preserves resource, mappings and progress; confirm removes only the owned resource and eligible orphan progress, refreshes list, and shows pending sync; smart/foreign delete refused |

### Native iOS Resources current-subset smoke checks (NOT source parity)

The cases below verify only the previously implemented browse/detail/three-field
metadata subset. They do not approve omission of Add/Delete, grid/list
preference, full transcript/translation editing, vocabulary rows, quick-add,
generation/import, or source filters. Complete native acceptance also requires
the source states/actions in `Resources.razor`, `ResourceAdd.razor`, and
`ResourceEdit.razor`. New Android Resources expansion remains on hold;
already-created Android auth, Dashboard, Quiz, and shell corrections do not
wait for iOS acceptance.
The formerly tested `UINavigationController` implementation was historical
behavior evidence, **not** permission to retain UIKit navigation. Corrected
iOS Resources must render in SwiftUI, use SwiftUI-managed navigation paths,
and retain these same observable outcomes; a thin SwiftUI host around UIKit
content is not sufficient.
Use the signed-in canonical squad-jayne simulator and its populated owner data.
Never seed, delete, or patch that database to make a test pass. Capture the
selected non-smart row's original metadata and a consistent SQLite backup
before making a reversible edit.

| State | Action | Expected native result |
|-------|--------|------------------------|
| Top level | Open the authenticated app; select Resources, then Dashboard and Resources | Exactly two working bottom destinations with independent SwiftUI `NavigationStack` paths; no bottom navigation during sign-in, and the assigned quiz retains its full-screen SwiftUI presentation and return behavior |
| Populated | Open Resources | Owner's actual resources, smart first/alphabetical then user-owned by `UpdatedAt` descending; Grid cards show title, type, language and created date, while compact List rows hide type/language on phone and show them at the medium-width breakpoint. Both use native lazy rows; count and real filter choices remain |
| Search | Search separately by title, description, tags and language | Case-insensitive owned matches only; clearing search restores the current type/language selection and source order |
| Filters and no match | Select a source-catalog type and language, then search for an absent term; clear both | Only matching owned rows; distinct no-match message (not the true-empty message); clearing restores the fixture's original owner-scoped row count |
| Adaptive filters | In English and Korean, light and dark, test standard and accessibility-extra-large text; in an isolated preview, select long type and language values | Default and selected labels remain visually complete inside their buttons without clipping or overlap; menu rows and selected values expose full accessibility text; each button is at least 44×44 pt, stays within portrait bounds, and search, keyboard and results remain usable |
| True empty and load error | Exercise isolated presenter fixtures, not the canonical database | Empty-only owner shows true empty; failed DB read shows retryable load error, not zero resources |
| Detail | Select a non-smart item with mapped vocabulary and transcript | System push and Back; title, description, type, language, URL, tags, timestamps, long title/URL wrap and scroll. Current-subset previews/count alone are insufficient: full text and vocabulary rows remain required for source fidelity |
| Smart and missing/foreign | Exercise smart resource in UI and missing/foreign IDs in isolated tests | Smart has system-updated explanation and no edit control; missing/foreign IDs never show another owner's data |
| Draft and cancel | Edit title, multiline description and tags; Cancel; repeat using system Back/swipe | Keyboard keeps fields in safe-area scrolling viewport; both exit paths discard draft without updating any row or sync marker |
| Validation and save | Attempt blank title, then edit all three metadata fields and Save once | Title required; one owner-bound metadata update only; form progress, explicit refusal/error on failure; detail and list reload from storage, sort reflects `UpdatedAt` |
| Nullable and empty metadata | In isolated owned resources, save only the title on rows with null, empty, and whitespace description/tags; separately clear nonempty description/tags in the editor | Untouched values retain exact null/empty/whitespace storage; deliberate clear stores an empty string, not null; title is nonblank without trimming the chosen text |
| Concurrent update | On an isolated fixture, open an editor, change the title, apply a newer CoreSync-style description/tags update, then Save | Conflict alert names the unsaved draft and newer data, Save cannot overwrite it, draft remains visible/editable, Reload and discard draft presents current owner-scoped fields and a fresh snapshot; Cancel/Back discard without writing |
| Refusals and interrupted reload | Exercise missing/foreign, newly smart, invalid snapshot, reload failure and repeated Save on disposable fixtures | Distinct honest feedback, no pending marker without successful write; reload error retains draft and offers Retry; back/cancel causes no write |
| Sync while editing | With an isolated delayed-sync fixture, start Sync then open an edit draft before it completes | Title/description/tags draft remains intact; Cancel performs no write; persisted detail refreshes only after the editor closes |
| Sync and cold restart | Observe after local save; request sync; terminate only the native app and relaunch in place | Pending shown until owner-confirmed CoreSync result; offline/unconfirmed never claims server success; confirmed only after a verified run; state/data survive a cold restart |
| Restoration and isolation | Restore the exact original title/description/tags through the same UI and sync again | Local/server selected metadata matches original; selected owner ID, vocabulary mappings, transcript, translation, progress, plans, and every other owner's rows remain unchanged |
| Accessibility and appearance | Capture list, detail and edit in light/dark, English/Korean, larger text, keyboard shown and scrolled | Native roles/names/states/IDs, text scaling, readable contrast, correct focus order and no clipped controls or text; no emoji |

Keep before/edit/restored local and server SHA-256 evidence with the private test
artifacts; do not put raw account credentials or user content in logs.

### Native iOS Safe Area and scroll viewport regression

Use the existing SS Review bundle on the named iOS simulator, preserving its
owner data, appearance selection, font size, and resource view-mode preference.
Capture the app-root screenshot and accessibility tree before and after an
in-place update. Measure the native `ScrollView`/`List` bounds, not just the
position of cards in a screenshot. Do not generate a plan, grade a Quiz turn,
save or delete a resource, or change learning rows for this layout check.

| State | Action | Required observation |
|-------|--------|----------------------|
| Dashboard — Today's Plan and Choose My Own | For each mode, capture top, swipe through content behind the translucent navigation and tab bars, capture the bottom, then return to the top | The scroll **viewport** reaches the screen edges; visual gutters stay on the scrolling content. First controls are usable at the top, and the last card is fully visible/reachable above the tab bar at rest, with no fixed blank band or duplicate system inset |
| Resources — Grid, List, loading/empty when naturally available | Scroll each mode from top to last owned row; open and Back from a resource, and restore the original mode | Grid and contiguous List rows share one native Scroll viewport that continues underneath the bars; search/filter controls and the last row remain tappable at rest. A route return does not strand a row under a bar. Do not manufacture loading or empty data by clearing storage |
| Settings | Open from Dashboard More, check saved System/theme/text size, scroll the last preference controls into view, then Back | Scroll viewport extends under navigation; last Save Preferences control is reachable without altering preferences, and the original System/theme/text-size selection is preserved |
| Vocabulary Quiz | On a backed-up disposable owner, open an assigned or safely resumable turn, capture question/footer, scroll only if actual content is long, then Back without answering | Quiz Scroll reaches the edges with native tabs hidden; the fixed footer divider/counter/pill remains visible and the final response is not trapped beneath it. If no long turn or keyboard input is available, record the case as blocked rather than scoring a protected row |
| Resource Add/Edit cards | Open without editing, focus a field, dismiss the keyboard when shown, scroll to bottom Save/Cancel or last field, Back without Save/Delete | The native `ScrollView` owns the full viewport under navigation; visual gutters live on its content, while fields and bottom actions remain reachable. If the simulator has a hardware keyboard and no software keyboard appears, capture focus but mark keyboard-occlusion proof blocked |

A no-answer Quiz visit is not read-only on the current native head: the activity
timer can update `DailyPlanCompletion`/`ActivitySession`, and audio playback
can add `StreamHistory`. Do not use protected SS Review data for further Quiz
geometry variants; use an authorized disposable copy or mark them blocked.

### SS Review iOS Learning Resources browser layout (2026-10-07; acceptance pending)

The pinned `Resources.razor` has Grid cards and a separate `list-group` of compact
rows, with secondary media/language metadata hidden below 768 points. Captain
explicitly requires the two filter menus **side by side on phone** even though
the source's generic control flex row may wrap. On the existing SS Review
simulator, the root coordinator first backs up the sandbox and installs the
reviewed binary in place. Capture app-root screenshots and Ailoha element
bounds; do not test against a stale installed build. Preserve real owner rows.
The original view-mode preference was **List**: restore it after toggling and
compare the backed-up preferences and owner data. Do not tap Add, Save, Delete,
Starter, Sync or Quiz for a browser-layout check.
Before opening Resources after a new SwiftUI binding package install, verify
the **installed app's** native `MSUNode` class responds to
`setLineLimit:` with Objective-C runtime `instancesRespondToSelector:` and
that its embedded `MicrosoftSwiftUIShim.framework` arm64 UUID matches the
package's simulator slice. A matching managed `WithLineLimit` method,
packaged selector string, or build exit code alone is insufficient: an
old native framework in the app crashed on `-[MSUNode setLineLimit:]`.
If the runtime check fails, leave Resources closed, preserve app data and
restore the backed-up last-working app binary.

| State | Action | Required observation |
|-------|--------|----------------------|
| Phone List, populated | Open Resources in List at top; inspect several adjacent rows, one with long title if already available | One compact bordered row group with a separator **between**, not after, rows; each row has leading media icon, bold flexible **single-line truncated** title and trailing date, with no phone media/language subtitle or individually rounded card. Whole row remains tappable and at least 44 points tall. Scoped SwiftUI `WithLineLimit(1)` applies to browser titles only; its runtime truncation/ellipsis and full accessibility label remain pending Ailoha verification |
| Phone Grid | Switch to Grid without creating data; inspect cards and toolbar | Existing rounded Grid cards retain media/language metadata, one column and their current accessible open action; search, both menus, mode toggle, count and feedback remain present |
| Both modes, filters | Inspect default Type and Language; choose a real catalog type and language and return both to All, in English and Korean when available | The **same two controls remain on one horizontal row**, with an 8-point gap, above the separate trailing phone mode-toggle row in Grid and List; options and current selection stay correct. Menus remain >=44 points tall, accessible full labels are available, no overlap/overflow or keyboard-focus loss |
| Accessibility-extra-large phone filters, both modes | With the existing seven owner resources and accessibility-extra-large Dynamic Type, capture List → Grid → List at a nonzero supported scroll offset, then inspect each menu's visible text, button bounds and toggle bounds. Repeat at default text size before handing off. | Type and Language remain side by side. Each full selected value wraps **inside its own bordered button**, whose height grows with its text; the toggle starts below the lower of the two buttons with the source 12-point control gap. No menu text crosses another button, search, or toggle. Menu selections and scroll-host identity remain stable across mode switches. Inspect text and border in screenshots as well as node bounds: equal filter frames alone do not satisfy this case. The previous installed accessibility-extra-large captures failed this condition (Type y126.67–280.67, Language y103.67–303.67, toggle y254.67–320.33); the revised binary needs a fresh root runtime capture before acceptance. |
| Switch at a scroll offset | On populated List, record search/filter/toggle bounds and a first visible row; scroll to a middle position supported by both modes, tap Grid then List without leaving the page | The native Scroll host and search/menu/toggle nodes retain their identity; header geometry does not jump between modes and focus/search text/filter selections remain. Viewport remains at the supported offset/visible anchor; if the **shorter** list cannot support the previous offset, only native content-extent clamping near its bottom is acceptable—record measured before/after geometry |
| Responsive width | In a legitimate wide fixture/viewport, inspect List and Grid; return to phone | List shows title, then media/language metadata, then trailing date on one row; Grid columns adapt per the source. Both filters stay paired. If no wide runtime is available, mark runtime proof blocked and retain the width-variant unit contract rather than stretching a phone screenshot |
| Existing empty/loading/error | Navigate naturally or use an isolated presenter fixture without changing canonical rows | No-match still has Add and Starter; load error still shows Retry; search and filter/toggle controls remain usable whenever the state allows them; no new visible debug UI |

### SS Review Resource Add/Edit card consistency (bounded, no writes)

On the named iPhone 17 Pro simulator, retain the existing SS Review bundle,
appearance and resource view mode. Back up its sandbox and hash all domain
SQLite rows before an in-place build/install. Never open Quiz during this
check. Compare app-root screenshots (not a whole-device capture when multiple
simulators are booted) at the same viewport/appearance:

| State | Action | Required observation |
|-------|--------|----------------------|
| Shared reference | Capture Dashboard cards and Resources Grid cards before and after the install | Shared card surface and 16-point corner radius; page gutter on scrolling content, not on the viewport. Keep the source's 16-point Grid card inset. |
| Resource Edit | Open an existing owned non-smart resource; capture Basic Info, Transcript, Translation and Vocabulary at top/middle/bottom | Each card uses the same shared surface/radius/page gutter with the source's 24-point interior inset and 16-point gap, without grouped-row separators. TextEditor interiors match the source input surface rather than showing an opaque black rectangle. All original fields, Quick Add and mapped rows remain. |
| Menus and keyboard | Focus a field; open and dismiss Media Type, Language, header More/Save/Delete menu without selecting an action. Search Quick Add without selecting or creating a word; toggle Bulk Import open then closed | Native inputs, menus, focus, suggestion overlay and keyboard remain usable; no import, attachment, Save or Delete occurs. If focus cannot show a software keyboard, report keyboard-occlusion as blocked. |
| Resource Add | Open and inspect Basic Info, Media Content and Vocabulary cards, bottom Save control; Cancel without saving | Same shared card contract, native pickers/editors/import affordances and scroll reachability; no new resource row. |
| Smart read-only | Open an existing smart row when available, inspect its cards and Back | Same card geometry with read-only fields, smart notice and no editable actions. Do not manufacture data to reach this state. |
| Data and restoration | Compare full-row SQLite digests and saved appearance before/after; return to existing Resource Edit Translation/Vocabulary area | No row or preference drift; existing title/content and selected appearance retained. Do not claim full parity or human acceptance from this scoped check. |

### SS Review iOS Resource Edit vocabulary rows (read-only styling)

Use the existing owned non-smart resource with at least two mapped words.
Capture app-root screenshots on the same iPhone, preserving its bundle ID,
theme, OS appearance, Dynamic Type, and resource view mode. The vocabulary
list has native Dividers **inside** the card; the card itself remains free
of grouped-form row separators. Never tap a Delete X or Save, Generate,
Attach, Import, Create, or open Quiz on canonical data.

| State | Action | Required observation |
|-------|--------|----------------------|
| Normal text, Light and Dark | Scroll to existing rows and the last row; capture both appearances | One literal native SwiftUI Text per row: left-aligned **bold target** immediately followed by **normal secondary native** text, not two adaptive columns or Markdown; separate X stays end-aligned and vertically centered. Exactly `N-1` real SwiftUI Dividers occur **between** `N` rows, with no last bottom line; last row clears the home indicator. The reference screenshot's line tint is **not pixel-exact**: report SwiftUI Divider's platform-rendered tint against the selected theme token as a qualification, not Captain's acceptance of exact color. |
| Enlarged text, Light and Dark | Temporarily select an accessibility text size, capture the same rows, then restore the original size and appearance | Target and translation wrap as one paragraph; continuation starts at the text block's left edge. The X remains trailing/vertically centered without clipping, overlap, or fixed-height truncation. Distinguish actual iPhone rows from offscreen long-string layout tests. |
| Smart read-only and busy | Inspect an existing smart resource without creating vocabulary; cover busy removal policy in focused tests or an authorized disposable fixture | Smart rows have no Delete X. If all available smart resources have zero mappings, record **empty-state no X only** and mark populated smart-row proof blocked; do not invent rows. `!isSmartResource && !busy` is covered by code/tests, not a claimed live busy-state check without observing one. |
| Data and restoration | Recheck the exact 54 SQLite table row hashes, preferences and persistent media against the pre-action backup; return to the same Resource Edit rows | No domain row, appearance preference, or media drift; original OS appearance and Dynamic Type restored. No Delete, mutation, or activity result is inferred from this styling check. |

### SS Review iOS view-composition refactor regression (2026-10-07)

The approved file-only extraction in `docs/ios-ui-structure.md` must keep
the original mounted SwiftUI node identities; rebuilding a search/form node
on each presenter change is a regression even if the resulting screenshot
looks the same. The root coordinator performs these checks **only after**
backing up the current SS Review sandbox and installing the reviewed binary
in place. Use the current owner's actual row count (the October 7 baseline has
two mapped rows in the selected editor), not an older three-row screenshot.

| Journey | Required observation without a canonical write |
|---------|-------------------------------------------------|
| Resources search | Focus once; search a present title, extend it to a no-match query and clear it without retapping. The same first content text field retains focus, keyboard and search text while sibling rows change; switch Grid/List only after clearing and restore the prior mode. |
| Edit Quick Add suggestions | Open existing non-smart resource; focus `native.resources.edit.quick.search`, type a short query, observe loading/results and keyboard navigation, then Escape/dismiss **without** selecting/creating/attaching a word. The overlay/suggestion slot updates without remounting the form or losing its text field; Return/Back preserves the original resource. |
| Add-stage-only | In Add, paste one disposable target/native pair and tap **Import Vocabulary**, inspect staged count/table, then Cancel or system Back **without Save**. `BeginCreate` + `StageVocabularyFromText` update the presenter draft only; `CancelEdit` discards it. Compare the backed-up owner DB and preferences after exiting. Add has no Quick Add field and therefore does **not** prove the Edit incremental-append branch. |
| Edit incremental append (separate gated case) | Only with an isolated fixture or a confirmed, backed-up draft-only path: change an existing non-smart resource's Language to a *different* supported, non-`Other` value without saving; Quick Add a new disposable term, inspect exactly one appended row/retained focus, then Cancel while still on the unsaved language. `HasUnsavedQuickAddLanguage` must remain true so `CreateNewVocabularyAsync` enters `StageQuickAdd`, never `store.CreateVocabularyAsync`. If the guard cannot be confirmed, **do not tap Attach/Create on canonical data**; mark the runtime case blocked and use the isolated presenter test instead. |

For Escape diagnostics, the pre-extraction SS Review baseline already logged
`Publishing changes during view update` warnings when Escape cleared Quick
Add. Compare the new binary's bounded log **against that baseline**, rather
than asserting a global zero or expanding this file refactor into an unrelated
tooling fix. Do not seed, save, attach, delete, generate, score a Quiz turn,
or restore any previous snapshot as part of this regression.

## 2. Add Resource (`/resources/add`)

**Services:** LearningResourceRepository

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/resources/add` | Empty form: Title, Description, MediaType, Language, Tags |
| 2 | Fill Title + select MediaType and Language | Fields populated |
| 3 | (Optional) Paste vocabulary in import box, select delimiter | Preview appears |
| 4 | Click Save | Toast confirms; redirects to `/resources` |
| 5 | Verify on list page | New resource card visible |

**DB:** `SELECT Id, Title, MediaType, Language FROM LearningResource ORDER BY CreatedAt DESC LIMIT 1`

## 3. Edit Resource (`/resources/edit/{id}`)

**Services:** LearningResourceRepository, AiService (vocab gen)

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/resources/edit/{id}` | Fields populated (Title, Description, Transcript, etc.) |
| 2 | Change Title, click Save | Toast confirms; title updated |
| 3 | Click Delete | Confirmation prompt; confirm → redirects to `/resources` |

**DB:** `SELECT * FROM LearningResource WHERE Id = '<id>'` — should be gone after delete.  
**Pitfall:** Deleting a resource cascades to ResourceVocabularyMapping entries.

---

## 4. Vocabulary List (`/vocabulary`)

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/vocabulary` | Stat badges (Total, Associated, Orphaned); word cards |
| 2 | Type in search box | List filters by term match |
| 3 | Toggle filter (All/Associated/Orphaned) | List updates |
| 4 | Click a word card | Navigates to `/vocabulary/edit/{id}` |
| 5 | Click "Add" (or navigate to `/vocabulary/edit/0`) | Empty edit form |

## 5. Add Vocabulary Word (`/vocabulary/edit/0`)

**Services:** LearningResourceRepository, ElevenLabsSpeechService

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/vocabulary/edit/0` | Empty form; Save button disabled |
| 2 | Fill TargetLanguageTerm + NativeLanguageTerm | Save button enabled |
| 3 | (Optional) Fill Lemma, Tags, Mnemonic Story | Fields populated |
| 4 | Check resource association boxes | Resources selected |
| 5 | Click Add | Toast confirms; redirects to `/vocabulary` |

**DB:** `SELECT * FROM VocabularyWord ORDER BY CreatedAt DESC LIMIT 1`  
**Pitfall:** Duplicate detection by (TargetLanguageTerm, NativeLanguageTerm) — prevented on save.

## 6. Edit Vocabulary Word (`/vocabulary/edit/{id}`)

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to edit URL | All fields populated; progress section visible |
| 2 | Click speaker 🔊 | Spinner → done (audio plays via TTS) |
| 3 | Change NativeLanguageTerm, click Save | Toast confirms; redirects to `/vocabulary` |
| 4 | Re-open same word | Updated value persisted |
| 5 | Toggle resource associations, click Save | Associations updated |
| 6 | Click Delete | Confirmation; redirects to `/vocabulary` |

**DB (associations):**
```sql
SELECT ResourceId FROM ResourceVocabularyMapping WHERE VocabularyWordId = '<id>';
```

### Native iOS Vocabulary browse/edit slice (source-derived; runtime verification pending)

Pinned source: `Vocabulary.razor:9-70,140-401,555-650,790-863,980-1067`,
`VocabularyWordEdit.razor:19-421,535-605,657-767,793-979`,
`NavMenu.razor:43-57`. The approved M07 correction in the rebuild session's
`modal-case-ledger.md` makes iPhone filters a working copy; desktop source
filters are live. Keep required Add, bulk/cleanup/delete, duplicate management,
constituent editing, reference-sentence management, and status controls open
until those paths have separate implementation and runtime evidence.

Use only the separate **SS Test** bundle and its existing trusted owner for any
Save. Do not Save/Delete/generate/sign out in canonical **SS Review**, and never
insert a second profile into the SS Test preview database. Test two-owner and
shared-word refusals on the isolated real-SQLite fixture, not the simulator
preview store.

| State | Action | Expected result |
|-------|--------|-----------------|
| Entry, search, card | Dashboard More or Resources More → Vocabulary; search a known owned target or native term using `native.vocabulary.search`; tap `native.vocabulary.card.{wordId}` | Only owned words show. Card has target/native, encoding, learning status or earned Known, resource count or Orphaned, lemma and up to three tags. Word editor opens with the same ID, original fields and owner-only resource toggles. |
| Phone draft cancellation | Begin with a committed query; tap `native.vocabulary.filters.open`; change `native.vocabulary.filter.type`/`.status`, then `native.vocabulary.filters.cancel`, X, or system sheet dismissal | Result cards and committed query remain unchanged. Reopen: controls reflect the prior committed values, not the canceled draft. |
| Apply and Clear | Open phone filter sheet; change association/type/status/resource/tag/lemma/language/encoding; tap `native.vocabulary.filters.apply`. Open again; tap `.clear`, make another draft change and Cancel | Apply commits query and visible results together. Clear commits the empty query immediately; later Cancel discards only edits made after Clear. Reopen/restart restores only committed values. |
| iPad controls | In a tablet-width view, choose inline filters and switch Added/Updated sort and Grid/List controls | Inline filters immediately affect results and remain committed after Back/reopen. Stats, sort and view-mode variants stay present rather than becoming phone-only UI. |
| Existing owner edit | On SS Test, snapshot owned and foreign word IDs, mappings, progress and query preference; open one privately owned word; edit its native term and optional lemma or owned association; Save, Back, reopen, restart | Same word ID and selected owner associations persist. Other owner rows/mappings/progress remain unchanged; committed query returns. A blank term, an unowned ID or a shared foreign-owned base word never produces a successful edit. |
| Back/cancel and read-only | Open a word, change a field, use native Back or Cancel; inspect earned Known, practice history and owner reference sentences when present | No write on cancellation. Known is not manually selectable. History and resource labels do not expose another owner's resources; absent progress is not created merely by opening detail. |

Target evidence required before acceptance: exact build/app/device/Ailoha identity,
before/after UI tree and screenshot, isolated SQLite row snapshot, persisted
query after restart, and logs. Passing source-string or store tests alone is
not a running-app verdict.

---

## 7. Skills List (`/skills`)

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/skills` | Skill profile cards (Title, Description) |
| 2 | Click "Add Skill Profile" | Navigates to `/skills/add` |
| 3 | Click a skill card | Navigates to `/skills/edit/{id}` |

## 8. Add Skill (`/skills/add`)

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/skills/add` | Empty form: Title, Description |
| 2 | Fill Title + Description | Fields populated |
| 3 | Click Save | Toast confirms; redirects to `/skills` |

**DB:** `SELECT Id, Title FROM SkillProfile ORDER BY CreatedAt DESC LIMIT 1`

## 9. Edit Skill (`/skills/edit/{id}`)

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to edit URL | Fields populated; CreatedAt/UpdatedAt shown |
| 2 | Change Title, click Save | Toast confirms; redirects to `/skills` |
| 3 | Click Delete | Confirmation modal; confirm → redirects to `/skills` |

**DB:** `SELECT * FROM SkillProfile WHERE Id = '<id>'` — gone after delete.

### Native iOS Skills source-derived journey (2026-10-07; runtime pending)

Pinned source: `Skills.razor:6-45,54-78`, `SkillAdd.razor:6-78`,
`SkillEdit.razor:5-85,109-218`, `NavMenu.razor:45-59`, and
`Dashboard` Choose My Own's existing owner-scoped skill selector. This
implements a bounded iOS slice; it neither accepts the 177-state rebuild
contract nor excludes Android/macOS. Use only an **isolated synthetic owner**
for Save and confirmed Delete. Do not write to, delete from, or interrupt the
canonical SS Review app or its resumable Quiz.

| State | Procedure | Source-derived expected result |
|-------|-----------|--------------------------------|
| Entry/list | From Dashboard More open Skills, Back; repeat from Resources More. With a populated isolated owner, open Skills again and tap a card. | Both entry points reach the same owner-only Skill Profiles list; Back returns to the originating tab. List loading is visible, cards contain Title and optional Description, and the tapped card opens Edit. Phone has one column; wide layouts have two/three. |
| Empty/error | With an isolated empty owner open Skills, then force a read failure without deleting rows and Retry. | Empty text plus Add Your First Skill Profile remains reachable; a failed read is an error with Retry, not an empty list. No other owner's row appears. |
| Add/validation/cancel | Tap Add or Add Your First Skill Profile, focus Title/Description, enter a draft, Back/Cancel and reopen. Try Save with blank/whitespace Title. | Add uses native bound Title and multiline Description; Back/Cancel writes nothing. Invalid Title shows `SkillAdd_TitleRequired`; busy Save cannot submit twice or pop. No row is created until valid Save succeeds. |
| Add/save/reload | Save one disposable Title and Description, return to list, leave Skills and reopen; cold-restart the isolated app. | Source success feedback, one owned card with exact entered values; it is still present on return and after restart. The original owner ID, other-owner rows and unrelated data remain unchanged. |
| Edit/read/cancel | Open that owned card; inspect Created/Updated; alter both draft fields and use Cancel, then repeat with system Back/swipe. Open a missing or foreign ID in an isolated test. | Dates and original values are visible. Both exit paths discard drafts; missing/foreign ID shows no foreign values, a not-found/error and Back/Retry as applicable. |
| Edit/save/selector | Save a valid changed Title/Description; return to Dashboard Choose My Own and open the skill selector; restart and repeat. | Exactly one owned record changes, save feedback is visible, list and selector show the new title/ID after reloading; selection stays owner-scoped. No progress/plan/quiz rows change. |
| Named Delete | On a disposable owned row, request Delete; separately use Close, Cancel, and native dismissal. Only in the isolated store, confirm once; simulate refusal on a missing/foreign ID. | Named `SkillEdit_ConfirmDelete`/`SkillEdit_DeleteMessage` confirmation appears. All dismissals retain the row. Confirm performs the source's **hard delete**, not an unrequested archive; pending disables duplicates/back, success refreshes list/selector, refusal leaves an error. Never confirm on canonical data. |
| Owner/appearance/accessibility | Switch the isolated scope to another owner before a pending edit, then inspect list; test Light/Dark/System, English/Korean, large text, keyboard shown and phone/wide widths. | No fallback to an empty/global owner or cross-owner query/write, previous owner's content disappears on scope change; fields, actions, dates, error text and native accessibility IDs remain legible and reachable. |

Root QA handoff: build identity and app bundle must match the review target;
install/update only after Captain's data-preserving runtime gate. Capture
before/after native trees and screenshots for list, Add, Edit, confirmation,
selector return and restart. Check actual SQLite `SkillProfile` rows plus
unrelated-row digests; a successful unit test or build alone is not runtime
acceptance.

---

## 10. Profile (`/profile`)

**Services:** UserProfileRepository, DataExportService, IAppState

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/profile` | Fields populated from current user (Name, Email, languages) |
| 2 | Change Name, click Save Profile | Toast confirms |
| 3 | Change Native/Target Language | Dropdowns update |
| 4 | Change Session Duration (click a minutes button) | Button highlights |
| 5 | Change CEFR Level | Button highlights |
| 6 | Click Save Profile | Toast confirms all changes saved |
| 7 | Reload page | All changes persisted |

**DB:** `SELECT Name, NativeLanguage, TargetLanguage FROM UserProfile WHERE Id = '<userId>'`  
**Pitfall:** Profile changes update both DB and in-memory `AppState.CurrentUserProfile`.

### Native iOS Profile — read and discard draft (first slice, runtime pending)

Pinned source: `Profile.razor:1-181,218-327` and `NavMenu.razor:45-59`.
Use only the existing isolated **SS Test** identity and validated synthetic owner.
Never run this sequence against Captain's real account or the normal SS Review
identity. Root controls the simulator, installation, screenshots, and database
inspection; a successful build alone does not pass these cases.

| Action | Source-derived expected result |
|--------|--------------------------------|
| From Dashboard **More**, open Profile, scroll to the bottom | A distinct Profile route shows owned Name/Email, seven-option native/target language selectors, English/Korean display language, six session-minute buttons, six CEFR buttons, password fields, and Danger Zone. The unsafe data-export action remains absent. |
| Enter a different Name and select another session duration; optionally select languages/CEFR | Text remains editable and the chosen duration/level visibly highlights without remounting the focused field. There is no server or SQLite write for a draft. |
| Use Back, reopen from More, and cold-restart/reopen | The original exact-owner values return; query the same `UserProfile.Id` in isolated SQLite before/after to confirm no row or field changed. Missing, foreign, and revoked owners must not show another profile. |
| In an isolated real-SQLite coordinator test, load A's Profile and switch scope to B or null without touching the form | A's Name, Email, unsaved draft and Delete confirmation disappear immediately; the Profile view version and change notification advance for the scene's owner-unavailable replacement. A pending A read cannot restore the old draft. B's row requires a separate trusted B open; neither owner's SQLite row changes. Do not switch owners in the SS Test GUI preview, which rejects a second owner. |
| Inspect Save Profile and Change Password | Both actions and password inputs are disabled with explicit unavailable text; no success toast or attempted partial PUT. Saving, password validation and account effects remain required/open. |
| Tap Delete My Account, then Cancel | Source confirmation message appears. Cancel dismisses it; final Delete remains disabled and no account data changes. The custom source modal's exact chrome/focus/backdrop remains open for comparison. |
| Repeat in English/Korean and phone/large-text layouts | Labels remain localized; cards, selectors, scroll boundary and focused keyboard stay usable. Capture screenshots and native accessibility tree, then inspect logs for errors. |

Normal Profile Save is **blocked**, not implemented by a local-only test write:
client Profile sync is DownloadOnly, and the current server partial PUT clears
omitted Email/API key fields. An owner-scoped, existing-row-only authenticated
server edit and verified reload require a separately approved backend contract;
password/deletion likewise need isolated authenticated proof and permission.

### Export Data

| Step | Action | Verify |
|------|--------|--------|
| 1 | Click "Export All Data" | ZIP file downloads |

### Reset Profile (Danger Zone)

| Step | Action | Verify |
|------|--------|--------|
| 1 | Click Reset Profile | Confirmation modal appears |
| 2 | Confirm | Profile deleted; `is_onboarded` cleared; app restart needed |

**⚠️ DESTRUCTIVE — only test on throwaway profiles.**

---

## 11. Settings (`/settings`)

**Services:** VocabularyQuizPreferences, SpeechVoicePreferences, ThemeService, IVoiceDiscoveryService

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/settings` | All preference sections visible |
| 2 | Click a theme swatch | Theme color changes immediately |
| 3 | Toggle Light/Dark mode | Page theme switches |
| 4 | Adjust Text Size slider | Font size changes in real-time |
| 5 | Change Voice Language dropdown | Voice dropdown repopulates |
| 6 | Toggle Quiz Direction (Forward/Reverse/Mixed) | Radio updates |
| 7 | Toggle Autoplay Audio / Show Mnemonics checkboxes | Checkboxes toggle |
| 8 | Adjust Auto-Advance Duration slider | Value updates (1–10s) |
| 9 | Click Save Preferences | Toast confirms |
| 10 | Go to Vocab Quiz, answer a question | Verify: auto-advance uses saved duration; quiz direction matches setting |

**Verify persistence:** Reload `/settings` → all saved values retained.

### Native iOS SS Review: System appearance and delivery regression

Use only the approved simulator and its existing SS Review Debug identity.
Before an in-place update, verify the bundle ID and back up its full app
sandbox, including preferences, SQLite, WAL, and SHM. Do not uninstall, reset,
or change learning data. Verify the installed executable hash matches the
newly built SS Review executable; a build alone does not update the running app.

| Step | Action | Verify |
|------|--------|--------|
| 1 | In the running app, open Settings and choose System | System is selected; the saved appearance token keeps the existing theme and font scale |
| 2 | With the same app process open, change that simulator's OS appearance Light → Dark → Light | Ailoha screenshots of the app root show the source page/card backgrounds and text, navigation header, tab bar, and native selection controls change and return; the process ID and saved System token stay unchanged |
| 3 | Choose Light, then change the simulator OS to Dark; choose Dark, then change its OS to Light | Each explicit override stays fixed; the token changes only when the in-app choice changes |
| 4 | Restore the simulator's original OS appearance and choose System; restart SS Review | System remains selected and the current OS palette is shown; source SQLite table counts and user-row hashes match the pre-test backup |

When multiple simulators are booted, Ailoha's whole-screen screenshot can
capture another simulator. Take an Ailoha screenshot of the SS Review root
element by ID, and check its package ID and executable before accepting
the result.
