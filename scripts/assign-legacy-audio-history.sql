-- One-time recovery: give the ownerless audio history rows to one named account.
--
-- Why: StreamHistory rows saved before 20260927100455_AddStreamHistoryOwnerScope have no
-- owner, so the owner checks hide them everywhere (docs/stream-history-owner-scoping.md).
-- Nothing in a row proves who recorded it. On 2026-11-05 the Captain named the owner of the
-- production rows himself ("assign the audio to my account"). This script applies exactly
-- that decision and nothing else. It does not touch SceneImage or any other table.
--
-- Run it only after the API has applied the owner migration, and only with the Captain's go.
-- It prints counts only, never row contents or IDs.
--
-- Variables (psql -v):
--   owner_email  the sign-in email whose linked profile receives the rows
--   expected     the number of ownerless rows counted just before; any other count aborts
--   apply        true commits; leave it out (or anything else) for a dry run that rolls back
--
-- Example (credentials in PG* environment variables, never on the command line):
--   psql -d sentencestudio -v ON_ERROR_STOP=1 -v owner_email="$OWNER_EMAIL" \
--     -v expected=217 -v apply=true -f scripts/assign-legacy-audio-history.sql
--
-- Every check runs inside one transaction. Any failed check rolls back the whole script.
-- The UPDATE fires the CoreSync change-tracking trigger, so the rows then sync to the
-- owner's devices like any other owned audio history entry. UpdatedAt is left as it was.

\set ON_ERROR_STOP on
\if :{?apply}
\else
\set apply false
\endif

BEGIN;

-- psql does not expand variables inside DO blocks, so pass them through a temp table.
CREATE TEMP TABLE assign_params ON COMMIT DROP AS
SELECT upper(:'owner_email') AS normalized_email, (:'expected')::int AS expected_rows;

CREATE TEMP TABLE assign_owner ON COMMIT DROP AS
SELECT DISTINCT u."UserProfileId" AS profile_id
FROM "AspNetUsers" u
JOIN "UserProfile" p ON p."Id" = u."UserProfileId"
WHERE u."NormalizedEmail" = (SELECT normalized_email FROM assign_params);

CREATE TEMP TABLE assign_updated (id text) ON COMMIT DROP;

DO $$
DECLARE
    owners int := (SELECT count(*) FROM assign_owner);
    expected int := (SELECT expected_rows FROM assign_params);
    ownerless int := (SELECT count(*) FROM "StreamHistory"
                      WHERE "UserProfileId" IS NULL OR "UserProfileId" = '');
BEGIN
    IF owners <> 1 THEN
        RAISE EXCEPTION 'The sign-in must link to exactly one profile; found %. Nothing changed.', owners;
    END IF;
    IF ownerless <> expected THEN
        RAISE EXCEPTION 'Found % ownerless audio history rows, expected %. Nothing changed.', ownerless, expected;
    END IF;
    RAISE NOTICE 'Profiles linked to the sign-in: 1. Ownerless audio history rows: %.', ownerless;
END $$;

WITH updated AS (
    UPDATE "StreamHistory"
    SET "UserProfileId" = (SELECT profile_id FROM assign_owner)
    WHERE "UserProfileId" IS NULL OR "UserProfileId" = ''
    RETURNING "Id"
)
INSERT INTO assign_updated (id)
SELECT "Id" FROM updated;

DO $$
DECLARE
    expected int := (SELECT expected_rows FROM assign_params);
    updated int := (SELECT count(*) FROM assign_updated);
    remaining int := (SELECT count(*) FROM "StreamHistory"
                      WHERE "UserProfileId" IS NULL OR "UserProfileId" = '');
    now_owned int := (SELECT count(*) FROM "StreamHistory" h
                      JOIN assign_updated a ON a.id = h."Id"
                      WHERE h."UserProfileId" = (SELECT profile_id FROM assign_owner));
BEGIN
    IF updated <> expected OR now_owned <> expected OR remaining <> 0 THEN
        RAISE EXCEPTION 'Assigned % (owned now %), expected %, % still ownerless. Nothing changed.',
            updated, now_owned, expected, remaining;
    END IF;
    RAISE NOTICE 'Assigned % audio history rows. Ownerless rows left: 0.', updated;
END $$;

\if :apply
COMMIT;
\echo 'Committed.'
\else
ROLLBACK;
\echo 'Dry run: rolled back. Nothing changed.'
\endif
