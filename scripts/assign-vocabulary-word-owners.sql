-- One-time server backfill: record the owner of every word (and example sentence) that exactly
-- one learner uses, so the one runtime ownership rule can apply to it.
--
-- Why: since 2026-11-06 a word is a learner's only when VocabularyWord.UserProfileId names
-- them (one runtime rule for the web app, MAUI, the native heads, the API's sync and the
-- Coach; nothing is inferred from links or progress at runtime). A word recorded to no one is
-- read-only for everyone. This script records an owner where the rows prove there is exactly
-- one, and nowhere else (docs/native-port-backports.md, "One ownership rule").
--
-- Rules, in order (the same "uses" as VocabularyWordDependents.OthersDependOnAsync, the rule
-- that decides whether a learner may delete a word):
--   1. An ownerless example sentence in a resource takes the resource's owner (phase 3).
--   2. An ownerless word is used by:
--        the owner of each resource that links it (ResourceVocabularyMapping);
--        each learner with progress on it (VocabularyProgress.UserId);
--        the owner of each example sentence on it (after step 1);
--        the learner of each minimal pair, and of each minimal-pair answer, that names it;
--        the owner of each phrase that has it as a part (PhraseConstituent, by phrase word).
--      A sentence still ownerless after step 1 is not a use: it follows its word (step 3).
--      The word gets an owner only when every use names a learner, they all name the same
--      one, and that learner is a profile. Words used by two or more learners, by a row that
--      names no one (for example a link from a resource with no owner), or by nobody, stay
--      ownerless. Recording a phrase word can resolve its parts, so step 2 repeats until it
--      records nothing more (a recorded word's uses all named its one learner already, so a
--      later pass never changes it).
--   3. An ownerless example sentence then takes its word's owner (phase 3).
-- Nothing is deleted, no recorded owner changes, and nothing else is written.
--
-- Run it only with the Captain's go. It prints counts only (learners as a short profile ID and
-- a masked email), never row contents.
--
-- Variables (psql -v):
--   expected_ownerless  ownerless words counted by the dry run; any other count aborts
--   expected_words      words the dry run would record; any other count aborts
--   expected_sentences  sentences the dry run would record; any other count aborts
--   apply               true commits (all three expected values are then required); leave it
--                       out for a dry run that prints the plan and rolls back
--
-- Example (credentials in PG* environment variables, never on the command line):
--   psql -d sentencestudio -v ON_ERROR_STOP=1 -f scripts/assign-vocabulary-word-owners.sql
--   psql -d sentencestudio -v ON_ERROR_STOP=1 -v expected_ownerless=337 \
--     -v expected_words=12 -v expected_sentences=0 -v apply=true \
--     -f scripts/assign-vocabulary-word-owners.sql
--
-- Everything runs in one transaction. Any failed check rolls back the whole script. The tables
-- it reads are locked against writes for its few seconds, so the plan cannot change under it.
-- Each UPDATE fires CoreSync's change-tracking trigger (__vocabularyword_ct_update__,
-- __examplesentence_ct_update__), which records the row in __core_sync_ct with no source
-- store, so every device that can see the row downloads its new owner on its next sync. The
-- script checks that one tracked change was recorded per updated row and aborts otherwise.
-- UpdatedAt is left as it was.

\set ON_ERROR_STOP on
\if :{?apply}
\else
\set apply false
\endif
\if :{?expected_ownerless}
\else
\set expected_ownerless -1
\endif
\if :{?expected_words}
\else
\set expected_words -1
\endif
\if :{?expected_sentences}
\else
\set expected_sentences -1
\endif

BEGIN;
SET LOCAL lock_timeout = '10s';

-- psql does not expand variables inside DO blocks, so pass them through a temp table.
CREATE TEMP TABLE backfill_params ON COMMIT DROP AS
SELECT (:'apply')::boolean AS apply,
       (:'expected_ownerless')::int AS expected_ownerless,
       (:'expected_words')::int AS expected_words,
       (:'expected_sentences')::int AS expected_sentences;

DO $$
DECLARE
    p backfill_params%ROWTYPE := (SELECT p FROM backfill_params p);
    tracking int;
BEGIN
    IF p.apply AND (p.expected_ownerless < 0 OR p.expected_words < 0 OR p.expected_sentences < 0) THEN
        RAISE EXCEPTION 'apply=true needs expected_ownerless, expected_words and expected_sentences from a dry run. Nothing changed.';
    END IF;
    IF to_regclass('public.__core_sync_ct') IS NULL THEN
        RAISE EXCEPTION 'CoreSync change tracking (__core_sync_ct) is missing, so devices would never receive the owners. Nothing changed.';
    END IF;
    SELECT count(*) INTO tracking FROM pg_trigger t
    WHERE NOT t.tgisinternal AND t.tgenabled <> 'D'
      AND ((t.tgrelid = '"VocabularyWord"'::regclass AND t.tgname = '__vocabularyword_ct_update__')
        OR (t.tgrelid = '"ExampleSentence"'::regclass AND t.tgname = '__examplesentence_ct_update__'));
    IF tracking <> 2 THEN
        RAISE EXCEPTION 'CoreSync update tracking is not enabled on VocabularyWord and ExampleSentence (% of 2 triggers). Nothing changed.', tracking;
    END IF;
END $$;

-- Writes to these tables wait until this transaction ends; reads go on.
LOCK TABLE "VocabularyWord", "ExampleSentence" IN SHARE ROW EXCLUSIVE MODE;
LOCK TABLE "ResourceVocabularyMapping", "LearningResource", "VocabularyProgress", "MinimalPair",
    "MinimalPairAttempt", "PhraseConstituent", "UserProfile" IN SHARE MODE;

CREATE TEMP TABLE backfill_before ON COMMIT DROP AS
SELECT (SELECT count(*) FROM "VocabularyWord"
        WHERE "UserProfileId" IS NULL OR "UserProfileId" = '') AS ownerless_words,
       (SELECT count(*) FROM "ExampleSentence"
        WHERE "UserProfileId" IS NULL OR "UserProfileId" = '') AS ownerless_sentences,
       (SELECT coalesce(max(id), 0) FROM __core_sync_ct) AS tracked_before;

CREATE TEMP TABLE backfill_sentences (id text PRIMARY KEY, owner text NOT NULL, via text NOT NULL)
    ON COMMIT DROP;
CREATE TEMP TABLE backfill_words (id text PRIMARY KEY, owner text NOT NULL, pass int NOT NULL)
    ON COMMIT DROP;
CREATE TEMP TABLE backfill_uses (word_id text NOT NULL, learner text) ON COMMIT DROP;

-- Step 1: sentences in a resource take the resource's owner.
INSERT INTO backfill_sentences (id, owner, via)
SELECT s."Id", r."UserProfileId", 'resource'
FROM "ExampleSentence" s
JOIN "LearningResource" r ON r."Id" = s."LearningResourceId"
WHERE (s."UserProfileId" IS NULL OR s."UserProfileId" = '')
  AND r."UserProfileId" IS NOT NULL AND r."UserProfileId" <> ''
  AND EXISTS (SELECT 1 FROM "UserProfile" p WHERE p."Id" = r."UserProfileId");

-- Step 2: words, repeated while recording a phrase word resolves its parts.
DO $$
DECLARE
    pass int := 0;
    added int;
BEGIN
    LOOP
        pass := pass + 1;
        TRUNCATE backfill_uses;
        INSERT INTO backfill_uses (word_id, learner)
        SELECT m."VocabularyWordId", NULLIF(r."UserProfileId", '')
        FROM "ResourceVocabularyMapping" m
        LEFT JOIN "LearningResource" r ON r."Id" = m."ResourceId"
        UNION ALL
        SELECT v."VocabularyWordId", NULLIF(v."UserId", '') FROM "VocabularyProgress" v
        UNION ALL
        SELECT s."VocabularyWordId", COALESCE(NULLIF(s."UserProfileId", ''), b.owner)
        FROM "ExampleSentence" s
        LEFT JOIN backfill_sentences b ON b.id = s."Id"
        WHERE COALESCE(NULLIF(s."UserProfileId", ''), b.owner) IS NOT NULL
        UNION ALL
        SELECT mp."VocabularyWordAId", NULLIF(mp."UserId", '') FROM "MinimalPair" mp
        UNION ALL
        SELECT mp."VocabularyWordBId", NULLIF(mp."UserId", '') FROM "MinimalPair" mp
        UNION ALL
        SELECT a."PromptWordId", NULLIF(a."UserId", '') FROM "MinimalPairAttempt" a
        UNION ALL
        SELECT a."SelectedWordId", NULLIF(a."UserId", '') FROM "MinimalPairAttempt" a
        UNION ALL
        SELECT pc."ConstituentWordId", COALESCE(NULLIF(w."UserProfileId", ''), b.owner)
        FROM "PhraseConstituent" pc
        LEFT JOIN "VocabularyWord" w ON w."Id" = pc."PhraseWordId"
        LEFT JOIN backfill_words b ON b.id = pc."PhraseWordId"
        WHERE pc."ConstituentWordId" IS NOT NULL;

        INSERT INTO backfill_words (id, owner, pass)
        SELECT w."Id", min(u.learner), pass
        FROM "VocabularyWord" w
        JOIN backfill_uses u ON u.word_id = w."Id"
        WHERE (w."UserProfileId" IS NULL OR w."UserProfileId" = '')
          AND NOT EXISTS (SELECT 1 FROM backfill_words b WHERE b.id = w."Id")
        GROUP BY w."Id"
        HAVING count(*) FILTER (WHERE u.learner IS NULL) = 0
           AND count(DISTINCT u.learner) = 1
           AND EXISTS (SELECT 1 FROM "UserProfile" p WHERE p."Id" = min(u.learner));
        GET DIAGNOSTICS added = ROW_COUNT;
        EXIT WHEN added = 0 OR pass >= 10;
    END LOOP;
END $$;

-- Step 3: the remaining sentences take their word's owner.
INSERT INTO backfill_sentences (id, owner, via)
SELECT s."Id", COALESCE(NULLIF(w."UserProfileId", ''), b.owner), 'word'
FROM "ExampleSentence" s
JOIN "VocabularyWord" w ON w."Id" = s."VocabularyWordId"
LEFT JOIN backfill_words b ON b.id = w."Id"
WHERE (s."UserProfileId" IS NULL OR s."UserProfileId" = '')
  AND NOT EXISTS (SELECT 1 FROM backfill_sentences done WHERE done.id = s."Id")
  AND COALESCE(NULLIF(w."UserProfileId", ''), b.owner) IS NOT NULL
  AND EXISTS (SELECT 1 FROM "UserProfile" p
              WHERE p."Id" = COALESCE(NULLIF(w."UserProfileId", ''), b.owner));

-- Why each remaining ownerless word stays ownerless.
CREATE TEMP TABLE backfill_untouched ON COMMIT DROP AS
SELECT w."Id" AS id,
       CASE
           WHEN count(u.word_id) = 0 THEN 'used by nobody'
           WHEN count(DISTINCT u.learner) >= 2 THEN 'used by two or more learners'
           WHEN count(*) FILTER (WHERE u.learner IS NULL) > 0 THEN 'a use names no learner'
           ELSE 'its one learner is not a profile'
       END AS reason
FROM "VocabularyWord" w
LEFT JOIN backfill_uses u ON u.word_id = w."Id"
WHERE (w."UserProfileId" IS NULL OR w."UserProfileId" = '')
  AND NOT EXISTS (SELECT 1 FROM backfill_words b WHERE b.id = w."Id")
GROUP BY w."Id";

\echo
\echo 'Before:'
SELECT ownerless_words AS "ownerless words", ownerless_sentences AS "ownerless sentences"
FROM backfill_before;

\echo 'To record, per learner:'
SELECT left(p."Id", 8) AS learner,
       CASE WHEN coalesce(p."Email", '') = '' THEN '(no email)'
            ELSE left(p."Email", 1) || '***@' || split_part(p."Email", '@', 2) END AS email,
       (SELECT count(*) FROM backfill_words b WHERE b.owner = p."Id") AS words,
       (SELECT count(*) FROM backfill_sentences s WHERE s.owner = p."Id") AS sentences
FROM "UserProfile" p
WHERE EXISTS (SELECT 1 FROM backfill_words b WHERE b.owner = p."Id")
   OR EXISTS (SELECT 1 FROM backfill_sentences s WHERE s.owner = p."Id")
ORDER BY 3 DESC, 4 DESC, 1;

\echo 'Words staying ownerless:'
SELECT reason, count(*) AS words FROM backfill_untouched GROUP BY reason ORDER BY 2 DESC, 1;

\echo 'Totals:'
SELECT (SELECT count(*) FROM backfill_words) AS "words to record",
       (SELECT count(*) FROM backfill_untouched) AS "words untouched",
       (SELECT count(*) FROM backfill_untouched WHERE reason = 'used by two or more learners')
           AS "of which ambiguous",
       (SELECT count(*) FROM backfill_sentences) AS "sentences to record",
       (SELECT ownerless_sentences FROM backfill_before)
           - (SELECT count(*) FROM backfill_sentences) AS "sentences untouched",
       (SELECT coalesce(max(pass), 0) FROM backfill_words) AS "passes";

DO $$
DECLARE
    p backfill_params%ROWTYPE := (SELECT p FROM backfill_params p);
    ownerless int := (SELECT ownerless_words FROM backfill_before);
    words int := (SELECT count(*) FROM backfill_words);
    sentences int := (SELECT count(*) FROM backfill_sentences);
BEGIN
    IF p.expected_ownerless >= 0 AND ownerless <> p.expected_ownerless THEN
        RAISE EXCEPTION 'Found % ownerless words, expected %. Nothing changed.', ownerless, p.expected_ownerless;
    END IF;
    IF p.expected_words >= 0 AND words <> p.expected_words THEN
        RAISE EXCEPTION 'Would record % words, expected %. Nothing changed.', words, p.expected_words;
    END IF;
    IF p.expected_sentences >= 0 AND sentences <> p.expected_sentences THEN
        RAISE EXCEPTION 'Would record % sentences, expected %. Nothing changed.', sentences, p.expected_sentences;
    END IF;
END $$;

CREATE TEMP TABLE backfill_updated (tbl text NOT NULL, id text NOT NULL) ON COMMIT DROP;

WITH updated AS (
    UPDATE "VocabularyWord" w
    SET "UserProfileId" = b.owner
    FROM backfill_words b
    WHERE w."Id" = b.id AND (w."UserProfileId" IS NULL OR w."UserProfileId" = '')
    RETURNING w."Id"
)
INSERT INTO backfill_updated (tbl, id) SELECT 'VocabularyWord', "Id" FROM updated;

WITH updated AS (
    UPDATE "ExampleSentence" s
    SET "UserProfileId" = b.owner
    FROM backfill_sentences b
    WHERE s."Id" = b.id AND (s."UserProfileId" IS NULL OR s."UserProfileId" = '')
    RETURNING s."Id"
)
INSERT INTO backfill_updated (tbl, id) SELECT 'ExampleSentence', "Id" FROM updated;

DO $$
DECLARE
    words int := (SELECT count(*) FROM backfill_words);
    sentences int := (SELECT count(*) FROM backfill_sentences);
    updated_words int := (SELECT count(*) FROM backfill_updated WHERE tbl = 'VocabularyWord');
    updated_sentences int := (SELECT count(*) FROM backfill_updated WHERE tbl = 'ExampleSentence');
    owned_words int := (SELECT count(*) FROM "VocabularyWord" w
                        JOIN backfill_words b ON b.id = w."Id" AND w."UserProfileId" = b.owner);
    owned_sentences int := (SELECT count(*) FROM "ExampleSentence" s
                            JOIN backfill_sentences b ON b.id = s."Id" AND s."UserProfileId" = b.owner);
    remaining_words int := (SELECT count(*) FROM "VocabularyWord"
                            WHERE "UserProfileId" IS NULL OR "UserProfileId" = '');
    remaining_sentences int := (SELECT count(*) FROM "ExampleSentence"
                                WHERE "UserProfileId" IS NULL OR "UserProfileId" = '');
    tracked_words int := (SELECT count(DISTINCT pk_text) FROM __core_sync_ct
                          WHERE id > (SELECT tracked_before FROM backfill_before)
                            AND tbl = 'VocabularyWord' AND op = 'U' AND src IS NULL
                            AND pk_text IN (SELECT id FROM backfill_words));
    tracked_sentences int := (SELECT count(DISTINCT pk_text) FROM __core_sync_ct
                              WHERE id > (SELECT tracked_before FROM backfill_before)
                                AND tbl = 'ExampleSentence' AND op = 'U' AND src IS NULL
                                AND pk_text IN (SELECT id FROM backfill_sentences));
    before_words int := (SELECT ownerless_words FROM backfill_before);
    before_sentences int := (SELECT ownerless_sentences FROM backfill_before);
BEGIN
    IF updated_words <> words OR owned_words <> words
       OR remaining_words <> before_words - words THEN
        RAISE EXCEPTION 'Recorded % words (owned now %), planned %, % still ownerless of % before. Nothing changed.',
            updated_words, owned_words, words, remaining_words, before_words;
    END IF;
    IF updated_sentences <> sentences OR owned_sentences <> sentences
       OR remaining_sentences <> before_sentences - sentences THEN
        RAISE EXCEPTION 'Recorded % sentences (owned now %), planned %, % still ownerless of % before. Nothing changed.',
            updated_sentences, owned_sentences, sentences, remaining_sentences, before_sentences;
    END IF;
    IF tracked_words <> words OR tracked_sentences <> sentences THEN
        RAISE EXCEPTION 'CoreSync tracked % of % words and % of % sentences, so some devices would miss the owner. Nothing changed.',
            tracked_words, words, tracked_sentences, sentences;
    END IF;
    RAISE NOTICE 'Recorded % words and % sentences, each tracked for sync. Ownerless now: % words, % sentences.',
        words, sentences, remaining_words, remaining_sentences;
END $$;

\if :apply
COMMIT;
\echo 'Committed.'
\else
ROLLBACK;
\echo 'Dry run: rolled back. Nothing changed.'
\endif
