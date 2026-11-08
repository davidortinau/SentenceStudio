# Sam / Learning Coach - Journey Audit

Scope: Sam (internal name "Coach") end to end, plus other AI-driven, non-activity learner features.
Read-only audit. No file in any checkout was modified.

## 0. Provenance labels and sources

| Label | Meaning | Source read |
|---|---|---|
| F | Frozen reference | a frozen copy of `main` at commit `59c72262` (the approved native-rebuild reference snapshot) |
| M | Live main, committed | the developer's `main` checkout at HEAD `59c72262` |
| MU | Live main, uncommitted | same checkout, `git status` (31 entries) |
| T | Target branch | the `native-platform-heads` branch at `81d2ddc4` (18 commits after `59c72262`) |

Paths below are relative to the repo root unless prefixed. Unless a finding says otherwise it is present in F, M, and T identically, and absent from MU.

Result of the diff checks that justify that statement:

- F vs M source: `diff -rq` of `src/` shows no difference in any Sam/Coach file. The only tracked-file differences are `src/SentenceStudio.UI/Pages/Resources.razor`, `src/SentenceStudio.UI/wwwroot/css/app.css`, `src/SentenceStudio.iOS/SentenceStudio.iOS.csproj` (all MU, none related to Sam). `docs/` and `.claude/skills/e2e-testing/references/learning-coach.md` are identical (only `SKILL.md` and `management-pages.md` differ).
- MU: the 31 modified/untracked entries are `.squad/*`, `AGENTS.md`, `Directory.Packages.props`, `.github/copilot-instructions.md`, e2e `SKILL.md`/`management-pages.md`, Resources page/css, iOS csproj, `tests/SentenceStudio.Reference.Resources.Tests/`, `scripts/reference-resources-repair/`. None touch Coach or Sam code. The only Sam-related MU-adjacent item is a decision note in a gitignored directory (section 11).
- T vs M: 18 files under Coach/Contracts/UI/WebApp-operator differ (section 11). No Sam UI exists in `src/SentenceStudio.Native*` (section 10).

## 1. What Sam is

Sam is the in-app AI learning coach. Technical name is "Coach"; "Sam" (English) and "쌤" (Korean) are localized display values of resource key `Coach_RoleCoach` (`src/SentenceStudio.Shared/Resources/Strings/AppResources.resx:7576-7577`, `AppResources.ko.resx:7146-7147`). The display name follows the language the learner is studying, not the UI language: a Korean target language shows 쌤, anything else falls back to Sam (`src/SentenceStudio.UI/Services/CoachPersona.cs`, `CoachPersonaLanguages.cs:39` fallback `en`; `docs/coach/coach-product-policy-v1.md:22-30`).

For the learner, Sam is a single dual-purpose partner (`.claude/skills/e2e-testing/references/learning-coach.md:1-60`, `docs/coach/coach-product-policy-v1.md:32-46`):

1. Language-learning partner. Answers language questions (form, meaning, usage, pronunciation mechanics), contrasts confusable forms, gives level-appropriate examples, converses in the target language, discusses study strategy, gives formative correction of a learner sentence. These "pedagogical answers" never write anything and load no vocabulary rows, due queue, diary, or transcripts.
2. Learner-state reporter. Answers from fresh, owner-scoped read tools (practice history, balance, due summary counts, resources, skills, settings, current plan) and shows the evidence behind the answer.
3. Safe editor of Today's Plan (minutes, audio/speech/typing allowed, skill emphasis, goal, energy, grounded vocabulary focus) with preview, accept/reject, receipt, and undo.
4. Proposer of changes to the learner's own material (12 `propose_*` tools: vocabulary, skills, resources, preference, YouTube import) behind an approval ledger, plus a topical vocabulary set that can launch a Vocab Quiz.
5. Remembers a small set of learner preferences (study goal, explanation depth, correction timing, example register) only after the learner approves them.

Sam cannot: start activities (no launch capability in this version, `learning-coach.md:4155-4166` COACH-POLICY-02), approve its own proposals, grade, credit practice time, read the diary or conversation transcripts, enumerate the review queue, reveal an in-progress assessment answer, or claim proficiency.

Sam is online-only. It runs on the API; coach state lives in server tables (`CoachDbContext`, `src/SentenceStudio.Api/Coach/Persistence/CoachDbContext.cs`) and never reaches mobile SQLite (`learning-coach.md:1159-1169` LC-SESS-07). It is gated by server config and a user cohort (section 8).

Model of the codebase: one Sam (Coach) runtime that grows (decision 2026-11-07, `docs/coach/sam-single-runtime.md`). The September "legacy runtime freeze" was removed. `docs/coach/coach-product-policy-v1.md` remains behavior reference; parts of it (typed ApplicationCapability catalog, direct Vocab Review client action, learner-state deterministic capabilities) are only partly implemented in code (`unconfirmed` per feature below).

## 2. Surfaces

"Mark operator/dev-only ones" - flagged in the Scope column.

| # | Surface | What the learner sees | Scope | Mount / gate (file:line) |
|---|---|---|---|---|
| S1 | Sam FAB (floating chat button, bottom-right) | 48px button, aria-label "Open {Sam}", unread dot | Learner, web + MAUI hybrid | `src/SentenceStudio.UI/Shared/Sam/SamFab.razor`; mounted by `SamOverlayHost.razor` when collapsed; gate `CoachSurfaceGate.Decide` (`src/SentenceStudio.UI/Services/CoachSurfaceGate.cs:41`), used at `src/SentenceStudio.UI/Layout/MainLayout.razor:83-90` |
| S2 | Sam panel (non-modal overlay, Compact / Expanded / FullScreen) | Header (title, conversations toggle, compact/expand, full screen/restore, close), chat log, composer | Learner | `src/SentenceStudio.UI/Shared/Sam/SamPanel.razor`, `SamOverlayVisualState.cs`; opens at `SamOverlayHost.razor:187` |
| S3 | Legacy coach workspace modal (`?coach=<id>` query-backed dialog) with Chat/Plan tabs and plan canvas | Mounted only when the Sam overlay flag is off or flags not yet loaded; no visible entry control in code | Learner, legacy | `src/SentenceStudio.UI/Shared/Coach/CoachWorkspaceHost.razor`, `CoachWorkspaceOverlay.razor`, `CoachDialog.razor` |
| S4 | `/coach` full-screen route | Activity-shell page with Chat / Plan panes; on viewports >= 768 redirects to `/` (overlay) | Learner, legacy / narrow | `src/SentenceStudio.UI/Pages/Coach.razor:1,215`; `CoachStateMachine.cs:19,32` |
| S5 | Conversation shelf (list drawer) | Saved conversations, New, rename, close/reopen, delete, export | Learner, requires durable history | `src/SentenceStudio.UI/Shared/Coach/CoachConversationList.razor` |
| S6 | Chat message stream | Learner/Sam messages, timestamps, Copy, report flag, evidence disclosure, repair note, refusal card, dispute notice, write card, suggestion card, memory card, vocabulary-set card, receipts | Learner | `src/SentenceStudio.UI/Shared/Coach/CoachChatPane.razor` (43-50 refusal card, 416 starter chips) |
| S7 | Starter prompt chips (5 / 10 / 20 min, No audio, No speech, No typing, Low energy) | Shown only in an empty conversation and only when the learner has a plan | Learner | `CoachChatPane.razor:416` (`ShowChips`), `CoachConstraintChips.razor:59` |
| S8 | Plan canvas (Today's Plan, constraints, practice balance, revision history with Undo) | Only in S3 and S4; the Sam panel (S2) does not mount it | Learner, legacy hosts | `CoachPlanCanvas.razor`, `CoachRevisionHistory.razor`; mounted at `CoachWorkspaceOverlay.razor:146`, `Pages/Coach.razor:88` |
| S9 | Proposal / receipt card for writes (`SamWriteCard`) | Inline per-operation card: Apply or Review-and-confirm, Decline, Undo, Check again | Learner, requires `IsSamWriteAvailable` | `src/SentenceStudio.UI/Shared/Sam/SamWriteCard.razor`, `SamElementIds.cs`, `SamWritePresentation.cs` |
| S10 | Plan suggestion card | Rationale, change lines, Accept, "Not now" | Learner | `CoachSuggestionCard.razor` |
| S11 | Vocabulary set proposal card | Whole-set list, "Approve complete set", "Decline", then "Start Vocab Review" | Learner | `CoachVocabularySetCard.razor:35-45` |
| S12 | Memory candidate card (in chat) | "What Sam would remember", Save, Edit, Only this chat, Not now | Learner, requires `IsMemoryAvailable` | `CoachMemoryCandidateCard.razor` |
| S13 | Memory panel (Settings) | Active / Candidates tabs, edit, forget, forget all | Learner | `src/SentenceStudio.UI/Pages/Settings.razor:208`, `CoachMemoryPanel.razor` |
| S14 | Settings "Learning Coach" section | Description, "Delete coach history" with confirm dialog, memory panel | Learner | `Settings.razor:176-236` |
| S15 | Limitation card (typed refusal) | Reason, count, coverage, window, as-of, destination label with consequence, alternatives, hint ladder, shorter-session offer | Learner | `CoachLimitationCard.razor`, mounted `CoachChatPane.razor:43-50` |
| S16 | Inline "View evidence" disclosure + evidence scope | Date window, summary, aggregate values, coverage/order/withheld counts | Learner | `CoachMessageEvidence.razor`, `CoachEvidenceList.razor`, `CoachEvidenceScope.razor` |
| S17 | Report-response control (flag beside Copy) | Inline closed-choice panel with 5 reasons, Report / Cancel, settles to "Reported for review" | Learner, `Coach:Reports:Enabled` | `CoachReportControl.razor`, `CoachWorkspaceState.Reports.cs` |
| S18 | State notices (stage rows, offline, failed, incomplete, limited, plan changed elsewhere, expired, stopped) | Status/alert cards with retry / keep / stop | Learner | `CoachStateNotice.razor` |
| S19 | Repair disclosure note and dispute notice | Per-message note ("Sam adjusted part of this answer...") and head-of-log correction status | Learner | `CoachRepairDisclosureNotice.razor`, `CoachDisputeNotice.razor` |
| S20 | In-app Help assistant (HelpKit) | "Help" nav item opens an AI Q and A over bundled help articles | Learner, MAUI iOS and Mac Catalyst heads only | `src/SentenceStudio.UI/Layout/NavMenu.razor:40-46,134-155`; `src/Shared/HelpKitIntegration.cs`; registered `src/SentenceStudio.iOS/MauiProgram.cs:38`, `src/SentenceStudio.MacCatalyst/MauiProgram.cs:33` |
| S21 | Operator page `/operator/sam-opportunities` | Rollup and row views, detail drawer, review form, "Reveal learner content" | OPERATOR / DEV-ONLY (Development env, allowlisted user) | `src/SentenceStudio.WebApp/Components/Pages/Operator/SamOpportunities.razor:1,26,34,368,409` |
| S22 | Operator API `/api/v1/coach/operator/opportunities` (list, rollup, export, get, review, evidence) | JSON | OPERATOR / DEV-ONLY | `src/SentenceStudio.Api/Coach/Opportunities/Endpoints/CoachOpportunityOperatorEndpoints.cs:67,93-101` |
| S23 | Weekly opportunity digest (CLI tool + GitHub workflow) | Content-free markdown / NDJSON | OPERATOR / ops only | `tools/SamOpportunityDigest/Program.cs`, `.github/workflows/sam-opportunity-digest.yml`, `scripts/sam-opportunity-digest.sh`, `docs/sam-opportunity-digest.md` |
| S24 | Reported-responses read API | List of message ids the learner already reported (to render "Reported for review" after reload) | Learner (API) | `src/SentenceStudio.Api/Coach/Reports/Endpoints/CoachResponseReportEndpoints.cs:39` |

Surfaces that do NOT exist (searched, none found; stated so the rebuild does not invent them):

- No dashboard card or entry button for Sam. `src/SentenceStudio.UI/Pages/Index.razor` has no coach component (single `coach` mention is a comment at line 1381). The E2E reference still says "dashboard coach entry" (`learning-coach.md:362-390` LC-ENTRY, `:397-410` LC-WEB-01, `:1100` LC-SESS-01); that wording is stale versus code. The FAB is the only entry in code.
- No notifications, push, nudge, reminder, or proactive messages from Sam. A grep for push/nudge/proactive/reminder across Coach, Sam UI and services found only one unrelated copy string (`CoachDeterministicCopy.cs:258`, "a nudge" as hint-ladder wording).
- No Workers involvement. `src/SentenceStudio.Workers` has no Coach/Sam reference. Background work is inside the API (section 7).
- No learner-visible "opportunities" surface. "Opportunities" is operator telemetry (S21-S23), and `docs/sam-future-opportunities.md` is a product backlog, not UI.
- `EntryPointLabel`, `RunsRemainingToday`, `RunsRemainingThisWeek` are sent by the server (`src/SentenceStudio.Api/Coach/Application/CoachSessionService.cs:340-344`) but no UI code reads them (grep of `SentenceStudio.UI`, `SentenceStudio.WebApp`, `SentenceStudio.AppLib` found none).
- `docs/AdaptiveLearningCoachSystem.md` (flow/"difficulty mood" design) has no implementation: grep for `DifficultyMood` / `difficulty_score` in `src` found nothing. Treat as design only.

## 3. Conversation, turn, and memory model

### 3.1 Two transport shapes over one conversation

- Durable conversations (current path when `Coach:DurableHistory:Enabled`): `POST/GET /api/v1/coach/conversations`, `/{id}/messages`, `/{id}/turns`, `/{id}/operations/{opId}` (+ `/cancel`), `/{id}/vocabulary-set`, `PATCH /{id}` (rename, close/reopen via `Close`), `DELETE /{id}`, `GET /{id}/export`. `src/SentenceStudio.Api/Coach/Endpoints/CoachConversationEndpoints.cs:35-77`.
- Write ledger routes on the same group: `/{id}/writes/{opId}` GET, `/receipt`, `/accept`, `/confirmation`, `/confirm`, `/reject`, `/undo` (`CoachConversationEndpoints.cs:55-73`). Confirmation secret is sent only in header `X-Coach-Write-Confirmation` (`src/SentenceStudio.Api/Coach/Operations/CoachWriteHeaders.cs`).
- Legacy session routes remain as aliases ("for one release"): `/api/v1/coach/availability`, `/sessions` (start/get), `/sessions/{id}/turns`, `/suggestions/{sid}/accept|reject`, `/undo`, `/cancel`, DELETE (`src/SentenceStudio.Api/Coach/Endpoints/CoachEndpoints.cs:31-60,62-64`).
- Vocabulary-set approval: `POST /api/v1/application/vocabulary-sets/approve` (`src/SentenceStudio.Api/Coach/Endpoints/CoachVocabularySetEndpoints.cs:13`).
- Memory: `/api/v1/coach/memories` GET list, `/candidates`, `/{id}/approve`, `/{id}/reject`, PUT edit, DELETE one, DELETE all (`src/SentenceStudio.Api/Coach/Memory/Endpoints/CoachMemoryEndpoints.cs:36-46`).
- Reports: `GET /{conversationId}/responses/reported`, `POST /{conversationId}/responses/{messageId}/report` under `/api/v1/coach/conversations` (`CoachResponseReportEndpoints.cs:37-42`).
- Host mapping: `src/SentenceStudio.Api/Program.cs:718-730`. Every group is `RequireAuthorization()` and the caller identity comes from the `user_profile_id` claim (`CoachSessionService.cs:309`).

### 3.2 Turn model

- Request: `CoachConversationTurnRequest { IdempotencyKey, OperationId, Turn }` with `CoachTurnRequest` (`src/SentenceStudio.Contracts/Coach/CoachConversationContracts.cs`, `CoachTurnRequest.cs`). Input kinds: Text, Chip, ConstraintAction (`CoachEnums.cs`). Text max 500 chars (`CoachConstraintLimits.MaxTurnTextLength`, `src/SentenceStudio.Contracts/Coach/CoachConstraintLimits.cs`); the composer shows a counter and blocks send over the limit (`CoachComposer.razor`).
- Turn operations are durable and recoverable: states Pending, Running, Completed, Cancelled (`CoachTurnOperationState`), retry reuses the same operation id so a double ask cannot produce two turns (`CoachChatPane.razor:331-350` Retry/Recover buttons).
- Response: `CoachTurnResponse` with status (Completed, Incomplete, Failed, Rejected) and stop reason (Completed, ClarificationRequested, InputRejected, ValidationFailed, ToolFailure, IterationLimit, OutputTokenLimit, Timeout, RateLimit, ConcurrencyLimit, Cancelled, SessionExpired) (`src/SentenceStudio.Contracts/Coach/CoachEnums.cs`). May carry: `Answer` (structured blocks), pending suggestion, change receipt, write operation, limitation, repair disclosure, dispute, evidence, memory candidate, vocabulary state.
- Message kinds: Text, Clarification, Suggestion, Receipt, Notice, PedagogicalAnswer, Unrecognized (appended sentinel; renders a placeholder with no controls) (`CoachEnums.cs`, rendered `CoachChatPane.razor` `UnsupportedMessage`).
- Answer shape: 1-8 blocks, <= 1600 chars total, kinds Answer/Form/Meaning/Use/Example/Contrast/Correction/Note/RetrievalPrompt, topics Vocabulary/Grammar/Usage/Pronunciation/LearnerText/Conversation/StudyStrategy/Other, language roles Display/Target/Native (`CoachAnswerEnums.cs`, `CoachAnswerLimits.cs`). Target-language spans carry `lang` attributes.
- Intent kinds (persisted ordinals): NoChange, DirectConstraintChange, SuggestConstraintChange, AcceptPendingSuggestion, RejectPendingSuggestion, AskClarification, OffTopic, PedagogicalAnswer (`src/SentenceStudio.Contracts/Coach/Intent/CoachIntentEnums.cs:11-41`; ordinals are persisted, so append only and never reorder).
- Policy gate: the learner's current message, not the model's intent, authorizes plan-reducer entry; negation wins; teaching plus plan request = answer now and keep plan pending (`learning-coach.md:4138-4252` COACH-POLICY-01..10; `docs/coach/coach-product-policy-v1.md:65-143`).
- Clarification cap 2 per session (`CoachConstraintLimits.MaxClarificationsPerSession`), request timeout 45 s, 6 tool iterations, 16000 output tokens, reasoning effort "minimal", 10 runs/day and 40/week, 24 h session expiry, 30-day revision retention (`src/SentenceStudio.Api/Coach/Runtime/CoachOptions.cs`, defaults around lines 140-240).
- Concurrency: one in-flight run per learner (`ConcurrencyLimit`, problem `coach-run-in-progress`).

### 3.3 History and retention

- Messages are stored encrypted (`CoachContentProtector`, `CoachAgentSessionProtector`; key ring options `src/SentenceStudio.Api/Security/DataProtection/CoachDataProtection*.cs`). After a reload without durable history the server holds no plaintext transcript, so the legacy flow shows "Resumed ... history hidden" with plan summary (`CoachChatPane.razor:60-85`, `LC-HIST-08`).
- Durable history pages 50/100 messages, 20/50 conversations; load earlier / history-boundary row / unreadable count shown (`CoachHistoryLimits.cs`, `CoachChatPane.razor:99-127`). Title max 120.
- Learner message is echoed immediately in order, even on failures (`learning-coach.md:1952-2040` LC-HIST).
- Cleanup: expired sessions and aged revisions removed by API background services (section 7).

### 3.4 Learner memory

- Kinds: PersistentStudyGoal, ExplanationDepth (Concise/Balanced/Detailed), CorrectionTiming (Immediate/AfterResponse), ExampleRegister (NeutralPolite/Casual/Formal). Scope: TargetLanguage or Global. Provenance: UserExplicit or UserConfirmed. Status: Candidate, Active, Superseded, Expired, ConflictPending (`src/SentenceStudio.Contracts/LearnerMemory/CoachMemoryEnums.cs`).
- Caps: 32 active facts, 16 candidates, <= 8 context facts / 512 tokens per turn, study goal <= 160 chars (`src/SentenceStudio.Api/Coach/Memory/CoachMemoryLimits.cs`).
- Candidate is proposed in-turn by the model through a memory intent, validated by `CoachMemoryProposalGate`; nothing is saved until the learner presses Save (`src/SentenceStudio.Api/Coach/Application/Memory/CoachMemoryProposalGate.cs`, `CoachMemoryTurnCoordinator.cs`).
- Memory is separate from the six-field preference allowlist that the write ledger can change (section 5, J17).

### 3.5 Evidence the answers use

Read tools (15, `src/SentenceStudio.Api/Coach/Tools/CoachToolNames.cs`): `get_learner_profile_summary`, `get_practice_balance`, `get_vocabulary_due_summary`, `get_resource_catalog`, `preview_practice_plan`, `get_practice_history_summary`, `list_user_vocabularies`, `get_vocabulary_word_detail`, `get_skill_list`, `get_skill_detail`, `get_learning_resource_list`, `get_learning_resource_detail`, `get_current_profile_summary`, `get_learner_settings_summary`, `get_current_plan_summary`. Read tools beyond the original five need `Coach:SamReadTools:Enabled`. Evidence kinds on screen: PracticeBalance, VocabularyDue, ResourceCatalog, LearnerProfile, PlanPreview (`CoachEvidenceKind`), each with coverage (CompleteOwnedSet, PageOfOwnedSet, WindowBounded, SingleItem, SingleDay, SettingsSnapshot, DerivedProjection, CompleteAggregateWithBreakdown), order, withheld count and reason (DueReviewEmbargo, ResultLimit, ArchivedExcluded, BelowMinimumEvidence) (`CoachEvidenceScopeEnums.cs`).

Embargoes enforced server-side (`src/SentenceStudio.Api/Coach/Validation/CoachEmbargoScanner.cs`, `CoachDueItemLeakValidator.cs`, `CoachToolAllowList.cs`): no due-item enumeration, no diary/transcript read, no grading, no progress/time credit from Coach turns, no proficiency claims, prompt-injection fencing (`CoachPromptFence.cs`).

### 3.6 Route catalog (`CoachRouteCatalog`)

`src/SentenceStudio.Contracts/Coach/CoachRouteCatalog.cs` is the closed table of destinations Sam may name. It is used only inside limitation payloads (`CoachLimitationDto.Destination`, `FullScopeSurface`, `ExportSurface`) and rendered as a screen name plus a stated consequence. It is not a link: `CoachLimitationCard.razor:82-115` prints a label and the side-effect sentence, no `href` or `NavigateTo`. Whether any caller later turns the label into navigation is `unconfirmed`; the DTO contains no raw URL by design.

| Route name | Screen (label key) | Accepted parameters | Side effect disclosed |
|---|---|---|---|
| ActivityLog | `Coach_Route_ActivityLog` | PlanDate (yyyy-MM-dd) | None |
| Vocabulary | `Coach_Route_Vocabulary` | VocabularyWordId, ResourceId (positive integers only) | EditsLearnerData |
| Settings | `Coach_Route_Settings` | none | EditsLearnerData (Settings holds coach-history deletion and, on M, data export) |
| Skills | `Coach_Route_Skills` | SkillId | EditsLearnerData |
| Writing | `Coach_Route_Writing` | ResourceId, SkillId | StartsActivity |
| Feedback | `Coach_Route_Feedback` | none | PublishesPublicly |

Unknown route renders no destination; unknown side effect renders "Consequences not stated" (`CoachEnums.cs` `CoachRouteName`/`CoachRouteSideEffect`; `learning-coach.md:3842-3860` SAM-LIM-15). Parameter values are allow-list validated (`CoachRouteCatalog.IsWellFormed`).

Separate from this catalog, the only concrete app route Sam emits is the vocabulary-set approval's `ActivityPath = /vocab-quiz?resourceIds=<id>` (`src/SentenceStudio.Api/Coach/Application/CoachVocabularySetApplicationService.cs:619`), and the legacy `/coach` and `?coach=` routes (`Pages/Coach.razor:1`, `CoachWorkspaceHost.razor:42-45`).

### 3.7 Limitations (`CoachAvailabilityResponse`, `CoachLimitationEnums`)

`CoachAvailabilityResponse` (`src/SentenceStudio.Contracts/Coach/CoachAvailabilityResponse.cs`): IsAvailable, State (Disabled, OutsideCohort, LimitReached, Available, ResumeAvailable), EntryPointLabel, CanEditPlan, ActiveSessionId/Status/ExpiresAtUtc, RunsRemainingToday/ThisWeek, IsDurableHistoryAvailable, IsMemoryAvailable, IsSamOverlayAvailable, IsSamWriteAvailable. All booleans default false so an older server or a failed call hides optional surfaces.

`CoachLimitationCode` (`src/SentenceStudio.Contracts/Coach/CoachLimitationEnums.cs`): NotBuilt, AvailableOnAnotherSurface, RefusedByDesign, WouldRemoveLearningValue, ExceedsSafeChangeScope, UnverifiedClaimWithheld, AnswerShapeInvalid (7, added after the 2026-08-22 incident), Unknown (renders "Something Sam can't do here" with no borrowed reason). `CoachLimitationDto` adds coverage, as-of, window, affected count, destination(s), withheld count/reason, alternatives (RemoveOneListAtATime, StartAFreshList, ExportBeforeRemoving [retired on T], TakeAShorterSession, UseHintLadder), hint ladder rungs (Category, FormCue, Cloze), shorter-session offer. Deterministic refusal copy (bulk vocabulary deletion, review-answer refusal, hint-ladder offer, shorter-session offer, unchecked learner state, partial coverage) is in `src/SentenceStudio.Api/Coach/Application/CoachDeterministicCopy.cs` (e.g. 221, 252-262, 279-303).

Where limitations render today: `CoachChatPane.razor:43-50` mounts the card for `Coach.Limitation` on a withheld answer. The E2E reference (`learning-coach.md:3796-3801`) still says the card is mounted nowhere; code contradicts it at least for the withheld-answer path. How often the server emits non-withheld codes (hint ladder, shorter session) is `unconfirmed` - the server `CoachLimitations.cs` builds them (`src/SentenceStudio.Api/Coach/Application/Limitations/CoachLimitations.cs`).

## 4. Learner journeys

Format: numbered steps "start -> ... -> end". Citations point to the UI/API that implements each step; E2E case ids from `learning-coach.md` are included for traceability.

### Entry, layout, and session

**J1. Discover and open Sam**
1. App loads while signed in, past onboarding, not syncing (`CoachSurfaceGate.Decide`, `CoachSurfaceGate.cs:41-56`; `MainLayout.razor:83-90`).
2. Layout reads availability once (`CoachFeatureFlags.EnsureLoadedAsync`, `MainLayout.razor:298`), resolves persona from the profile's target language (`MainLayout.razor:303`).
3. If `IsSamOverlayAvailable` -> FAB appears bottom-right on every authenticated page; otherwise the legacy host mounts (inert, no visible button) (`CoachSurfaceGate.cs:56-63`).
4. Learner taps FAB -> `OpenPanelAsync` (`SamOverlayHost.razor:187`): availability ensured, persona ensured, `ResumeMostRecentAsync` (J2), panel opens Expanded (>= 992 px) or Compact (< 992 px), unread dot cleared, focus to composer.
5. End: panel open on the most recent conversation, or a new one.

**J2. Resume the most recent conversation (and cold start / reload)**
1. Learner taps FAB after a reload, a route change, or a native cold start.
2. Workspace consults circuit state then the durable directory; resumes the most recent conversation, else creates one (`CoachWorkspaceState.Durable.cs:352`).
3. Messages page in (50), "Load earlier" / "history boundary" shown; unreadable messages are counted, not guessed (`CoachChatPane.razor:99-127`).
4. End: same transcript, same pending suggestion/write card, same plan version. Without durable history: "resumed, history hidden" notice with plan summary and revision count (`CoachChatPane.razor:60-85`).

**J3. Resize, compact, expand, full screen, restore, close**
1. Open panel; Compact/Expanded toggle shrinks or grows without closing (`SamPanel.razor`, `SamOverlayHost.razor:CompactAsync/ExpandAsync`).
2. Full screen is the same panel at inset 0; draft, transcript, focus and scroll survive; background scroll locked and restored (`GoFullScreenAsync`, `SamOverlayHost.razor:276-340`).
3. Restore returns to the pre-full-screen size (not recomputed).
4. Close collapses to the FAB; conversation is not ended or deleted; focus returns to FAB.
5. Below 992 px the panel is a modal dialog with backdrop and inert background (`IsMobileModal`); above it is non-modal `role=complementary` with no focus trap.
6. Escape layers innermost first: confirm step, then report panel, then leave full screen, then collapse (`SamOverlayEscape.Resolve`; `learning-coach.md` SAM-A11Y-06).
7. Navigating to `/coach` collapses the overlay (`SamOverlayHost.razor:398-415`).

**J4. Sign out, account switch, token expiry**
1. Learner signs out, or another account signs in, or the refresh token is rejected.
2. `CoachAccountBoundary` detects a real account change (not a silent refresh) and clears workspace, directories, persona, and re-reads availability for the new learner (`src/SentenceStudio.UI/Services/CoachAccountBoundary.cs`, `CoachAccountIdentity.cs`, `MainLayout.razor:524-534`).
3. Overlay collapses, unread dot cleared, open confirmation step dropped.
4. End: no FAB and no coach DOM for a signed-out shell; new learner sees only their own threads (SAM-ACCT-01..18, `learning-coach.md:3331-3430`). Same-account re-notification and silent refresh clear nothing.

### Asking Sam (language partner)

**J5. Ask a language question**
1. Learner types in the composer (500 char limit, Enter sends on wide, send button, counter) and sends.
2. Learner message appears immediately; named progress stage ("Reading..."); after 8 s "Still working"; after 12 s a "Stop" button (`CoachStateNotice.razor`, `CoachStateMachine.cs:22,25`).
3. Server runs the pedagogical path: no learner vocabulary/due/diary loaded; structured answer blocks (form, meaning, use, example, contrast, optional one retrieval prompt) (`CoachAnswerEnums.cs`, `CoachAnswer.razor`).
4. Answer renders atomically (no streaming) with timestamp, Copy, report flag, and optional evidence/repair note.
5. End: no plan revision, no write, no practice-time credit (LC-LL-01..06, `learning-coach.md:1384-1480`).

**J6. Correction of a learner sentence**
1. Learner pastes a target-language sentence and asks for feedback.
2. Sam returns a Correction block, formative only (no grade/score) (LC-LL-06, LC-EMB-04).
3. If memory has `CorrectionTiming`, delivery honors it (`CoachMemoryContextSelector.cs`; unconfirmed that this changes UI timing, it changes prompt context).
4. End: answer only; nothing persisted beyond the conversation.

**J7. Conversation practice in the target language and study-strategy chat**
1. Learner writes in Korean (or the target language) or asks for study advice.
2. Sam replies with `lang`-tagged target spans; answers independent of review schedule and due queue (LC-LL-04, LC-LL-05, LC-LL-14).
3. End: conversation continues; Sam is not allowed to grade or claim proficiency (LC-EMB-07). This is separate from the Conversation activity (other agent's scope).

**J8. Follow-up across turns and mixed turn**
1. Learner asks "another example" or asks a language question plus a plan request ("What does 좋아하다 mean? Also suggest making Today's Plan 10 minutes").
2. Teaching answer renders first; a separate pending suggestion renders second; no receipt until Accept (COACH-POLICY-07, LC-LL-09).
3. A language question while a suggestion is pending leaves the suggestion pending; `좋아요?` and `What does 좋아요 mean?` never count as acceptance (LC-LL-10..12).

### Learner-state answers and evidence

**J9. Ask about my practice, plan, resources, skills, settings**
1. Learner asks "How am I doing this week?" or "What is in my plan today?".
2. Read tools run (balance, history, plan summary, resources, skills, settings) and the answer states window/coverage and limits (policy 9.1).
3. Under the message a "View evidence" button appears; press expands in place (date window, summary, aggregates); control becomes "Hide evidence"; messages without cited evidence show no control (SAM-EV-01..05).
4. End: answer + evidence; due-queue terms never enumerated (LC-EMB-01). Tool failure yields an explicit failure notice, never a fabricated summary (LC-LIMIT-10).

**J10. "When did I last study?" and dispute**
1. Learner asks the latest-study question; deterministic pre-model classifier answers from authoritative history (`CoachLatestStudyClassifier.cs`; policy 11.3). Whether the zero-model-call requirement is met in live code is `unconfirmed`; the classifier exists.
2. Learner says "That's wrong, I practiced yesterday".
3. `CoachDisputeCoordinator` opens a dispute (signals MeantSomethingElse, NotWhatIAsked, WrongClaim, DifferentCohort), Sam re-reads, then corrects or states the record; the dispute status notice stays at the head of the log until resolved or dismissed (`CoachDisputeNotice.razor`; flag `Coach:CorrectionState:Enabled`).

**J11. See what Sam did to an answer (repair disclosure)**
1. Answer includes a note "Sam adjusted part of this answer..." or "Sam found something here worth checking...", or a neutral "this version can't describe" for an unknown value (`CoachRepairDisclosureNotice.razor`, SAM-RPD matrix `learning-coach.md:3961-4130`).
2. Evidence pointer sentence appears only when this turn actually read evidence.

**J12. Refusal with typed limitation (e.g. "delete all my vocabulary")**
1. Learner asks for something outside Sam's scope.
2. Sam refuses with a limitation card: reason, affected count (omitted when zero or uncounted), coverage line, destination screen name plus the consequence on that screen, alternatives (clear one list at a time, start a fresh list), and zero destructive proposals (SAM-LIM-15).
3. Review-answer request ("just tell me") is refused without disclosing the answer; offers hint ladder and shorter session (SAM-LIM-16a/b, `CoachDeterministicCopy.cs:252-262`).
4. On T the export alternative and export surface are no longer produced (section 11).

### Editing Today's Plan

**J13. Direct constraint change by text**
1. Learner types "Make it 10 minutes and no audio" (explicit plan wording; a general study wish is NOT a plan request - COACH-POLICY-01).
2. Progress stage "Updating Today's Plan"; server validates (3-90 min, version check) and applies once.
3. Receipt shows what was replaced and preserved with Undo; completed and started items are never touched (LC-PRES-01..04).
4. End: plan on the dashboard updated; one `CoachPlanRevision` (`Source=DirectRequest`). Stale plan version -> "Nothing changed" with retry and keep (LC-DIR-03). Out-of-range -> refused or clamped and stated (LC-DIR-04).

**J14. Quick constraint by chip in an empty conversation**
1. In a new conversation with a plan, chips appear under "starter prompts": 5, 10, 20 min; No audio; No speech; No typing; Low energy (`CoachConstraintChips.razor:59`).
2. Tap a chip -> same immediate-apply path (`InputKind=Chip`/`ConstraintAction`).
3. Once the learner has spoken, chips move to the plan canvas (S8), available only in the legacy modal and `/coach` (`CoachChatPane.razor:411-420`). In the Sam overlay there is no path to edit constraints after the first learner turn except typing. Whether that is intended is `unconfirmed`.
4. Chips are withdrawn when there is no plan (`CanEditPlan` false); a neutral "no plan to edit" row replaces them (`CoachChatPane.razor:422-428`).

**J15. Suggestion preview, accept, reject**
1. Learner asks "What should I focus on today?"; Sam returns a suggestion: rationale, read-only diff, evidence with explicit date range, two actions (accept-equivalent, "Not now") (`CoachSuggestionCard.razor`).
2. Plan unchanged while pending; suggestion survives pane switches and reload; only the newest suggestion is actionable (LC-SUG-01..03).
3. Learner taps Accept (idempotent) or types a clear acceptance ("Yes, add that", "Do it", "좋아요, 그렇게 해 주세요") -> one `AcceptedSuggestion` revision and receipt; or taps/types rejection -> no write (LC-ACC-01..04, LC-REJ-01..03).
4. Ambiguous replies ("Maybe", "I guess so?", "Yes, but not the listening one") keep the preview and ask once, capped at two clarifications; then `ClarificationLimitReached` (LC-AMB-01..03).
5. Acceptance without a pending suggestion produces a question, not a write (LC-ACC-05).

**J16. Vocabulary focus proposal ("I want to focus today on active verbs")**
1. Learner states a semantic focus.
2. Application, not the model, selects the exact words from classified vocabulary; Sam shows the concrete word set as a pending proposal (never auto-applied, product decision 21.1) (`CoachVocabularyFocus.razor`, `CoachFocusSelection.cs`, `CoachVocabularyFocusService.cs`, LC-FOC-01..09).
3. Accept -> one revision and receipt; focus can later be cleared directly ("Drop the verb focus"). No match / thin match / missing metadata (unclassified part of speech) yields typed no-write outcomes; unrecognized phrasing asks (LC-FOC-03/04, LC-MIG).
4. Part-of-speech backfill is a prerequisite (`VocabularyPartOfSpeechBackfillService.cs`).

**J17. Undo a plan change**
1. From the receipt "Undo" button or the revision history in the plan canvas (S8).
2. Server restores remaining work only; completed work stays; idempotent and bounded; undo is blocked or limited after a completed activity (LC-UNDO-01..04).
3. A newer revision marks older ones "Superseded".

**J18. Plan changed on another device**
1. A turn or action finds a newer plan version.
2. State `PlanChangedElsewhere`: "out of date" notice with "Reload plan" and "Keep chatting" (`CoachStateNotice.razor`); no write.

### Writes to the learner's own material (requires `Coach:SamWriteTools`)

**J19. Soft write proposal (apply)**
1. Learner asks Sam to add or edit a word, add a skill, add/edit a resource, or link a word to a resource.
2. Sam calls one `propose_*` tool (max 1 proposal per turn, `CoachWriteLimits.ProposalsPerTurnMax`); a card appears inline at that turn with title, summary, lines (<= 12) and stage badge (`SamWriteCard.razor`).
3. Learner presses "Apply" (WriteSoft only) or "Decline".
4. Server rechecks identity, ownership, state, expiry (10 min), acceptance channel; executes; card shows receipt and an Undo button for 5 minutes where the handler supports undo (`CoachWriteLimits.cs`, `CoachWriteOperationService.cs`).
5. End: stage Applied/Undone/Declined/Expired/In-doubt ("Check again" when outcome unknown)/Failed; no "applied" claim before the receipt (SAM-SEQ).

**J20. Hard write proposal (confirm)**
1. Learner asks to remove a word, remove a resource, archive a skill, change a preference, or import a YouTube video.
2. Card shows "Review and confirm" (no direct Apply).
3. Review opens a non-modal alert-dialog confirmation step; server issues a one-use secret (5 min), sent only by header; secret never appears in DOM or URL (`SamWriteCard.razor`, `CoachWriteApprovalService.cs`).
4. Learner presses Confirm or Back/Escape to cancel.
5. Execute -> receipt. Undo matrix: skill archive and preference change reversible; vocabulary removal, resource removal, YouTube import not reversible (`learning-coach.md:3042-3068` 31.4).

**J21. Preference change proposal**
1. Learner asks Sam to change a setting.
2. Closed six-field allowlist: `target_language`, `native_language`, `display_language`, `session_minutes` (5-180), `cefr_level` (A1-C2), `quiz_show_text_with_photo`. Every field is a hard write (confirm flow). No-op and out-of-range are refused (SAM-PREF-01..07; `CoachPreferenceHandler.cs`). Note a stale comment in `docs/sam-future-opportunities.md:9-12` says the allowlist is empty; the E2E file says six fields from source.
3. The ledger records `preference_setting_*` problems for the operator when Sam refuses a direct change (SAM-OPP-02).

**J22. YouTube import proposal**
1. Learner pastes a YouTube link and asks Sam to import it.
2. Hard write card; on confirm the import starts (`CoachYouTubeImportHandler.cs`, `CoachYouTubeUrl.cs`); not undoable (SAM-YT, 31.10).

**J23. Topical vocabulary set -> Vocab Review**
1. Learner says "I want to study vocabulary about house rooms" or "start a vocabulary review with words about food".
2. Policy says Sam asks "replace the untouched part of Today's Plan, or start Vocab Review directly?" before doing anything (`coach-product-policy-v1.md:372-420`); how much of that two-way clarification exists in live code is `unconfirmed`. The live piece is the set proposal.
3. Card lists the full term set (target / native), buttons "Approve complete set" and "Decline" (all-or-nothing) (`CoachVocabularySetCard.razor:14-46`).
4. Approve -> `POST /api/v1/application/vocabulary-sets/approve`; server creates a "Vocabulary List" resource tagged `coach-vocabulary-proposal:<opId>` and the words with mappings, under the learner's profile (`CoachVocabularySetApplicationService.cs:560-620`).
5. Result shows "Start Vocab Review" which navigates to `/vocab-quiz?resourceIds=<id>`; saved quiz settings apply.
6. Defect to carry into the rebuild: this card's text is hard-coded English ("Review all N terms...", "Approve complete set", "Decline", "Saving...", "Start Vocab Review"), not localized; Sam's persona is Korean-capable, so Korean UIs get English here (`CoachVocabularySetCard.razor:14-46`).

### Memory

**J24. Save a remembered preference from chat**
1. During a turn Sam surfaces a candidate ("Sam can remember: ...") with scope (this language or all) and explainer (`CoachMemoryCandidateCard.razor`).
2. Learner picks Save, Edit then Save edited, "Only this chat", or "Not now"; nothing is stored without a press (`Coach_MemorySave`, `Coach_MemoryOnlyThisChat`, `Coach_MemoryNotNow`).
3. Memory becomes Active, appears in Settings, and is injected as bounded context in later turns.

**J25. Manage memory in Settings**
1. Settings -> Learning Coach -> memory panel with Active and Candidates tabs (counts) (`CoachMemoryPanel.razor`, `Settings.razor:208`).
2. Per item: scope, provenance, saved time, last used; Edit, Approve (candidate), Reject, Forget; "Forget all" with confirm dialog.
3. Paused state message when memory is unavailable (`Coach_MemoryPaused`). Memory vs Settings explainer text.

### Conversations

**J26. Manage saved conversations**
1. Open the list from the panel header toggle (only if `IsDurableHistoryAvailable`) (`SamPanel.razor`, `CoachConversationList.razor`).
2. New conversation; open one (closes drawer); Rename; Close (confirm) / Reopen; Delete (confirm); Export as JSON or Markdown; "Load more".
3. Closed conversation shows a closed notice and no composer send path (`CoachChatPane.razor:353-359`).
4. Delete scopes differ: one conversation here, current session or all history in Settings (J33), and server cascades through deletion contributors (history, checkpoints, reports, opportunities, memory) (`src/SentenceStudio.Api/Coach/Persistence/Deletion/CoachDataDeletionService.cs`).

### Trust and feedback

**J27. Report a response**
1. Hover/tab to the flag beside Copy on a durable Sam response (also on durable notices; not on learner messages, receipts, or no-change markers) (`CoachReportControl.razor`; SAM-RPT-01/01a).
2. Press -> inline panel with five reasons: Did not answer my request, Incorrect or misleading, Expected an app action, Confusing, Other. No free text.
3. Report response -> settles to "Reported for review", focus on the settled text, survives reload, idempotent (AlreadyReported) (SAM-RPT-02..07).
4. Failure shows "That report could not be sent. Nothing was reported."; unpairable response is refused 422 (SAM-RPT-08). Off when `Coach:Reports:Enabled=false` (404) (SAM-RPT-09).
5. Report raises a `UserReportedResponse` ledger row even when automatic capture is off (SAM-RPT-10).

**J28. Copy a message**
1. Copy button on each Sam message copies its readable text; per-message `DescribedById` accessible name (`CoachCopyButton.razor`).

### Failure, limit, and recovery states (all visible, typed)

**J29. Errors and limits during a turn**
1. Timeout (45 s): notice "nothing changed" + retry (LC-LIMIT-01).
2. Iteration limit or output-token limit: "Incomplete" card with Try again / Keep plan; never presents a truncated or blank message as an answer (LC-LIMIT-03/04).
3. Daily or weekly limit: `Limited` card "You reached today's message limit. Try again tomorrow" (`AppResources.resx:7236-7237`); availability `LimitReached` (`CoachSessionService.cs:328-337`). The Sam FAB still shows because the overlay gate reads only `IsSamOverlayAvailable`. The "tomorrow" text is used for weekly limits as well - unconfirmed whether that is accurate.
4. Concurrent run -> `coach-run-in-progress` (LC-LIMIT-07). Input > 500 chars blocked client-side and rejected server-side (LC-LIMIT-08). Empty send disabled (LC-LIMIT-09).
5. Offline: "Offline" notice, statement that nothing changed, retry; no queued/optimistic edit (LC-AVAIL-03; `CoachUiState.Offline`).
6. 5xx: "I could not update Today's Plan. Nothing changed." with retry/keep (LC-AVAIL-04, `CoachStateNotice.razor` Failed).
7. Session expired (24 h): expired state, offer to start fresh (LC-SESS-04).
8. Unreadable stored messages: "unreadable" rows and counts; unsupported message kind: placeholder with no controls.
9. A turn whose response was lost can be recovered/polled: "Check/Recover" or "Retry" under the stream (`CoachChatPane.razor:331-350`).

**J30. Stop / cancel an in-flight turn**
1. After 12 s a "Stop" button appears (`CoachStateMachine.cs:25`).
2. Press -> `POST .../operations/{id}/cancel` or `/sessions/{id}/cancel`; composer returns to ready; "Stopped" marker (`CoachWorkspaceState.cs:1251`, LC-LIMIT-02).

**J31. End session / delete coach history (Settings and legacy menus)**
1. Settings -> Learning Coach -> "Delete coach history" -> dialog "removes your conversation with Sam and any pending suggestion; Today's Plan and logged progress are not changed" (`AppResources.resx:7621`) -> confirm.
2. `CoachWorkspace.DeleteSessionAsync()` calls the legacy `DELETE /sessions/{SessionId}` and does nothing if there is no `SessionId` (`CoachWorkspaceState.cs:1344-1350`; `Settings.razor:490-506`). The button label says "history" but the code deletes the current session id only. Whether that is the whole durable history (the sessions routes alias the conversation per `CoachEndpoints.cs:62-64`) is `unconfirmed`; durable conversations are individually deletable in J26.
3. Toast "deleted"; plan unchanged; revision audit rows remain within retention (LC-SESS-05).
4. The legacy menus also have "End session" with its own confirm (`Pages/Coach.razor`, `CoachWorkspaceOverlay.razor`).

### Legacy surfaces (keep in the inventory because they still exist in M, F, T)

**J32. Legacy workspace modal / `/coach`**
1. If the Sam overlay flag is off, or flags are not loaded, `CoachWorkspaceHost` mounts; it opens only through `?coach=<id|new>` (no visible control in code) (`CoachWorkspaceHost.razor:42-45,93-130`).
2. Wide: modal with Chat + Plan canvas split (>= 992) or tabs (< 992); narrow: `/coach` route with Chat/Plan panes, pinned composer (`CoachStateMachine.SplitCanvasMinimumViewportWidth=992`).
3. Plan pane exposes constraint chips, practice balance, revision history with Undo. This is the only place to edit constraints without typing after the first turn (J14).
4. Close never reverts and never ends the session; Back exits without discarding applied changes (LC-WEB-03/05).

### Other AI-driven features (not part of one activity)

**J33. Help assistant (HelpKit)** - MAUI iOS and Mac Catalyst heads only.
1. Learner opens nav, taps "Help" (shown only when `Plugin.Maui.HelpKit.IHelpKit` resolves) (`NavMenu.razor:40-46,78-80`).
2. HelpKit (retrieval over `src/SentenceStudio.AppLib/Resources/Raw/sentencestudio-help/*.md`, embeddings from the app's OpenAI/Foundry config) opens its own UI, 10 questions/minute, 30-day history scoped to the active profile (`src/Shared/HelpKitIntegration.cs`).
3. Not present on Android, Windows, WebApp, or in the Sam surface. It is a separate AI feature from Sam. Help content covers dashboard, settings, profile, sync, getting started, and six activities.

**J34. Today's Plan generation and narrative** (consumed by Sam; screen owned by the dashboard agent).
1. Dashboard generates or regenerates the daily plan through `IProgressService` -> `IPlanGenerator` (deterministic by default, LLM strategy optional) -> API `PlansApiClient` (`src/SentenceStudio.Shared/Services/Plans/IPlanGenerator.cs`, `PlanBuildRequest.cs`, `docs/PLAN_GENERATION_SYSTEM.md`; `docs/coach/ui-outcome-inventory-v1.md:49-50` rows 1-2).
2. Inputs: resource list, due vocabulary, practice history, `PlanConstraints` (minutes, modality allowances, skill emphasis, goal, energy, vocabulary focus ids) - Sam supplies `PlanConstraints`/`FocusVocabularyWordIds` and uses `AllowWrites=false` for previews (`PlanBuildRequest.cs:14-50`).
3. Server returns `TodaysPlanDto` with `PlanNarrativeDto` (story, focus areas, resources with selection reason, vocab insight with preview words and struggling categories), localized per request (`src/SentenceStudio.Contracts/Plans/PlanNarrativeDto.cs`).
4. Regeneration currently clears cached completion rows per the inventory row 2 risk note; Sam revisions must preserve started/completed items.

**J35. AI-generated content elsewhere** (confirm ownership with the other agents; listed only so nothing is dropped)
- Diary writing prompt and feedback generation (`ui-outcome-inventory-v1.md` row 36), transcript-to-vocabulary generation (row 29), example sentence generation (row 21), content import classification (row 27), channel transcript polish (row 56), feedback issue draft preview (row 60), and AI grading in Translation/Writing/Scene/WordAssociation/Conversation activities (rows 43-49). None of these route through Sam.

### Operator-only journeys (mark as dev-only)

**J36. Operator reviews the opportunity ledger (Development only)**
1. Operator signs in with an account explicitly in `Coach:AllowedUserProfileIds` (`__dev_all__` does NOT open this surface) and opens `/operator/sam-opportunities` (WebApp Development only; standard not-found elsewhere) (`SamOpportunities.razor:26`, `CoachOpportunityOperatorEndpoints.cs:67` 404 outside Development).
2. Rollup view (counts by capability code and kind) or Rows view; select a row; detail drawer shows closed codes only.
3. Review the row (status, reviewer note code, linked spec path) -> server returns a paste-ready markdown block with no learner text and writes no file (`CoachOpportunityMarkdown.cs`).
4. Optionally press "Reveal learner content": explicit acknowledgement literal; durable key ring required; owner-checked; reveal count incremented (SAM-OPP-16..20).
5. Capture rules: what is and is not recorded (refused destructive requests are aggregate-only; injection attempts record nothing; out-of-the-blue "yes" records nothing) (SAM-OPP-01..14).

**J37. Weekly digest**
1. Scheduled GitHub workflow (Mondays 13:00 UTC) or `scripts/sam-opportunity-digest.sh` reads the production DB read-only and emits content-free markdown/NDJSON; skips if `COACH_DIGEST_CONNECTION_STRING` is unset (`.github/workflows/sam-opportunity-digest.yml`).
2. Human commits accepted items into `docs/sam-future-opportunities.md` (process steps 1-6 there).

## 5. Account, owner scoping, and privacy

- Owner comes only from the authenticated `user_profile_id` claim; missing identity returns 401/404, never a broader query (`CoachSessionService.cs:309`; LC-AVAIL-05; Coach write failure kinds map Unauthorized to 404).
- Cross-user access to session/conversation/operation returns 404 for every route; UI does not distinguish "gone" from "not yours" (LC-AVAIL-05c, SAM-ACCT-14/15). Write-ledger refusals use indistinguishable messages ("No such pending change for this learner").
- Coach tables are server-only and encrypted with Data Protection; an ephemeral key ring disables evidence reveal (`CoachKeyRingPlan.cs`, SAM-OPP-19).
- Telemetry allow-list excludes learner text, user ids, persona names (`CoachTelemetryTags.cs`, LC-TEL-01..03, LC-PER-05).
- Opportunity ledger is content-free, cascades on account deletion, retention 180 days spares reviewed decisions (SAM-OPP-24/25).
- Learning Value Gate applies to any plan emitted by Sam: no dead-language states, due-review minimum kept, low energy cannot lower difficulty floor, focus must earn its place (`learning-coach.md:2359-2460` LC-LVG).
- `docs/coach/ui-outcome-inventory-v1.md` rows 62-66 classify the Coach management actions: coach turn (Internal), conversation manage (Capability, Server, OnlineOnly), memory manage (Capability), report (Capability), operation lifecycle (Internal), and rows 34-36, 14, 68, 69, 70 deny diary, account export, account delete, debug-health, operator evidence to Sam. Blockers 14, 16, 50, 55, 57, 58, 59, 68 prevent Coach exposure of those capabilities until repaired.

## 6. Offline, error, and empty states (consolidated)

| State | Behavior | Evidence |
|---|---|---|
| Coach disabled or outside cohort | Availability endpoint answers `IsAvailable=false`. The endpoint maps both to `State=Disabled` (unavailable result -> Disabled) even though the contract and E2E LC-AVAIL-02 name `OutsideCohort`. All other `/api/v1/coach/*` return 404. No FAB; legacy host stays inert. | `CoachSessionService.cs:309-318`, `CoachEndpoints.cs:73-87`, `learning-coach.md:1002-1007` (discrepancy) |
| Availability call fails or times out | `CoachFeatureFlags` caches Disabled, hides Sam until the next availability read (the 2026-08-22 incident hid Sam this way) | `CoachFeatureFlags.cs`, `docs/incidents/2026-08-22-sam-missing-and-recovery-rca.md` section 1-2 |
| Offline client | Offline card, "nothing changed", Try again; never queued | `CoachStateNotice.razor`, LC-AVAIL-03; policy 10 says native clients must also preflight local dirty/sync state before a turn (unconfirmed implemented) |
| No plan for today | Sam still answers language questions; plan chips hidden; neutral "no plan" row; plan canvas shows "no plan yet" | `CoachChatPane.razor:422-428`, `CoachPlanCanvas.razor` |
| Empty conversation list | "No conversations" + hint | `CoachConversationList.razor` |
| Durable history off | Conversation toggle absent, reports not offered (session-only turns have no durable message identity), "history hidden" on reload | SAM-CONV-08, SAM-RPT preconditions |
| Closed conversation | Notice, no new turns | `CoachChatPane.razor:353-359` |
| Memory unavailable | Paused text in memory panel | `CoachMemoryPanel.razor` |
| Write surface unavailable | Write card renders nothing at all (not disabled buttons) | `learning-coach.md:3298-3304` |
| Unknown enum values from newer server | Wire tolerance: safe zero or sentinel per enum; unknown message kind -> placeholder with no controls | `docs/coach-client-wire-tolerance.md`, `src/SentenceStudio.Contracts/Wire/*` |

## 7. Server APIs and background workers

APIs: section 3.1. Services: `CoachSessionService` (4601 lines), `CoachConversationService` (2618), `CoachWriteOperationService` (2172), `CoachWriteAuthority`, `CoachAgentTurnRunner`, `BaselineLearningCoach` / `HarnessLearningCoach` (arm chosen by `Coach:Implementation` baseline|harness), tool registry, claim-grounding rule engine (`Validation/Claims/*`, `Coach:Grounding:Stage`), opportunity recorder and mappers (`Opportunities/*`), report service, memory service/store, data deletion service.

Background work (inside the API process, not `SentenceStudio.Workers`):

- `CoachCleanupBackgroundService` with lease (`src/SentenceStudio.Api/Coach/Persistence/Cleanup/CoachCleanupBackgroundService.cs`, options `Coach:Cleanup`) - expired session and checkpoint cleanup (`CoachExpiryCleanupService.cs`).
- Revision retention sweep (30 days default, LC-SESS-06).
- `CoachOpportunityRetentionSweep` and `CoachResponseReportRetentionSweep` (180 days; `RetentionSweepEnabled`).
- Coach migrations run at API startup with their own history table `__CoachMigrationsHistory`, in a separate try/catch so a coach failure leaves Today's Plan working (`src/SentenceStudio.Api/Program.cs:262-276,518-555`).
- Startup validators fail the host when flags are inconsistent (e.g. duplicate allowlist entries: the 2026-08-22 outage) (`CoachConfigurationKeyValidator.cs`, `CoachOptionsValidator.cs:245-265`, `AppHost/CoachConfigurationReader.cs`).
- External: weekly digest workflow (S23).

## 8. Feature flags and configuration

`Coach` options (`src/SentenceStudio.Api/Coach/Runtime/CoachOptions.cs`, AppHost passthrough `src/SentenceStudio.AppHost/CoachConfigurationReader.cs:61-80`):

| Key | Default | Notes |
|---|---|---|
| `Coach:Enabled` | false | Master switch |
| `Coach:AllowedUserProfileIds[]` | empty | Cohort allowlist; `__dev_all__` admits all only in Development; empty cohort exposes nobody; max 16 entries; duplicates fail startup (`AppHost.cs:145`, RCA) |
| `Coach:Implementation` | baseline | baseline or harness |
| `Coach:DurableHistory:Enabled` | false | Required by SamOverlay |
| `Coach:Memory:Enabled` | false | Independent of history |
| `Coach:SamOverlay:Enabled` | false | Requires DurableHistory (`CoachOptionsValidator.cs:245-252`) |
| `Coach:SamReadTools:Enabled` | false | Requires SamOverlay |
| `Coach:SamWriteTools:Enabled` | false | Requires SamReadTools; availability `IsSamWriteAvailable` also needs the ledger service |
| `Coach:CorrectionState:Enabled` | false | Dispute notices |
| `Coach:Grounding:Stage` | Off | Claim-rule staging (shadow etc.) |
| `Coach:Reports:Enabled` | false in options; true in `appsettings.Development.json` and `appsettings.Production.json` | Learner response reports |
| `Coach:Opportunities:Enabled` / `:OperatorSurface:Enabled` | false; true in Development file, false in Production file | Operator ledger; surface is Development-only regardless |
| `Coach:MaxRunsPerDay` / `MaxRunsPerWeek` | 10 / 40 | Budget |
| `Coach:MaxOutputTokens` / `ReasoningEffort` / `RequestTimeoutSeconds` / `MaxIterationsPerRequest` | 16000 / minimal / 45 / 6 | |
| `Coach:SessionExpiryHours` / `RevisionRetentionDays` | 24 / 30 | |
| `Coach:DataProtection:*` | - | Key Vault key and managed identity; durable key ring required for evidence reveal |

Whether `Coach:Enabled` and the Sam flags are on in production is `unconfirmed` from code (`appsettings.Production.json` sets only Opportunities and Reports; production values are supplied by infra/AppHost environment). The 2026-08-22 RCA describes the local pilot cohort as two accounts and says "production deployment not authorized".

Client-side: no learner setting toggles Sam. The coach name and persona follow target language.

## 9. Platform availability

| Platform | State |
|---|---|
| Web (Blazor WebApp, Blazor Server interactive) | Full Sam (S1-S19). Scope of services is per circuit so account change is a new circuit. |
| MAUI Blazor Hybrid heads (iOS, Mac Catalyst, Android, Windows; temporary reference heads) | Same Razor UI via `SentenceStudio.UI`, `ICoachApiClient` registered in `src/SentenceStudio.AppLib/ServiceCollectionExtentions.cs:115`; the scope persists for the process lifetime, which is why `CoachAccountBoundary` exists and native re-runs are required (`learning-coach.md:3331-3345`). HelpKit (J33) only on iOS and Mac Catalyst. |
| Native SwiftUI (iOS/macOS) and Jetpack Compose (Android) on T | No Sam implementation. See section 10. |
| Operator surfaces | WebApp Development only. |

## 10. Target branch (T) state for the rebuild

- No coach or Sam UI, presenter, adapter, or contract exists under `src/SentenceStudio.Native`, `src/SentenceStudio.Native.iOS`, or `src/SentenceStudio.Native.Android`. The only matches are a reused localization key `Coach_Hint_Unknown` as an onboarding error string (`src/SentenceStudio.Native.iOS/App/ApplicationController.cs:426`, `.../Features/Onboarding/OnboardingView.cs:129`); the key is a Coach resource at `AppResources.resx:8631` being used for a non-Sam message.
- `docs/native-shared-foundation.md` only mentions Sam in passing (line 30: the Sam operator `GET /export` is a different gated API; lines 404, 427, 430: Coach key ring and Coach render tests).
- `src/SentenceStudio.Native/Adapters/OwnerBoundNativeAiGateway.cs` is a native AI prompt gateway for resource/quiz generation (owner-bound), not a Sam client.
- API, Contracts, `SentenceStudio.UI` Coach code, and the WebApp operator page are still present on T (so a native client can call the same `/api/v1/coach/*` API). A native Sam client, per the policy, must run a local dirty/sync preflight before sending a turn (`coach-product-policy-v1.md:346-366`).
- Product decision note, live main only and gitignored (`.squad/decisions/inbox/` is in `.gitignore:435`), not in F, not tracked on T: `.squad/decisions/inbox/copilot-sam-is-one-system-with-coach-internals.md` (2026-09-26): Sam is one system with Coach internals; native must not build legacy Coach and Sam as two experiences; consolidate navigation, overlays, focus, state, gating into one Sam experience while preserving capabilities. This directly affects J1-J4 vs J32: the legacy workspace modal and `/coach` route (S3/S4) should collapse into the single overlay in the native rebuild, but their unique capability (plan canvas, chips after first turn, revision history, undo list) must be reachable somewhere (`unconfirmed` where).

## 11. What changed after the frozen snapshot

Frozen snapshot base = live main HEAD (`59c72262`); no Sam-relevant change exists between F and M or in MU. All changes are on T (18 commits after `59c72262`; Sam-relevant diffs in 18 files):

1. Data export retired as a Sam alternative (owner-scoping fix, because the Profile/Settings exporter was not owner-scoped):
   - `CoachDeterministicCopy.cs`: `BulkVocabularyDeletionExportSurface` removed; refusal copy changed ("We can look at smaller steps before you decide whether to remove anything").
   - `CoachLimitations.cs`: S15 limitation drops `ExportBeforeRemoving` and sets `ExportSurface = null`; alternatives now only RemoveOneListAtATime and StartAFreshList.
   - `CoachLimitationDto.cs`: `ExportSurface` kept for wire compatibility, new responses leave it null; `ExportBeforeRemoving` documented as legacy wire value.
   - `CoachLimitationCard.razor`: export block removed; alternatives filter drops `ExportBeforeRemoving`; stale export metadata cannot direct to the retired action.
   - Learner effect: refusal for "delete all my vocabulary" no longer suggests exporting first. E2E SAM-LIM-15 (`learning-coach.md:3804-3841`) still expects "Export them first" and "Take a copy first" and is therefore stale on T.
2. Operator ledger made owner-scoped (cross-tenant removed):
   - `CoachOpportunityOperatorService.cs` (138 lines changed), endpoints, contracts: queries filter by the authenticated owner before aggregation; rollup `DistinctLearners` is at most 1 and kept for wire compatibility; export is the current owner's rollup only; `CrossOwnerRefused` collapses into 404; evidence `CrossOwner` always false.
   - `CoachOpportunityOptions.cs` / `Validator.cs`: `AllowCrossOwnerEvidence` option and its validation removed.
   - `SamOpportunities.razor`, `SamOpportunityNotices.cs`, `SamOpportunityOperatorClient.cs`: operator page adjusted (94 lines in the razor page).
   - Learner effect: none. Operator effect: SAM-OPP-15 (two-learner rollup) and SAM-OPP-17 (cross-owner evidence refusal) change meaning.
3. Handler and import tweaks (`CoachVocabularyHandlers.cs`, `CoachYouTubeImportHandler.cs`, `CoachVocabularySetApplicationService.cs`, `CoachOpportunity.cs`, `CoachOpportunityFingerprint.cs`): small owner-scope and compile-alignment edits; behavior change unconfirmed (not opened beyond the diffstat).
4. `.github/workflows/sam-opportunity-digest.yml`: digest build now restores from the repository-local CoreSync feed and builds with `-p:IncludeMobileTargets=false`; the "disable localnugets ... || true" fallback no longer swallows failure.
5. Tests changed alongside (Coach limitation, opportunity isolation/rollout/postgres, report harness, operator surface), plus new platform-neutral guard and migration scripts unrelated to Sam.
6. Not changed on any branch: UI Sam overlay, panel, FAB, chat pane, composer, memory, conversation list, write cards, route catalog, availability contract, E2E reference.

## 12. Documentation vs code discrepancies and risks to carry into the rebuild

1. E2E reference says Sam has a dashboard entry point; code has only the FAB (S1) and an inert legacy host (`learning-coach.md:362-390`, vs `MainLayout.razor:83-90`).
2. E2E LC-AVAIL-02 expects `State=OutsideCohort`; the endpoint returns `State=Disabled` for both disabled and out-of-cohort (`CoachEndpoints.cs:73-87`, `CoachSessionService.cs:309-318`).
3. E2E section 35 says the limitation card is mounted nowhere; `CoachChatPane.razor:43-50` mounts it.
4. `docs/sam-future-opportunities.md` header says the preference allowlist is empty; code and E2E 31.11 list six fields.
5. `EntryPointLabel` is hard-coded English on the server and ignored by the client; `RunsRemaining*` ignored by the client; the `Limited` notice always says "tomorrow".
6. Vocabulary set card strings are not localized (J23).
7. "Delete coach history" deletes the current session id only (J31).
8. In the Sam overlay the plan canvas is not mounted; constraint editing after the first turn requires typing (J14).
9. `CoachRouteName` has 6 destinations; none is wired as navigation; a rebuild that adds links must preserve the consequence-first disclosure and the parameter allow-list.
10. Policy trajectories (house/food topical review, plan-or-review clarification, direct Vocab Review client action, deterministic latest-study with zero model calls) are specified in `coach-product-policy-v1.md` section 11 but only the vocabulary-set approval flow was confirmed in code.

## 13. Unconfirmed items

- Production values of `Coach:Enabled`, cohort, and Sam flags (not in checked-in appsettings).
- Whether the legacy host/`/coach` route has any visible entry in shipping UI (none in code); whether anyone reaches `?coach=new`.
- Whether Settings "Delete coach history" removes durable history or only the current session.
- Whether route-catalog destinations are ever turned into navigation.
- Whether the policy's plan-replace-or-review clarification and deterministic latest-study (zero model calls) are live.
- Whether the target-language memory `CorrectionTiming` changes visible UI timing (it feeds prompt context).
- Behavior changes in item 3 of section 11 (handler tweaks), and whether the digest workflow change affects the digest content.
- Android, Windows, and WebApp Help availability: HelpKit is wired only in iOS and Mac Catalyst `MauiProgram.cs`; other heads not opened for a guard.
- Where, on native, the unique legacy-host capabilities (plan canvas, revision history list) will live under the "Sam is one system" decision.
