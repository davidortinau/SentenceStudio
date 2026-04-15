# Cross-Activity Mastery — Specification

> **Source of truth** for how non-quiz activities contribute to vocabulary mastery. If the code doesn't match this spec, the code is wrong.
>
> **Created:** 2025-07-25
> **Revised:** 2025-07-25 — R2: Captain's processing-order/SRS/dedup decisions, reviewer mechanical fixes (verification probe separation, GradeTranslation method, LastExposedAt, [NOT YET IMPLEMENTED] markers)
> **Author:** Zoe (Lead) — derived from codebase audit + Captain's design decisions
> **Captain's decisions captured in:** `.squad/decisions/inbox/copilot-directive-2025-07-25-cross-activity.md`

---

## 0. Relationship to quiz-learning-journey.md

This spec and `quiz-learning-journey.md` are **siblings**, not parent-child. They share a common scoring engine but own different concerns.

| Concern | Owner |
|---|---|
| Scoring formulas: EffectiveStreak, MasteryScore, penalty multipliers, mastery threshold, SRS scheduling | `VocabularyProgressService.RecordAttemptAsync` (code is source of truth; both specs reference it) |
| Quiz session lifecycle: rounds, turns, batch pools, mode selection (MC vs Text), tiered rotation, PendingRecognitionCheck, DueOnly filter | `quiz-learning-journey.md` |
| Per-activity recording rules, DifficultyWeight assignments, multi-word sentence scoring, AI grading extensions, passive exposure tracking | **This spec** (`cross-activity-mastery.md`) |

**Rule:** If a formula or constant appears in both specs, `VocabularyProgressService.cs` is the tiebreaker. Constants for reference:

| Constant | Value | Location |
|---|---|---|
| `MASTERY_THRESHOLD` | 0.85 | VocabularyProgressService.cs:15 |
| `MIN_PRODUCTION_FOR_KNOWN` | 2 | VocabularyProgressService.cs:16 |
| `EFFECTIVE_STREAK_DIVISOR` | 7.0 | VocabularyProgressService.cs:17 |
| `WRONG_ANSWER_PENALTY` | 0.6 | VocabularyProgressService.cs:18 |

> **[NOT YET IMPLEMENTED] — R5 Quiz Spec Formulas**
>
> The following formulas from `quiz-learning-journey.md` (R5) are **approved by the Captain but not yet implemented in code**. Both specs reference them; implementers must build them before the formulas take effect:
>
> - **DifficultyWeight streak acceleration:** `CurrentStreak += DifficultyWeight` instead of `CurrentStreak++`. Currently code does `progress.CurrentStreak++` (simple +1 regardless of weight).
> - **CurrentStreak as float:** R5 specifies `CurrentStreak` changes from `int` to `float` to support fractional increments. Currently `VocabularyProgress.CurrentStreak` is `int`.
> - **Temporal weighting (scaled penalties):** Wrong-answer penalty scales by track record (0.6–0.92 multiplier, partial streak preservation 0–50%). Currently code applies flat `*= 0.6` and full streak reset.
> - **Recovery boost:** `+0.02` mastery boost per correct answer during recovery period (`MasteryScore > streakScore`). Currently code uses simple `Math.Min(effectiveStreak / 7.0, 1.0)`.
>
> **Phase 0 of implementation** (shared between both specs) must address these before the cross-activity work begins, or both specs will reference formulas that don't exist in code.

---

## 1. Activity Taxonomy

### 1.1 Canonical Difficulty Ordering

The Captain's ordering, from easiest to hardest:

```
passive exposure < MC recognition < text entry < cloze < conversation < translation = writing/scene < quiz sentence shortcut
```

### 1.2 Classification Table

Every activity is classified along two axes and assigned a DifficultyWeight.

| Activity | Category | Input Type | Single/Multi-word | DifficultyWeight | InputMode | ContextType | Records Mastery? |
|---|---|---|---|---|---|---|---|
| **Reading (word tap)** | Passive | Lookup | Single | 0.0 (log only) | — | — | No (exposure only) |
| **VocabMatching** | Recognition | MC | Single | 0.8 | MultipleChoice | Isolated | Yes (already) |
| **WordAssociation** | Recognition | MC | Single | 1.0 | MultipleChoice | Isolated | Yes (already) |
| **VocabQuiz MC** | Recognition | MC | Single | 1.0 | MultipleChoice | Isolated | Yes (already) |
| **Cloze** | Production | Text (gap fill) | Single | 1.2 | Text | Sentence | Yes (already) |
| **Conversation** | Production | Text (chat) | Multi-word | 1.2 | Text | Application | **No → Yes** |
| **VocabQuiz Text** | Production | Text (recall) | Single | 1.5 | Text | Isolated | Yes (already) |
| **Translation** | Production | Text (sentence) | Multi-word | 1.5 | Text | Application | **No → Yes** |
| **Writing** | Production | Text (sentence) | Multi-word | 1.5 | Text | Application | Yes (already, weight change 1.0→1.5) |
| **Scene** | Production | Text (sentence) | Multi-word | 1.5 | Text | Application | **No → Yes** |
| **VocabQuiz Sentence** | Production | Text (targeted) | Single (scored) | 2.5 | Text | Sentence | Yes (already) |

### 1.3 Excluded Activities

These activities do **not** record vocabulary mastery and are out of scope for this spec:

| Activity | Reason |
|---|---|
| **Shadowing** | Audio repetition — no text output to analyze for vocabulary correctness |
| **VideoWatching** | Passive consumption — watching/listening only |
| **HowDoYouSay** | Reference/lookup tool — no graded output |
| **MinimalPairs** | Separate phonetics system (`MinimalPairSessionRepository`), orthogonal to vocabulary mastery |

### 1.4 DifficultyWeight Rationale

- **0.0 (Passive):** Reading lookups. No mastery change. Logged as `VocabularyLearningContext` for analytics (exposure count, recency) but `RecordAttemptAsync` is NOT called.
- **0.8 (VocabMatching):** Easiest MC — see both term and definition, tap to match. Lower than Quiz MC because options are more constrained.
- **1.0 (Quiz MC, WordAssociation):** Standard MC — select from 4 options with distractors.
- **1.2 (Cloze, Conversation):** Cloze requires recall with sentence context hints. Conversation is real-time production but heavily scaffolded (quick phrases, AI partner context, no explicit vocabulary targeting).
- **1.5 (Quiz Text, Translation, Writing, Scene):** Production from memory. Quiz Text = type the word. Translation/Writing/Scene = compose Korean sentences using vocabulary naturally. All require active recall.
- **2.5 (Quiz Sentence Shortcut):** Highest — the user is specifically challenged on one target word AND must produce a full sentence demonstrating it. Deliberate + constrained + productive.

---

## 2. Penalty Rules

### 2.1 Standard Penalty (All Activities Except Conversation)

Wrong vocabulary usage applies the **standard penalty** from `VocabularyProgressService`:

```
MasteryScore *= WRONG_ANSWER_PENALTY (0.6)
CurrentStreak = 0
ProductionInStreak = 0
ReviewInterval = 1 day  (SRS resets — same as Quiz)
NextReviewDate = tomorrow
```

**SRS reset is the same everywhere (Captain's decision).** Wrong usage in Writing, Translation, Scene, or any other activity resets `ReviewInterval` to 1 day — identical to Quiz. No special SRS softening for non-quiz activities.

This applies to: VocabQuiz (all modes), VocabMatching, WordAssociation, Cloze, Translation, Writing, Scene.

### 2.2 Conversation Penalty (Softer)

**Captain's decision:** Conversation uses a softer penalty multiplier of **0.8** (instead of 0.6) because:
- It's real-time chat with time pressure
- The AI partner provides scaffolding and context
- Penalizing as hard as deliberate writing would discourage experimentation

```
// Conversation only:
MasteryScore *= 0.8  (instead of standard 0.6)
CurrentStreak = 0    (same — streak resets on any wrong)
ProductionInStreak = 0 (same)
ReviewInterval = 1 day (same — SRS reset is universal, Captain's decision)
NextReviewDate = tomorrow (same)
```

**Implementation:** `RecordAttemptAsync` currently hardcodes `WRONG_ANSWER_PENALTY = 0.6`. Two options:

- **Option A (recommended):** Add an optional `penaltyOverride` parameter to `VocabularyAttempt`. If set, `RecordAttemptAsync` uses it instead of the constant. Only Conversation passes 0.8; all other callers omit it (get default 0.6).
- **Option B:** Conversation calls a separate method. Rejected — duplicates scoring logic.

### 2.3 Penalty Does Not Apply To

- **Passive exposure** (Reading lookups): No `RecordAttemptAsync` call, so no penalty possible.
- **Words not in the user's vocabulary:** If the AI identifies a word the user wrote but it doesn't match any tracked `VocabularyWord`, it is ignored (no phantom penalties on untracked words).

---

## 3. Sentence Vocabulary Extraction Pipeline

### 3.1 The Problem

When a user writes a Korean sentence in Translation, Writing, Scene, or Conversation, that sentence may contain **multiple tracked vocabulary words**. Each word must be scored independently:

- Word A used correctly → credit for Word A
- Word B used incorrectly → penalty for Word B
- Word C not in user's vocabulary → ignored

### 3.2 The `VocabularyAnalysis` Model (Already Exists)

Defined in `GradeResponse.cs`:

```csharp
public class VocabularyAnalysis
{
    public string UsedForm { get; set; }        // Conjugated/inflected form the user wrote
    public string DictionaryForm { get; set; }   // Base/dictionary form for matching
    public string Meaning { get; set; }          // Native language meaning
    public bool UsageCorrect { get; set; }       // Was this word used correctly?
    public string UsageExplanation { get; set; } // Why right/wrong
}
```

This model is returned as `List<VocabularyAnalysis>` on `GradeResponse.VocabularyAnalysis`. Currently `TeacherSvc.GradeSentence()` (used by Writing) and `TeacherSvc.GradeTranslation()` both request `vocabulary_analysis` in their templates.

### 3.3 The Existing Pattern (Writing.razor)

Writing.razor already implements the full pipeline (lines 314–348):

```
1. AI grades user's sentence → returns GradeResponse with VocabularyAnalysis[]
2. For each VocabularyAnalysis item:
   a. Match DictionaryForm against allVocabulary (case-insensitive on TargetLanguageTerm)
   b. If matched → call RecordAttemptAsync(wordId, wasCorrect: UsageCorrect, ...)
   c. If not matched → skip (not a tracked word)
3. Handle verification probe for Familiar words
```

This pattern must become a **shared service method** to avoid copy-pasting into 4 pages.

### 3.4 New Method: `ExtractAndScoreVocabularyAsync`

Add to `VocabularyProgressService`:

```csharp
/// <summary>
/// Scores all tracked vocabulary words found in an AI grading response.
/// Each word in VocabularyAnalysis is matched against the user's vocabulary
/// and scored independently via RecordAttemptAsync.
/// </summary>
/// <returns>List of scored results for UI feedback.</returns>
public async Task<List<VocabScoringResult>> ExtractAndScoreVocabularyAsync(
    List<VocabularyAnalysis>? vocabularyAnalysis,
    List<VocabularyWord> userVocabulary,
    string userId,
    string activity,
    float difficultyWeight,
    float? penaltyOverride = null)
```

**Parameters:**

| Parameter | Description |
|---|---|
| `vocabularyAnalysis` | The `VocabularyAnalysis` list from the AI grading response. Null/empty = no-op. |
| `userVocabulary` | The user's tracked vocabulary words (pre-loaded by the activity page). |
| `userId` | Active user profile ID. |
| `activity` | Canonical activity name (see section 3.5). |
| `difficultyWeight` | From section 1.2 table. |
| `penaltyOverride` | Optional. If provided, overrides `WRONG_ANSWER_PENALTY` for incorrect answers. Used by Conversation (0.8). |

**Return type:**

```csharp
public class VocabScoringResult
{
    public VocabularyWord Word { get; set; }
    public VocabularyProgress UpdatedProgress { get; set; }
    public bool WasCorrect { get; set; }
    public string UsedForm { get; set; }
    public string? UsageExplanation { get; set; }
}
```

**Algorithm:**

```
1. If vocabularyAnalysis is null or empty → return empty list
2. Deduplicate: items = vocabularyAnalysis.DistinctBy(v => v.DictionaryForm)
   // First occurrence wins. Prevents scoring the same word twice
   // when it appears multiple times in one sentence.
3. results = []
4. verificationProbes = []   // Collect, don't fire inline
5. For each item in items (deduplicated):
   a. matched = userVocabulary.FirstOrDefault(v =>
        v.TargetLanguageTerm.Equals(item.DictionaryForm, OrdinalIgnoreCase))
   b. If matched is null → continue (not a tracked word)
   c. Build VocabularyAttempt:
      - VocabularyWordId = matched.Id
      - UserId = userId
      - Activity = activity
      - InputMode = "Text"
      - WasCorrect = item.UsageCorrect
      - DifficultyWeight = difficultyWeight
      - PenaltyOverride = penaltyOverride  // NEW field
      - ContextType = "Application"
      - UserInput = item.UsedForm ?? item.DictionaryForm
      - ExpectedAnswer = item.DictionaryForm
   d. updatedProgress = await RecordAttemptAsync(attempt)
   e. results.Add(new VocabScoringResult { ... })
   f. If updatedProgress.IsFamiliar:
      - verificationProbes.Add((matched.Id, userId, item.UsageCorrect))
6. // Handle verification probes AFTER all scoring completes
   For each (wordId, uid, wasCorrect) in verificationProbes:
      await HandleVerificationProbeResultAsync(wordId, uid, wasCorrect)
7. Return results
```

> **Processing order (Captain's decision):** Each word's scoring is independent because each has its own `VocabularyProgress` record. `RecordAttemptAsync` operates on different word records — processing order within a sentence does not affect outcomes.

> **Verification probe separation (R2 fix):** Do NOT call `HandleVerificationProbeResultAsync` inside the scoring loop. `HandleVerificationProbeResultAsync` directly overwrites mastery/streak fields on the progress record (e.g., sets `MasteryScore = 1.0` for correct, `0.3` for wrong). If called inside the loop interleaved with `RecordAttemptAsync` calls, the re-fetch in `HandleVerificationProbeResultAsync` could read stale state. Collect all probes during the loop, then fire them after step 5 completes. This matches how Writing.razor calls them today (sequentially after each RecordAttemptAsync), but makes the separation explicit for the shared method.

### 3.5 Canonical Activity Names

The `Activity` field on `VocabularyAttempt` and `VocabularyLearningContext` must use these exact strings:

| Activity | String Value |
|---|---|
| Vocabulary Quiz | `"VocabularyQuiz"` |
| Vocabulary Matching | `"VocabularyMatching"` |
| Word Association | `"WordAssociation"` |
| Cloze | `"Clozure"` |
| Writing | `"Writing"` |
| Translation | `"Translation"` |
| Scene Description | `"SceneDescription"` |
| Conversation | `"Conversation"` |
| Reading (passive) | `"Reading"` |

### 3.6 Edge Cases

| Scenario | Behavior |
|---|---|
| Same word appears twice in one sentence (e.g., "나는 먹고 먹었다") | Score it **once**. `.DistinctBy(v => v.DictionaryForm)` in step 2 of section 3.4 ensures first occurrence wins; duplicates are removed before the scoring loop. |
| AI returns a word not in VocabularyAnalysis model | Ignored — only `DictionaryForm` matches against tracked vocabulary are scored. |
| User writes a word correctly but AI misidentifies `UsageCorrect` | This is an AI grading quality issue, not a scoring logic issue. The spec trusts the AI's `UsageCorrect` field. Prompt engineering (section 6) should minimize false negatives. |
| User writes no tracked vocabulary words | `VocabularyAnalysis` comes back empty or no matches → no scoring happens. The activity still records a `UserActivity` for general session tracking. |
| Word is in vocabulary but `DictionaryForm` doesn't match exactly (e.g., spacing, romanization) | Case-insensitive match on `TargetLanguageTerm`. If this proves insufficient, a future enhancement can add fuzzy matching (out of scope for this spec). |

---

## 4. Per-Activity Integration

### 4.1 Writing (Refactor Only — No New Behavior)

**Current state:** Writing.razor (lines 314–348) implements the full multi-word scoring pipeline inline.

**Change:** Replace the inline loop with a single call to `ExtractAndScoreVocabularyAsync`. Writing's DifficultyWeight changes from 1.0 → **1.5** (Captain's decision).

```csharp
// Before (inline loop, 30+ lines):
if (grade.VocabularyAnalysis?.Any() == true)
{
    foreach (var vocabItem in grade.VocabularyAnalysis) { ... }
}

// After (one call):
var scoringResults = await ProgressService.ExtractAndScoreVocabularyAsync(
    grade.VocabularyAnalysis, allVocabulary, activeUserId,
    activity: "Writing", difficultyWeight: 1.5f);
```

**Risk:** Low — behavior-preserving refactor. Only the DifficultyWeight value changes.

### 4.2 Translation (Bug Fix + New Wiring)

**Current state:**
- `VocabularyProgressService` is injected (`Translation.razor:139`) but **never used**.
- Grading uses `AiSvc.SendPrompt<GradeResponse>(prompt)` with an ad-hoc prompt string that does NOT request `VocabularyAnalysis`.
- `currentVocabulary` is already loaded (`Translation.razor:163, 210`) — the vocabulary data is available.

**Changes needed:**

1. **AI grading method:** The ad-hoc prompt in `GradeMe()` (line 240) must be replaced with a call to `TeacherSvc.GradeTranslation()` (line 138 in `TeacherService.cs`). This method already exists and uses `GradeTranslation.scriban-txt` which already requests `vocabulary_analysis`. **Do NOT use `GradeSentence()`** — `GradeTranslation()` is purpose-built for native→target translation grading with the correct parameters (`userInput`, `originalSentence`, `recommendedTranslation`, `targetLanguage`, `nativeLanguage`).

2. **Add scoring call after grading:**

```csharp
// In GradeMe(), after recording UserActivity:
if (currentVocabulary?.Any() == true)
{
    var scoringResults = await ProgressService.ExtractAndScoreVocabularyAsync(
        feedback.VocabularyAnalysis, currentVocabulary, activeUserId,
        activity: "Translation", difficultyWeight: 1.5f);
}
```

3. **Load `activeUserId`:** Add `var activeUserId = Preferences.Get("active_profile_id", "");` in the grading flow.

**Risk:** Medium — requires prompt change + new integration. Test that `VocabularyAnalysis` is populated when grading translations.

### 4.3 Scene (New AI Extension + New Wiring)

**Current state:**
- Uses `TeacherSvc.GradeDescription()` which returns `GradeResponse` but the `GradeMyDescription.scriban-txt` template does NOT request `VocabularyAnalysis`.
- No `VocabularyProgressService` injected.
- No vocabulary list loaded on the page.

**Changes needed:**

1. **Extend GradeDescription template:** Update `GradeMyDescription.scriban-txt` to request vocabulary analysis. The template must receive the user's vocabulary list as context so the AI can identify which words are tracked.

2. **Inject VocabularyProgressService** and a vocabulary source (e.g., `VocabularyService`) into Scene.razor.

3. **Load user vocabulary on page init.** Scene doesn't currently load vocabulary — it only loads scene images. Add vocabulary loading in `OnInitializedAsync` or lazily before first grading.

4. **Add scoring call after grading:**

```csharp
// In SubmitDescription(), after recording UserActivity:
var scoringResults = await ProgressService.ExtractAndScoreVocabularyAsync(
    grade.VocabularyAnalysis, userVocabulary, activeUserId,
    activity: "SceneDescription", difficultyWeight: 1.5f);
```

**Risk:** Medium — requires template change, new service injection, and vocabulary loading.

### 4.4 Conversation (New AI Extension + New Wiring + Softer Penalty)

**Current state:**
- Uses `ConversationSvc.ContinueConversation()` which returns `Reply` — a model with `Message`, `Comprehension`, `GrammarCorrections`. No `VocabularyAnalysis`.
- No `VocabularyProgressService` injected.
- No vocabulary list loaded.

**Changes needed:**

1. **Extend `Reply` model:** Add `VocabularyAnalysis` to the Reply class:

```csharp
public class Reply
{
    public string? Message { get; set; }
    public double Comprehension { get; set; }
    public string? ComprehensionNotes { get; set; }
    public List<GrammarCorrectionDto> GrammarCorrections { get; set; } = new();

    // NEW:
    [Description("Per-word vocabulary analysis of the user's message")]
    [JsonPropertyName("vocabulary_analysis")]
    public List<VocabularyAnalysis>? VocabularyAnalysis { get; set; }
}
```

2. **Update conversation Scriban templates** (`ContinueConversation.scriban-txt`, `ContinueConversation.scenario.scriban-txt`) to request vocabulary analysis of the user's message. The prompt must receive the user's vocabulary list.

   > **JSON format prerequisite (R2 note):** The current Conversation templates return free-form text that is parsed into `Reply` fields. Before adding `vocabulary_analysis`, the templates must be updated to return **properly structured JSON output** (matching the format used by `GradeSentence.scriban-txt` and `GradeTranslation.scriban-txt`). Without explicit JSON output format examples in the template, the AI may return vocabulary_analysis in an unparseable format. Template authors should model the JSON structure after the existing `GradeResponse` serialization pattern.

3. **Inject VocabularyProgressService** and vocabulary source into Conversation.razor.

4. **Load user vocabulary on page init.**

5. **Add scoring call after receiving reply:**

```csharp
// In SendMessage(), after applying grammar corrections:
if (userVocabulary?.Any() == true)
{
    var scoringResults = await ProgressService.ExtractAndScoreVocabularyAsync(
        reply.VocabularyAnalysis, userVocabulary, activeUserId,
        activity: "Conversation", difficultyWeight: 1.2f,
        penaltyOverride: 0.8f);  // Softer penalty for chat
}
```

**Risk:** High — Conversation has the most complex AI interaction (multi-agent, streaming-like). Adding vocabulary analysis to the prompt increases response size and latency. Consider making vocabulary analysis **optional** (skip if vocabulary list is empty or user hasn't loaded any vocab).

### 4.5 Activities Already Correct (No Changes)

| Activity | Status |
|---|---|
| **VocabQuiz** (all modes) | Fully wired. No changes needed. |
| **VocabMatching** | Fully wired (DW=0.8). No changes. |
| **WordAssociation** | Fully wired (DW=1.0). No changes. |
| **Cloze** | Fully wired (DW=1.2). No changes. |

---

## 5. Passive Exposure Tracking (Reading)

### 5.1 Captain's Decision

When a user taps a word in Reading to look it up, log it as a **passive exposure**. No mastery change. No penalty. No credit. Analytics only.

### 5.2 Current State

Reading.razor already records a `UserActivity` on word tap (line 636–644):

```csharp
await ActivityRepo.SaveAsync(new UserActivity
{
    Activity = Activity.Reading.ToString(),
    Input = $"Dictionary lookup: {cleanWord}",
    ...
});
```

This logs the lookup but does NOT touch `VocabularyProgress`.

### 5.3 Required Changes

For tracked vocabulary words (words that match the user's vocabulary), additionally record a `VocabularyLearningContext` entry:

```csharp
// In HandleWordClicked(), after matching localWord:
if (localWord != null)
{
    var progress = await ProgressService.GetProgressAsync(localWord.Id, activeUserId);
    await ProgressService.RecordPassiveExposureAsync(localWord.Id, activeUserId, "Reading");
}
```

**New method on VocabularyProgressService:**

```csharp
/// <summary>
/// Records a passive exposure (e.g., Reading word lookup).
/// Does NOT call RecordAttemptAsync — no streak/mastery change.
/// Only creates a VocabularyLearningContext entry for analytics.
/// </summary>
public async Task RecordPassiveExposureAsync(
    string vocabularyWordId, string userId, string activity)
{
    var progress = await GetOrCreateProgressAsync(vocabularyWordId, userId);

    var context = new VocabularyLearningContext
    {
        VocabularyProgressId = progress.Id,
        Activity = activity,
        InputMode = "Passive",
        WasCorrect = true,  // N/A for passive, default true
        DifficultyScore = 0f,
        ContextType = "Exposure",
        LearnedAt = DateTime.Now
    };

    await _contextRepo.SaveAsync(context);

    // Update LastExposedAt — a NEW field that tracks passive encounters.
    // Do NOT update LastPracticedAt — that field is used by SRS scheduling
    // and active-practice analytics. Passive exposure must not contaminate
    // active practice signals. (R2 fix — Captain's decision)
    progress.LastExposedAt = DateTime.Now;
    progress.UpdatedAt = DateTime.Now;
    await _progressRepo.SaveAsync(progress);
}
```

### 5.4 What Passive Exposure Does NOT Do

- Does NOT increment `CurrentStreak` or `ProductionInStreak`
- Does NOT change `MasteryScore`
- Does NOT update `NextReviewDate` or `ReviewInterval`
- Does NOT update `LastPracticedAt` (uses `LastExposedAt` instead — R2 fix)
- Does NOT count toward `TotalAttempts` or `CorrectAttempts`
- Does NOT trigger verification probes for Familiar words

### 5.5 Analytics Value

Passive exposure data enables future queries like:
- "Words the user has seen but never actively practiced"
- "Most frequently looked-up words" (candidates for quiz inclusion)
- "Time between first exposure and first active attempt"

---

## 6. AI Grading Changes

### 6.1 Overview

Three AI grading touchpoints need to return `VocabularyAnalysis`:

| Service Method | Template File | Current Output | Required Addition |
|---|---|---|---|
| `TeacherSvc.GradeSentence()` | `GradeSentence.scriban-txt` | `GradeResponse` with `VocabularyAnalysis` | **None — already works** |
| `TeacherSvc.GradeTranslation()` | `GradeTranslation.scriban-txt` | `GradeResponse` with `VocabularyAnalysis` | **None — already works** (template already requests `vocabulary_analysis`) |
| `TeacherSvc.GradeDescription()` | `GradeMyDescription.scriban-txt` | `GradeResponse` without `VocabularyAnalysis` | Add vocabulary analysis section to template |
| `ConversationSvc.ContinueConversation()` | `ContinueConversation.scriban-txt` / `ContinueConversation.scenario.scriban-txt` | `Reply` without `VocabularyAnalysis` | Add vocabulary analysis section to templates + extend `Reply` model |

Additionally, `Translation.razor` uses an ad-hoc prompt instead of `TeacherSvc.GradeTranslation()`. It must switch to the existing method (line 138 in `TeacherService.cs`).

### 6.2 Vocabulary Context Passing

For the AI to identify which words are tracked, the prompt must include the user's vocabulary list. The pattern:

```
The student is tracking these vocabulary words:
{{ for word in vocabulary_words }}
- {{ word.target_language_term }} ({{ word.native_language_term }})
{{ end }}

For each tracked vocabulary word used in the student's response, analyze whether it was used correctly.
```

**Performance consideration:** If the user has hundreds of vocabulary words, passing all of them in every prompt is expensive. Recommended approach:

1. Pass only vocabulary words associated with the current learning resource (if applicable).
2. If no resource context, pass the most recent/active 50–100 words (sorted by `LastPracticedAt` descending).
3. As a fallback, pass all words. The AI can handle it but it costs more tokens.

### 6.3 Template Changes

#### GradeMyDescription.scriban-txt (Scene)

Add vocabulary context and analysis request. The existing template already receives `my_description`, `ai_description`, `native_language`, `target_language`. Add:

- New template variable: `vocabulary_words` (list of tracked words)
- New prompt section requesting `vocabulary_analysis` array in the JSON response

#### ContinueConversation templates (Conversation)

Add vocabulary context and analysis of the user's message. The AI must:
1. Continue the conversation normally (existing behavior)
2. Additionally analyze the user's Korean message for vocabulary usage
3. Return `vocabulary_analysis` alongside `message`, `comprehension_score`, and `grammar_corrections`

### 6.4 Translation Grading Fix

**Current (ad-hoc prompt, line 240):**
```csharp
var prompt = $"Grade this translation.\n\nOriginal ({targetLanguage}): {currentSentence}\n...";
var feedback = await AiSvc.SendPrompt<GradeResponse>(prompt);
```

**Required (use TeacherSvc.GradeTranslation):**
```csharp
var feedback = await TeacherSvc.GradeTranslation(
    userInput, currentSentence, recommendedTranslation, targetLanguage, nativeLanguage);
```

This automatically gets `VocabularyAnalysis` from the existing `GradeTranslation.scriban-txt` template, which already requests `vocabulary_analysis`. The vocabulary context (tracked word list) may still need to be passed if the template doesn't already include it.

---

## 7. VocabularyProgressService Changes Summary

### 7.1 New Methods

| Method | Purpose |
|---|---|
| `ExtractAndScoreVocabularyAsync(...)` | Shared multi-word scoring pipeline (section 3.4) |
| `RecordPassiveExposureAsync(...)` | Reading word lookup tracking (section 5.3) |

### 7.2 Model Changes

| Change | Location |
|---|---|
| Add optional `PenaltyOverride` to `VocabularyAttempt` | `Models/VocabularyAttempt.cs` |
| Add `VocabularyAnalysis` to `Reply` | `Models/Reply.cs` |
| Add `VocabScoringResult` class | New file or nested in `VocabularyProgressService` |
| Add `LastExposedAt` to `VocabularyProgress` | `Models/VocabularyProgress.cs` — `DateTime? LastExposedAt` for passive exposure tracking (R2 fix). Must NOT contaminate `LastPracticedAt` which is used for SRS scheduling. |

### 7.3 RecordAttemptAsync Change

One change to `RecordAttemptAsync` to support `PenaltyOverride`:

```csharp
// Current (line 143):
progress.MasteryScore *= WRONG_ANSWER_PENALTY;

// New:
float penalty = attempt.PenaltyOverride ?? WRONG_ANSWER_PENALTY;
progress.MasteryScore *= penalty;
```

Add to `VocabularyAttempt`:

```csharp
/// <summary>
/// Optional penalty multiplier override. If set, used instead of WRONG_ANSWER_PENALTY (0.6).
/// Currently only Conversation uses this (0.8 — softer penalty for chat context).
/// </summary>
public float? PenaltyOverride { get; set; }
```

### 7.4 No Other Formula Changes

`RecordAttemptAsync` already handles everything else correctly:
- Streak increment on correct (`CurrentStreak++`, `ProductionInStreak++` for text input)
- EffectiveStreak calculation (`CurrentStreak + ProductionInStreak * 0.5`)
- MasteryScore from EffectiveStreak (`Math.Min(effectiveStreak / 7.0, 1.0)`)
- Mastery threshold check (`>= 0.85` + `ProductionInStreak >= 2`)
- SRS scheduling updates
- `VocabularyLearningContext` recording

---

## 8. CURRENT vs EXPECTED Summary

| Activity | CURRENT | EXPECTED | Bug? |
|---|---|---|---|
| **Writing** | Records multi-word via inline loop. DW=1.0 | Use `ExtractAndScoreVocabularyAsync`. DW=**1.5** | No bug, refactor + weight fix |
| **Translation** | ProgressService injected, **never called**. No VocabularyAnalysis. | Switch to `TeacherSvc.GradeTranslation()` (already exists). Add `ExtractAndScoreVocabularyAsync`. DW=1.5 | **YES — recording bug** |
| **Scene** | No ProgressService. No VocabularyAnalysis. | Extend `GradeDescription` template. Inject + wire ProgressService. DW=1.5 | No bug, missing feature |
| **Conversation** | No ProgressService. No VocabularyAnalysis. | Extend `Reply` model + templates. Inject + wire ProgressService. DW=1.2, penalty=0.8 | No bug, missing feature |
| **Reading** | Logs `UserActivity` on tap. No vocab progress. | Add `RecordPassiveExposureAsync`. No mastery change. | No bug, missing feature |
| **VocabMatching** | DW=0.8 | No change | — |
| **WordAssociation** | DW=1.0 | No change | — |
| **Cloze** | DW=1.2 | No change | — |
| **VocabQuiz MC** | DW=1.0 | No change | — |
| **VocabQuiz Text** | DW=1.5 | No change | — |
| **VocabQuiz Sentence** | DW=2.5 | No change | — |

---

## 9. Implementation Sequence

Work should proceed in this order to minimize risk:

1. **Phase 1 — Foundation (no AI changes):**
   - Add `PenaltyOverride` to `VocabularyAttempt`
   - Add `LastExposedAt` to `VocabularyProgress` (R2 fix)
   - Update `RecordAttemptAsync` to use `PenaltyOverride`
   - Add `ExtractAndScoreVocabularyAsync` to `VocabularyProgressService`
   - Add `RecordPassiveExposureAsync` to `VocabularyProgressService` (uses `LastExposedAt`, not `LastPracticedAt`)
   - Add `VocabScoringResult` model
   - Refactor Writing.razor to use `ExtractAndScoreVocabularyAsync` (DW=1.5)
   - Wire Reading passive exposure

2. **Phase 2 — Translation fix (moderate AI change):**
   - Replace ad-hoc prompt with `TeacherSvc.GradeTranslation()` in Translation.razor (method already exists at line 138)
   - Wire `ExtractAndScoreVocabularyAsync` into Translation grading flow
   - Verify VocabularyAnalysis is returned for Korean→English translations

3. **Phase 3 — Scene extension (template change):**
   - Update `GradeMyDescription.scriban-txt` to include vocabulary analysis
   - Inject VocabularyProgressService + vocabulary source into Scene.razor
   - Load user vocabulary on page init
   - Wire `ExtractAndScoreVocabularyAsync` into Scene grading flow

4. **Phase 4 — Conversation extension (model + template change):**
   - Add `VocabularyAnalysis` to `Reply` model
   - Update Conversation Scriban templates
   - Inject VocabularyProgressService + vocabulary source into Conversation.razor
   - Load user vocabulary on page init
   - Wire `ExtractAndScoreVocabularyAsync` with `penaltyOverride: 0.8f`

5. **Phase 5 — Validation:**
   - E2E tests for each activity recording progress
   - Verify mastery accrual across activities (word practiced in Quiz + Writing should accumulate)
   - Verify Conversation's softer penalty (0.8 vs 0.6)
   - Verify passive exposure in Reading doesn't change mastery
   - Verify edge cases: duplicate words in one sentence, untracked words ignored

---

## 10. Cross-Activity Behavior Notes

These rules govern how mastery accrues when a user practices the same word across multiple activities:

1. **Global progress is shared.** `VocabularyProgress` (CurrentStreak, MasteryScore, ProductionInStreak) is per-word, per-user — NOT per-activity. A correct answer in Writing advances the same streak as a correct answer in Quiz.

2. **Activity is recorded per-attempt.** The `Activity` field on `VocabularyLearningContext` tracks which activity generated each data point. This enables per-activity analytics without fragmenting the progress model.

3. **Session-local state is Quiz-scoped.** Fields like `PendingRecognitionCheck`, `SessionCorrectCount`, `SessionMCCorrect` exist only in VocabQuiz's in-memory model (`VocabQuizItem`). Other activities don't have session-local state — each grading event is independent.

4. **Mastery threshold is universal.** A word can be mastered through any combination of activities, as long as `MasteryScore >= 0.85` and `ProductionInStreak >= 2`. Two correct production attempts in Writing alone can satisfy `MIN_PRODUCTION_FOR_KNOWN` — the user doesn't need to go through Quiz.

5. **SRS scheduling is universal.** When any activity records an attempt via `RecordAttemptAsync`, the SRS schedule (`NextReviewDate`, `ReviewInterval`, `EaseFactor`) is updated. A word practiced in Conversation today won't come due in Quiz tomorrow (assuming the interval pushes it out).

---

*End of specification.*
