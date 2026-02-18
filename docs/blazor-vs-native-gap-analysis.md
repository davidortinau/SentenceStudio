# Blazor Hybrid vs Native MauiReactor — Gap Analysis

## 1. Cloze (Fill-in-the-Blank)

**Blazor**: `Cloze.razor` | **Native**: `ClozurePage.cs`

### Feature-by-Feature Comparison

| Feature | Blazor | Native | Gap? |
|---|---|---|---|
| Loading state | Spinner + "Loading sentences..." text | Full-screen overlay "Thinking....." (large text) | Different style |
| Empty state | "No cloze sentences available" + Go Back | — (no explicit empty state) | ✅ Native missing empty state |
| Sentence display | Display-styled text with blank (`______`) | Large font sentence (64/32px desktop/mobile) | Parity |
| Translation hint | Shows `translationHint` below sentence | Shows `RecommendedTranslation` below sentence | Parity |
| Multiple choice options | Horizontal flex-wrap buttons | Vertical stacked Border tiles | Layout differs |
| Text input mode | `<input>` with placeholder "Type the missing word..." | Entry with "Answer" label, bordered | Parity |
| Mode toggle | Button ("Choices" / "Type") in input row | `ModeSelector` component in footer | Different placement |
| GO button | Submit button in input form | GO button in footer grid | Different placement |
| Feedback (correct/incorrect) | Alert banner (green/red) | ❌ No visible feedback banner | ✅ Native missing feedback display |
| Correct answer reveal | Shows complete sentence on correct | — (no explicit reveal in UI code) | ✅ Native missing answer reveal |
| Auto-advance on correct | Yes, 1200ms delay then next | Yes, via auto-transition timer (4000ms) | Timing differs |
| Session summary | Full summary: Correct/Incorrect/Accuracy% + Continue/Done buttons | Full summary overlay: Correct/Incorrect/Accuracy% + Continue button | Native missing "Done" button |
| Navigation (prev/next) | Bottom bar: chevron-left, counter, chevron-right | Footer grid: chevron buttons with dividers | Different layout |
| Progress counter | "X / Y" text | Scrollable scoreboard with circle indicators per sentence | ✅ Native has richer progress (Blazor simpler) |
| Scoreboard (sentence indicators) | ❌ Not present | ✅ Scrollable HStack with check/X circles, clickable to jump | ✅ Blazor missing scoreboard |
| Auto-transition progress bar | ❌ Not present | ✅ ProgressBar at top | ✅ Blazor missing auto-transition bar |
| Activity timer (from plan) | ❌ Not present | ✅ Shell.TitleView with ActivityTimerBar | ✅ Blazor missing activity timer |
| Vocabulary progress tracking | ✅ Records VocabularyAttempt | ✅ Records VocabularyAttempt (more detailed with 5 match strategies) | Native has richer tracking |
| User activity recording | ✅ Saves UserActivity | ✅ Saves UserActivity | Parity |
| UserConfidence | ❌ Not present | ✅ State.UserConfidence field | ✅ Blazor missing confidence |
| Desktop/mobile responsive | Not explicit | ✅ IsDesktopPlatform flag, different font sizes/padding | ✅ Blazor missing responsive |

### Summary of Gaps (Blazor missing from Native)
1. **Scoreboard** — Native has scrollable sentence indicators; Blazor has simple counter
2. **Auto-transition progress bar** — Native shows visual countdown; Blazor doesn't
3. **Activity timer** (plan context) — Native shows timer in title view
4. **Desktop/mobile responsive layout** — Native adjusts font sizes/padding for platform
5. **User confidence tracking** — Native captures; Blazor doesn't
6. **Enhanced vocabulary lookup** (5-strategy matching) — Native more robust

### Summary of Gaps (Native missing from Blazor)
1. **Feedback banner** — Blazor shows green/red alert after grading; Native doesn't show inline feedback text
2. **Correct answer reveal** — Blazor shows complete sentence; Native unclear
3. **"Done" button** on session summary — Blazor has both "Continue" and "Done"; Native only has "Continue"
4. **Explicit empty state** — Blazor shows empty message with Go Back; Native lacks explicit empty state

---

## 2. VocabQuiz

**Blazor**: `VocabQuiz.razor` | **Native**: `VocabularyQuizPage.cs`

### Feature-by-Feature Comparison

| Feature | Blazor | Native | Gap? |
|---|---|---|---|
| Loading state | Spinner only | Full-screen overlay "Loading Vocabulary" (large text) | Different style |
| Empty state | "No vocabulary loaded" + Go Back | — (no explicit empty state visible) | ✅ Native missing empty state |
| Question display | Large display text with primary color | Large font (64/32px) with bold + "What is this in {Language}?" prompt | Native has language prompt |
| Correct answer display | Shows answer text below question | Shows answer + "Type correct answer" prompt for text mode | Native has richer feedback |
| Multiple choice options | Vertical full-width buttons | Vertical Border tiles with colors | Parity (different styling) |
| Text input mode | Form with label + large input | Entry with hint label ("Type your answer" / "Type correct answer") | Native has require-correct-typing flow |
| Require correct typing | ✅ On wrong text answer, must retype correctly | ✅ Same behavior | Parity |
| Feedback message | Alert banner (success/danger) | ❌ No visible feedback banner in main UI | ✅ Native missing feedback banner |
| Auto-advance | ✅ Progress bar + timer-based auto-advance | ✅ Auto-transition ProgressBar + timer | Parity |
| "Next" button | Shows "Next >" button after answer | — (auto-advances or tap to skip) | ✅ Native missing explicit Next button |
| Session summary | Correct/Total/Mastered/Rounds + per-word list with icons | Rich summary: Round/Session stats, per-word mastery scores, SRS info, Strong/Learning/NeedsWork counts | ✅ Blazor missing SRS detail |
| Summary word list | Shows icon (check/repeat/x) + target term → native term | Shows status icon + native + target + Session%/Mastery% + SRS status + attempt count | ✅ Blazor much simpler |
| Summary buttons | "Continue" + "Done" | "Continue" (free practice) OR "Next Activity"/"Continue" (plan mode) | ✅ Blazor missing plan-aware buttons |
| Audio playback | ✅ Play button in footer + auto-play option | ✅ Play button next to term + auto-play | Parity |
| Audio caching | ✅ Checks StreamHistory cache, saves new | ✅ Same pattern | Parity |
| Progress bar (turn counter) | Footer: "X / 10" text + correct count badge | ✅ Visual ProgressBar with numbered bubbles (turn X of 10) | ✅ Blazor simpler progress bar |
| Learning progress bar | ❌ Not present | ✅ Custom bar with green/gray bubbles showing turn/total | ✅ Blazor missing learning progress bar |
| Card transition animation | ❌ Not present | ✅ Fade in/out animation (IsCardTransitioning) | ✅ Blazor missing animation |
| Activity timer (from plan) | ❌ Not present | ✅ ActivityTimerBar in TitleView | ✅ Blazor missing activity timer |
| Plan-aware completion | ❌ Not present | ✅ "Next Activity" button, plan progress tracking | ✅ Blazor missing plan integration |
| MasteryScore-based mode selection | ❌ Always MultipleChoice initially | ✅ MasteryScore >= 0.50 → Text mode automatically | ✅ Blazor missing smart mode |
| Response time tracking | ✅ Stopwatch-based | ✅ Same | Parity |

### Summary of Gaps (Blazor missing from Native)
1. **Learning progress bar** — Native has visual bubble-based progress
2. **Card transition animation** — Native has fade in/out
3. **Activity timer** — Native shows for plan context
4. **Plan-aware summary buttons** — Native offers "Next Activity" in plan mode
5. **SRS detail in summary** — Native shows mastery%, SRS status, attempt count per word
6. **MasteryScore-based mode promotion** — Native auto-promotes to text mode
7. **"What is this in {Language}?" prompt** — Native provides context label

### Summary of Gaps (Native missing from Blazor)
1. **Feedback banner** — Blazor shows inline success/danger alert
2. **Explicit "Next" button** after answer — Blazor shows clickable Next
3. **"Done" button** to exit — Blazor has explicit Done button
4. **Explicit empty state** with Go Back button

---

## 3. VocabMatching

**Blazor**: `VocabMatching.razor` | **Native**: `VocabularyMatchingPage.cs`

### Feature-by-Feature Comparison

| Feature | Blazor | Native | Gap? |
|---|---|---|---|
| Header actions | "New Game" button + dropdown "Hide/Show Native" | ToolbarItems: "New Game" + "Show All/Hide Native" | Parity (different UI pattern) |
| Loading state | Spinner + "Loading vocabulary..." | ActivityIndicator + localized "Loading Vocabulary" | Parity |
| Empty state | "No vocabulary available" + Go Back | Empty grid (no explicit message) | ✅ Native has message via GameMessage but less prominent |
| Game complete screen | Trophy icon + "Congratulations!" + Matched/Misses stats + "Play Again" | "Congratulations!" + "All Pairs Matched!" + "Play Again" button | ✅ Blazor has richer completion (stats) |
| Status bar | "Matched: X / Y | Misses: Z" + game message | Same info in header with localized strings | Parity |
| Tile grid | CSS grid with cards | Responsive Grid (2-4 columns based on device/orientation) | ✅ Native more responsive |
| Tile styling | CSS classes for selected/matched/target | Color functions for background/text/border per state | Parity |
| Tile opacity | Matched tiles at 0.3 opacity | Matched tiles at 0.0 opacity with 0.8 scale | Different treatment |
| Hide native mode | ✅ Toggle via dropdown | ✅ Toggle via ToolbarItem | Parity |
| Tile reveal on target tap | ✅ Shows native tiles when target selected | ✅ Same behavior | Parity |
| Match check delay | 600ms | 800ms | Minor timing difference |
| Progress tracking | ✅ VocabularyAttempt recording | ✅ Dual recording: VocabularyAttempt + UserActivity | ✅ Blazor missing dual recording |
| Response time tracking | ✅ Stopwatch | ✅ Stopwatch | Parity |
| Activity timer (from plan) | ❌ Not present | ✅ ActivityTimerBar + IActivityTimerService | ✅ Blazor missing timer |
| Tile animation | ❌ No animation | ✅ `.WithAnimation(Easing.CubicInOut, 300)` on tiles | ✅ Blazor missing animation |
| Responsive layout | ❌ CSS grid (fixed) | ✅ Adjusts columns (2-4) based on idiom + orientation | ✅ Blazor less responsive |
| Vocabulary dedup | GroupBy Id | GroupBy (NativeTerm, TargetTerm) — more thorough | Native more thorough |

### Summary of Gaps (Blazor missing from Native)
1. **Activity timer** for plan context
2. **Tile animations** (cubic ease in/out)
3. **Responsive column count** based on device/orientation
4. **Dual activity recording** (VocabularyAttempt + UserActivity)
5. **Enhanced deduplication** by term pairs

### Summary of Gaps (Native missing from Blazor)
1. **Game complete stats** — Blazor shows Matched count and Misses count on completion; Native just says "All Pairs Matched"
2. **Matched tile visibility** — Blazor keeps matched tiles visible (faded); Native hides them completely

---

## 4. HowDoYouSay

**Blazor**: `HowDoYouSay.razor` | **Native**: `HowDoYouSayPage.cs`

### Feature-by-Feature Comparison

| Feature | Blazor | Native | Gap? |
|---|---|---|---|
| Input field | Single-line `<input>` with label "Enter a phrase in {language}" | Multi-line `Editor` with placeholder, bordered, min/max height | Native has richer input |
| Voice selector | `<select>` dropdown with voice name + gender | Button showing selected voice name → opens `VoiceSelectionPopup` | Different UI pattern |
| Submit button | Full-width "Speak" button with icon + spinner during busy | "Submit" button + separate voice selector button | ✅ Blazor has better busy indicator |
| Keyboard submit | ✅ Enter key triggers submit | ❌ No keyboard shortcut | ✅ Native missing keyboard shortcut |
| History loading | ✅ Separate spinner for history loading | ❌ Full IsLoading state for page | Blazor has more granular loading |
| History list | Cards with phrase + timestamp + play/delete buttons | CollectionView with play/save/delete per item | ✅ Native has save/export |
| History item actions | Play/Pause toggle + Delete | Play/Pause + Save as MP3 + Delete | ✅ Blazor missing Save/Export |
| Delete confirmation | ❌ Immediate delete | ✅ Confirmation popup ("Confirm Deletion" with Aye/Nay) | ✅ Blazor missing delete confirmation |
| Save/Export audio | ❌ Not present | ✅ `SaveAudioAsMp3` via FileSaver | ✅ Blazor missing export |
| Playback position | ❌ Not tracked | ✅ `PlaybackPosition` with timer-based updates | ✅ Blazor missing playback progress |
| Pause/Resume | ✅ Toggle play/stop | ✅ True pause/resume with position tracking | ✅ Blazor only stops, doesn't pause |
| History header | `<h3>` "History" | CollectionView with Header "History" | Parity |
| Timestamp display | ✅ Shows `CreatedAt.ToString("g")` | ❌ Not shown | ✅ Native missing timestamp |
| User activity tracking | ❌ Not present | ✅ Saves UserActivity for each submission | ✅ Blazor missing activity tracking |
| Error handling popups | Toast notifications | ✅ SimpleActionPopup for errors | Different approach |
| Voice loading state | ❌ Not shown | ✅ IsLoadingVoices prevents popup while loading | Native has better voice loading UX |

### Summary of Gaps (Blazor missing from Native)
1. **Save/Export audio** as MP3 to device
2. **Delete confirmation** dialog
3. **Playback position tracking** with timer
4. **True pause/resume** (Blazor only stop/play)
5. **User activity tracking** (UserActivity recording)
6. **Voice loading state** indicator
7. **Multi-line Editor** (richer input field)

### Summary of Gaps (Native missing from Blazor)
1. **Timestamp display** on history items
2. **Keyboard enter-to-submit** shortcut
3. **Separate history loading spinner** (more granular loading state)
4. **Inline busy indicator** on submit button (spinner inside button)

---

## 5. Shadowing

**Blazor**: `Shadowing.razor` | **Native**: `ShadowingPage.cs`

### Feature-by-Feature Comparison

| Feature | Blazor | Native | Gap? |
|---|---|---|---|
| Loading state | Spinner + "Generating sentences..." | Full-screen "Thinking....." overlay | Different style |
| Empty state | "No sentences available" + Go Back | ❌ No explicit empty state UI | ✅ Native missing empty state |
| Activity timer | ✅ `<ActivityTimer>` component when fromPlan | ✅ ActivityTimerBar in Shell.TitleView | Parity |
| Sentence display | Card with target text + optional translation + pronunciation notes | ScrollView with H2 text + optional translation + pronunciation notes | Parity |
| Translation toggle | ✅ "Show/Hide translation" link button | ✅ "Show/Hide Translation" button | Parity |
| Waveform display | ✅ `<WaveformDisplay>` component with seek support | ✅ Custom `WaveformView` with interactive seeking, play/pause position tracking | ✅ Native has richer waveform |
| Playback controls | Play/Pause button in footer | Play/Pause in footer center | Parity |
| Speed controls | ✅ "Slow" (0.6x) and "Normal" (1.0x) buttons | ✅ 0.6x, 0.8x, 1.0x speed buttons | ✅ Blazor missing 0.8x speed |
| Navigation | Prev/Next buttons with counter | Prev/Next (SkipStart/SkipEnd icons) | Parity |
| Progress counter | "X / Y" in footer | ❌ Not visible in footer (but has sentence list state) | ✅ Native missing counter |
| Buffering indicator | ✅ Spinner in play button during buffer | ❌ Not explicit | ✅ Native missing buffering indicator |
| Voice selection | ❌ Not present | ✅ Voice selector (button → popup) with per-language prefs | ✅ Blazor missing voice selection |
| Export/Save audio | ❌ Not present | ✅ "Save as MP3" via FileSaver | ✅ Blazor missing export |
| Responsive layout | ❌ Not present | ✅ IsNarrowScreen detection with menu bottom sheet | ✅ Blazor missing responsive |
| Narrow screen menu | ❌ Not present | ✅ Bottom sheet with Speed/Voice/Export options | ✅ Blazor missing narrow menu |
| Export bottom sheet | ❌ Not present | ✅ Dedicated export UI with progress | ✅ Blazor missing export UI |
| Audio caching | ❌ Saves to temp file | ✅ Dictionary-based audio cache per sentence | ✅ Blazor less efficient caching |
| Waveform seek interaction | ✅ `OnPositionChanged` callback | ✅ Pause-on-interact + seek + resume | Native has richer seek behavior |
| Time display | ❌ Not shown | ✅ "Audio Time: X:XX / Y:YY" display | ✅ Blazor missing time display |

### Summary of Gaps (Blazor missing from Native)
1. **Voice selection** — Native lets user choose TTS voice
2. **0.8x speed option** — Blazor only has 0.6x and 1.0x
3. **Export/Save as MP3**
4. **Responsive layout** with narrow screen detection
5. **Narrow screen bottom sheet menu**
6. **Audio time display** ("Current / Duration")
7. **Dictionary-based audio caching**
8. **Richer waveform seek** (pause on interact, seek and resume)

### Summary of Gaps (Native missing from Blazor)
1. **Progress counter** ("X / Y") in footer
2. **Buffering spinner** on play button
3. **Explicit empty state** with Go Back button

---

## 6. Scene (Describe a Scene)

**Blazor**: `Scene.razor` | **Native**: `DescribeAScenePage.cs`

### Feature-by-Feature Comparison

| Feature | Blazor | Native | Gap? |
|---|---|---|---|
| Loading state | Spinner + "Loading scene..." | Full-screen overlay with "Loading scene..." / "Analyzing the image..." | Native has contextual loading messages |
| Empty state | "No scene images available" + Browse Gallery + Go Back | ❌ No explicit empty state (uses default hardcoded image URL) | Different approach |
| Activity timer | ✅ `<ActivityTimer>` when fromPlan | ✅ ActivityTimerBar in Shell.TitleView | Parity |
| Layout | Two-column: image left, results right (responsive) | Two-column Grid: image left, sentences right | Parity |
| Scene image | `<img>` with max-height 400px | `Image()` with AspectFit | Parity |
| Results list | Cards with accuracy badge, explanation expand/collapse | CollectionView with accuracy display + grammar corrections inline | Different detail level |
| Grammar corrections | ❌ Not present as separate section | ✅ Inline strikethrough original → corrected with explanations | ✅ Blazor missing grammar corrections |
| Expand/collapse detail | ✅ "More ▼" / "Less ▲" per sentence | ✅ Tap sentence → popup with full explanation | Different interaction |
| Fluency display | ✅ Shows Fluency% in expanded section | ✅ Shows in explanation popup | Parity (different presentation) |
| Recommended sentence | ✅ Lightbulb icon + recommendation text | ✅ In `RecommendedSentence` field displayed via popup | Different presentation |
| Input bar | Sticky bottom: text input + New Scene + Submit | Bottom grid: Entry + Send/Translate/Clear buttons | Different actions |
| Submit button | Send icon, shows spinner during grading | Send icon button | ✅ Blazor has grading spinner |
| Translate button | ❌ Not present | ✅ Translate icon button → translates input | ✅ Blazor missing translate |
| Clear button | ❌ Not present | ✅ Eraser icon button → clears input | ✅ Blazor missing clear |
| New Scene button | ✅ Refresh button in input bar | ❌ Not present as direct button (uses gallery) | Different approach |
| Image Gallery | ✅ Full overlay with header, add URL bar, image grid, select mode, delete | ✅ SfBottomSheet with gallery grid, add URL popup, select/delete | Parity (different UI) |
| Gallery trigger | Gallery icon in PageHeader toolbar | Gallery icon in ToolbarItem | Parity |
| Gallery: add image | Inline URL input + Add button | ✅ FormPopup asking for URL | Different input method |
| Gallery: multi-select | ✅ Toggle select mode, checkboxes on images | ✅ Toggle selection mode with checkboxes | Parity |
| Gallery: delete | ✅ Delete selected images | ✅ Delete selected images | Parity |
| Gallery: loading | ✅ Spinner while loading | ❌ Not explicit | ✅ Blazor has better gallery loading |
| View description | ❌ Not present | ✅ Info toolbar button → popup showing AI-generated description | ✅ Blazor missing description viewer |
| Async grading | ❌ Shows grading spinner, blocks input | ✅ Adds "Grading..." placeholder immediately, grades in background, user can keep typing | ✅ Blazor missing async grading |
| Vocabulary tracking | ❌ Only UserActivity | ✅ Enhanced: UserActivity + per-word VocabularyAttempt tracking | ✅ Blazor missing vocab tracking |
| Toast feedback | ✅ Shows "Excellent/Good/Keep practicing" toast | ✅ Enhanced feedback via toast/popup | Parity |
| "Grading..." state per sentence | ❌ Not present | ✅ `IsGrading` flag per sentence, shows "Grading..." label | ✅ Blazor missing per-sentence grading state |

### Summary of Gaps (Blazor missing from Native)
1. **Translate button** — Native can translate user input
2. **Clear button** — Native has explicit clear
3. **Grammar corrections** — Native shows inline strikethrough corrections
4. **View description** — Native has info button to see AI-generated scene description
5. **Async grading** — Native allows continued typing while grading in background
6. **Per-sentence "Grading..." indicator** — Native shows progress per sentence
7. **Enhanced vocabulary tracking** — Native tracks per-word attempts
8. **Contextual loading messages** ("Loading scene..." vs "Analyzing the image...")

### Summary of Gaps (Native missing from Blazor)
1. **"New Scene" button** in input bar — easier scene refresh
2. **Gallery loading spinner** while fetching images
3. **Grading spinner** on submit button (visual indicator)
4. **Inline expand/collapse** for result details (vs popup in Native)

---

## Cross-Cutting Gaps (All Pages)

### Blazor consistently missing:
1. **Activity Timer (Plan Context)** — Most Blazor pages lack the ActivityTimerBar
2. **Plan-aware navigation** — No "Next Activity" / plan completion flow
3. **Responsive layout** — No device idiom/orientation detection
4. **Animations** — No tile/card transitions
5. **Enhanced vocabulary tracking** — Simpler VocabularyAttempt recording
6. **Localization** — Blazor uses hardcoded English strings; Native uses `LocalizationManager`

### Native consistently missing:
1. **Feedback banners** — Blazor shows inline success/danger alerts; Native often relies on state changes without visible feedback text
2. **"Done" / "Go Back" buttons** — Blazor consistently offers exit buttons; Native relies on Shell back navigation
3. **Empty state UX** — Blazor has better explicit empty states with "Go Back" buttons
4. **Grading/busy spinners on buttons** — Blazor shows inline spinners on submit buttons

---

## 7. Vocabulary (VocabularyManagement)

**Blazor**: `Vocabulary.razor` | **Native**: `VocabularyManagementPage.cs`

| Feature | Blazor | Native | Gap |
|---------|--------|--------|-----|
| **Stats bar** | Total / Associated / Orphaned badges | Total / Associated / Orphaned badges | ✅ Parity |
| **Search** | Text input with oninput binding | GitHub-style search syntax with autocomplete, debounced timer | 🔴 Blazor missing: advanced search syntax parser (`is:orphaned`, `tag:nature`, `resource:X`, `lemma:X`, `status:known`), autocomplete popup |
| **Filter toggles** | Dropdown select (All/Associated/Orphaned) | Button toggles (All/Associated/Orphaned) | ⚠️ Different UI: Blazor uses `<select>`, Native uses segmented buttons |
| **Filter bottom bar** | None | Compact bottom search bar + icon filter buttons (Tag, Resource, Lemma, Status) | 🔴 Blazor missing: bottom filter bar with icon quick-filters |
| **Clear filter button** | X button appears when filter active | X button in search entry | ✅ Parity |
| **Card layout** | Responsive grid (col-12/col-md-6/col-lg-4) | CollectionView with `GridLayoutHelper` (adaptive columns, phone vs tablet layout) | ⚠️ Different but equivalent approach |
| **Card content** | Target term, native term, status badge, orphaned warning, tags (up to 3) | Target term, native term, status + orphaned text combined, encoding strength badge | 🔴 Blazor missing: encoding strength badge on cards |
| **Tags display** | Shows up to 3 tag badges per card | No tags on card (available via search filter) | 🔴 Native missing: inline tag display on cards |
| **Multi-select mode** | None | Checkbox per card, bulk actions bar (Delete, Associate) | 🔴 Blazor missing: multi-select mode with bulk delete/associate |
| **Cleanup tool** | None | Toolbar menu item → cleanup options | 🔴 Blazor missing: vocabulary cleanup operations |
| **Add Word** | PageHeader primary action button | Toolbar plus icon | ⚠️ Different UI pattern |
| **Empty state** | "No vocabulary words yet" + "Add Your First Word" button | "No vocabulary words" + "Get Started" button | ✅ Parity |
| **Loading state** | Spinner (Bootstrap) | ActivityIndicator centered | ✅ Parity |
| **Phone-specific layout** | Responsive CSS (stacks columns) | Separate `RenderVocabularyCardMobile` with compact layout | 🔴 Blazor missing: dedicated mobile card layout |
| **Resource-name pre-filter** | None | Props.ResourceName pre-applies `resource:` search filter on mount | 🔴 Blazor missing: navigation-prop-based resource pre-filtering |
| **Progress data** | Loads progress via `VocabularyProgressService.GetAllProgressDictionaryAsync()` | Loads progress via `VocabularyProgressService` | ✅ Parity |

---

## 8. VocabularyWordEdit (EditVocabularyWord)

**Blazor**: `VocabularyWordEdit.razor` | **Native**: `EditVocabularyWordPage.cs`

| Feature | Blazor | Native | Gap |
|---------|--------|--------|-----|
| **Title** | "Edit Vocabulary Word" / "Add Vocabulary Word" | Same | ✅ Parity |
| **Target Language field** | Text input | Entry with Border + audio play button inline | 🔴 Blazor missing: inline audio play button for target term |
| **Native Language field** | Text input | Entry with Border | ✅ Parity |
| **Encoding Section** | Encoding strength label, Lemma, Tags, Mnemonic Story, Mnemonic Image URL + preview | Same fields | ✅ Parity |
| **Example Sentences** | None | Full section: list of examples, "Generate with AI" button, "Add Manually" button, per-sentence audio play, toggle core, delete | 🔴 Blazor missing: entire Example Sentences section |
| **Progress Section** | Status badge, details (streak/production/mastery), next review date | Same data displayed | ✅ Parity |
| **Resource Associations** | Checkbox list with count | Checkbox list with count, visual highlight on selected | ✅ Parity |
| **Save button** | In PageHeader primary action, with spinner | Bottom action bar with full-width button | ⚠️ Different placement |
| **Delete button** | In PageHeader secondary dropdown, with confirmation dialog | Trash icon button next to save, with popup confirmation | ⚠️ Different UI pattern |
| **Saving indicator** | Spinner in button | ActivityIndicator + "Saving..." text row | ⚠️ Different style |
| **Duplicate check** | Yes, via `FindDuplicateVocabularyWordAsync` | Yes, same | ✅ Parity |
| **Error display** | Alert div | Label with danger color | ⚠️ Different style |
| **Audio playback** | None | Play button for target term + per-example-sentence audio via ElevenLabs | 🔴 Blazor missing: all audio playback features |
| **Form validation** | `IsFormValid` computed property disables save | `IsEnabled` check on button | ✅ Parity |
| **Loading state** | Spinner | ActivityIndicator | ✅ Parity |

---

## 9. VocabularyProgress

**Blazor**: `VocabularyProgress.razor` | **Native**: `VocabularyLearningProgressPage.cs`

| Feature | Blazor | Native | Gap |
|---------|--------|--------|-----|
| **Filter tabs** | Button-based tabs: All(n), Known(n), Learning(n), Unknown(n) | Button-based filter bar with same categories | ✅ Parity |
| **Search** | Text input with oninput | Entry with OnTextChanged | ✅ Parity |
| **Card layout** | Responsive grid (col-12/sm-6/md-4/lg-3) | CollectionView with `GridLayoutHelper` | ✅ Parity |
| **Card content** | Left colored border, target term, native term, status badge, progress text, review date | Left colored BoxView, target term, native term, status badge, progress text, review date | ✅ Parity |
| **Resource filter** | None | Resource picker (dropdown to filter by specific resource) + "All Resources" option | 🔴 Blazor missing: resource-based filtering |
| **Resource-scoped loading** | Loads all words globally | Can load words per-resource or globally via Props.ResourceId | 🔴 Blazor missing: resource-scoped vocabulary progress |
| **Initial filter from Props** | WordId parameter (unused in filtering) | Props.InitialFilter pre-sets filter tab | 🔴 Blazor missing: prop-based initial filter |
| **Empty state** | "No vocabulary words match the current filter" | Not explicitly shown (empty CollectionView) | ⚠️ Blazor has better empty state message |
| **Loading state** | Spinner | ActivityIndicator | ✅ Parity |

---

## 10. MinimalPairs (Landing Page)

**Blazor**: `MinimalPairs.razor` | **Native**: `MinimalPairsPage.cs`

| Feature | Blazor | Native | Gap |
|---------|--------|--------|-----|
| **Mode selector** | "Mode:" label + btn-group (Focus/Mixed) | "Mode:" label + button group (Focus/Mixed) | ✅ Parity |
| **Start Session button** | Conditional: Focus requires selection, Mixed shows pair count | Same logic | ✅ Parity |
| **Card layout** | Responsive grid (col-12/md-6/lg-4) | CollectionView (linear list) | ⚠️ Blazor uses grid cards, Native uses list |
| **Card content** | WordA "vs" WordB + contrast label + delete button | WordA "vs" WordB + contrast label + delete icon | ✅ Parity |
| **Selection highlighting** | CSS `border-primary` class | Background color change to theme.Primary | ✅ Parity (different visual) |
| **Delete confirmation** | JavaScript `showConfirm` dialog | Popup via `SimpleActionPopup` | ✅ Parity (different mechanism) |
| **Create button** | PageHeader primary action | Toolbar item with plus icon | ⚠️ Different UI pattern |
| **Empty state** | "No minimal pairs yet" + "Create Your First Pair" button | Text label only, no CTA button | 🔴 Native missing: CTA button in empty state |
| **Loading state** | Spinner | Label("Loading...") only | ⚠️ Native has text-only loading, Blazor has spinner |
| **Error toast on start** | `Toast.ShowError` | `SimpleActionPopup` | ✅ Parity (different mechanism) |

---

## 11. MinimalPairCreate

**Blazor**: `MinimalPairCreate.razor` | **Native**: `CreateMinimalPairPage.cs`

| Feature | Blazor | Native | Gap |
|---------|--------|--------|-----|
| **Word A selection** | Search input + `<select>` list (size=5) + selected badge | SearchBar + Picker dropdown + selected label | ⚠️ Different UI: Blazor has inline list, Native uses Picker |
| **Word B selection** | Same as Word A | Same as Word A | ✅ Parity |
| **Contrast label** | Text input | Entry | ✅ Parity |
| **Create button** | PageHeader primary action with spinner | Bottom button | ⚠️ Different placement |
| **Validation** | Both words required, same word check | Same | ✅ Parity |
| **Error display** | Alert div | Label with danger color | ⚠️ Different style |
| **Loading state** | Spinner | Label("Loading...") | ⚠️ Different loading indicator |
| **Success feedback** | Toast + navigate back | Navigate back (no toast) | 🔴 Native missing: success toast notification |
| **Word search** | Uses `StartsWith` for target, `Contains` for native | Uses `StartsWith` for both | ⚠️ Minor search behavior difference |

---

## 12. MinimalPairSession

**Blazor**: `MinimalPairSession.razor` | **Native**: `MinimalPairSessionPage.cs`

| Feature | Blazor | Native | Gap |
|---------|--------|--------|-----|
| **Trial counter** | "Trial X / Y" text + check/X icons with counts | "Trial X / Y" text + ✓/✗ emoji with counts | ✅ Parity |
| **Answer tiles** | 150×150px cards with border feedback | 120-150px (responsive) cards with border feedback | ✅ Parity |
| **Selection feedback** | Border color changes (green selected, blue/red checked) | Border color changes (primary selected, success/danger checked) | ⚠️ Slightly different colors |
| **Check Answer button** | Separate button below tiles | Separate button below tiles | ✅ Parity |
| **Double-tap auto-check** | Not implemented | `.OnTapped(..., 2)` triggers auto-check on double-tap | 🔴 Blazor missing: double-tap auto-check |
| **Replay button** | Text button "Replay" with icon | ImageButton (play icon only) | ⚠️ Different style |
| **Audio playback** | ElevenLabs with caching | Same | ✅ Parity |
| **Session summary** | Correct, Incorrect, Accuracy%, Duration in centered card | Same data in centered card | ✅ Parity |
| **Auto-advance delay** | 1500ms | 1500ms | ✅ Parity |
| **Correct answer indicator** | Bootstrap check icon on correct tile | BootstrapIcons CheckCircleFill image | ✅ Parity |
| **Dispose/cleanup** | `IDisposable` audio player cleanup | `OnWillUnmount` audio player cleanup | ✅ Parity |
| **Params** | URL query params (pairIds, mode, trials) | Props object (PairIds, Mode, PlannedTrialCount) | ✅ Parity (different mechanism) |

---

## 13. VideoWatching

**Blazor**: `VideoWatching.razor` | **Native**: `VideoWatchingPage.cs`

| Feature | Blazor | Native | Gap |
|---------|--------|--------|-----|
| **Video player** | `<iframe>` with YouTube embed URL | `WebView` with mobile YouTube URL (`m.youtube.com`) | ⚠️ Different: Blazor uses embed iframe, Native uses mobile site |
| **Activity Timer** | `<ActivityTimer>` component when `fromPlan=true` | `ActivityTimerBar` in Shell TitleView when `FromTodaysPlan=true` | ✅ Parity (different placement) |
| **Timer service lifecycle** | None (component-only) | Full `IActivityTimerService` integration (Start on mount, Pause on unmount) | 🔴 Blazor missing: timer service lifecycle management |
| **Title display** | `<h2>` with resource title | ContentPage title | ⚠️ Different placement |
| **Open in YouTube** | Anchor tag with `target="_blank"` | ToolbarItem + `Launcher.Default.OpenAsync` | ⚠️ Different mechanism |
| **Transcript section** | Collapsible card with toggle, scrollable `pre-wrap` text | None | 🔴 Native missing: transcript display section |
| **Loading state** | Spinner + "Loading video..." text | ActivityIndicator + "Loading video..." text | ✅ Parity |
| **Error state** | Error text + "Go Back" button | Error text + "Go Back" button | ✅ Parity |

---

## 14. Import (YouTube Import)

**Blazor**: `Import.razor` | **Native**: `YouTubeImportPage.cs`

| Feature | Blazor | Native | Gap |
|---------|--------|--------|-----|
| **URL input** | Text input with Fetch button + Enter key support | Entry with "Fetch Transcripts" button | ✅ Parity |
| **Enter key fetch** | `@onkeydown` for Enter triggers fetch | Not implemented | 🔴 Native missing: Enter key to fetch |
| **Language picker** | `<select>` dropdown (multiple transcripts) | Picker dropdown | ✅ Parity |
| **Transcript editor** | `<textarea>` rows=12 | Editor with AutoSize | ✅ Parity |
| **Polish with AI** | Button with inline spinner | Button + full-screen overlay with ActivityIndicator | ⚠️ Native has more prominent polishing overlay |
| **Save as Resource** | Button with spinner | Button (state-based) | ⚠️ Different feedback |
| **Duplicate detection** | None | Checks duplicate URL + duplicate title, shows popup | 🔴 Blazor missing: duplicate resource detection |
| **Success result** | Inline alert with "View Resource" + "Import Another" | Popup with Yes/No to view | ⚠️ Different UX |
| **Reset** | "Import Another" button | "Clear" toolbar item | ✅ Parity |
| **Resource fields saved** | Title, MediaUrl, Transcript, MediaType, Language | Title, Description, Language, MediaType, MediaUrl, Transcript, Tags, timestamps | 🔴 Blazor missing: Description, Tags, CreatedAt/UpdatedAt |
| **Loading indicators** | Spinner in Fetch button + transcript download spinner | State-based messages | ⚠️ Different loading UX |

---

## 15. Onboarding

**Blazor**: `Onboarding.razor` | **Native**: `OnboardingPage.cs`

| Feature | Blazor | Native | Gap |
|---------|--------|--------|-----|
| **Step flow** | 7 steps: Welcome → Native → Target → Name → API Key → Preferences → Finish | Same steps (API key conditional) | ✅ Parity |
| **Step indicator** | Circular dots (8px, filled/unfilled) | Elongated dots (active=24px, inactive=8px, progressive fill) | ⚠️ Native has more polished indicator |
| **Language selection** | Button list (stacked vertically) | Picker dropdown from `Constants.Languages` | ⚠️ Different UI: Blazor large buttons vs Native picker |
| **Multi-target language** | Single target only | `TargetLanguages` list (multi support) | 🔴 Blazor missing: multiple target languages |
| **Name suggestions** | 2-column grid of suggestions | 4-column grid, 2 rows (masculine + feminine grouping, 8 names) | ⚠️ Native shows more suggestions with gender grouping |
| **API Key step** | Always step 4 (skipped if env var set) | Conditional (checked via `IConfiguration`) | ✅ Parity |
| **API Key link** | None | Underlined link → opens OpenAI API keys page | 🔴 Blazor missing: link to get API key |
| **Session minutes** | Single-row button group (5,10,15,20,30,45) | Two-row layout (5,10,15,20 / 25,30,45) + recommendation text | ⚠️ Native has 25min option and recommendation |
| **CEFR levels** | Button group with level codes only | Buttons with level + description ("A1 - Beginner") in 3 rows | 🔴 Blazor missing: CEFR level descriptions |
| **Final step** | Buttons: "Create Starter Content" + "Skip" | Cards with titles + descriptions | ⚠️ Native has richer final step |
| **Creation progress** | Spinner + progress message | ActivityIndicator + progress message | ✅ Parity |
| **Cancellation safety** | None | `CancellationTokenSource` prevents state updates after unmount | 🔴 Blazor missing: cancellation safety |
| **Welcome text** | "Welcome" + "Let's set up..." | "Welcome to Sentence Studio!" + longer description | ⚠️ Different copy |

---

## Consolidated Critical Gaps (Batch 2)

### Blazor Missing from Native (high priority for Blazor):
1. **Vocabulary**: GitHub-style search syntax with autocomplete
2. **Vocabulary**: Multi-select mode with bulk delete/associate
3. **Vocabulary**: Cleanup tools
4. **Vocabulary**: Encoding strength badge on cards
5. **Vocabulary**: Bottom filter bar with icon quick-filters
6. **EditVocabularyWord**: Inline audio play button for target term
7. **EditVocabularyWord**: Entire Example Sentences section (generate AI, add manual, audio, core toggle, delete)
8. **VocabularyProgress**: Resource-based filtering + initial filter from props
9. **MinimalPairSession**: Double-tap auto-check
10. **VideoWatching**: Timer service lifecycle (start/pause)
11. **Import**: Duplicate resource detection (URL + title)
12. **Import**: Description, Tags, timestamps saved to resource
13. **Onboarding**: Multiple target languages, CEFR descriptions, API key link, cancellation safety

### Native Missing from Blazor (high priority for Native):
1. **Vocabulary**: Inline tag display on cards
2. **MinimalPairs**: Empty state CTA button
3. **MinimalPairCreate**: Success toast notification
4. **VideoWatching**: Transcript display section
5. **Import**: Enter key to fetch transcripts
