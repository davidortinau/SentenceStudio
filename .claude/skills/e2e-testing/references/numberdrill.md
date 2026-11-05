# NumberDrill E2E Tests

## Phase 1: Listen&Type and Read&Produce (Shipped)

**Prereqs:** Korean language profile  
**URL:** `/numberdrill` (context picker), then `/numberdrill?context=<context>&submode=<mode>`  
**Services:** NumberDrillService, NumberMasteryProgressRepository, NumberAttemptRepository, ElevenLabs (audio)

### Context Picker Flow

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate to `/numberdrill` | Context picker displays Time, Age, Counting cards |
| 2 | Select a context (e.g., Counting) | Sub-mode picker displays: Listen&Type, Read&Produce |
| 3 | Select Listen&Type | Item page loads with Korean audio cue |
| 4 | Play audio (🔊) | Audio plays; spinner while loading |
| 5 | Type correct answer in Hangul | "Correct!" feedback; auto-advance after 1.2s |
| 6 | Type incorrect answer | "The answer is: X" feedback; shows canonical answer |
| 7 | Complete 10 items | Summary screen with accuracy % |
| 8 | Navigate back | Returns to context picker |

**DB Checks:**
```sql
-- Verify NumberAttempt records for Listen&Type
SELECT UserId, ContextCode, SubModeCode, Bucket, IsCorrect, LatencyMs, AnsweredAt
FROM NumberAttempt
WHERE UserId = '<userId>' AND SubModeCode = 'ListenAndType'
ORDER BY AnsweredAt DESC LIMIT 5;

-- Verify NumberMasteryProgress updated with DueDate
SELECT UserId, ContextCode, SubModeCode, Bucket, MasteryLevel, DueDate
FROM NumberMasteryProgress
WHERE UserId = '<userId>' AND SubModeCode = 'ListenAndType'
LIMIT 5;
```

**Pitfalls:**
- UserId must be GUID from `active_profile_id`, never `"1"`
- Audio uses ElevenLabs TTS with Korean voice (not JS fallback like vocab quiz)
- Bucket ranges: `1-10`, `11-99` for Phase 1

## Phase 2: TapTheCounter (Current Wave)

**Prereqs:** Korean language profile  
**URL:** `/numberdrill?context=Counting&submode=TapTheCounter`  
**Services:** NumberDrillService, NumberMasteryProgressRepository, NumberAttemptRepository

### TapTheCounter Flow

| Step | Action | Verify |
|------|--------|--------|
| 1 | Navigate via context picker → Counting | Sub-mode picker includes "Tap the Counter" |
| 2 | Select "Tap the Counter" | Item page loads with Korean prompt text |
| 3 | View the grid | 80×80px chips rendered; border-only (no fill) |
| 4 | Tap correct number of chips | Border color changes; animation on tap |
| 5 | Submit answer | Correct/incorrect feedback; auto-advance |
| 6 | Tap wrong number | "Incorrect" feedback; shows expected count |
| 7 | Complete round | Summary screen with accuracy |

**DB Checks:**
```sql
-- Verify TapTheCounter attempts
SELECT UserId, SubModeCode, Bucket, IsCorrect, LatencyMs, AnsweredAt
FROM NumberAttempt
WHERE UserId = '<userId>' AND SubModeCode = 'TapTheCounter'
ORDER BY AnsweredAt DESC LIMIT 5;
```

**Pitfalls:**
- Chips must be 80×80px for mobile usability
- Border-only styling (no background fill) per design spec
- Tap animations should be smooth (CSS transitions, not JS)
- DB persistence at canonical path: `/Users/davidortinau/Library/Application Support/sentencestudio/server/sentencestudio.db` (SQLite) OR PostgreSQL (check AppHost config)

## Plan-Slot Integration (Wave 2)

**Feature:** NumberDrill replaces VocabularyMatching in Step 4 of DailyPlan when `NumberMasteryProgress.DueDate <= tomorrow`

### Integration E2E

| Step | Action | Verify |
|------|--------|--------|
| 1 | Seed NumberMasteryProgress row with `DueDate <= tomorrow` | Row exists in DB for test user |
| 2 | Navigate to `/` (Dashboard) | Plan generation triggers |
| 3 | Check Step 4 card | **NumberDrill card** appears (not VocabularyMatching) |
| 4 | Verify card title | Uses `PlanItemNumberDrillTitle` localization key (not snake_case AI key) |
| 5 | Tap NumberDrill card | Routes to `/numberdrill` with resolved due bucket |
| 6 | Complete session | DB updated; DueDate advances |
| 7 | Return to Dashboard (next day) | If no more due items, VocabularyMatching returns to Step 4 |

**Seeding Command (SQL):**
```sql
INSERT INTO NumberMasteryProgress (UserId, ContextCode, SubModeCode, Bucket, MasteryLevel, DueDate, CreatedAt, UpdatedAt)
VALUES (
  'f452438c-b0ac-4770-afea-0803e2670df5', -- David's Korean profile
  'Counting',
  'ListenAndType',
  '1-10',
  1, -- Novice
  CURRENT_DATE, -- Due today
  CURRENT_TIMESTAMP,
  CURRENT_TIMESTAMP
);
```

**Pitfalls:**
- DailyPlan service must query NumberMasteryProgress for `DueDate <= CURRENT_DATE + 1` (tomorrow inclusive)
- Localization key is `PlanItemNumberDrillTitle`, NOT `plan_item_numberdrill_title` (snake_case from AI prompt)
- Plan regenerates daily; test by seeding DueDate and refreshing Dashboard

## Phase 2 Future: Disambiguate (Deferred)

**Status:** Deferred to Wave 3; Kaylee owns E2E for this sub-mode.

**Overview:** Paired-prompt design testing same digit in different contexts (Sino vs Native).  
**Example:** "3rd floor" (삼 층, Sino ordinal) vs "3 floors of stairs" (세 층, Native count).

**Placeholder:** When Kaylee implements this, add E2E steps here matching paired-choice UI pattern.

## Phase 2 Future: Listen-and-Place

**Status:** Not yet implemented; tentative Phase 2 addition.

**Overview:** Drag-and-drop numbers onto a visual timeline/grid (e.g., clock face for Time context).

**Placeholder:** When implemented, add E2E steps for drag-drop interactions, visual feedback, and DB verification.

## WebApp/MAUI regression: native-port fixes (docs/native-port-backports.md B18-B23, B34, B37)

Use an isolated test database and the canonical test accounts. Element IDs:
`numberdrill-gate`, `numberdrill-feedback`, `numberdrill-override`,
`numberdrill-next`, `numberdrill-error`, `numberdrill-hint-a`/`-b`,
`numberdrill-summary`, `numberdrill-summary-median`,
`numberdrill-summary-patterns`, `drill-progress`, `drill-correct-count`;
mode chips carry `data-submode`.

Learning Value Gate matrix (objective: retrieve and recognize Korean number
forms in context; every reachable row has Korean in the prompt or response):

| Sub-mode | Prompt | Response | L2 action | Verdict |
|---|---|---|---|---|
| ListenAndType | Korean audio | typed digits or Hangul | comprehension | ship |
| ReadAndProduce | digits | typed Hangul | production | ship |
| TapTheCounter (Counting) | Korean sentence frame | Korean counter chip | recall | ship |
| ListenAndPlace (Time) | Korean audio | digital time card; feedback shows the correct time | comprehension | ship |
| Disambiguate | English contexts | Korean forms; each prompt's hint after Submit | recall of the number system | ship |
| Any, learner's target language not Korean | none | none | none | unreachable: gate message and Go Back |

| Step | Action | Verify |
|------|--------|--------|
| R1 | Open `/numberdrill` as a learner whose target language is not Korean | `numberdrill-gate` shows the Korean-only message and Go Back; no setup card; no plan minutes recorded |
| R2 | As a Korean learner, open setup | Modes in seed order: Listen & Type, Read & Produce, Disambiguate the System (Any); Time adds Listen & Place last; Counting adds Tap the Counter |
| R3 | Time + Listen & Place: Start, Play, tap a time card | `numberdrill-feedback` (verdict, Number, Korean) and `drill-correct-count` update; auto-advance after 1.5 s; a `NumberAttempt` row with `SubModeCode = 'ListenAndPlace'` for the learner |
| R4 | Counting + Tap the Counter: tap a chip, press Next during the 1.5 s pause | `drill-progress` moves by exactly one item |
| R5 | Disambiguate: choose A and B, Submit Both | Both cards stay; `numberdrill-hint-a` and `numberdrill-hint-b` show; verdict Both correct / One correct / Review the hints |
| R6 | Answer wrong, press "I was correct" | Verdict "정확해요! (Overridden)"; that `NumberAttempt.IsCorrect = true` (ErrorClass kept); its `NumberMasteryProgress` has one more correct answer and the SM-2 values of a correct grade; auto-advance |
| R7 | As R6, but change the mastery row first (another device practices the same bucket) | `numberdrill-error` says the correction could not be saved; no auto-advance; attempt and mastery rows unchanged |
| R8 | Finish a round with some wrong answers | `numberdrill-summary-median` is the round's median answer time (not 0 ms); `numberdrill-summary-patterns` lists the wrong answers' error classes; an overridden answer is not listed |
| R9 | Switch display language to Korean | Setup, session and summary copy in Korean; the next-review date is in the learner's time zone |
| R10 | Play an item's audio, restart (MAUI: reinstall on a simulator), play it again | The cached clip plays without a new synthesis; one `StreamHistory` row for the cue and voice |
| R11 | Disambiguate (B61): read both strips, then Submit the two answers; next item, Submit one wrong choice | Four choices per strip, each once; no two differ only by spacing (no 삼층 beside 삼 층); both answers: Both correct; a wrong choice: that card red and One correct; the learner's `NumberAttempt` row has `SubModeCode = 'Disambiguate'`, `UserAnswer = 'A | B'` and the matching `IsCorrect` |
