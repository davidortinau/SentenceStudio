# Incident RCA — Sam Missing and Recovery (2026-08-22)

**Author:** Zoe (Lead)
**Date:** 2026-08-22
**Severity:** S2 — feature completely unavailable in local dev, no data loss
**Status:** Environment repaired; product-side answer-shape defect repaired and tested; production deployment not authorized

Written in ASD-STE100 Simplified Technical English. No emoji. No secrets, full user IDs, private prompt internals, or personal email addresses.

---

## 1. Executive summary

Captain reported that Sam did not appear in the shared browser Canvas at
`https://localhost:7071`. Investigation found a chain of five faults across the
Aspire orchestrator, the AppHost configuration, the recovery gate, the Copilot
session context, and the Coach product path. Each fault masked the next.

The environment was repaired in three stages: user-secret correction and safe
AppHost restart; a product-side answer-shape repair (R1 through R6) shipped as a
distinct failure code with observability and localized copy; a durable
environment hardening pass added an AppHost dedup guard and a post-restore
health script.

After the repairs, the literal S7 prompt
`Explain when I use -는데 versus -지만.` completed live in the shared Canvas. The
API logged `StopReason=Completed` with `Intent=PedagogicalAnswer`. The Sam
answer is still on screen with the canonical test account.

No secrets, DB rows, migrations, or DataRecoveryService paths were touched.

Severity S2 is retained because the feature was unusable in local dev for
several hours, but data was not lost and no production surface was affected.

---

## 2. Timeline (Central time; UTC where system-recorded)

| Time (CT)      | Event                                                                                                                            |
|----------------|----------------------------------------------------------------------------------------------------------------------------------|
| Earlier (window not fully retained) | `aspire run --isolated` was issued from the running AppHost project path. DCP took over the live stack and reused resource identity `461cee1b`. |
| ~10:25 AM      | AppHost user-secrets file was last modified with a duplicate entry at `Coach:AllowedUserProfileIds:2` (copy of `[0]`).            |
| 11:04:52 UTC 16:04:52 | AppHost PID `45028` started. API resource `api-nkqdecau` began boot.                                                     |
| 11:04:57 UTC 16:04:57 | API `Host.StartAsync` failed. `OptionsValidationException: Coach:AllowedUserProfileIds[2] is a duplicate entry.` Resource state → `Finished`. |
| From 11:04     | DCP kept port `7012` bound with no backend behind it. Requests timed out instead of connection-refused.                          |
| From 11:04     | WebApp `webapp-haxrrjgv` started. `CoachFeatureFlags.EnsureLoadedAsync` timed out on the availability endpoint, cached `Disabled`, and hid the Sam FAB. |
| ~14:50 CT      | Captain reported Sam missing.                                                                                                    |
| 14:51 CT       | Zoe published the Investigation Contract with a strict read-only evidence pass.                                                  |
| ~14:55 CT      | Wash confirmed direct cause: duplicate allowlist entry at index 2, DB intact, WebApp fail-closed as designed.                    |
| 14:50 CT       | Zoe reviewed and approved Option A only: remove index 2; keep the intended pilot cohort `{Jayne, Captain}`.                       |
| 15:25 CT       | Wash executed the runbook: backup, remove index 2, graceful restart, five infrastructure gates passed. API `/health` = Healthy.  |
| 15:24 CT       | Jayne executed acceptance gates. Gates 1–4 PASS. Gates 5–10 were blocked because the sub-agent had no Canvas action tool.        |
| ~15:25 CT      | Captain signed in on the shared Canvas and reproduced the literal S7 prompt. Sam did not respond. The learner turn appeared, but no coach message and no error.        |
| ~15:40 CT      | River proposed a numeric limit raise plus a `Notice` message. Zoe rejected the proposal (see §4.F).                              |
| ~16:00 CT      | Wash, Kaylee, and Jayne shipped the substituted repair R1–R6: prompt schema mentions the total budget, distinct limitation code `AnswerShapeInvalid = 7`, observability names the rule that fired, localized card copy for `en` and `ko`, sixteen regression tests. Simon reviewed and approved. |
| 16:40 CT       | Wash restarted the AppHost against the repaired binaries. Five infrastructure gates passed.                                      |
| 16:44 CT       | Wash landed durable hardening: `CoachConfigurationReader` deduplicates with an ordinal comparison and reports source index only; new `scripts/post-aspire-restore.sh` with hermetic shell tests. |
| ~16:50 CT      | Captain reissued the literal S7 prompt on the repaired stack. Sam completed the answer. API logs: `StopReason=Completed`, `Intent=PedagogicalAnswer`. |
| Now            | Shared Canvas remains open on the successful Sam answer with the canonical test account.                                          |

---

## 3. Impact and data-safety statement

- Local dev Sam feature: unavailable for the incident window. Repaired.
- All non-Coach features on the WebApp: functional.
- Workers: functional throughout the incident.
- Postgres database: no writes performed during investigation or repair. The
  named volume `sentencestudio-local-crispy-barnacle-db-data` stayed mounted on
  `db-461cee1b` from start to finish. The `EmptyUsersStartupCheck` reported
  15 users before and after the repair.
- Azurite and Redis: no writes performed. Redis was up throughout.
- DataRecoveryService: not invoked at any point. The `enable_automatic_data_recovery`
  preference was not touched.
- Production: not affected. Not deployed.

No user data was lost, retagged, or exposed at any point.

---

## 4. Fault chains

The incident is not one fault. It is five faults in series. Each fault, on its
own, would have been recoverable. Together they made the failure invisible for
hours.

### 4.A `aspire run --isolated` from the live AppHost path

DCP identity is a hash of the AppHost project path. `--isolated` provides port
isolation, but not container-identity isolation and not volume isolation. When
`aspire run --isolated` was issued from the same worktree path as the running
AppHost, the second invocation reused the identity hash `461cee1b`. DCP treated
the live stack as its own, stopped the containers Captain's AppHost owned, and
started replacements. The DCP process kept the forwarding port bound.

This is the earning event for the entire incident. Every downstream fault
follows from it.

### 4.B Duplicate Coach allowlist entry crashed the API

After the takeover, when a fresh AppHost lineage started at 11:04:52, the
merged config included AppHost user-secrets. The user-secrets file, last
modified at ~10:25 CT, held a duplicate entry:

- `Coach:AllowedUserProfileIds:0` → GUID prefix `7384f806-` (canonical test account)
- `Coach:AllowedUserProfileIds:1` → GUID prefix `ba20bcc5-` (Captain)
- `Coach:AllowedUserProfileIds:2` → same GUID as `[0]`

`CoachOptionsValidator.ValidateOnStart` uses
`HashSet<string>(StringComparer.Ordinal)`. Adding index 2 returned false, and
the validator threw `OptionsValidationException` with the exact message
`Coach:AllowedUserProfileIds[2] is a duplicate entry.` Because
`ValidateOnStart` is fatal to `Host.StartAsync`, Kestrel never bound and the
API resource went to `Finished` 5 seconds after start.

The DCP forwarding port `7012` stayed bound. Requests to it timed out. The
WebApp's `CoachFeatureFlags.EnsureLoadedAsync` caught the timeout, wrote
`Disabled` into the per-circuit cache, and `CoachSurfaceGate.Decide` chose
`LegacyWorkspaceHost` — which does not render the Sam FAB. The feature
disappeared silently on the client.

**We cannot recover the exact command that first wrote the duplicate.** The
tool history that would name it was not retained. What we can prove:

- The secrets file mtime is 2026-08-22 10:25:46 CT.
- Two of the three entries at that time were the same 36-character GUID.
- The process failure is deterministic given those inputs.

We do not invent the missing command.

### 4.C The recovery gate proved the wrong thing

The prior verification pass checked two things:

1. `https://localhost:7071` returns HTTP 302.
2. The named volume `sentencestudio-local-crispy-barnacle-db-data` is present.

Both were true throughout the incident. The WebApp process was healthy from
the WebApp's own point of view; the volume was mounted the whole time. Neither
check proves that the API process is alive, that the API resource state is
`Running`, or that Sam is visible.

The 302 was a healthy-WebApp signal, not a Sam-visibility signal.

### 4.D Active Copilot session context was not hydrated

The persistent squad state held 61 turns and 22 checkpoints for this session,
plus an active RFC. The active model context did not include the stored
history at the time the coordinator answered the soak and E2E questions. The
coordinator answered from the current turn buffer alone.

The consequence: decisions that depended on the earlier turns were made
without them, and the same ground had to be re-covered by a sub-agent.

This is a client-side rehydrate defect, not a squad governance defect. The
history was present and retrievable through `squad_state_*`. The active model
context omitted it.

### 4.E Canvas provider reconnect orphaned the automation instance

During the incident the browser Canvas provider reconnected. The automation
instance that was addressable at the start of the investigation became
unreachable. A new Canvas came up with a new cookie jar and no reference to
the original automated page.

This is distinct from product auth. Product auth (ASP.NET Identity, JWT,
CoachSurfaceGate cohort scoping) was healthy for the whole window. The failure
was in the Canvas provider's ability to keep an automation binding alive
across the reconnect.

The consequence: the sub-agent tester could not act in the shared Canvas
without opening a fresh Playwright context, which the Investigation Contract
explicitly forbids. Captain drove the remaining browser gates.

### 4.F Product defect exposed after environment repair: shape rejection produced no learner-visible result

After the environment came back, Captain reissued the literal S7 prompt
`Explain when I use -는데 versus -지만.` The learner turn appeared. Sam wrote
nothing. There was no error message and no retry affordance.

Independent code trace found the mechanism:

- `CoachSessionService.BuildAnswerAsync` calls
  `CoachAnswerProjection.Project(...)`.
- On projection failure, it logged only the count of failed rules
  (`the answer failed {n} shape rule(s)`) — not the identity of the rule.
- It called `RefuseAnswerAsync` without setting `_turnViolation`.
- `RefuseAnswerAsync` sent `messages: []` (correct — the client owns learner
  copy) and delegated to `CoachRefusalLimitationProjection.Project`, which
  hardcodes `CoachLimitationCode = UnverifiedClaimWithheld`. That code was
  authored for the grounding-refusal path.
- `CoachStateMachine.Compute` collapsed
  `Rejected + ValidationFailed + no receipt + no clarifying question` to
  `CoachUiState.Ready`.
- `CoachLimitationCard` did render, but resolved to
  `Coach_Limitation_UnverifiedClaimWithheldNoEvidence`, which is semantically
  a false statement about the failure (no evidence read had occurred).

River's initial proposal was to raise `MaxTotalCharacters` from 1600 to
2400 or 2800 and to emit a `CoachMessage(Notice, ...)`. Zoe rejected this
proposal because:

- The retained log named a count, not a rule. There are fifteen rules that
  `CoachAnswerProjection` can reject on. Raising the total cap does not fix
  any of the other fourteen, and it may not even be the failing rule.
- Emitting a server-authored `Notice` reintroduces the exact defect that
  motivated `messages: []` (English hardcode reached Korean learners).
- 2400/2800 is a doubling motivated by an unproven claim about which rule
  fired. It is not an evidence-based bound.

**We do not document the rejected proposal as the fix.** The repair that
shipped is R1 through R6:

- R1. Prompt schema `[Description]` on
  `CoachPedagogicalAnswerIntent.Blocks` now names the total-character bound
  (1600) so the model can self-regulate.
- R2. The shape-refusal branch now logs the specific rule strings from
  `CoachAnswerProjection` (operator-authored constants, never learner or
  model content). `_turnViolation` is set to `LengthLimit` or `IntentShape`
  by prefix-matching those constants. The opportunity mapper emits a distinct
  code `answer_shape_invalid` (formerly indistinguishable from
  `intent_shape_invalid`).
- R3. `CoachLimitationCode.AnswerShapeInvalid = 7` was appended (ordinals
  stable). `CoachRefusalLimitationProjection.ProjectShape(...)` emits a
  content-free limitation with `Coverage = Unknown`. `RefuseAnswerAsync`
  accepts an optional `limitationOverride` so the shape path can pass
  `ProjectShape(...)` while the grounding path keeps `Project(evidence, ...)`.
- R4. Client added `Coach_Limitation_AnswerShapeInvalid` in `en` and `ko`
  with the established Sam persona register (`쌤`, polite-casual, no emoji).
  `CoachLimitationCard.razor` gained a `switch` arm for the new code.
  Because the composer is already visible in `Ready` state, no additional
  retry button was added; the learner can retype immediately.
- R5. Sixteen new test methods across four files pinned the whole chain:
  the schema description carries `1600`, the shape-refusal branch produces
  the new code, the state machine keeps the surface `Ready` with the card
  visible, the card renders the localized copy, wire ordinals are stable
  including `PedagogicalAnswer = 7` and the six earlier
  `CoachLimitationCode` values.
- R6. `_answerShapeRefused` flag drives the mapper split; the mapper
  now emits `answer_shape_invalid` for the shape path so operators can
  slice production telemetry. A follow-up telemetry pass over the next
  week will decide, on evidence, whether the 1600 cap needs a bump. That
  decision is not being made today.

River is locked out of authoring or advising on this cycle under the strict
reviewer rejection protocol.

Simon reviewed the diff across contracts, API application, validation,
opportunity mapping, UI limitation card, and localization. Approved with no
high-confidence bugs found.

After activation, Captain reissued the literal S7 prompt. Sam completed the
answer. API logged `StopReason=Completed` with `Intent=PedagogicalAnswer`.

---

## 5. Root causes vs contributing factors

Root causes:

- **RC1.** `aspire run --isolated` from the live AppHost path took ownership
  of DCP identity and stopped the live stack. Same-path isolation is not
  hermetic today.
- **RC2.** `CoachOptionsValidator` was strict and correct, but the AppHost
  layer allowed duplicates through. A single copy-paste typo in per-developer
  user-secrets was enough to make `Host.StartAsync` fatal on boot.
- **RC3.** The shape-refusal path in `CoachSessionService.BuildAnswerAsync`
  used the wrong limitation code, dropped the rule identity from the log,
  and did not set `_turnViolation`. The learner surface then rendered a card
  that misdescribed the failure.

Contributing factors:

- **CF1.** The recovery gate proved WebApp 302 and volume presence only. It
  did not prove API health or Sam visibility, so the outage went unnoticed
  after the initial takeover.
- **CF2.** DCP kept the forwarding port bound on a resource whose backend
  had exited. Requests timed out instead of returning `connection-refused`.
  This is harder to detect and hid the API crash from simple probes.
- **CF3.** The user-secrets file overrode the on-disk `__dev_all__` sentinel
  in `appsettings.Development.json`. The sentinel could never take effect
  while the secrets had explicit GUIDs. This is intentional ASP.NET config
  precedence, but the interaction was not obvious.
- **CF4.** The Copilot session's persisted transcript and checkpoints were
  present, but not in the active model context. The coordinator answered
  from the current turn buffer alone.
- **CF5.** Canvas provider reconnect orphaned the automated page and started
  a fresh cookie jar. This is a Copilot Canvas provider problem, distinct
  from product auth.
- **CF6.** The generic `intent_shape_invalid` telemetry code was shared
  across four distinct failure classes, so no historical dashboard could
  have surfaced the shape defect on its own.

**Explicitly unproven.** The exact `dotnet user-secrets set` (or equivalent
edit) that first wrote the duplicate at index 2 is not recoverable from
retained tool history. We name the proven configuration state, the mtime
(2026-08-22 10:25:46 CT), and the process failure. We do not invent the
command that produced it.

---

## 6. Why prior verification missed each fault

- **RC1 (DCP takeover).** No pre-flight check refused an isolated launch from
  the same project path. The prior operational guidance in several skill
  files even suggested `--isolated` as the safe agent path from a clone.
  Neither the CLI nor our own guidance caught the misuse.
- **RC2 (duplicate allowlist).** The API validator did throw the correct
  error. The stack trace was captured to a DCP-managed stdout file, not
  surfaced in a health probe. Nothing checked API resource state after
  boot.
- **RC3 (shape-refusal code).** The historical operator log line
  `intent_shape_invalid` matched four distinct rejection paths. No test
  pinned the semantics of the code emitted on shape failure. No integration
  test asserted that a shape-refused turn produced a learner-visible
  limitation card with a code that matched the true cause.
- **CF1 (302-only gate).** The gate was written to catch a total outage of
  the WebApp process. It did what it was written to do. It was not written
  to catch a healthy WebApp fronting a dead API.
- **CF4 (session context).** No monitoring surface reports "active model
  context omitted persisted history." The failure is silent by construction.

---

## 7. Remediation completed

Environment repair (owner: Wash; reviewer: Zoe):

- Backup of `secrets.json` to `secrets.json.pre-fix-20260822-1450`.
- Removed the single duplicate at `Coach:AllowedUserProfileIds:2`. Cohort
  remained `{canonical test account, Captain}`.
- Graceful stop of the prior AppHost lineage. Restart via `aspire run` from
  the same worktree, without `--isolated`.
- Five infrastructure gates confirmed: DB mount, API resource state, user
  count, no duplicate error, `/health` = Healthy.

Product-side answer-shape repair (owners: Wash R1–R3, Kaylee R4, Jayne R5;
reviewer: Zoe; second reviewer: Simon):

- R1. Prompt schema `[Description]` states the total-character budget (1600).
- R2. Warning log includes projection error strings; `_turnViolation` is set
  by prefix-matching operator-authored constants; opportunity mapper emits a
  distinct code for the shape path.
- R3. `CoachLimitationCode.AnswerShapeInvalid = 7` appended;
  `ProjectShape(...)` returns content-free DTO; `RefuseAnswerAsync` accepts
  `limitationOverride`.
- R4. Localized copy for `en` and `ko`; card `switch` arm; no redundant
  retry button because the composer is already visible in `Ready`.
- R5. Sixteen new test methods across API, unit, and UI projects. Test
  counts after the repair: API 4160/4160, Unit 1757/1757, UI 6/6, and
  regression baseline 2125/2125 across the broader UI test project.
- R6. `answer_shape_invalid` telemetry code is now distinct from
  `intent_shape_invalid`. A one-week telemetry pass is queued to decide,
  on evidence, whether the 1600 cap needs a modest bump.

Environment hardening (owner: Wash; reviewer: Zoe):

- `CoachConfigurationReader.ReadAllowedUserProfileIds` deduplicates with
  ordinal comparison and reports the source index of any dropped duplicate
  through a new `CoachAllowlistResult`. AppHost emits a `Console.WriteLine`
  warning naming only the index, never the profile ID value.
- The API validator remains strict as the last line of defense.
- `scripts/post-aspire-restore.sh` — read-only health gate. Checks WebApp,
  API `/health`, expected named volume, and the absence of
  `OptionsValidationException` or the duplicate-entry error in a supplied
  log file. Companion `scripts/post-aspire-restore.test.sh` runs seven
  hermetic shell tests.
- `.claude/skills/e2e-testing`, `.squad/skills/aspire-orphan-recovery`,
  `.agents/skills/aspire`, `src/.agents/skills/aspire`, and
  `.github/skills/aspire` were updated so no active guidance recommends
  `aspire run --isolated` from a live AppHost project path. The recommended
  agent path is a separately materialized project path (git worktree or
  full clone) with its own `LocalDb:DataVolume`.
- `docs/local-dev-database-volumes.md` records the earning event and the
  new post-recovery gate.

Post-restore verification of the repaired stack:

- `curl -sk https://localhost:7012/health` → `Healthy`.
- `curl -sk -o /dev/null -w '%{http_code}' https://localhost:7071/` → `302`.
- `docker inspect db-461cee1b --format '{{range .Mounts}}{{.Name}}{{end}}'`
  → `sentencestudio-local-crispy-barnacle-db-data`.
- `scripts/post-aspire-restore.sh` PASS on WebApp, API, volume, and log
  scan.

Live proof after activation:

- Literal S7 prompt `Explain when I use -는데 versus -지만.` completed on the
  shared Canvas with the canonical test account.
- API log line for that turn: `StopReason=Completed`,
  `Intent=PedagogicalAnswer`.
- The Sam answer is still on screen at the time of sign-off.

---

## 8. Preventive controls and owners

| Control | Owner | Status |
|---|---|---|
| `CoachConfigurationReader` dedup + source-index warning | Wash | Landed |
| `CoachOptionsValidator` strict duplicate rejection (unchanged, defense in depth) | Wash | Landed |
| `CoachLimitationCode.AnswerShapeInvalid` + projector split | Wash | Landed |
| `answer_shape_invalid` opportunity code | Wash | Landed |
| Localized card copy `en` and `ko` | Kaylee | Landed |
| Sixteen regression tests (schema description, shape refusal, state machine, card render, wire format) | Jayne | Landed |
| `scripts/post-aspire-restore.sh` + hermetic shell tests | Wash | Landed |
| Skill updates: no active same-path `--isolated` guidance | Jayne | Landed |
| Telemetry pass over one week to decide on the 1600 cap | Wash | Queued |
| Update Squad decisions log through Scribe (this incident) | Scribe | Pending |
| Copilot Canvas provider reconnect + session hydrate report | Zoe | Draft (see §9) |
| Upstream Aspire report on same-path isolation and DCP port binding | Simon (writer), Zoe (reviewer) | Draft (see §9) |

---

## 9. Upstream reports

### 9.A Aspire — draft

Title: `aspire run --isolated` from an in-use AppHost project path takes
ownership of the live stack; DCP forwarding port stays bound after backend
exit.

Environment (to include when filed):

- Aspire CLI 13.4.6.
- `Aspire.Hosting.Orchestration` 13.5.0-preview.1.26315.1.
- .NET SDK from `dotnet --info` on the reporting machine.
- macOS.

Repro plan (both effects):

1. Start AppHost A from a project path P (`aspire run` from that path).
2. While A is running, from the same path P, run `aspire run --isolated`.
3. Observe: DCP re-controls the resource identity keyed on P. The live
   containers are stopped and replaced. `--isolated` did not create a
   hermetic sub-stack, only port isolation.
4. Now stop the backend process of a project resource under DCP (any way
   that leaves DCP up, for example a `Host.StartAsync` failure such as a
   validation exception in `ValidateOnStart`).
5. Observe: DCP keeps the forwarding port bound and requests time out
   rather than returning `connection-refused`.

Expected:

- `--isolated` should be a hermetic sub-stack, or the CLI should refuse the
  second run with a clear error naming the identity collision.
- DCP should either close forwarding ports when the backend exits, or make
  the state visible so that a probe against the forwarding port fails
  fast with 502/503, not a timeout.

Actual:

- Second `--isolated` invocation seized the first stack. Container name
  `db-<hash>` was reused. `LocalDb:DataVolume` env override on the running
  process was not honored by the second boot.
- Forwarding port stayed bound after the backend crashed; requests timed
  out.

Requested fix, in priority order:

- DCP identity must factor a run-scoped nonce when `--isolated` is set, or
  the CLI must refuse when an existing DCP api-server owns the same project
  path.
- DCP forwarding ports should close or fail fast when the backend
  resource state is `Finished`.

Evidence to attach:

- `~/.aspire/logs/cli_20260822T160442005_detach-child_0fc3b7e2207c4fae91ea6ec2d59911d3.log` excerpt showing
  the API resource `Running` at 16:04:52.647 UTC → `Finished` at
  16:04:57.660 UTC.
- API stdout excerpt showing the `OptionsValidationException` at that
  boot.
- Post-incident `docker volume ls` output showing both the intended and
  an isolated auto-generated volume.

### 9.B Copilot App and session — draft

Title: Persisted transcript and checkpoints were present but omitted from the
active model context; Canvas provider reconnect orphaned a live automation
instance.

Symptoms (both need to be reported):

- Sixty-one turns and twenty-two checkpoints were readable through
  `squad_state_*`. The active turn buffer did not include them. Answers to
  questions that depended on earlier turns were made without the earlier
  turns.
- During a Canvas provider reconnect, the automation binding to the shared
  browser instance was lost. A new Canvas came up with a new cookie jar
  and no reference to the earlier automated page.

Ask:

- On session hydrate, active model context should include the persisted
  transcript or announce a clean-context state.
- Canvas provider reconnect should re-attach the automation binding when
  possible, or announce the loss so the caller can decide whether to reissue
  a fresh binding rather than assume continuity.

Status: draft. The target tracker for this report is not yet known. This
section is deliberately captured in the RCA so it is not lost.

---

## 10. Current handoff

- Shared Canvas remains open at `https://localhost:7071` on the successful
  Sam answer.
- Signed in as the canonical test account (`squad-jayne@…`).
- Expected view: the Sam workspace, with the S7 turn
  `Explain when I use -는데 versus -지만.` and Sam's completed reply above the
  composer. The composer is enabled (Ready state).
- Suggested Captain checks:
  1. Reissue any short bilingual grammar prompt in the same conversation
     and confirm the reply arrives.
  2. Reissue a deliberately oversize prompt to trigger the shape refusal;
     the card should render `Coach_Limitation_AnswerShapeInvalid` copy
     and the composer should stay usable.
  3. Confirm `scripts/post-aspire-restore.sh` still returns success.

---

## 11. Evidence index

- Squad state notes:
  - `.squad/decisions/inbox/wash-2026-08-22-sam-missing-rca.md`
  - `.squad/decisions/inbox/zoe-2026-08-22-sam-missing-investigation-contract.md`
  - `.squad/decisions/inbox/zoe-2026-08-22-sam-missing-remediation.md`
  - `.squad/decisions/inbox/wash-2026-08-22-sam-environment-repair.md`
  - `.squad/decisions/inbox/river-2026-08-22-sam-shape-rule-rca.md` (rejected proposal; evidence only)
  - `.squad/decisions/inbox/zoe-2026-08-22-sam-shape-repair-review.md`
  - `.squad/decisions/inbox/wash-2026-08-22-sam-shape-server-repair.md`
  - `.squad/decisions/inbox/kaylee-2026-08-22-sam-shape-client-repair.md`
  - `.squad/decisions/inbox/jayne-2026-08-22-sam-shape-regression-tests.md`
  - `.squad/decisions/inbox/simon-2026-08-22-sam-incident-repair-review.md`
  - `.squad/decisions/inbox/wash-2026-08-22-sam-shape-repair-activation.md`
  - `.squad/decisions/inbox/wash-2026-08-22-sam-environment-hardening.md`
  - `.squad/decisions/inbox/jayne-2026-08-22-aspire-e2e-guidance-fix.md`
  - `.squad/decisions/inbox/jayne-2026-08-22-sam-environment-verification.md`
- Source paths cited:
  - `src/SentenceStudio.Api/Coach/Runtime/CoachOptionsValidator.cs`
  - `src/SentenceStudio.Api/Coach/Runtime/CoachRuntimeServiceCollectionExtensions.cs`
  - `src/SentenceStudio.Api/Coach/Application/CoachSessionService.cs`
  - `src/SentenceStudio.Api/Coach/Validation/Claims/CoachRefusalLimitationProjection.cs`
  - `src/SentenceStudio.Api/Coach/Validation/CoachAnswerProjection.cs`
  - `src/SentenceStudio.Api/Coach/Opportunities/Mapping/CoachTurnOutcomeOpportunityMapper.cs`
  - `src/SentenceStudio.AppHost/AppHost.cs`
  - `src/SentenceStudio.AppHost/CoachConfigurationReader.cs`
  - `src/SentenceStudio.AppHost/appsettings.Development.json`
  - `src/SentenceStudio.Contracts/Coach/CoachAnswerLimits.cs`
  - `src/SentenceStudio.Contracts/Coach/CoachLimitationEnums.cs`
  - `src/SentenceStudio.Contracts/Coach/Intent/CoachPedagogicalAnswerIntent.cs`
  - `src/SentenceStudio.UI/Services/CoachFeatureFlags.cs`
  - `src/SentenceStudio.UI/Services/CoachSurfaceGate.cs`
  - `src/SentenceStudio.UI/Shared/Coach/CoachLimitationCard.razor`
  - `src/SentenceStudio.Shared/Resources/Strings/AppResources.resx`
  - `src/SentenceStudio.Shared/Resources/Strings/AppResources.ko.resx`
- Scripts:
  - `scripts/post-aspire-restore.sh`
  - `scripts/post-aspire-restore.test.sh`
- Test counts at sign-off:
  - `SentenceStudio.Api.Tests` — 4160/4160 pass.
  - `SentenceStudio.UnitTests` — 1757/1757 pass.
  - `SentenceStudio.UI.Tests` (broader regression baseline) — 2125/2125.
- Live runtime facts at sign-off:
  - `https://localhost:7071` → HTTP 302.
  - `https://localhost:7012/health` → `Healthy`.
  - DB container `db-461cee1b` mounts named volume
    `sentencestudio-local-crispy-barnacle-db-data`.

---

## 12. Open items

- Production deployment and soak are not authorized. The repair has not
  been shipped to Azure or DX24, and this RCA does not authorize it.
- Uncommitted work remains in the worktree. This RCA does not authorize
  a commit or a push. Captain reviews the diff and decides.
- NuGet public endpoint was unavailable during the incident window (likely
  VPN). Offline package cache validation succeeded. `dotnet build`
  succeeded across all changed projects using `--no-build` or cached
  packages, but a fresh restore was not attempted.
- The rejected numeric-limit proposal (River, RC3-adjacent) has not been
  reopened. The one-week telemetry pass owned by Wash will decide, on
  evidence, whether the 1600 cap needs a bump. That decision is out of
  scope for this RCA.
- The upstream Copilot App and session report (§9.B) is a draft. The
  target tracker is not yet known.

---

Verified: environment repaired and confirmed live (WebApp 302, API `/health`
= Healthy, DB volume `sentencestudio-local-crispy-barnacle-db-data` mounted,
`scripts/post-aspire-restore.sh` PASS); the literal S7 prompt
`Explain when I use -는데 versus -지만.` completed in the shared Canvas with
API log `StopReason=Completed` and `Intent=PedagogicalAnswer`; test counts
API 4160/4160, Unit 1757/1757, UI 2125/2125; product-side answer-shape
repair R1–R6 shipped and reviewed by Simon; environment hardening (dedup
guard, post-restore script, skill updates) landed; no data mutation, no
migration, no DataRecoveryService invocation at any point.

— Zoe (Lead), 2026-08-22
