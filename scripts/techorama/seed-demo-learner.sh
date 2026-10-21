#!/usr/bin/env bash
#
# seed-demo-learner.sh
#
# Resets the Techorama demo learner's practice history so the Sam (Learning Coach) demo has
# something real to answer from.
#
# Everything is written relative to the moment the script runs. Run it on the morning of the
# talk, and before each rehearsal: words come due "right now" and "this week", and the last two
# weeks of practice end yesterday.
#
# What it changes, for the demo learner only:
#   * Rewrites VocabularyProgress for the words in the three demo resources:
#     12 due now (6 of them struggling), 12 due later this week, 6 well known, 12 never practised.
#   * Rewrites UserActivity (two weeks of practice, with three rest days) and
#     DailyPlanCompletion (ten days of plans, mostly completed).
#   * Removes what a rehearsal adds: the word 도서관 (the "add a word" beat) and the
#     "Morning routine" resource (the import demo).
#
# It never touches another learner, and it refuses to run unless the database container mounts a
# Techorama demo volume (a name containing "techorama-demo").
#
# Coach state is reset in the app, not here: Settings > "Delete coach history" and
# "Forget everything". Sam allows 10 runs per learner per day; restarting the API resets the count.
#
# Usage:
#   scripts/techorama/seed-demo-learner.sh [--email <address>] [--volume <volume>]
#
# Defaults: --email techorama-demo@example.test --volume sentencestudio-techorama-demo-db-data
#
# The stack must be running with that volume. For the talk, from src/SentenceStudio.AppHost:
#   LocalDb__DataVolume=sentencestudio-techorama-demo-db-data \
#   Coach__Memory__Enabled=true Coach__SamWriteTools__Enabled=true \
#   Coach__AllowedUserProfileIds__3=__dev_all__ Coach__ReasoningEffort=low aspire run
# At the default reasoning effort (minimal), Sam often refuses "What's due for review?" on this
# learner: an evidence-window validation rule rejects the answer.

set -euo pipefail

EMAIL="techorama-demo@example.test"
VOLUME="sentencestudio-techorama-demo-db-data"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --email)  EMAIL="$2"; shift 2 ;;
    --volume) VOLUME="$2"; shift 2 ;;
    -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

case "$VOLUME" in
  *techorama-demo*) ;;
  *) echo "Refusing: '$VOLUME' is not a Techorama demo volume." >&2; exit 2 ;;
esac

CONTAINER="$(docker ps --filter "volume=$VOLUME" --format '{{.Names}}' | head -1)"
if [[ -z "$CONTAINER" ]]; then
  echo "No running container mounts '$VOLUME'." >&2
  echo "Start the stack with: LocalDb__DataVolume=$VOLUME aspire run" >&2
  exit 1
fi

echo "Seeding demo learner <$EMAIL> in container '$CONTAINER' (volume '$VOLUME')."

docker exec -i "$CONTAINER" sh -c \
  'PGPASSWORD="$POSTGRES_PASSWORD" exec psql -X -q -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d sentencestudio -v email="$1"' \
  sh "$EMAIL" <<'SQL'
begin;

create temp table demo_learner on commit drop as
  select u."UserProfileId" as pid
  from "AspNetUsers" u
  where u."NormalizedEmail" = upper(:'email') and u."UserProfileId" is not null;

do $$
begin
  if (select count(*) from demo_learner) <> 1 then
    raise exception 'Demo learner not found. Register the account in the app first.';
  end if;
end $$;

-- What a rehearsal adds -------------------------------------------------------------------------

-- The "add a word" beat.
delete from "ResourceVocabularyMapping" m
using "LearningResource" r, "VocabularyWord" w, demo_learner d
where m."ResourceId" = r."Id" and m."VocabularyWordId" = w."Id"
  and r."UserProfileId" = d.pid and w."TargetLanguageTerm" = '도서관';

-- The import demo. Mappings and example sentences first, then the resource itself.
create temp table rehearsal_resources on commit drop as
  select r."Id" as rid
  from "LearningResource" r join demo_learner d on r."UserProfileId" = d.pid
  where r."Title" = 'Morning routine';

delete from "ExampleSentence" e using rehearsal_resources x where e."LearningResourceId" = x.rid;
delete from "ResourceVocabularyMapping" m using rehearsal_resources x where m."ResourceId" = x.rid;
delete from "LearningResource" r using rehearsal_resources x where r."Id" = x.rid;

-- History -----------------------------------------------------------------------------------------

delete from "VocabularyLearningContext" c
using "VocabularyProgress" p, demo_learner d
where c."VocabularyProgressId" = p."Id" and p."UserId" = d.pid;
delete from "VocabularyProgress" p using demo_learner d where p."UserId" = d.pid;
delete from "UserActivity" a using demo_learner d where a."UserProfileId" = d.pid;
delete from "DailyPlanCompletion" c using demo_learner d where c."UserProfileId" = d.pid;

-- Vocabulary progress. Bands by position (n % 7): 1 due, 4 struggling and overdue, 2 and 5 due
-- later this week, 3 well known, 0 and 6 never practised.
with words as (
  select distinct on (w."Id") w."Id" as wid, d.pid,
         dense_rank() over (order by r."Title", w."TargetLanguageTerm") as n
  from "VocabularyWord" w
  join "ResourceVocabularyMapping" m on m."VocabularyWordId" = w."Id"
  join "LearningResource" r on r."Id" = m."ResourceId"
  join demo_learner d on r."UserProfileId" = d.pid
  where r."Title" in ('Rainy day', 'Visiting grandmother', 'At the office')
),
banded as (
  select wid, pid, n,
         case n % 7 when 1 then 'due' when 4 then 'struggling' when 2 then 'week' when 5 then 'week'
                    when 3 then 'known' else 'new' end as band
  from words
),
shaped as (
  select wid, pid, n, band,
    case band when 'due' then 0.45 when 'struggling' then 0.25 when 'week' then 0.62 else 0.92 end as mastery,
    case band when 'due' then 6 when 'struggling' then 8 when 'week' then 6 else 10 end as total,
    case band when 'due' then 4 when 'struggling' then 3 when 'week' then 5 else 9 end as correct,
    case band when 'struggling' then 0 when 'known' then 2 else 1 end as phase,
    case band
      when 'due'        then now() - make_interval(days => (n % 3)::int, hours => 2)
      when 'struggling' then now() - make_interval(days => 1 + (n % 3)::int)
      when 'week'       then now() + make_interval(days => 1 + (n % 6)::int)
      else                   now() + make_interval(days => 10 + (n % 10)::int)
    end as next_review,
    case band when 'due' then 2 when 'struggling' then 1 when 'week' then 4 else 14 end as interval_days,
    case band when 'struggling' then 1.9 when 'known' then 2.7 else 2.4 end as ease
  from banded
  where band <> 'new'
)
insert into "VocabularyProgress" (
  "Id", "VocabularyWordId", "UserId", "MasteryScore", "TotalAttempts", "CorrectAttempts",
  "CurrentStreak", "ProductionInStreak", "RecognitionAttempts", "RecognitionCorrect",
  "ProductionAttempts", "ProductionCorrect", "ApplicationAttempts", "ApplicationCorrect",
  "CurrentPhase", "NextReviewDate", "ReviewInterval", "EaseFactor", "MultipleChoiceCorrect",
  "TextEntryCorrect", "IsPromoted", "IsCompleted", "IsUserDeclared", "VerificationState",
  "FirstSeenAt", "LastPracticedAt", "MasteredAt", "CreatedAt", "UpdatedAt", "LastExposedAt",
  "ExposureCount", "QuizProductionDemonstrations", "QuizRecognitionDemonstrations")
select
  gen_random_uuid()::text, wid, pid, mastery, total, correct,
  case when band = 'struggling' then 0 else 2 end, 0,
  total - total / 3, correct - correct / 3, total / 3, correct / 3, 0, 0,
  phase, next_review, interval_days, ease, correct - correct / 3, correct / 3,
  false, false, false, 0,
  now() - make_interval(days => 18 + (n % 5)::int),
  least(now() - interval '1 hour', next_review - make_interval(days => interval_days)),
  case when band = 'known' then now() - interval '5 days' end,
  now() - make_interval(days => 18 + (n % 5)::int), now(),
  least(now() - interval '1 hour', next_review - make_interval(days => interval_days)),
  total, correct / 3, correct - correct / 3
from shaped;

-- Two weeks of practice, ending yesterday. Days 4, 9, and 12 are rest days.
insert into "UserActivity" ("Id", "Activity", "Input", "Fluency", "Accuracy", "UserProfileId", "CreatedAt", "UpdatedAt")
select gen_random_uuid()::text,
       (array['VocabularyQuiz', 'VocabularyQuiz', 'VocabularyMatching', 'Reading', 'Shadowing', 'Clozure'])[1 + (day + k) % 6],
       null,
       70 + (day * 7 + k * 13) % 25,
       68 + (day * 11 + k * 5) % 28,
       d.pid,
       date_trunc('day', now()) - make_interval(days => day) + make_interval(hours => 19, mins => k * 20),
       date_trunc('day', now()) - make_interval(days => day) + make_interval(hours => 19, mins => k * 20)
from demo_learner d,
     generate_series(1, 14) as day,
     generate_series(0, 2) as k
where day not in (4, 9, 12) and (k < 2 or day % 2 = 0);

insert into "UserActivity" ("Id", "Activity", "Input", "Fluency", "Accuracy", "UserProfileId", "CreatedAt", "UpdatedAt")
select gen_random_uuid()::text, 'Conversation', null, 72, 75, d.pid,
       date_trunc('day', now()) - make_interval(days => day) + interval '20 hours',
       date_trunc('day', now()) - make_interval(days => day) + interval '20 hours'
from demo_learner d, generate_series(1, 14) as day
where day in (3, 8);

-- Ten days of plans: vocabulary review every day, then reading, then a speaking or listening item.
with demo_resources as (
  select r."Id" as rid, row_number() over (order by r."Title") as i
  from "LearningResource" r join demo_learner d on r."UserProfileId" = d.pid
  where r."Title" in ('Rainy day', 'Visiting grandmother', 'At the office')
),
items as (
  select day, slot,
         case slot when 1 then 'VocabularyReview' when 2 then 'Reading'
                   else (array['Conversation', 'Shadowing', 'Listening'])[1 + day % 3] end as activity
  from generate_series(1, 10) as day, generate_series(1, 3) as slot
  where day not in (4, 9)
)
insert into "DailyPlanCompletion" (
  "Id", "Date", "PlanItemId", "ActivityType", "ResourceId", "SkillId", "IsCompleted", "CompletedAt",
  "MinutesSpent", "EstimatedMinutes", "Priority", "TitleKey", "DescriptionKey", "Rationale",
  "CreatedAt", "UpdatedAt", "UserProfileId", "NarrativeJson", "IsArchived")
select gen_random_uuid()::text,
       (current_date - day),
       'seed-' || to_char(current_date - day, 'YYYY-MM-DD') || '-' || activity,
       activity,
       case when activity = 'VocabularyReview' then null else (select rid from demo_resources where i = 1 + day % 3) end,
       null,
       (slot = 1 or (slot = 2 and day % 5 <> 0) or (slot = 3 and day % 2 = 0)),
       case when (slot = 1 or (slot = 2 and day % 5 <> 0) or (slot = 3 and day % 2 = 0))
            then date_trunc('day', now()) - make_interval(days => day) + make_interval(hours => 19, mins => slot * 15) end,
       case when (slot = 1 or (slot = 2 and day % 5 <> 0) or (slot = 3 and day % 2 = 0))
            then (case slot when 1 then 12 when 2 then 9 else 8 end) + day % 3 else 0 end,
       case slot when 1 then 10 else 8 end,
       slot,
       case activity when 'VocabularyReview' then 'plan_item_vocab_review_title'
                     when 'Reading' then 'plan_item_reading_title'
                     when 'Conversation' then 'plan_item_conversation_title'
                     when 'Shadowing' then 'plan_item_shadowing_title'
                     else 'plan_item_listening_title' end,
       case activity when 'VocabularyReview' then 'plan_item_vocab_review_desc'
                     when 'Reading' then 'plan_item_reading_desc'
                     when 'Conversation' then 'plan_item_conversation_desc'
                     when 'Shadowing' then 'plan_item_shadowing_desc'
                     else 'plan_item_listening_desc' end,
       '',
       date_trunc('day', now()) - make_interval(days => day) + interval '7 hours',
       date_trunc('day', now()) - make_interval(days => day) + interval '20 hours',
       d.pid, null, false
from items, demo_learner d;

-- Summary -----------------------------------------------------------------------------------------
select
  (select count(*) from "VocabularyProgress" p join demo_learner d on p."UserId" = d.pid) as tracked_words,
  (select count(*) from "VocabularyProgress" p join demo_learner d on p."UserId" = d.pid
     where p."NextReviewDate" <= now()) as due_now,
  (select count(*) from "VocabularyProgress" p join demo_learner d on p."UserId" = d.pid
     where p."NextReviewDate" > now() and p."NextReviewDate" <= now() + interval '7 days') as due_later_this_week,
  (select count(*) from "UserActivity" a join demo_learner d on a."UserProfileId" = d.pid) as activities,
  (select count(*) from "DailyPlanCompletion" c join demo_learner d on c."UserProfileId" = d.pid) as plan_items;

commit;
SQL

echo "Done."
