# Learning Value Gate: Translate word blocks (all clients, 2026-11-19)

Gate: `.squad/skills/learning-value-gate/SKILL.md`. Change (issue 233): in Translate's Blocks
mode the blocks are the words of the target sentence being translated, shuffled, instead of the
daily plan's focus words. Code: `src/SentenceStudio.Shared/Services/TranslationSentenceBlocks.cs`,
used by `src/SentenceStudio.UI/Pages/Translation.razor` (web, MAUI) and
`src/SentenceStudio.Native/Activities/Translation/NativeTranslationPresenter.cs` (iOS, Android).
No mode, toggle, default, prompt or grader was added or changed; what a block holds changed.

## 1. Learning objective

Given a sentence in the native language, the learner produces its target-language translation;
in Blocks mode the learner recognises the target-language words on offer and puts them in the
sentence's order (word order and particles), as a scaffold on the way to typing it unaided.

## 2. Language-role matrix

Translate has one direction and no audio or photo. The prompt text is never hidden.

| Mode | Prompt | Response | L2 exposure | L2 retrieval | Verdict |
|---|---|---|---|---|---|
| Type (default, reset on every sentence) | native sentence | typed target sentence | the learner's own writing, then the suggested translation after grading | production | ship (unchanged) |
| Blocks | native sentence | target-language blocks tapped into the field, which stays editable | every block is target-language text | recognition of each word and recall of the sentence's order | ship |
| Blocks, sentence written without spaces whose AI components do not write it | native sentence | one block: the whole target sentence | yes | none: the answer is on the block | ship as a known limit (below); not reachable in Korean, French, German or Spanish |

No row is native prompt and native response: a block is always target-language text taken from
the target sentence.

## 3. Learner action and L2 evidence

- Type: production. The learner writes the target sentence from memory.
- Blocks: recognition and ordering. The learner reads each target-language block, works out its
  part in the sentence and chooses the order. Nothing is offered that is not in the sentence, so
  word choice is not tested in this mode; the field can still be edited before grading.

## 4. Empty-state and default review

A first-time learner lands in Type mode with no block on screen. Blocks appear only after the
learner taps Blocks, and every new sentence starts in Type mode again. There is no stored
preference. If a sentence yields no blocks (no target sentence came back), Blocks mode shows
none and typing still works.

## 5. Answer-leakage checklist

- [x] Type mode shows no word of the answer: the blocks are drawn only in Blocks mode (web
  `inputMode == "MultipleChoice"`, native `ShowsBlocks`).
- [x] Sam's screen description carries the prompt, the draft and the position, not the blocks
  (`Translation.razor` `DescribeForSam`, `NativeSamScreen.Translation`).
- [x] The order shown says nothing about the sentence's order: a plain shuffle, so a two-word
  sentence is not always reversed. A short sentence can come out in its own order by chance.
- [x] Sentence punctuation and quotation marks are removed from the blocks, so no block marks
  itself as the last (or first) one.
- [x] Accessibility labels and automation IDs are the block's own text and its position in the
  shuffled list.
- [ ] Capital letters: in a target language with capitals (French, German, Spanish) the first
  word keeps its capital and can be told apart. Not changed: lower-casing it would be wrong for
  a name and for a German noun. Korean, Japanese and Chinese have no capitals.
- [ ] Blocks mode is, by the Captain's request, the whole sentence in pieces. A graded answer
  built from blocks is recorded and scored like a typed one (Text, weight 1.5), as it was
  before this change. Scoring it lower is a product decision left to the Captain.

## 6. Acceptance tests

`.claude/skills/e2e-testing/references/quiz-activities.md`, section 5 "Blocks mode". Automated:
`TranslationSentenceBlocksTests`, `NativeTranslationPresenterTests
.PlanCard_BlocksAreTheSentenceWords_NotThePlanFocusWords`, `TranslationGradeRecordingTests
.Blocks_AreTheSentenceWords_NotThePlanFocusWords`.

## Known limit: languages written without spaces

Japanese and Chinese sentences have no spaces to split at. The blocks are then the components
the AI listed for the sentence (`TranslationDto.TranslationVocabulary`), and only when those
components, joined in their order, are the sentence. When they are not (dictionary forms, a
missing particle), the sentence stays whole as one block. Tapped blocks are joined with spaces,
as before. A proper fix needs a word segmenter for those languages.
