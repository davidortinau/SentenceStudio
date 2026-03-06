# Management Pages E2E Tests

## 1. Learning Resources List (`/resources`)

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/resources` | Resource cards displayed with title, media type, language |
| 2 | Type in search box | List filters by title match |
| 3 | Select media type filter | List filters by type |
| 4 | Click a resource card | Navigates to `/resources/edit/{id}` |
| 5 | Click "Add Resource" | Navigates to `/resources/add` |

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
