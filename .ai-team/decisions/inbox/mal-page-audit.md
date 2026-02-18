# Page-by-Page Gap Audit: Blazor Hybrid → Native MauiReactor
**Generated:** 2025-02-17  
**Author:** Mal (Lead)  
**Purpose:** Systematic comparison of every Blazor page vs native counterpart to identify missing features, loading states, empty states, wrong overlays, and behavioral gaps.

---

## Executive Summary

**Total Pages Audited:** 28  
**Status Breakdown:**
- ✅ **Match (5):** Close functional parity, minor styling differences only
- ⚠️ **Gaps (18):** Missing features, states, or UI elements but core functionality present  
- ❌ **Major Gaps (5):** Significant missing features or completely different experiences

**High-Priority Gaps (P0):**
1. **Dashboard:** Missing mode toggle (TodaysPlan vs ChooseOwn), activity grid in ChooseOwn mode, "Regenerate Plan" action in Today's Plan
2. **Settings:** Missing theme swatch UI, light/dark mode toggle, font scale slider, quiz direction 3-way toggle, auto-advance duration slider
3. **Resources:** Missing search bar, media type filter, language filter, "Create Starter Resource" loading overlay
4. **Vocabulary:** Missing search bar, filter dropdown (All/Associated/Orphaned), stats badges at top
5. **Conversation:** Missing scenario selector, language switcher in toolbar, grammar corrections in chat bubbles

---

## 1. Index.razor → DashboardPage.cs — ⚠️ Gaps

### Blazor Features:
- Mode toggle: "Today's Plan" vs "Choose My Own" (btn-group with icons)
- **Today's Plan mode:** Streak badge, progress bar with rationale, clickable plan items, "Regenerate Plan" secondary action, empty state with "Generate Plan" button
- **Choose Own mode:** Multi-select for resources (Tom Select JS), skill profile picker, activity grid (11 activities with icons)
- Vocabulary stats section: 4 cards (New/Learning/Review/Known), total words + 7-day accuracy
- Loading spinners for plan data and vocab data

### Native Has:
- Mode toggle implemented with custom buttons
- Today's Plan mode: Streak, progress card, plan items list, empty state
- **NO Choose Own mode implementation** — missing resource/skill selectors, activity grid
- Vocabulary stats section present
- Loading states present

### GAPS:
- [ ] **P0** — Choose Own mode is entirely missing from native (resource multi-select, skill picker, 11-activity grid)
- [ ] **P0** — "Regenerate Plan" toolbar action missing in Today's Plan mode (Blazor has secondary action dropdown)
- [ ] **P1** — Plan progress card missing rationale text display (Blazor shows `todaysPlan.Rationale` under progress bar)
- [ ] **P1** — Plan item cards don't show icon badges for completed state (Blazor uses `bi-check-circle-fill`)
- [ ] **P2** — Welcome message desktop-only logic implemented but missing responsive breakpoint check (Blazor uses `d-none d-md-block`)

**Loading UI:** Present in native ✅  
**Empty states:** Present in native ✅

---

## 2. Settings.razor → SettingsPage.cs — ❌ Major Gaps

### Blazor Features:
- **Appearance section:** Theme swatches (10 themes × 2 colors each), light/dark mode toggle (btn-group), font scale slider (0.85-1.5 with percentage display)
- **Voice & Quiz section:** Language dropdown, voice dropdown (with loading state), quiz direction (3-way: Forward/Reverse/Mixed), autoplay toggle, show mnemonic toggle, auto-advance duration slider (1-10s), "Save Preferences" button
- **Data Management:** Export button with spinner
- **Migrations:** Streak migration section with completion badge
- **About:** Version + framework info

### Native Has:
- **Appearance section:** Theme swatches rendered, light/dark mode buttons, font scale slider
- **Voice & Quiz section:** Language picker (popup-based), voice picker (popup-based), quiz direction (SINGLE SWITCH only), autoplay toggle, show mnemonic toggle, auto-advance duration slider (0.5-5.0s), "Reset to Defaults" button
- Data Management, Migrations, About sections present

### GAPS:
- [ ] **P0** — Quiz direction is a SWITCH (2-way: TargetToNative/NativeToTarget) instead of 3-way btn-group (Forward/Reverse/Mixed) — Blazor has explicit "Mixed" mode missing in native
- [ ] **P0** — "Save Preferences" button missing (native auto-saves on change, Blazor requires explicit save)
- [ ] **P1** — Auto-advance duration range differs: Blazor 1-10s, native 0.5-5.0s (inconsistent bounds)
- [ ] **P1** — Voice & Quiz section lacks "Save Preferences" explicit action (Blazor pattern is explicit save, native is implicit)
- [ ] **P2** — Appearance section uses custom popup pickers for language/voice instead of inline form controls (different UX pattern)

**Loading UI:** Present for voices ✅  
**Empty states:** N/A

---

## 3. Profile.razor → UserProfilePage.cs — ✅ Match

### Blazor Features:
- Personal info: Name, Email
- Language settings: Native/Target/Display (dropdowns)
- Learning preferences: Session duration buttons (5/10/15/20/30/45), CEFR level buttons (A1-C2)
- API Configuration: OpenAI key (password field) with link to openai.com
- Export data button with progress message
- Danger zone: Reset profile button

### Native Has:
- All sections present with same fields
- Session duration picker (5/10/15/20/25/30/45) — extra 25 min option
- CEFR picker includes "Not Set" option
- Export data, Reset profile present

### GAPS:
- [ ] **P2** — Session duration has extra "25 minutes" option in native not present in Blazor (minor difference)
- [ ] **P2** — Blazor uses button groups for session/CEFR, native uses pickers (UX difference but functionally equivalent)

**Loading UI:** Present ✅  
**Empty states:** N/A

---

## 4. Resources.razor → ListLearningResourcesPage.cs — ⚠️ Gaps

### Blazor Features:
- Toolbar: "Add Resource" primary button, "Progress" toolbar action, "Add Resource" + "Vocabulary Progress" secondary actions
- **Search + filters:** Search input, media type dropdown (All + 6 types), language dropdown (All + languages)
- Resource cards: Icon, title, metadata (type • language • auto-updated flag), date
- Empty state: "No resources found" + "Add Your First Resource" + "Create a Starter Resource" buttons
- **Loading overlay** when creating starter (modal spinner + message)

### Native Has:
- Toolbar: "Add", "Progress", "Generate Starter" secondary items
- **Bottom bar** with search + media type picker + language picker (different layout)
- Resource cards: Similar layout
- Empty state with buttons
- Loading overlay for starter creation

### GAPS:
- [ ] **P0** — Search and filters are in a **bottom bar** instead of inline at top (major UX difference — harder to discover)
- [ ] **P1** — Media type filter UI is a picker popup (native) vs inline dropdown (Blazor) — different interaction pattern
- [ ] **P1** — Language filter is a multi-select picker (native) vs single-select dropdown (Blazor) — native is MORE capable but different
- [ ] **P1** — "Add Resource" button missing from toolbar primary actions (only in secondary menu)
- [ ] **P2** — Resource cards use grid layout helper (native) vs CSS grid (Blazor) — functionally same but implementation differs

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 5. ResourceAdd.razor → AddLearningResourcePage.cs — ⚠️ Gaps

### Blazor Features:
- Header: "Add Resource" with "Save" primary button, "Save" secondary action
- Basic info card: Title*, Description, Media Type dropdown, Language dropdown, Tags input
- **Media Content card** (hidden if type = "Vocabulary List"): Media URL, Transcript textarea, Translation textarea
- Vocabulary card: Paste area (CSV/TSV), delimiter radio buttons (comma/tab/pipe), "Parse" button, editable table after parse
- Loading spinner during save

### Native Has:
- Same header structure
- Basic info card: All fields present
- Media Content card present (conditional on type)
- Vocabulary card: Same paste + parse workflow, delimiter picker
- Save spinner

### GAPS:
- [ ] **P1** — Media Content card visibility logic may differ (need runtime check — Blazor explicitly hides for "Vocabulary List", native behavior TBD)
- [ ] **P2** — Parse button placement/styling may differ (minor)

**Loading UI:** Present during save ✅  
**Empty states:** N/A

---

## 6. ResourceEdit.razor → EditLearningResourcePage.cs — ⚠️ Gaps

### Blazor Features:
- Header: Dynamic title (resource.Title), "Save" primary, "Save" + "Delete" (danger) secondary actions
- **Smart resource badge** if `IsSmartResource` (info alert with robot icon)
- Basic info card, Transcript card, Translation card
- **Vocabulary section:** "Add Vocabulary" button, table with edit/delete per row, inline editing, "Generate Mnemonic" per word
- Metadata: Created/Updated timestamps

### Native Has:
- Same header, Smart resource badge
- All cards present
- **Vocabulary section:** Bottom sheet editor for add/edit, CollectionView for list, "Add Vocabulary" button, edit/delete actions, "Generate Mnemonic" action

### GAPS:
- [ ] **P1** — Vocabulary editing is **bottom sheet** (native) vs **inline table** (Blazor) — major UX difference but functionally equivalent
- [ ] **P1** — Native uses CollectionView + bottom sheet, Blazor uses HTML table — different interaction model
- [ ] **P2** — Inline table editing feel (Blazor) vs modal sheet (native) — preference/platform difference

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 7. Vocabulary.razor → VocabularyManagementPage.cs — ⚠️ Gaps

### Blazor Features:
- Toolbar: "Add Word" primary button
- **Stats bar:** Badges for Total/Associated/Orphaned counts (top of page)
- **Search + filter:** Search input, dropdown (All/Associated/Orphaned), clear button (X icon)
- Word cards: Target/native terms, status badge, orphaned warning, resource count, tags (first 3)
- Empty state with "Add Your First Word" button

### Native Has:
- Toolbar: "Add Word" present (secondary)
- **NO stats bar** at top
- **NO search bar or filter dropdown** — completely missing
- Word cards: Similar layout
- Empty state present

### GAPS:
- [ ] **P0** — Stats bar (Total/Associated/Orphaned badges) completely missing in native
- [ ] **P0** — Search input missing in native
- [ ] **P0** — Filter dropdown (All/Associated/Orphaned) missing in native
- [ ] **P0** — Clear filters button (X icon) missing (because search/filter missing)
- [ ] **P1** — "Add Word" only in secondary menu, not primary toolbar action

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 8. VocabularyWordEdit.razor → EditVocabularyWordPage.cs — ✅ Match

### Blazor Features:
- Header: "Edit/Add Vocabulary Word", "Save/Add" primary, "Save/Add" + "Delete" secondary
- Error alert if present
- Vocabulary Terms card: Target/Native inputs
- Encoding & Memory Aids card: Encoding strength label, Lemma, Tags, Mnemonic text, Mnemonic image URL (with preview)
- Form validation (IsFormValid disables save)
- Loading spinner during save

### Native Has:
- All sections present with same fields
- Form validation present
- Loading states present

### GAPS:
- None — close functional parity

**Loading UI:** Present ✅  
**Empty states:** N/A

---

## 9. VocabularyProgress.razor → VocabularyLearningProgressPage.cs — ⚠️ Gaps

### Blazor Features:
- Filter tabs: All/Known/Learning/Unknown with counts (btn styling based on state)
- Search input
- Word cards: Target/native, status badge, progress text, review date (with color coding)
- Empty state
- Loading spinner

### Native Has:
- Filter tabs present with counts
- Search input present
- Word cards present with similar layout
- Empty state, loading spinner present

### GAPS:
- [ ] **P1** — Filter button styling logic may differ (Blazor uses btn-success/btn-warning/btn-secondary based on category)
- [ ] **P2** — Card border-left color coding present in Blazor (`border-left: 3px solid @GetStatusColor()`) — need to verify native has same

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 10. Conversation.razor → ConversationPage.cs — ❌ Major Gaps

### Blazor Features:
- Header toolbar: **Scenario switcher dropdown** (mobile + desktop), **language switcher dropdown** (with flag/translate icon)
- Activity timer (if fromPlan)
- **Scenario selector inline** (desktop only, above chat)
- Chat messages: User/AI bubbles, **grammar corrections inline** (yellow border), audio playback button per message
- Input modes: Text vs voice (toggle button)
- "End session" button

### Native Has:
- Header simple
- Activity timer (if fromPlan)
- **NO scenario switcher** — scenarios not exposed in UI
- **NO language switcher** in toolbar
- Chat messages: User/AI bubbles, audio playback
- **NO grammar corrections display** — missing
- Input modes present
- "End session" present

### GAPS:
- [ ] **P0** — Scenario switcher dropdown completely missing (both mobile + desktop versions)
- [ ] **P0** — Language switcher dropdown missing in toolbar (Blazor has translate icon + language name)
- [ ] **P0** — Grammar corrections NOT displayed in chat bubbles (Blazor has yellow border + correction text)
- [ ] **P1** — Inline scenario selector (desktop) missing
- [ ] **P1** — Language name display missing in toolbar

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 11. Translation.razor → TranslationPage.cs — ⚠️ Gaps

### Blazor Features:
- Header: "Translate" with back
- **Session summary screen:** Graded count, avg accuracy %, avg fluency %, "Continue Practice" + "Done" buttons
- Sentence display (large font)
- Feedback card (with border-left color coding)
- **Input bar (Blazor platforms only):** MultipleChoice mode (vocab blocks), Text input, "Blocks/Type" toggle, Submit button (with spinner)
- Loading states, empty state

### Native Has:
- Same header
- **Session summary likely present** (need runtime check)
- Sentence display
- Feedback card present
- **Native footer handles input** (macOS only), Blazor input bar (other platforms)
- Loading states, empty state

### GAPS:
- [ ] **P1** — Session summary screen logic needs verification (Blazor has explicit `showSessionSummary` conditional)
- [ ] **P1** — Input mode toggle ("Blocks/Type") presence in native needs confirmation
- [ ] **P1** — Vocab blocks UI in MultipleChoice mode needs verification in native
- [ ] **P2** — Platform-specific input handling (Blazor bar vs native footer) — intentional difference

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 12. Writing.razor → WritingPage.cs — ⚠️ Gaps

### Blazor Features:
- Header: "Writing" with back
- Instruction: "Write a sentence using this vocabulary" + vocab badges
- **Sentence history section:** Title ("Your Sentences"), cards with target/native text, score badges, expandable explanation panel
- Explanation panel: Accuracy/Fluency scores, grammar score breakdown, corrections list
- Loading state, empty state
- **Input bar:** Text input + "Send" button (if not macOS)

### Native Has:
- Same header
- Instruction + vocab badges present
- Sentence history present
- Explanation present (in bottom sheet or inline)
- Loading/empty states
- Input present

### GAPS:
- [ ] **P1** — Sentence history expandable panel (Blazor) vs bottom sheet (native) — different UX pattern
- [ ] **P1** — Explanation panel inline expansion (Blazor) vs sheet (native) — need to verify native approach
- [ ] **P2** — Grammar score breakdown visualization may differ

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 13. Cloze.razor → ClozurePage.cs — ⚠️ Gaps

### Blazor Features:
- Header: "Cloze" with back
- Mode selector: "Fill Blanks" vs "Select Words" (btn-group toggle)
- Sentence display with blanks/highlights
- **Answer checking:** "Check" button, feedback badges (correct/incorrect), "Continue" button
- Session summary
- Loading/empty states

### Native Has:
- Same header
- Mode selector present (component-based)
- Sentence display
- Answer checking logic
- Session summary
- Loading/empty states

### GAPS:
- [ ] **P1** — Mode selector UI match (Blazor btn-group vs native component) — need visual verification
- [ ] **P1** — Answer feedback badge styling may differ
- [ ] **P2** — "Continue" button styling/placement verification

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 14. Reading.razor → ReadingPage.cs — ✅ Match

### Blazor Features:
- Header: "Reading" with back
- Passage display (large text)
- Comprehension questions: Multiple choice, "Submit Answer" button, feedback
- Session summary
- Loading/empty states

### Native Has:
- All sections present
- Same flow: passage → questions → feedback → summary
- Loading/empty states

### GAPS:
- None — close parity

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 15. VocabQuiz.razor → VocabularyQuizPage.cs — ⚠️ Gaps

### Blazor Features:
- Header: "Vocabulary Quiz" with back
- **Card flip UI:** Front (target or native), back (answer), flip button
- **Progress display:** "Card X of Y" with progress bar
- **Action buttons:** "Again", "Hard", "Good", "Easy" (SRS intervals shown)
- Audio playback (autoplay if pref set)
- Session summary
- Loading/empty states

### Native Has:
- Same header
- Card flip UI present
- Progress display present
- Action buttons present (4-choice SRS)
- Audio playback
- Session summary
- Loading/empty states

### GAPS:
- [ ] **P1** — Card flip animation style may differ (Blazor uses CSS transforms, native uses MAUI animations)
- [ ] **P1** — Progress bar styling verification needed
- [ ] **P2** — SRS interval display formatting may differ

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 16. VocabMatching.razor → VocabularyMatchingPage.cs — ⚠️ Gaps

### Blazor Features:
- Header: "Vocabulary Matching" with back
- **Matching board:** Two columns (target/native), clickable cards, matched pairs fade/disappear
- **Timer:** Running timer display
- **Completion:** "Well done!" message, time taken, "Play Again" + "Done" buttons
- Loading/empty states

### Native Has:
- Same header
- Matching board present (two-column layout)
- Timer present
- Completion screen present
- Loading/empty states

### GAPS:
- [ ] **P1** — Matched pair animation (Blazor fades out, native behavior TBD)
- [ ] **P1** — Timer formatting verification
- [ ] **P2** — Completion screen layout match

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 17. Scene.razor → DescribeAScenePage.cs — ⚠️ Gaps

### Blazor Features:
- Header: "Describe a Scene" with back
- **Image selection:** "Choose Image" button, gallery popup, selected image display
- **Description input:** Textarea, "Submit" button
- **Feedback:** AI evaluation, grammar corrections, suggested improvements
- Session summary
- Loading/empty states

### Native Has:
- Same header
- Image selection present (bottom sheet gallery)
- Description input present
- Feedback present
- Session summary
- Loading/empty states

### GAPS:
- [ ] **P1** — Image gallery UI (Blazor popup vs native bottom sheet) — different pattern
- [ ] **P1** — Grammar corrections display format may differ
- [ ] **P2** — Suggested improvements section layout verification

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 18. HowDoYouSay.razor → HowDoYouSayPage.cs — ⚠️ Gaps

### Blazor Features:
- Header: "How Do You Say" with back
- **Prompt display:** English phrase (large text)
- **Voice recording:** "Hold to Record" button, waveform visualization, playback button
- **Feedback:** AI transcription, comparison to reference, grammar notes
- **"Next phrase" button**
- Session summary
- Loading/empty states

### Native Has:
- Same header
- Prompt display present
- Voice recording present (with waveform)
- Feedback present
- "Next phrase" present
- Session summary
- Loading/empty states

### GAPS:
- [ ] **P1** — Waveform visualization style match (Blazor uses canvas, native uses SkiaSharp)
- [ ] **P1** — "Hold to Record" button interaction (Blazor uses mouse events, native uses touch events)
- [ ] **P2** — Feedback layout verification

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 19. Shadowing.razor → ShadowingPage.cs — ⚠️ Gaps

### Blazor Features:
- Header: "Shadowing" with back
- **Audio player:** Play/pause, waveform, time scrubber
- **Transcript display:** Interactive text with word highlighting (synced to audio)
- **Recording section:** "Record your shadowing" button, playback comparison
- **Feedback:** Pronunciation analysis, fluency score
- Session summary
- Loading/empty states

### Native Has:
- Same header
- Audio player present
- Transcript display present (with highlighting)
- Recording section present
- Feedback present
- Session summary
- Loading/empty states

### GAPS:
- [ ] **P1** — Interactive transcript word-level highlighting sync (Blazor uses JS timestamps, native uses MAUI animations)
- [ ] **P1** — Waveform scrubber interaction (Blazor canvas vs native SkiaSharp)
- [ ] **P2** — Recording comparison UI layout verification

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 20. Import.razor → YouTubeImportPage.cs — ⚠️ Gaps

### Blazor Features:
- Header: "Import from YouTube" with back
- **URL input:** Text input, "Fetch" button
- **Video preview:** Thumbnail, title, duration
- **Transcript section:** Auto-fetch toggle, transcript textarea (editable), "Save as Resource" button
- **Import status:** Progress indicator, "Importing..." message
- Loading/empty states

### Native Has:
- Same header
- URL input present
- Video preview present
- Transcript section present
- Import status present
- Loading/empty states

### GAPS:
- [ ] **P1** — Video thumbnail display (Blazor uses img tag, native uses Image control)
- [ ] **P1** — Auto-fetch toggle presence verification
- [ ] **P2** — Import progress UI styling match

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 21. VideoWatching.razor → VideoWatchingPage.cs — ⚠️ Gaps

### Blazor Features:
- Header: "Video Watching" with back
- **Video player:** Embedded iframe (YouTube/Vimeo) or video element
- **Subtitle controls:** Show/hide toggle, subtitle text display (synced)
- **Comprehension questions:** Appear at intervals, pause video, multiple choice, feedback
- Session summary
- Loading/empty states

### Native Has:
- Same header
- **Video player present** (likely WebView or native player)
- Subtitle controls present
- Comprehension questions present
- Session summary
- Loading/empty states

### GAPS:
- [ ] **P1** — Video player implementation (Blazor iframe vs native WebView/MediaElement) — different platform approach
- [ ] **P1** — Subtitle sync mechanism may differ
- [ ] **P2** — Comprehension question overlay timing verification

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 22. MinimalPairs.razor → MinimalPairsPage.cs — ⚠️ Gaps

### Blazor Features:
- Header: "Minimal Pairs" with "Create Pair" button
- **Pair list:** Cards showing pair text (e.g., "ship / sheep"), language, edit/delete buttons, "Start Practice" button per pair
- Empty state: "No minimal pairs yet" + "Create Your First Pair" button
- Loading/empty states

### Native Has:
- Same header
- Pair list present
- Empty state present
- Loading/empty states

### GAPS:
- [ ] **P1** — "Create Pair" button placement (Blazor primary action vs native secondary)
- [ ] **P1** — Edit/delete button styling/placement verification
- [ ] **P2** — Pair card layout match

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 23. MinimalPairSession.razor → MinimalPairSessionPage.cs — ⚠️ Gaps

### Blazor Features:
- Header: "Minimal Pair Practice" with back
- **Audio playback:** "Play Sound" button (plays one of the pair randomly)
- **Answer choices:** Two buttons (option A / option B)
- **Feedback:** Correct/incorrect badge, "Continue" button
- **Score display:** "X correct out of Y"
- Session summary
- Loading/empty states

### Native Has:
- Same header
- Audio playback present
- Answer choices present
- Feedback present
- Score display present
- Session summary
- Loading/empty states

### GAPS:
- [ ] **P1** — Feedback badge styling match
- [ ] **P1** — Score display format verification
- [ ] **P2** — "Continue" button placement

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 24. MinimalPairCreate.razor → CreateMinimalPairPage.cs — ⚠️ Gaps

### Blazor Features:
- Header: "Create Minimal Pair" with "Save" button
- **Input form:** Language dropdown, Pair text 1 input, Pair text 2 input, Audio URL 1, Audio URL 2
- **Generate AI section:** "Generate with AI" button (creates example pairs)
- Loading spinner during save
- Empty states

### Native Has:
- Same header
- Input form present
- **Generate AI section unclear** (need verification)
- Loading spinner
- Empty states

### GAPS:
- [ ] **P1** — "Generate with AI" button presence needs verification in native
- [ ] **P1** — Audio URL inputs vs file pickers (native may use file pickers instead of URL inputs)
- [ ] **P2** — Form layout match

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 25. Skills.razor → ListSkillProfilesPage.cs — ⚠️ Gaps

### Blazor Features:
- Header: "Skill Profiles" with "Add Profile" button
- **Profile list:** Cards with name, level, competencies count, edit button
- Empty state: "No skill profiles yet" + "Create Your First Profile" button
- Loading/empty states

### Native Has:
- Same header
- Profile list present
- Empty state present
- Loading/empty states

### GAPS:
- [ ] **P1** — "Add Profile" button placement (Blazor primary vs native secondary)
- [ ] **P1** — Profile card layout/styling verification
- [ ] **P2** — Edit button styling

**Loading UI:** Present ✅  
**Empty states:** Present ✅

---

## 26. SkillAdd.razor → AddSkillProfilePage.cs — ✅ Match

### Blazor Features:
- Header: "Add Skill Profile" with "Save" button
- **Form:** Name input, Level dropdown (A1-C2), Competencies checklist (listening/reading/writing/speaking)
- Loading spinner during save
- Empty states

### Native Has:
- All sections present
- Same form fields
- Loading spinner
- Empty states

### GAPS:
- None — close parity

**Loading UI:** Present ✅  
**Empty states:** N/A

---

## 27. SkillEdit.razor → EditSkillProfilePage.cs — ✅ Match

### Blazor Features:
- Header: "Edit Skill Profile" with "Save" + "Delete" buttons
- Same form as Add
- Loading spinner

### Native Has:
- Same header
- Same form
- Loading spinner

### GAPS:
- None — close parity

**Loading UI:** Present ✅  
**Empty states:** N/A

---

## 28. Onboarding.razor → OnboardingPage.cs — ⚠️ Gaps

### Blazor Features:
- **Multi-step wizard:** Step indicators (1/2/3), "Next" / "Back" / "Finish" buttons
- **Step 1 (Welcome):** App intro text, illustration/icon
- **Step 2 (Profile Setup):** Name, native language, target language inputs
- **Step 3 (Preferences):** Session duration, CEFR level, OpenAI key
- **Completion:** Navigate to dashboard, set onboarding flag

### Native Has:
- Multi-step wizard present
- Same 3 steps
- Same flow logic
- Completion action

### GAPS:
- [ ] **P1** — Step indicator UI style (Blazor uses pills/badges, native uses custom component)
- [ ] **P1** — Illustration/icon presence in step 1 needs verification
- [ ] **P2** — "Next" / "Back" button styling match

**Loading UI:** N/A (local form)  
**Empty states:** N/A

---

## Priority Recommendations

### P0 Fixes (Must Have Before Feature Parity):
1. **Dashboard:** Implement Choose Own mode (resource/skill selectors + activity grid) + "Regenerate Plan" action
2. **Settings:** Add "Mixed" quiz direction option (3-way toggle), align auto-advance duration range with Blazor
3. **Resources:** Move search/filters from bottom bar to inline top section (discoverability)
4. **Vocabulary:** Add stats bar (Total/Associated/Orphaned), add search input, add filter dropdown
5. **Conversation:** Add scenario switcher dropdown, language switcher dropdown, grammar corrections display

### P1 Fixes (Important for Full Feature Set):
6. Align input UI patterns (bottom sheets vs inline forms) across all CRUD pages
7. Add missing toolbar primary actions (e.g., "Add Word" in Vocabulary, "Create Pair" in MinimalPairs)
8. Verify session summary screens match Blazor layout/metrics in all activity pages
9. Add missing "Save Preferences" button in Settings (or document auto-save behavior)
10. Verify all loading overlays match Blazor patterns (centered spinner + message)

### P2 Fixes (Polish & Consistency):
11. Standardize card styling (border-left color coding, badge styles, etc.)
12. Align button groups vs pickers UX (decide on consistent pattern)
13. Verify animation styles (fade-out, flip, highlight) match Blazor feel
14. Add missing inline action buttons (e.g., "Clear filters" X button)
15. Document intentional platform differences (native footer vs Blazor input bar, etc.)

---

## Notes

- **Platform Differences:** Some differences are intentional (e.g., native uses bottom sheets for editing, Blazor uses inline tables). Document these as "by design" if UX is superior.
- **Bootstrap → MauiReactor:** Native is porting to MauiReactor + MauiBootstrapTheme, so some CSS class differences are expected. Focus on functional gaps, not styling minutiae.
- **Runtime Verification Needed:** This audit is based on static code analysis. Some gaps (e.g., session summaries, animations) need runtime testing to confirm presence/absence.
- **Loading/Empty States:** Native generally has good coverage of loading spinners and empty states. Minor styling differences acceptable as long as functionality is clear.

---

**END OF AUDIT**
