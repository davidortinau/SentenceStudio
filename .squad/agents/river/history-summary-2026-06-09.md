# River History Summary (2026-06-01 to 2026-06-09)

**Agent:** River — AI Pedagogy Specialist  
**Role:** Prompt engineering, LLM grading strategies, vocabulary teaching frameworks  
**History Size:** 23846 bytes → summarized to preserve recent decisions

## Key Delivery Areas

1. **Korean Language Teaching Framework**
   - Vocabulary classification taxonomy (Word/Phrase/Sentence)
   - Sino/Native number system pedagogy
   - Sound-change and tense rules embedded in grading

2. **Grading AI Prompt Strategy**
   - Unified grader template with `target_word` context switching
   - Conversation penalty override (0.8f vs standard 0.6f)
   - Error-classification priority order (counter-mismatch before swap detection)

3. **LLM Reliability Patterns**
   - JSON schema + [Description] attributes for output specification
   - Template reusability across activities (GradeSentence shared by Writing/Cloze/Quiz)
   - Canonical activity names for mastery recording

4. **Recent Sessions**
   - 2026-05-04: Phase 1 Number grading implementation (system-aware rules, 7 error classes)
   - 2026-05-05: UI redesign constraints (Bootstrap tokens, emoji removal, alert styling)
   - 2026-05-12+: Normalization rules (fullwidth digits, internal commas, trailing punctuation)
   - 2026-06-09: Code-review validation ensuring grader updates align with pedagogy

## Standing Decisions

- Conversation grading uses softer penalty (0.8f) — Captain's directive
- Scene and Conversation load full user vocabulary (no resource-specific subset)
- Target word grading flows through dedicated params, not userMeaning slot
- Grader error classes prioritized: CounterMismatch → SinoNativeSwap → SoundChangeMissed → (magnitude/typo/format/unknown)

## Cross-Agent Context

- Works closely with Jayne (testing/E2E), Kaylee (UI), Wash (data model)
- Prompt quality gate: all grading templates must pass code review before merge
- Recent code-review follow-up: Fallback plan path symmetry (RationaleFacts non-null coalesce)
