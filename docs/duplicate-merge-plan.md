# Duplicate merge: one step for the safe groups, review only the risky ones

Captain, 2026-11-14: "We need a better duplicate merge UI. The cramped space in the split view
doesn't work, and only merging a few at a time is inefficient. I almost always do all of them at
once with no review. Only flag for me merges that pose real risk and require a review."
Also: 먹다 (six copies) said "No merge was needed": the merge refused and the UI reported a no-op.

## Why merges fail or stay stuck today (read 2026-11-14)

- **The keeper is chosen without the merge's own checks.** `VocabularyDuplicateGroup.RecommendedKeeper`
  orders copies by encoding score, memory aids, then the newest `UpdatedAt`. The merge then refuses
  the whole group when that keeper has any outside reference
  (`LearningResourceRepository.MergeWithinTransactionAsync`): 먹다's keeper `38` is linked from the
  ownerless Starter Vocabulary resource.
- **Any phrase link blocks a copy,** even when the phrase and its parts are all the learner's own
  words (`GetDeleteWordIdsWithUnownedArtifactsAsync`). About 49 server groups and 50 on Duotopia
  were stuck on this alone.
- **Words outside the learner's resources are left out.** The finder joins through resource
  mappings, and the merge refuses a keeper with no mapping, though a word is the learner's by its
  recorded owner (`VocabularyWordOwnership`).
- **A refusal looks like success:** web and native report "No merge was needed".
- **Differences are dropped silently.** Fields are copied only into an empty keeper field
  (`MergeVocabularyWordFieldsAsync`), so a second meaning, memory aid or base form is lost.
- **One page at a time.** Native merges one group per tap; the web's "Merge all safe groups" covers
  only the groups loaded so far.

## Rules

A group is the learner's own copies of one target term (normalized as today).

- **Keeper:** the best copy by today's order that the merge can keep (no outside reference).
- **Removable copies:** copies with no outside reference. A copy another learner's data uses stays,
  but its own links and history still move to the keeper (as today).
- **Safe** (merged by "Merge all" without review), when nothing is lost:
  - same language and kind (lexical type);
  - one meaning: the copies' native terms are equal after normalizing (case, spacing, line
    endings, trailing punctuation, a leading "to ");
  - at most one distinct memory aid text, memory aid image and base form (lemma) among the copies;
  - tags are united, audio is regenerable, so neither makes a group risky.
- **Needs review** (shown to the Captain with the differences): different meanings (e.g. 맞다 "to fit"
  and "to be right"), different memory aids, different base forms, or a different language or kind.
  "Merge" keeps the keeper's meaning and memory aid and adds nothing to it; "Skip" leaves the group.
- **Can't merge** (shown with the reason): no copy can be kept, or none can be removed.

## Work

1. **Shared** (`SentenceStudio.Shared`):
   - The finder covers all of the learner's words, rates each group (safe, review, can't), and
     carries its keeper and the reasons.
   - The merge accepts a keeper outside the learner's resources, moves phrase links whose words are
     all the learner's, unites tags, and returns why it refused.
   - `MergeSafeDuplicateGroupsAsync` merges every safe group (all pages) and reports merged groups,
     removed copies and refusals.
   - Tests in `VocabularyMergeHistoryTests` (SQLite) and the owner-isolation tests (PostgreSQL).
2. **iOS:** a full-width Duplicates page (not the list column): a summary, one "Merge N groups"
   button, then "Needs review" rows (copies, differences, Merge or Skip) and "Can't merge" rows.
3. **Web and MAUI** (shared Razor) and **Android** (Compose): the same flow.

Every merge on a device syncs through CoreSync.Sqlite 0.1.129-local4 (B87), so a large merge no
longer echoes deletes.

## Status (2026-11-14, branch `library-split-view`, not committed)

Built, reviewed (10 findings: 7 fixed, 3 to the backlog) and run on the SS iPad simulator against
the local stack (Test Sailor, 22 safe and 2 review groups seeded): Clean Up Duplicates opens the
page in the detail column, Skip hides a review group, "Merge 22 Group(s)" removed 22 copies
(125 -> 103 words, history kept: two words now "Learning", no orphan progress or links), Merge on
the 맞다 review group kept "to be correct", a word opened from the list replaces the page, and the
editor's Find Duplicates opens it focused. Not run: the web/MAUI panel (built only), Android
(Release compile and contract tests; the Debug build lacks the Ailoha AARs on Lilliput), Korean UI.
Open: the server keeps an old copy whose Minimal Pairs answer the same upload re-pointed
(`docs/native-review-backlog.md`).

Review fixes: an empty meaning is filled from another copy; one group's failure no longer stops
Merge all (reported as `Vocabulary_MergeFailed`); a phrase and its own part no longer leave a
self-link; each merge loads only the pairs and phrase links of its words; Merge on a group whose
other copies someone else uses moves the learner's history again (as before); Merge all takes the
focused term in the repository (no client loops); the web reloads after a failed Merge all.

- Shared: as above (`ScanDuplicateGroupsAsync`, `MergeSafeDuplicateGroupsAsync`, refusal keys,
  phrase links, tag union). "Merge all" stops between groups when cancelled and still reports and
  syncs the groups it merged.
- Native state (`VocabularyDuplicates.cs`): Merge folds into the group's keeper (it used the first
  listed copy), review groups merge on request, Skip lasts while the page is open, Merge all on a
  page focused on one word merges only that word's group. Store:
  `MergeSafeOwnedVocabularyDuplicatesAsync` (active owner only).
- iOS: `VocabularyDuplicatesView` is a grouped Form page on route `vocabulary-duplicates`, pushed
  above the Vocabulary list (the split view's detail column, with Full screen); Clean Up Duplicates
  (`native.vocabulary.duplicates.open`) in the list toolbar; the editor's Find Duplicates opens the
  same page focused on its word. A word opened from the list beside it takes the detail column.
  "Edit record" is gone (no group is blocked by its fields any more).
- Web and MAUI (`Vocabulary.razor`) and Android (a full page like Recent lookups, opened from the
  list's overflow menu): the same sections.
