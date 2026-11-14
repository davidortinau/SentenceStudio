# AI duplicate resolution: plan

Captain, 2026-11-14: "We need to get ai involved in the merging. In some cases the words have
multiple meanings. In others they are obvious to merge. I shouldn't have to make most of these
decisions. Plan out using ai to efficiently and accurately resolve real duplicates."

Builds on `docs/duplicate-merge-plan.md` (the rules, Merge all, Keep This, choosing copies).

## Where we are (Duotopia, read-only copy, 2026-11-14 13:30)

46 duplicate groups are left, all pairs. The rules cannot tell the two kinds apart, because both
show up as "different meanings":

| Same word, worded differently (merge) | Different words, same spelling (keep apart) |
|---|---|
| 소리 "sound" / "sound / noise" | 달 "month" / "moon" |
| 아빠 "dad" / "father" | 안 "inside" / "not" |
| 아까 "a while ago" / "earlier" | 쓰다 "to spend (money)" / "to write" |
| 시원하다 "cool (temperature)" / "to be cool" | 약 "medicine" / "approximately" |
| 싫어하다 "to dislike" / "to hate" | 뭐 "something" / "what" (senses of one word) |

New duplicates keep arriving through imports, so this is an ongoing job, not a one-off.

## Goal

The Captain decides only what genuinely needs a person. AI resolves the rest. Nothing is lost
(history always moves to the kept word), every AI action is visible with its reason, and it can
be undone.

## How it works

1. **Rules first (exists, no AI).** Merge all folds safe groups and identical copies.
2. **AI triage** of every group left for review, in batches of up to 25 groups per call on the
   Fast tier (gpt-5-mini). For each group the AI returns:
   - a verdict: `SameWord` (merge all copies), `DifferentWords` (keep apart), or `Mixed`
     (sets of copies that are the same word);
   - for each set: the copy IDs, the meaning to keep (in the learner's language, following
     dictionary convention, e.g. "to be cool" for a Korean descriptive verb; "sound; noise" when
     both wordings are right), and a language fix when one copy says "Unknown";
   - a confidence (0 to 1) and a reason of at most 20 words.
3. **Check the answer** before acting (deterministic, in shared code):
   - every copy ID is in exactly one set and belongs to the group;
   - the target term is never changed;
   - the meaning is non-empty, short, and in the learner's native language (Learning Value Gate:
     no target-language-only meaning);
   - the existing merge rules still apply (owner only, copies other learners use stay, history
     moves).
   An answer that fails a check is treated as uncertain.
4. **Act by confidence:**
   - at or above the auto threshold (proposed 0.85; 0.9 for `Mixed`): apply. Merge into the
     keeper (the copy the learner has learned, as today) and set its meaning to the AI's; for
     `DifferentWords`, record the group as kept apart (and, if approved, clarify the meanings,
     e.g. "moon (in the sky)" / "month");
   - below it: ask once more on the Reasoning tier (gpt-5);
   - still below: the group goes to **Needs You**, with the AI's choice already selected (copies,
     keeper, meaning) and its reason. Accept is one tap; the existing Keep This, copy choice,
     Edit and Skip still work.
5. **Report:** "AI merged 18 groups (19 copies) · kept 22 apart as different words · 6 need
   you". Each line opens to show what was done and why, with Undo.

## Remembering decisions (schema change: needs the Captain's go)

Without storage, a group kept apart (달 month/moon) comes back on every scan, and an AI merge
cannot be undone. One owner-scoped, synced table covers both:

`VocabularyDuplicateDecision`: owner, term key, kind (`Merged` or `KeptApart`), the copies'
IDs, who decided (AI or learner), reason, the merged-away copies' fields and the keeper's
fields before the merge (for Undo), created at.

- The scan skips a group whose copies match a `KeptApart` decision; a new copy of that term
  reopens it.
- Undo restores the removed words (same IDs, original fields) and the keeper's meaning. Their
  practice history stays on the keeper; it cannot be split back exactly, and the Undo note says so.
- Work: PostgreSQL and SQLite migrations (`.squad/skills/ef-dual-provider-migrations`),
  CoreSync registration, owner checks in both sync providers (native and MAUI), two-learner
  isolation tests, migration proofs on scratch databases.

## Where it runs (one API allow-list entry)

- A shared `VocabularyDuplicateResolver` (`src/SentenceStudio.Shared/Services/VocabularyDuplicateResolver.cs`)
  builds the prompt, batches, asks fast then reasoning, and checks every answer. Callers pass the
  transport (`VocabularyDuplicateAiAsk`): `IAiService.SendPrompt<T>` on web/MAUI, the owner-bound
  gateway on native. The answer type `DuplicateResolutionResponse` carries `[Description]`s (MEAI
  serializes it; no hand-written schema). The prompt is C# in shared code, so every client sends
  the same one with no embedded template.
- `/api/v1/ai/chat` hydrates only allow-listed types, so `DuplicateResolutionResponse` is added
  to `AiResponseTypeRegistry` (and its baseline test). Native AI duplicate resolution needs that
  API deploy first.
- Web/API run it in process. Native and MAUI already reach AI through the owner-bound gateway
  (`IOwnerBoundNativeAiGateway`, `/api/v1/ai/chat`), so the device sends only the group data it
  already holds and applies the merges locally through the same shared merge, which syncs as today.
- What the AI sees per copy: target term, meaning, base form, part of speech, kind, language,
  a memory-aid snippet, one example sentence, and the titles of the resources it came from (the
  best clue for which sense a copy is). Plus the learner's native and target languages.
- Offline or AI failure: nothing is applied automatically; the page works as it does today.

## Accuracy before automation

1. **Dry run on the real 46 groups** (dev Foundry, Duotopia read-only export): a table of
   verdict, meaning, confidence and reason per group. The Captain skims it (about 5 minutes) and
   marks any wrong call. Prompt and threshold are tuned until the auto-applied calls have no
   errors; the threshold is then fixed from that run.
2. **Tests without network** (AGENTS.md): a fake AI returns canned answers. The tests cover the
   checks, the thresholds, Mixed sets, escalation, owner isolation on PostgreSQL and SQLite, the
   kept-apart skip and Undo.
3. **After release**: the report and Undo let the Captain catch a wrong call; each one becomes a
   new labeled example for the prompt.

## Dry run results (2026-11-14, Duotopia copy `duotopia-db5`, read-only)

`tools/duplicate-ai-dryrun` (Phase 0) read the copy with `Mode=ReadOnly` (file hash unchanged
before and after) and wrote a report and `calls.json`. Of the 46 groups, 9 are safe (only
Unknown versus Korean), which Merge Matching Copies already folds, and 37 go to the AI.

| Prompt rule | Merge / keep apart / needs you | Problem found |
|---|---|---|
| 1. Examples only (the plan's first wording) | 28 / 8 / 1 | Unstable: a rerun flipped 잡다 "to catch"/"to hold" and 일어나다 "to stand"/"to wake up" between keep apart (0.95) and merge (0.90) |
| 2. Dictionary headword (senses of one headword merge) | 34 / 3 / 0, stable over 2 runs | Wrong merges at high confidence: 달 "moon"/"month" (0.99) and 쓰다 "to write"/"to spend" (0.98) |
| 3. **Meaning** (different meanings stay apart, even senses of one word; when unsure, keep apart) | **26 / 11 / 0, same verdicts on 2 runs** | None found. Every homonym stays apart (달, 쓰다, 약, 안, 적다), and so do the related senses (잡다, 일어나다, 맞다, 좋다, 좋아요, 뭐) |

Rule 3 is in the resolver. A wrong merge is the costly mistake, because it mixes two meanings
into one entry and its history can't be split back. A wrong keep-apart costs nothing: the learner
can still merge by hand. So when unsure, the resolver keeps copies apart. The examples in the
prompt are words outside the learner's data (엄마, 눈, 타다, 열다), so the dry run stays a fair
test.

Labels: many copies carry a wrong language ("German", "Other", "Korean (auto-generated)") on
plainly Korean words (새, 전화번호, 주소, 춤추다, 컴퓨터, 옷, 찾다, 혹시, 옮기다, 지키다). The
AI reports the target language for these, and the checks accept only the learner's target
language.

Measured cost: about 7k tokens in and 7k out per full pass, 50 to 90 seconds (batches run one
after another), with 0 to 9 groups asked again on the reasoning tier. Every rule-3 call scored
at least 0.85, so the thresholds stay 0.85 (0.9 for Mixed).

## Cost and speed

46 groups take about 2 Fast calls: roughly 6k tokens in and 4k out, a few cents, 10 to 30
seconds. Escalations use the Reasoning tier for only the uncertain few. The pass runs in the
background with progress, can be cancelled (merges already made stay and are reported), and
syncs once at the end.

## Phases

| # | Work | Needs |
|---|---|---|
| 0 | Dry-run evaluation on the 46 groups; report for the Captain | dev AI calls only |
| 1 | Shared resolver, response record, checks, prompt, tests with a fake AI | |
| 2 | Decision table: migrations, sync, owner checks, isolation tests | Captain's go (schema) |
| 3 | Apply by confidence, escalation, report, Undo | phase 2 |
| 4 | Clean Up Duplicates on iOS, Android, web and MAUI: Resolve with AI, report, Needs You | review, simulator, Playwright |
| 5 | Later: Sam can run it ("clean up my duplicates"); imports check a new word against the learner's same-term words before creating a copy | |

## Decisions for the Captain

1. **Auto-apply:** apply confident AI calls without asking (recommended; report and Undo
   after), or have the AI pre-select everything and confirm each group with one tap.
2. **Meaning wording:** let the AI write the kept meaning ("sound; noise", "to be cool")
   (recommended; the original meanings are kept in the decision record), or only choose between
   the existing meanings.
3. **Decision table** (schema change for kept-apart memory and Undo): approve phase 2?
4. **Clarify kept-apart meanings** ("moon (in the sky)" / "month"), so quizzes show which
   word is meant (recommended).
