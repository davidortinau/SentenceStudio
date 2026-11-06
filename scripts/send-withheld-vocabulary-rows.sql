-- One-time server step: send to each learner's devices the vocabulary rows the old sync filter
-- withheld from them.
--
-- Why: until 2026-11-06 a device received the words its learner's resources link
-- (ResourceVocabularyMapping), and only their dependents. Since then it receives the words
-- recorded to its learner (VocabularyWord.UserProfileId), linked or not (Captain 2026-11-06:
-- "every word should have an owner and only be visible and used by those owners"). Changing the
-- filter does not resend a row whose change devices already moved past: CoreSync sends a row only
-- when __core_sync_ct holds a change for it newer than the device's anchor. This script records a
-- no-op change ("touch") for every row the new filter sends that the old filter withheld, so each
-- owner's devices download it on their next sync.
--
-- What it touches, per learner O (a profile), with the new filter's rules
-- (src/SentenceStudio.Api/Sync/NativeOwnerSyncProvider.cs, IsVisible):
--   words        recorded to O and not linked from any of O's resources (set W);
--   progress     O's progress (UserId = O) on a word in W, and
--   contexts     the learning contexts of that progress;
--   sentences    O's example sentences (UserProfileId = O) on a word in W whose resource is
--                none or O's;
--   phrase parts whose phrase word and part word (when there is one) are both recorded to O,
--                and one of them is in W (the old filter checked only the phrase word, so a part
--                whose word it withheld could have failed its foreign key on the device);
--   pairs        O's minimal pairs, and minimal-pair answers, whose two words are both recorded
--                to O and not both linked from O's resources.
-- Links need nothing: the old filter already sent every link from O's resources, and the new
-- one sends fewer. NULL and '' owners are no owner. Only owners that are profiles count.
--
-- Run it only with the Captain's go, after a fresh verified backup, after the new sync filter is
-- deployed, and right after every device of his has synced with it
-- (docs/deploy-runbook.md, "Ownerless shared words and withheld rows", explains why). It prints
-- counts only (learners as a short profile ID and a masked email), never row contents.
--
-- Variables (psql -v):
--   expected_words, expected_progress, expected_contexts, expected_sentences,
--   expected_phrases, expected_pairs, expected_answers
--                  the dry run's counts; any other count aborts
--   expected_tracked
--                  the dry run's "tracked up to" value (the newest CoreSync change id). Any
--                  CoreSync change since the dry run, including an earlier apply of this script,
--                  moves it, so an apply always needs a fresh dry run.
--   apply          true commits (every expected value is then required); leave it out for a dry
--                  run that prints the plan and rolls back
--
-- Example (credentials in PG* environment variables, never on the command line):
--   psql -d sentencestudio -v ON_ERROR_STOP=1 -f scripts/send-withheld-vocabulary-rows.sql
--   psql -d sentencestudio -v ON_ERROR_STOP=1 -v expected_words=3 -v expected_progress=3 \
--     -v expected_contexts=12 -v expected_sentences=0 -v expected_phrases=0 \
--     -v expected_pairs=0 -v expected_answers=0 -v expected_tracked=123456 -v apply=true \
--     -f scripts/send-withheld-vocabulary-rows.sql
--
-- Safe to repeat: the touch changes no value (SET "Id" = "Id"), and the set it picks depends
-- only on the data, so a second run picks the same rows and re-sends them. Re-sending is
-- harmless to a device that already has them, but carries the same conflict rules as the first
-- run, so repeat it only for a reason and, like the first run, right after the devices synced.
--
-- Everything runs in one transaction; any failed check rolls back the whole script. The tables
-- it reads are locked against writes for its few seconds, so the plan cannot change under it.
-- Each UPDATE fires CoreSync's AFTER UPDATE trigger (__<table>_ct_update__), which inserts
-- (tbl, 'U', id) into __core_sync_ct with no source store. The script checks that one tracked
-- change was recorded per touched row and aborts otherwise. No value changes, UpdatedAt included.

\set ON_ERROR_STOP on
\if :{?apply}
\else
\set apply false
\endif
\if :{?expected_words}
\else
\set expected_words -1
\endif
\if :{?expected_progress}
\else
\set expected_progress -1
\endif
\if :{?expected_contexts}
\else
\set expected_contexts -1
\endif
\if :{?expected_sentences}
\else
\set expected_sentences -1
\endif
\if :{?expected_phrases}
\else
\set expected_phrases -1
\endif
\if :{?expected_pairs}
\else
\set expected_pairs -1
\endif
\if :{?expected_answers}
\else
\set expected_answers -1
\endif
\if :{?expected_tracked}
\else
\set expected_tracked -1
\endif

BEGIN;
SET LOCAL lock_timeout = '10s';

-- psql does not expand variables inside DO blocks, so pass them through a temp table.
CREATE TEMP TABLE withheld_params ON COMMIT DROP AS
SELECT (:'apply')::boolean AS apply,
       (:'expected_words')::int AS expected_words,
       (:'expected_progress')::int AS expected_progress,
       (:'expected_contexts')::int AS expected_contexts,
       (:'expected_sentences')::int AS expected_sentences,
       (:'expected_phrases')::int AS expected_phrases,
       (:'expected_pairs')::int AS expected_pairs,
       (:'expected_answers')::int AS expected_answers,
       (:'expected_tracked')::bigint AS expected_tracked;

DO $$
DECLARE
    p withheld_params%ROWTYPE := (SELECT p FROM withheld_params p);
    tracking int;
BEGIN
    IF p.apply AND (p.expected_words < 0 OR p.expected_progress < 0 OR p.expected_contexts < 0
                    OR p.expected_sentences < 0 OR p.expected_phrases < 0 OR p.expected_pairs < 0
                    OR p.expected_answers < 0 OR p.expected_tracked < 0) THEN
        RAISE EXCEPTION 'apply=true needs every expected_* value, expected_tracked included, from a dry run. Nothing changed.';
    END IF;
    IF to_regclass('public.__core_sync_ct') IS NULL THEN
        RAISE EXCEPTION 'CoreSync change tracking (__core_sync_ct) is missing, so devices would never receive the rows. Nothing changed.';
    END IF;
    SELECT count(*) INTO tracking FROM pg_trigger t
    WHERE NOT t.tgisinternal AND t.tgenabled <> 'D'
      AND ((t.tgrelid = '"VocabularyWord"'::regclass AND t.tgname = '__vocabularyword_ct_update__')
        OR (t.tgrelid = '"VocabularyProgress"'::regclass AND t.tgname = '__vocabularyprogress_ct_update__')
        OR (t.tgrelid = '"VocabularyLearningContext"'::regclass AND t.tgname = '__vocabularylearningcontext_ct_update__')
        OR (t.tgrelid = '"ExampleSentence"'::regclass AND t.tgname = '__examplesentence_ct_update__')
        OR (t.tgrelid = '"PhraseConstituent"'::regclass AND t.tgname = '__phraseconstituent_ct_update__')
        OR (t.tgrelid = '"MinimalPair"'::regclass AND t.tgname = '__minimalpair_ct_update__')
        OR (t.tgrelid = '"MinimalPairAttempt"'::regclass AND t.tgname = '__minimalpairattempt_ct_update__'));
    IF tracking <> 7 THEN
        RAISE EXCEPTION 'CoreSync update tracking is not enabled on all seven vocabulary tables (% of 7 triggers). Nothing changed.', tracking;
    END IF;
END $$;

-- Writes to these tables wait until this transaction ends; reads go on.
LOCK TABLE "VocabularyWord", "VocabularyProgress", "VocabularyLearningContext", "ExampleSentence",
    "PhraseConstituent", "MinimalPair", "MinimalPairAttempt" IN SHARE ROW EXCLUSIVE MODE;
LOCK TABLE "ResourceVocabularyMapping", "LearningResource", "UserProfile" IN SHARE MODE;

CREATE TEMP TABLE withheld_before ON COMMIT DROP AS
SELECT (SELECT coalesce(max(id), 0) FROM __core_sync_ct) AS tracked_before;

-- Words each learner's resources link: what the old filter sent.
CREATE TEMP TABLE withheld_linked (owner text NOT NULL, word_id text NOT NULL,
    PRIMARY KEY (owner, word_id)) ON COMMIT DROP;
INSERT INTO withheld_linked (owner, word_id)
SELECT DISTINCT r."UserProfileId", m."VocabularyWordId"
FROM "ResourceVocabularyMapping" m
JOIN "LearningResource" r ON r."Id" = m."ResourceId"
WHERE r."UserProfileId" IS NOT NULL AND r."UserProfileId" <> ''
  AND EXISTS (SELECT 1 FROM "UserProfile" p WHERE p."Id" = r."UserProfileId");

-- Every word recorded to a profile, and whether the old filter sent it to that owner.
CREATE TEMP TABLE withheld_owned (word_id text PRIMARY KEY, owner text NOT NULL,
    linked boolean NOT NULL) ON COMMIT DROP;
INSERT INTO withheld_owned (word_id, owner, linked)
SELECT w."Id", w."UserProfileId",
       EXISTS (SELECT 1 FROM withheld_linked l
               WHERE l.owner = w."UserProfileId" AND l.word_id = w."Id")
FROM "VocabularyWord" w
WHERE w."UserProfileId" IS NOT NULL AND w."UserProfileId" <> ''
  AND EXISTS (SELECT 1 FROM "UserProfile" p WHERE p."Id" = w."UserProfileId");

CREATE TEMP TABLE withheld_rows (tbl text NOT NULL, id text NOT NULL, owner text NOT NULL,
    PRIMARY KEY (tbl, id)) ON COMMIT DROP;

INSERT INTO withheld_rows (tbl, id, owner)
SELECT 'VocabularyWord', o.word_id, o.owner FROM withheld_owned o WHERE NOT o.linked;

INSERT INTO withheld_rows (tbl, id, owner)
SELECT 'VocabularyProgress', v."Id", o.owner
FROM "VocabularyProgress" v
JOIN withheld_owned o ON o.word_id = v."VocabularyWordId" AND o.owner = v."UserId"
WHERE NOT o.linked;

INSERT INTO withheld_rows (tbl, id, owner)
SELECT 'VocabularyLearningContext', c."Id", t.owner
FROM "VocabularyLearningContext" c
JOIN withheld_rows t ON t.tbl = 'VocabularyProgress' AND t.id = c."VocabularyProgressId";

INSERT INTO withheld_rows (tbl, id, owner)
SELECT 'ExampleSentence', s."Id", o.owner
FROM "ExampleSentence" s
JOIN withheld_owned o ON o.word_id = s."VocabularyWordId" AND o.owner = s."UserProfileId"
LEFT JOIN "LearningResource" r ON r."Id" = s."LearningResourceId"
WHERE NOT o.linked
  AND (s."LearningResourceId" IS NULL OR s."LearningResourceId" = ''
       OR r."UserProfileId" = o.owner);

INSERT INTO withheld_rows (tbl, id, owner)
SELECT 'PhraseConstituent', pc."Id", phrase.owner
FROM "PhraseConstituent" pc
JOIN withheld_owned phrase ON phrase.word_id = pc."PhraseWordId"
LEFT JOIN withheld_owned part ON part.word_id = pc."ConstituentWordId"
WHERE (pc."ConstituentWordId" IS NULL OR pc."ConstituentWordId" = ''
       OR part.owner = phrase.owner)
  AND (NOT phrase.linked OR (part.word_id IS NOT NULL AND NOT part.linked));

INSERT INTO withheld_rows (tbl, id, owner)
SELECT 'MinimalPair', mp."Id", a.owner
FROM "MinimalPair" mp
JOIN withheld_owned a ON a.word_id = mp."VocabularyWordAId" AND a.owner = mp."UserId"
JOIN withheld_owned b ON b.word_id = mp."VocabularyWordBId" AND b.owner = mp."UserId"
WHERE NOT (a.linked AND b.linked);

INSERT INTO withheld_rows (tbl, id, owner)
SELECT 'MinimalPairAttempt', ma."Id", a.owner
FROM "MinimalPairAttempt" ma
JOIN withheld_owned a ON a.word_id = ma."PromptWordId" AND a.owner = ma."UserId"
JOIN withheld_owned b ON b.word_id = ma."SelectedWordId" AND b.owner = ma."UserId"
WHERE NOT (a.linked AND b.linked);

\echo
\echo 'Rows to send, per learner:'
SELECT left(p."Id", 8) AS learner,
       CASE WHEN coalesce(p."Email", '') = '' THEN '(no email)'
            ELSE left(p."Email", 1) || '***@' || split_part(p."Email", '@', 2) END AS email,
       count(*) FILTER (WHERE t.tbl = 'VocabularyWord') AS words,
       count(*) FILTER (WHERE t.tbl = 'VocabularyProgress') AS progress,
       count(*) FILTER (WHERE t.tbl = 'VocabularyLearningContext') AS contexts,
       count(*) FILTER (WHERE t.tbl = 'ExampleSentence') AS sentences,
       count(*) FILTER (WHERE t.tbl = 'PhraseConstituent') AS phrases,
       count(*) FILTER (WHERE t.tbl = 'MinimalPair') AS pairs,
       count(*) FILTER (WHERE t.tbl = 'MinimalPairAttempt') AS answers
FROM withheld_rows t
JOIN "UserProfile" p ON p."Id" = t.owner
GROUP BY p."Id", p."Email"
ORDER BY 3 DESC, 4 DESC, 1;

\echo 'Totals:'
SELECT count(*) FILTER (WHERE tbl = 'VocabularyWord') AS words,
       count(*) FILTER (WHERE tbl = 'VocabularyProgress') AS progress,
       count(*) FILTER (WHERE tbl = 'VocabularyLearningContext') AS contexts,
       count(*) FILTER (WHERE tbl = 'ExampleSentence') AS sentences,
       count(*) FILTER (WHERE tbl = 'PhraseConstituent') AS phrases,
       count(*) FILTER (WHERE tbl = 'MinimalPair') AS pairs,
       count(*) FILTER (WHERE tbl = 'MinimalPairAttempt') AS answers,
       (SELECT tracked_before FROM withheld_before) AS "tracked up to"
FROM withheld_rows;

DO $$
DECLARE
    p withheld_params%ROWTYPE := (SELECT p FROM withheld_params p);
    tracked bigint := (SELECT tracked_before FROM withheld_before);
    r record;
BEGIN
    IF p.expected_tracked >= 0 AND tracked <> p.expected_tracked THEN
        RAISE EXCEPTION 'CoreSync tracked up to %, expected % from the dry run: something changed since (a sync, an edit, or an earlier apply). Run the dry run again. Nothing changed.', tracked, p.expected_tracked;
    END IF;
    FOR r IN
        SELECT v.tbl, v.expected, (SELECT count(*) FROM withheld_rows t WHERE t.tbl = v.tbl) AS found
        FROM (VALUES ('VocabularyWord', p.expected_words),
                     ('VocabularyProgress', p.expected_progress),
                     ('VocabularyLearningContext', p.expected_contexts),
                     ('ExampleSentence', p.expected_sentences),
                     ('PhraseConstituent', p.expected_phrases),
                     ('MinimalPair', p.expected_pairs),
                     ('MinimalPairAttempt', p.expected_answers)) AS v(tbl, expected)
    LOOP
        IF r.expected >= 0 AND r.found <> r.expected THEN
            RAISE EXCEPTION 'Would send % % rows, expected %. Nothing changed.', r.found, r.tbl, r.expected;
        END IF;
    END LOOP;
END $$;

-- The touches, parents first. SET "Id" = "Id" changes nothing but fires the update trigger.
CREATE TEMP TABLE withheld_touched (tbl text NOT NULL, id text NOT NULL) ON COMMIT DROP;

WITH touched AS (
    UPDATE "VocabularyWord" x SET "Id" = x."Id"
    FROM withheld_rows t WHERE t.tbl = 'VocabularyWord' AND x."Id" = t.id RETURNING x."Id")
INSERT INTO withheld_touched SELECT 'VocabularyWord', "Id" FROM touched;
WITH touched AS (
    UPDATE "VocabularyProgress" x SET "Id" = x."Id"
    FROM withheld_rows t WHERE t.tbl = 'VocabularyProgress' AND x."Id" = t.id RETURNING x."Id")
INSERT INTO withheld_touched SELECT 'VocabularyProgress', "Id" FROM touched;
WITH touched AS (
    UPDATE "VocabularyLearningContext" x SET "Id" = x."Id"
    FROM withheld_rows t WHERE t.tbl = 'VocabularyLearningContext' AND x."Id" = t.id RETURNING x."Id")
INSERT INTO withheld_touched SELECT 'VocabularyLearningContext', "Id" FROM touched;
WITH touched AS (
    UPDATE "ExampleSentence" x SET "Id" = x."Id"
    FROM withheld_rows t WHERE t.tbl = 'ExampleSentence' AND x."Id" = t.id RETURNING x."Id")
INSERT INTO withheld_touched SELECT 'ExampleSentence', "Id" FROM touched;
WITH touched AS (
    UPDATE "PhraseConstituent" x SET "Id" = x."Id"
    FROM withheld_rows t WHERE t.tbl = 'PhraseConstituent' AND x."Id" = t.id RETURNING x."Id")
INSERT INTO withheld_touched SELECT 'PhraseConstituent', "Id" FROM touched;
WITH touched AS (
    UPDATE "MinimalPair" x SET "Id" = x."Id"
    FROM withheld_rows t WHERE t.tbl = 'MinimalPair' AND x."Id" = t.id RETURNING x."Id")
INSERT INTO withheld_touched SELECT 'MinimalPair', "Id" FROM touched;
WITH touched AS (
    UPDATE "MinimalPairAttempt" x SET "Id" = x."Id"
    FROM withheld_rows t WHERE t.tbl = 'MinimalPairAttempt' AND x."Id" = t.id RETURNING x."Id")
INSERT INTO withheld_touched SELECT 'MinimalPairAttempt', "Id" FROM touched;

DO $$
DECLARE
    tracked_before bigint := (SELECT tracked_before FROM withheld_before);
    planned int := (SELECT count(*) FROM withheld_rows);
    touched int := (SELECT count(*) FROM withheld_touched);
    logged int := (SELECT count(*) FROM withheld_rows t
                   WHERE EXISTS (SELECT 1 FROM __core_sync_ct ct
                                 WHERE ct.id > tracked_before AND ct.tbl = t.tbl
                                   AND ct.op = 'U' AND ct.src IS NULL AND ct.pk_text = t.id));
    -- Only these seven tables: they are locked, so any other change in them is this script's.
    stray int := (SELECT count(*) FROM __core_sync_ct ct
                  WHERE ct.id > tracked_before
                    AND ct.tbl IN ('VocabularyWord', 'VocabularyProgress',
                                   'VocabularyLearningContext', 'ExampleSentence',
                                   'PhraseConstituent', 'MinimalPair', 'MinimalPairAttempt')
                    AND NOT EXISTS (SELECT 1 FROM withheld_rows t
                                    WHERE t.tbl = ct.tbl AND t.id = ct.pk_text));
BEGIN
    IF touched <> planned THEN
        RAISE EXCEPTION 'Touched % rows, planned %. Nothing changed.', touched, planned;
    END IF;
    IF logged <> planned THEN
        RAISE EXCEPTION 'CoreSync tracked % of % touched rows, so some devices would miss them. Nothing changed.', logged, planned;
    END IF;
    IF stray <> 0 THEN
        RAISE EXCEPTION 'CoreSync recorded % changes for rows this script did not plan. Nothing changed.', stray;
    END IF;
    RAISE NOTICE 'Touched % rows, each tracked for sync.', planned;
END $$;

\if :apply
COMMIT;
\echo 'Committed.'
\else
ROLLBACK;
\echo 'Dry run: rolled back. Nothing changed.'
\endif
