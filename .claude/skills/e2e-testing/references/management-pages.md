# Management Pages E2E Tests

## 1. Learning Resources List (`/resources`)

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/resources` | Resource cards displayed with title, media type, language |
| 2 | Type in search box | List filters by title match |
| 3 | Select media type filter | List filters by type |
| 4 | Click a resource card | Navigates to `/resources/edit/{id}` |
| 5 | Click "Add Resource" | Navigates to `/resources/add` |

### Native iOS Resources browse/detail/metadata-edit acceptance (R07/R09)

This is a bounded native replacement of the WebApp Resources and Resource Edit
journey, not a replacement of WebApp Add/Delete/import/vocabulary editing.
Use the signed-in canonical squad-jayne simulator and its populated owner data.
Never seed, delete, or patch that database to make a test pass. Capture the
selected non-smart row's original metadata and a consistent SQLite backup
before making a reversible edit.

| State | Action | Expected native result |
|-------|--------|------------------------|
| Top level | Open the authenticated app; select Resources, then Dashboard and Resources | Exactly two working bottom tabs, each with its own `UINavigationController` stack; no tab during sign-in, and the assigned quiz retains its full-screen stack |
| Populated | Open Resources | Owner's actual resources, smart first/alphabetical then user-owned by `UpdatedAt` descending, native virtualized list with title, type, language and created date; count and real type/language options |
| Search | Search separately by title, description, tags and language | Case-insensitive owned matches only; clearing search restores the current type/language selection and source order |
| Filters and no match | Select a real synced type and language, then search for an absent term; clear both | Only matching owned rows; distinct no-match message (not the true-empty message); clearing restores 14 canonical rows |
| Adaptive filters | In English and Korean, light and dark, test standard and accessibility-extra-large text; in an isolated preview, select long type and language values | Default and selected labels remain visually complete inside their buttons without clipping or overlap; menu rows and selected values expose full accessibility text; each button is at least 44×44 pt, stays within portrait bounds, and search, keyboard and results remain usable |
| True empty and load error | Exercise isolated presenter fixtures, not the canonical database | Empty-only owner shows true empty; failed DB read shows retryable load error, not zero resources |
| Detail | Select a non-smart item with mapped vocabulary and transcript | System push and Back; full title, description, type, language, URL, tags, timestamps, bounded read-only transcript/translation previews and vocabulary count; long title/URL wrap and scroll |
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
