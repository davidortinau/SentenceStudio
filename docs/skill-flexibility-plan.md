# Skill flexibility plan

Status: proposal, deferred by the Captain (2026-11-14) and tracked as issue #224. Nothing here is built.

## Why

The Captain designed Skills as language skills: grammar points such as past tense with -았/었어요.
The Library job-flow mockups showed situational skills ("Ordering food"), and he said that is
appropriate. But practising a skill still needs vocabulary chosen elsewhere. A skill picked on its
own also doesn't say which language to practise. The ask: make Skill flexible enough to practise
by itself, without forcing a vocabulary pick.

## How skills work today (read from code, 2026-11-12)

- **The model:** `SkillProfile` (`src/SentenceStudio.Shared/Models/SkillProfile.cs`) has `Title`,
  `Description`, `Language` (defaults to "Korean"), `IsArchived` and an owner.
- **Language is stored but not used.** Sam's skill tools accept a language
  (`CoachSkillHandlers.cs:147`). The native skill form doesn't show it, and no activity reads it.
- **Archiving can't be reached.** `IsArchived` hides a skill from lists and practice
  (`SkillProfileRepository.ListAsync` and `GetSkillProfileAsync`), but nothing sets it. The
  native app offers only a permanent Delete.
- **Activities use only the description.** It goes into the AI prompt as `skills =
  skill?.Description`, to bias grammar patterns:
  - Translation: `NativeTranslationPresenter.cs:465`.
  - Cloze: `NativeClozeSentenceSource.cs:190`, where the comment says "Skill is optional: it only
    biases grammar patterns".
- **The language comes from elsewhere.** It is the resource's `Language`, or else the profile's
  `TargetLanguage` (`NativeTranslationPresenter.cs:540-551`). Profiles can have several target
  languages (`UserProfile.TargetLanguagesList`).
- **Vocabulary is required, but it varies by activity:**
  - Translation needs a resource or focus words, and a skill unless focus words exist
    (`NativeTranslationPresenter.cs:527-561`).
  - Cloze can run without a resource, using the learner's due words
    (`NativeClozeSentenceSource.cs:48-62`).
- **Choose your own** (`NativeDashboardPresenter.ChooseOwn.cs`) launches with the chosen resource
  IDs and an optional skill ID.
- **Plans pick a skill** from the primary resource or the most recent completion
  (`DeterministicPlanBuilder.cs:227, 345`).

## Proposal

### 1. A skill states its language

- Show `Language` in the skill form. Offer the profile's target languages and default to the
  current one.
- Activities launched for a skill use the skill's language when no resource sets one.
- A resource's language still wins when both are given. When they differ, ask once before
  starting rather than silently picking one.
- **No schema change:** the column already exists and syncs.

### 2. A skill says what kind it is

- Add a **Focus** with two values:
  - **Grammar or pattern:** today's skills, such as -았/었어요.
  - **Situation:** for example ordering food, or giving directions.
- Prompts use it to frame the activity:
  - Grammar: practise this pattern with these words.
  - Situation: hold this conversation and use language that fits it.
- Existing skills default to Grammar.
- **Needs a schema change** (a new column, PostgreSQL and SQLite migrations, owner-scoped sync), so
  it needs the Captain's approval. Until then, the description can carry the framing.

### 3. Practising a skill no longer requires choosing vocabulary

When a skill is launched with no resource, the words come from one of these sources, in order:

1. The skill's **suggested resources**, if any are attached (step 4).
2. The learner's **due and learning words** in the skill's language. Cloze already does this; the
   other activities would follow the same rule.
3. **Words the situation calls for:** the activity introduces a small set of new words that fit
   the skill. They are shown as new, and the learner can save them to Vocabulary.

The learner can still choose resources in Choose your own, as today.

**Learning Value Gate:** source 3 changes an activity's prompts and content, so it goes through
`.squad/skills/learning-value-gate/SKILL.md` first. Every mode must still be target-language prompt
with native-language response, or the reverse; never native language on both sides.

### 4. Optional: suggested resources on a skill

- Allow a skill to list a few resources that suit it, for example a restaurant dialogue video for
  "Ordering food".
- **Needs a new join table and sync,** so it needs the Captain's approval. Defer until 1–3 are in
  use.

### 5. Practise this skill (the S1 mockup)

The skill page gets **Practise this skill**. It opens Choose your own with the skill already
selected and the resource selection left optional. Activities that can't run without a resource
show "needs a resource" instead of failing.

## Activity check before building

Go through each activity in `src/SentenceStudio.Native/Activities` and record whether it runs from
a skill alone, using sources 2 and 3. So far only Translation and Cloze have been read. Write the
results in this document as a table.

## Parity and data

- **Every client changes:** web, MAUI through the shared Razor UI, the native heads, and Sam's skill
  tools, which already take a language.
- **Data stays safe:**
  - Step 1 changes no schema.
  - Step 2 adds a nullable column with a default.
  - Step 4 adds a table.
  - None of them rewrites existing rows.

## Questions for the Captain

1. Are Grammar and Situation enough, or is there a third kind (for example pronunciation or
   register)?
2. When a skill brings new words (source 3), should they be saved to Vocabulary automatically, or
   only when the learner chooses?
3. Should a skill be allowed to have no language ("any of my languages") and ask at launch?

## Deferred

- Practice and mastery by language on the skill page: issue #220.
- Study history on the resource page: issue #219.
