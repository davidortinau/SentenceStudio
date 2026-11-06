-- One-time server step: record to one learner the ownerless words that learner shares only with
-- test accounts and with progress left by deleted accounts.
--
-- Why: a word is a learner's only when VocabularyWord.UserProfileId names them, and a word
-- recorded to no one is not shown to anyone (Captain 2026-11-06). The owner backfill
-- (scripts/assign-vocabulary-word-owners.sql) records a word only when exactly one learner uses
-- it, so it left alone the words a real learner shares with a test account or with a deleted
-- account's progress. On the 2026-11-05 production backup that was 96 words, all in the
-- Captain's resources (52 shared with a test account, 41 with a deleted account's progress, 3
-- with both). Captain approved recording them to him (2026-11-06). Run this BEFORE the change
-- that hides ownerless words reaches production, or they vanish from his resources.
--
-- Rule. An ownerless word (UserProfileId NULL or '') is recorded to the target profile only when
--   * the target links it: one of the target's resources maps it, or the target has progress on
--     it; and
--   * every use of it is allowed. The uses are the owner backfill's (the delete rule's):
--       a resource's link (ResourceVocabularyMapping, through the resource's owner);
--       progress (VocabularyProgress.UserId);
--       an example sentence's owner (an ownerless sentence in a resource counts as the
--         resource owner's; an ownerless sentence outside an owned resource is not a use);
--       a minimal pair and a minimal-pair answer naming it (each word column);
--       a phrase it is a part of (PhraseConstituent, through the phrase word's owner).
--     A use is allowed when it names the target, or a test account, or (progress only) an ID
--     that is no profile (an account deleted since). A test account is a profile other than
--     the target all of whose known emails (UserProfile.Email, and AspNetUsers.Email of every
--     sign-in linked to it by UserProfileId) end in one of the listed domains (exact match,
--     case-insensitive), with at least one known email. A link from a resource with no owner,
--     a blank owner, a use by any other learner, or a non-progress use by an ID that is no
--     profile stops the word from being recorded; and
--   * at least one use names a test account or a non-profile ID (a word only the target uses
--     is the owner backfill's to record, and is reported but not recorded here).
-- Recording a phrase word can make its parts' phrase use name the target, so the rule repeats
-- until nothing more is recorded (at most 50 passes; reaching the cap aborts, nothing changes).
-- The recorded words' ownerless example sentences take the target as owner only when the
-- sentence has no resource or its resource is the target's. Nothing else is written: other
-- accounts' links, progress, pairs and sentences stay as they are, nothing is deleted, and no
-- recorded owner changes. Idempotent: a second run finds nothing to record.
--
-- Run it only with the Captain's go, after a fresh verified backup (docs/deploy-runbook.md,
-- "Ownerless shared words and withheld rows"). It prints counts only (profiles as a short ID and
-- a masked email), never row contents. Never put a real profile ID in this file.
--
-- Variables (psql -v):
--   target_profile       the UserProfile."Id" that receives the words (required; looked up at
--                        run time, never stored here)
--   test_domains         comma-separated email domains of test accounts, for example
--                        sentencestudio.test (required; pass '' for none)
--   expected_test        words shared with test accounts only (from the dry run)
--   expected_nonprofile  words shared with non-profile progress only
--   expected_both        words shared with both
--   expected_sentences   ownerless sentences of those words that take the target
--   apply                true commits (every expected value is then required); leave it out
--                        for a dry run that prints the plan and rolls back
--
-- Example (credentials in PG* environment variables, never on the command line):
--   psql -d sentencestudio -v ON_ERROR_STOP=1 -v target_profile="$TARGET" \
--     -v test_domains=sentencestudio.test -f scripts/record-shared-vocabulary-words.sql
--   psql -d sentencestudio -v ON_ERROR_STOP=1 -v target_profile="$TARGET" \
--     -v test_domains=sentencestudio.test -v expected_test=52 -v expected_nonprofile=41 \
--     -v expected_both=3 -v expected_sentences=0 -v apply=true \
--     -f scripts/record-shared-vocabulary-words.sql
--
-- Everything runs in one transaction; any failed check rolls back the whole script. The tables
-- it reads are locked against writes for its few seconds, so the plan cannot change under it.
-- Each UPDATE fires CoreSync's change-tracking trigger (__vocabularyword_ct_update__,
-- __examplesentence_ct_update__), which records the row in __core_sync_ct with no source store,
-- so the target's devices download the new owner on their next sync. The script checks that one
-- tracked change was recorded per updated row and aborts otherwise. UpdatedAt is left as it was.

\set ON_ERROR_STOP on
\if :{?apply}
\else
\set apply false
\endif
\if :{?target_profile}
\else
\set target_profile ''
\endif
\if :{?test_domains}
\else
\set test_domains '<unset>'
\endif
\if :{?expected_test}
\else
\set expected_test -1
\endif
\if :{?expected_nonprofile}
\else
\set expected_nonprofile -1
\endif
\if :{?expected_both}
\else
\set expected_both -1
\endif
\if :{?expected_sentences}
\else
\set expected_sentences -1
\endif

BEGIN;
SET LOCAL lock_timeout = '10s';

-- psql does not expand variables inside DO blocks, so pass them through a temp table.
CREATE TEMP TABLE shared_params ON COMMIT DROP AS
SELECT (:'apply')::boolean AS apply,
       NULLIF(btrim(:'target_profile'), '') AS target,
       :'test_domains'::text AS domains_raw,
       (:'expected_test')::int AS expected_test,
       (:'expected_nonprofile')::int AS expected_nonprofile,
       (:'expected_both')::int AS expected_both,
       (:'expected_sentences')::int AS expected_sentences;

CREATE TEMP TABLE shared_domains ON COMMIT DROP AS
SELECT DISTINCT lower(btrim(d)) AS domain
FROM shared_params p, unnest(string_to_array(p.domains_raw, ',')) AS d
WHERE p.domains_raw <> '<unset>' AND btrim(d) <> '';

DO $$
DECLARE
    p shared_params%ROWTYPE := (SELECT p FROM shared_params p);
    tracking int;
BEGIN
    IF p.target IS NULL THEN
        RAISE EXCEPTION 'target_profile is required (the UserProfile Id that receives the words). Nothing changed.';
    END IF;
    IF p.domains_raw = '<unset>' THEN
        RAISE EXCEPTION 'test_domains is required (comma-separated email domains of test accounts; pass '''' for none). Nothing changed.';
    END IF;
    IF EXISTS (SELECT 1 FROM shared_domains WHERE domain !~ '^[a-z0-9.-]+\.[a-z0-9-]+$') THEN
        RAISE EXCEPTION 'test_domains must be plain domains such as sentencestudio.test. Nothing changed.';
    END IF;
    IF p.apply AND (p.expected_test < 0 OR p.expected_nonprofile < 0 OR p.expected_both < 0
                    OR p.expected_sentences < 0) THEN
        RAISE EXCEPTION 'apply=true needs expected_test, expected_nonprofile, expected_both and expected_sentences from a dry run. Nothing changed.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM "UserProfile" WHERE "Id" = p.target) THEN
        RAISE EXCEPTION 'target_profile is not a profile. Nothing changed.';
    END IF;
    IF to_regclass('public."AspNetUsers"') IS NULL THEN
        RAISE EXCEPTION 'AspNetUsers is missing, so test accounts cannot be told apart by every email. Nothing changed.';
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
    "MinimalPairAttempt", "PhraseConstituent", "UserProfile", "AspNetUsers" IN SHARE MODE;

-- Every known email of each profile: its own, and each linked sign-in's.
CREATE TEMP TABLE shared_emails ON COMMIT DROP AS
SELECT p."Id" AS profile, lower(btrim(p."Email")) AS email
FROM "UserProfile" p WHERE coalesce(btrim(p."Email"), '') <> ''
UNION
SELECT u."UserProfileId", lower(btrim(u."Email"))
FROM "AspNetUsers" u
WHERE coalesce(btrim(u."Email"), '') <> ''
  AND EXISTS (SELECT 1 FROM "UserProfile" p WHERE p."Id" = u."UserProfileId");

DO $$
DECLARE
    p shared_params%ROWTYPE := (SELECT p FROM shared_params p);
BEGIN
    IF EXISTS (SELECT 1 FROM shared_emails e JOIN shared_domains d
               ON split_part(e.email, '@', 2) = d.domain WHERE e.profile = p.target) THEN
        RAISE EXCEPTION 'target_profile has an email in a test domain; it would be treated as a test account. Nothing changed.';
    END IF;
END $$;

-- Test accounts: profiles other than the target whose every known email is in a listed domain.
CREATE TEMP TABLE shared_test_profiles (id text PRIMARY KEY) ON COMMIT DROP;
INSERT INTO shared_test_profiles (id)
SELECT e.profile
FROM shared_emails e
WHERE e.profile <> (SELECT target FROM shared_params)
GROUP BY e.profile
HAVING bool_and(split_part(e.email, '@', 2) IN (SELECT domain FROM shared_domains));

CREATE TEMP TABLE shared_before ON COMMIT DROP AS
SELECT (SELECT count(*) FROM "VocabularyWord"
        WHERE "UserProfileId" IS NULL OR "UserProfileId" = '') AS ownerless_words,
       (SELECT coalesce(max(id), 0) FROM __core_sync_ct) AS tracked_before;

CREATE TEMP TABLE shared_words (id text PRIMARY KEY, category text NOT NULL, pass int NOT NULL)
    ON COMMIT DROP;
CREATE TEMP TABLE shared_uses (word_id text NOT NULL, learner text, kind text NOT NULL)
    ON COMMIT DROP;
CREATE TEMP TABLE shared_verdicts (id text PRIMARY KEY, verdict text NOT NULL) ON COMMIT DROP;

DO $$
DECLARE
    target text := (SELECT target FROM shared_params);
    pass int := 0;
    added int;
BEGIN
    LOOP
        pass := pass + 1;
        IF pass > 50 THEN
            RAISE EXCEPTION 'Phrase nesting still recorded words after 50 passes; stopped. Nothing changed.';
        END IF;
        TRUNCATE shared_uses;
        INSERT INTO shared_uses (word_id, learner, kind)
        SELECT m."VocabularyWordId", NULLIF(r."UserProfileId", ''), 'link'
        FROM "ResourceVocabularyMapping" m
        LEFT JOIN "LearningResource" r ON r."Id" = m."ResourceId"
        UNION ALL
        SELECT v."VocabularyWordId", NULLIF(v."UserId", ''), 'progress' FROM "VocabularyProgress" v
        UNION ALL
        SELECT s."VocabularyWordId",
               COALESCE(NULLIF(s."UserProfileId", ''), NULLIF(r."UserProfileId", '')), 'sentence'
        FROM "ExampleSentence" s
        LEFT JOIN "LearningResource" r ON r."Id" = s."LearningResourceId"
        WHERE COALESCE(NULLIF(s."UserProfileId", ''), NULLIF(r."UserProfileId", '')) IS NOT NULL
        UNION ALL
        SELECT mp."VocabularyWordAId", NULLIF(mp."UserId", ''), 'pair' FROM "MinimalPair" mp
        UNION ALL
        SELECT mp."VocabularyWordBId", NULLIF(mp."UserId", ''), 'pair' FROM "MinimalPair" mp
        UNION ALL
        SELECT a."PromptWordId", NULLIF(a."UserId", ''), 'answer' FROM "MinimalPairAttempt" a
        UNION ALL
        SELECT a."SelectedWordId", NULLIF(a."UserId", ''), 'answer' FROM "MinimalPairAttempt" a
        UNION ALL
        SELECT pc."ConstituentWordId",
               COALESCE(NULLIF(w."UserProfileId", ''), CASE WHEN b.id IS NOT NULL THEN target END),
               'phrase'
        FROM "PhraseConstituent" pc
        LEFT JOIN "VocabularyWord" w ON w."Id" = pc."PhraseWordId"
        LEFT JOIN shared_words b ON b.id = pc."PhraseWordId"
        WHERE pc."ConstituentWordId" IS NOT NULL AND pc."ConstituentWordId" <> '';

        TRUNCATE shared_verdicts;
        INSERT INTO shared_verdicts (id, verdict)
        SELECT w."Id",
               CASE
                   WHEN count(u.word_id) = 0 THEN 'used by nobody'
                   WHEN count(*) FILTER (WHERE u.learner IS NULL) > 0
                       THEN 'a use names no learner'
                   WHEN count(*) FILTER (WHERE u.learner <> target
                           AND u.learner NOT IN (SELECT id FROM shared_test_profiles)
                           AND NOT (u.kind = 'progress'
                                    AND NOT EXISTS (SELECT 1 FROM "UserProfile" pr
                                                    WHERE pr."Id" = u.learner))) > 0
                       THEN 'used by another learner (or, outside progress, by an ID that is no profile)'
                   WHEN count(*) FILTER (WHERE u.learner = target
                           AND u.kind IN ('link', 'progress')) = 0
                       THEN 'the target does not link it'
                   WHEN count(*) FILTER (WHERE u.learner IN (SELECT id FROM shared_test_profiles)) > 0
                    AND count(*) FILTER (WHERE u.kind = 'progress' AND u.learner <> target
                           AND u.learner NOT IN (SELECT id FROM shared_test_profiles)) > 0
                       THEN 'both'
                   WHEN count(*) FILTER (WHERE u.learner IN (SELECT id FROM shared_test_profiles)) > 0
                       THEN 'test'
                   WHEN count(*) FILTER (WHERE u.kind = 'progress' AND u.learner <> target) > 0
                       THEN 'nonprofile'
                   ELSE 'the target alone uses it (assign-vocabulary-word-owners.sql records it)'
               END
        FROM "VocabularyWord" w
        LEFT JOIN shared_uses u ON u.word_id = w."Id"
        WHERE (w."UserProfileId" IS NULL OR w."UserProfileId" = '')
          AND NOT EXISTS (SELECT 1 FROM shared_words b WHERE b.id = w."Id")
        GROUP BY w."Id";

        INSERT INTO shared_words (id, category, pass)
        SELECT id, verdict, pass FROM shared_verdicts
        WHERE verdict IN ('test', 'nonprofile', 'both');
        GET DIAGNOSTICS added = ROW_COUNT;
        EXIT WHEN added = 0;
    END LOOP;
END $$;

-- The recorded words' ownerless sentences outside any resource, or in one of the target's.
CREATE TEMP TABLE shared_sentences (id text PRIMARY KEY) ON COMMIT DROP;
INSERT INTO shared_sentences (id)
SELECT s."Id"
FROM "ExampleSentence" s
JOIN shared_words b ON b.id = s."VocabularyWordId"
LEFT JOIN "LearningResource" r ON r."Id" = s."LearningResourceId"
WHERE (s."UserProfileId" IS NULL OR s."UserProfileId" = '')
  AND (NULLIF(s."LearningResourceId", '') IS NULL
       OR r."UserProfileId" = (SELECT target FROM shared_params));

\echo
\echo 'Target and test accounts:'
SELECT CASE WHEN p."Id" = (SELECT target FROM shared_params) THEN 'target' ELSE 'test account' END
           AS "role",
       left(p."Id", 8) AS profile,
       CASE WHEN coalesce(p."Email", '') = '' THEN '(no email)'
            ELSE left(p."Email", 1) || '***@' || split_part(p."Email", '@', 2) END AS email
FROM "UserProfile" p
WHERE p."Id" = (SELECT target FROM shared_params)
   OR p."Id" IN (SELECT id FROM shared_test_profiles)
ORDER BY 1, 2;

\echo 'Ownerless words, by what this step does:'
SELECT CASE v.verdict
           WHEN 'test' THEN 'record: target + test accounts'
           WHEN 'nonprofile' THEN 'record: target + non-profile progress'
           WHEN 'both' THEN 'record: target + test accounts + non-profile progress'
           ELSE 'leave: ' || v.verdict
       END AS "word", count(*) AS words
FROM (SELECT id, category AS verdict FROM shared_words
      UNION ALL SELECT id, verdict FROM shared_verdicts) v
GROUP BY 1 ORDER BY 1;

\echo 'Totals:'
SELECT (SELECT ownerless_words FROM shared_before) AS "ownerless words",
       (SELECT count(*) FROM shared_words WHERE category = 'test') AS "test",
       (SELECT count(*) FROM shared_words WHERE category = 'nonprofile') AS "nonprofile",
       (SELECT count(*) FROM shared_words WHERE category = 'both') AS "both",
       (SELECT count(*) FROM shared_words) AS "words to record",
       (SELECT count(*) FROM shared_sentences) AS "sentences to record",
       (SELECT coalesce(max(pass), 0) FROM shared_words) AS "passes";

DO $$
DECLARE
    p shared_params%ROWTYPE := (SELECT p FROM shared_params p);
    test int := (SELECT count(*) FROM shared_words WHERE category = 'test');
    nonprofile int := (SELECT count(*) FROM shared_words WHERE category = 'nonprofile');
    both_ int := (SELECT count(*) FROM shared_words WHERE category = 'both');
    sentences int := (SELECT count(*) FROM shared_sentences);
BEGIN
    IF p.expected_test >= 0 AND test <> p.expected_test THEN
        RAISE EXCEPTION 'Would record % words shared with test accounts, expected %. Nothing changed.', test, p.expected_test;
    END IF;
    IF p.expected_nonprofile >= 0 AND nonprofile <> p.expected_nonprofile THEN
        RAISE EXCEPTION 'Would record % words shared with non-profile progress, expected %. Nothing changed.', nonprofile, p.expected_nonprofile;
    END IF;
    IF p.expected_both >= 0 AND both_ <> p.expected_both THEN
        RAISE EXCEPTION 'Would record % words shared with both, expected %. Nothing changed.', both_, p.expected_both;
    END IF;
    IF p.expected_sentences >= 0 AND sentences <> p.expected_sentences THEN
        RAISE EXCEPTION 'Would record % sentences, expected %. Nothing changed.', sentences, p.expected_sentences;
    END IF;
END $$;

CREATE TEMP TABLE shared_updated (tbl text NOT NULL, id text NOT NULL) ON COMMIT DROP;

WITH updated AS (
    UPDATE "VocabularyWord" w
    SET "UserProfileId" = (SELECT target FROM shared_params)
    FROM shared_words b
    WHERE w."Id" = b.id AND (w."UserProfileId" IS NULL OR w."UserProfileId" = '')
    RETURNING w."Id"
)
INSERT INTO shared_updated (tbl, id) SELECT 'VocabularyWord', "Id" FROM updated;

WITH updated AS (
    UPDATE "ExampleSentence" s
    SET "UserProfileId" = (SELECT target FROM shared_params)
    FROM shared_sentences b
    WHERE s."Id" = b.id AND (s."UserProfileId" IS NULL OR s."UserProfileId" = '')
    RETURNING s."Id"
)
INSERT INTO shared_updated (tbl, id) SELECT 'ExampleSentence', "Id" FROM updated;

DO $$
DECLARE
    target text := (SELECT target FROM shared_params);
    words int := (SELECT count(*) FROM shared_words);
    sentences int := (SELECT count(*) FROM shared_sentences);
    updated_words int := (SELECT count(*) FROM shared_updated WHERE tbl = 'VocabularyWord');
    updated_sentences int := (SELECT count(*) FROM shared_updated WHERE tbl = 'ExampleSentence');
    owned_words int := (SELECT count(*) FROM "VocabularyWord" w
                        JOIN shared_words b ON b.id = w."Id" AND w."UserProfileId" = target);
    owned_sentences int := (SELECT count(*) FROM "ExampleSentence" s
                            JOIN shared_sentences b ON b.id = s."Id" AND s."UserProfileId" = target);
    remaining_words int := (SELECT count(*) FROM "VocabularyWord"
                            WHERE "UserProfileId" IS NULL OR "UserProfileId" = '');
    tracked_words int := (SELECT count(DISTINCT pk_text) FROM __core_sync_ct
                          WHERE id > (SELECT tracked_before FROM shared_before)
                            AND tbl = 'VocabularyWord' AND op = 'U' AND src IS NULL
                            AND pk_text IN (SELECT id FROM shared_words));
    tracked_sentences int := (SELECT count(DISTINCT pk_text) FROM __core_sync_ct
                              WHERE id > (SELECT tracked_before FROM shared_before)
                                AND tbl = 'ExampleSentence' AND op = 'U' AND src IS NULL
                                AND pk_text IN (SELECT id FROM shared_sentences));
    before_words int := (SELECT ownerless_words FROM shared_before);
BEGIN
    IF updated_words <> words OR owned_words <> words
       OR remaining_words <> before_words - words THEN
        RAISE EXCEPTION 'Recorded % words (owned now %), planned %, % still ownerless of % before. Nothing changed.',
            updated_words, owned_words, words, remaining_words, before_words;
    END IF;
    IF updated_sentences <> sentences OR owned_sentences <> sentences THEN
        RAISE EXCEPTION 'Recorded % sentences (owned now %), planned %. Nothing changed.',
            updated_sentences, owned_sentences, sentences;
    END IF;
    IF tracked_words <> words OR tracked_sentences <> sentences THEN
        RAISE EXCEPTION 'CoreSync tracked % of % words and % of % sentences, so some devices would miss the owner. Nothing changed.',
            tracked_words, words, tracked_sentences, sentences;
    END IF;
    RAISE NOTICE 'Recorded % words and % sentences to the target, each tracked for sync. Ownerless words now: %.',
        words, sentences, remaining_words;
END $$;

\if :apply
COMMIT;
\echo 'Committed.'
\else
ROLLBACK;
\echo 'Dry run: rolled back. Nothing changed.'
\endif
