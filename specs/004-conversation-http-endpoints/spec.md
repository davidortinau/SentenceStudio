# Feature Specification: Conversation HTTP Endpoints

**Target repo**: `~/work/SentenceStudio` (the .NET / Aspire backend)
**Suggested location**: `specs/004-conversation-http-endpoints/spec.md`
**Status**: Draft — handoff from SentenceStudioFlutter session
**Created**: 2026-05-19
**Origin**: The Flutter port (`~/work/SentenceStudioFlutter`) cannot start a Conversation activity in production because `/api/v1/conversation/{scenarios,start,continue}` return 404. The MAUI/Blazor app sidesteps the gap by consuming `IConversationAgentService` in-process via DI, so there is no existing HTTP layer to inherit. This spec asks a backend agent to expose that service over HTTP and ship the result through the normal deploy pipeline so the Flutter client can hit it.

## Why the Flutter team can't do this themselves

1. The AGENTS.md in the Flutter repo explicitly forbids modifying `~/work/SentenceStudio` from there.
2. The Azure Container App deploy story for the backend lives in this repo (`docs/deploy-runbook.md`, `azure.yaml`) and isn't owned by the Flutter port.
3. The endpoint must be live in the Azure Container App at `api.agreeablesky-76d2f81f.westus3.azurecontainerapps.io` before the Flutter release build on DX24 can verify it.

---

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Flutter learner starts a Conversation (Priority: P1)

As a learner on the Flutter app, I want to start a Conversation activity from a scenario picker so I can practice Korean with an AI partner — exactly as I can in the .NET MAUI app today.

**Why this priority**: The Conversation activity is completely broken in the Flutter release build today (user-facing error: "Could not start the conversation. Tap retry."). Every other activity works.

**Independent Test**: After deploy, calling `GET /api/v1/conversation/scenarios` with a valid auth token returns the same scenarios the MAUI app shows in its dropdown, and `POST /api/v1/conversation/start` returns a non-empty opening message.

**Acceptance Scenarios**:

1. **Given** I'm signed in on the Flutter app, **When** I open the Conversation activity, **Then** the scenario picker is populated from `GET /api/v1/conversation/scenarios`.
2. **Given** I pick a scenario and tap Start, **When** the request goes through, **Then** the first assistant bubble appears with the persona's opening line.
3. **Given** I send a Korean message, **When** `/continue` returns, **Then** I see the AI reply, an inline grammar-corrections section on my bubble (if any), and a comprehension score (0–100).

### User Story 2 - Parity with MAUI agent service (Priority: P1)

As a backend owner, I want the HTTP endpoints to be a thin wrapper around the existing `IConversationAgentService` so the Flutter client and the MAUI client share one source of truth for conversation behavior (prompts, grading, vocabulary analysis).

**Independent Test**: For a given scenario + user message, the JSON the endpoint returns is the same `Reply` object the MAUI app would receive in-process from `ContinueConversationAsync`, just serialized.

### User Story 3 - Auth & DI parity with other endpoints (Priority: P1)

As an API maintainer, I want this endpoint group to follow the conventions used by `SpeechEndpoints.cs`, `ProfileEndpoints.cs`, `PlanEndpoints.cs`, `FeedbackEndpoints.cs` (file-per-feature, `MapGroup("/api/v1/...").RequireAuthorization()`, `[FromServices]` DI, `ClaimsPrincipal` for user resolution).

---

## Required Endpoints

All endpoints are `RequireAuthorization()` and live under `MapGroup("/api/v1/conversation")`.

### `GET /api/v1/conversation/scenarios`

Returns the full list of conversation scenarios for the dropdown.

- **Source**: `ScenarioRepository` / `IScenarioService` — same data the MAUI app loads.
- **Wire shape** (PascalCase keys — see *Wire Contract* below):
  ```json
  [{
    "Id": 1,
    "Name": "...",
    "NameKorean": "...",
    "PersonaName": "...",
    "PersonaDescription": "...",
    "SituationDescription": "...",
    "ConversationType": "OpenEnded" | "Finite",
    "QuestionBank": "newline-delimited quick phrases",
    "IsPredefined": true
  }]
  ```

### `POST /api/v1/conversation/start`

Opens a new conversation against a scenario.

- **Request** (camelCase):
  ```json
  { "scenarioId": 1, "targetLanguage": "ko" }
  ```
  `scenarioId` is optional (free chat allowed). `targetLanguage` is BCP-47 — normalize to label ("ko" → "Korean") using the same map as `SpeechEndpoints.Bcp47ToLabel`.
- **Behavior**: Resolve the scenario by id (404 if not found). Call `IConversationAgentService.StartConversationAsync(scenario)`. Return the opening message.
- **Response** (camelCase):
  ```json
  {
    "firstAssistantMessage": "안녕하세요...",
    "personaName": "Mr. Kim",
    "conversationType": "OpenEnded"
  }
  ```

### `POST /api/v1/conversation/continue`

Sends one user turn and gets the AI reply + grading.

- **Request** (camelCase):
  ```json
  {
    "scenarioId": 1,
    "targetLanguage": "ko",
    "history": [
      { "role": "assistant", "author": "Mr. Kim", "text": "안녕하세요..." },
      { "role": "user", "text": "안녕하세요, 저는..." }
    ]
  }
  ```
  History is oldest → newest. The **last** entry is the user's new message (the endpoint extracts `userMessage` from it; everything before becomes `conversationHistory` for the service call).
- **Behavior**: Map `history[*]` to `List<ConversationChunk>` (Role / Author / Text / SentTime=UtcNow). Call `IConversationAgentService.ContinueConversationAsync(userMessage, conversationHistory, scenario)`. Translate `Reply` to the wire shape below.
- **Response** (camelCase):
  ```json
  {
    "assistantMessage": "...",
    "comprehensionScore": 85,
    "comprehensionNotes": "Clear and natural...",
    "grammarCorrections": [
      { "original": "...", "corrected": "...", "explanation": "..." }
    ],
    "vocabularyAnalysis": [
      { "usedForm": "...", "dictionaryForm": "...", "meaning": "...",
        "usageCorrect": true, "usageExplanation": null }
    ],
    "isComplete": false
  }
  ```

> ⚠️ **Wire conversion gotcha**: `Reply.Comprehension` is a `double` in `0.0–1.0`. The Flutter wire contract expects `comprehensionScore: int` in `0–100` (mirrors how the MAUI Conversation.razor renders it as a percentage). The endpoint MUST multiply by 100 and round to `int`. **Do not change `Reply.Comprehension`** — that breaks MAUI.

> ⚠️ **`isComplete`**: The service doesn't directly return this. Derive it as `scenario?.ConversationType == ConversationKind.Finite && /* end-of-script signal */`. If you can't derive cleanly, return `false` and file a follow-up — the Flutter UI gracefully handles `false`.

---

## Wire Contract (CANONICAL — must match exactly)

The Flutter client treats `lib/features/activities/conversation/data/conversation_dtos.dart` as the source of truth. Any drift breaks the activity silently (parse failures swallowed at the repository boundary).

| Endpoint | Key casing | Notes |
|---|---|---|
| `GET /scenarios` response | **PascalCase** | Matches existing `ConversationScenario` EF model JSON serialization the MAUI app already expects. |
| `POST /start` request | **camelCase** | `{ scenarioId, targetLanguage }`. |
| `POST /start` response | **camelCase** | `{ firstAssistantMessage, personaName, conversationType }`. |
| `POST /continue` request | **camelCase** | `{ scenarioId, targetLanguage, history[].{role,author,text} }`. |
| `POST /continue` response | **camelCase** | See above. `comprehensionScore` is integer 0–100. |

`ConversationType` wire values are the strings `"OpenEnded"` and `"Finite"` (PascalCase). Unknown values fall back to `OpenEnded` on the Flutter side.

`role` wire values on history entries are lowercase: `"user"` / `"assistant"`.

---

## Implementation Notes

1. **New file**: `src/SentenceStudio.Api/ConversationEndpoints.cs` following the `SpeechEndpoints.cs` template.
2. **Register the group** in `Program.cs` alongside the other `Map*Endpoints` calls.
3. **DI registration**: `IConversationAgentService` is currently registered only in `SentenceStudio.AppLib/ServiceCollectionExtentions.cs:26` (`AddScoped`). The API project needs the same registration plus any transitive deps the MAUI app already provides (`ConversationMemory`, the agent's AI client factory, `IScenarioService`, etc.). If those transitive deps are missing in `SentenceStudio.Api`, add them to a small `ConversationServiceCollectionExtensions.cs` keyed off the same configuration the MAUI app uses (`AI:ConversationPartner:*` etc.).
4. **DTOs**: Add request/response records to `SentenceStudio.Contracts` so the Flutter team has a paste-able C# reference if they ever want to share. Suggested names:
   - `ConversationScenarioDto` (PascalCase serialization — already exists as `ConversationScenario` EF model; you can return the entity directly if its JSON shape already matches, but a thin DTO is safer against future EF changes).
   - `ConversationStartRequest`, `ConversationStartResponse`.
   - `ConversationContinueRequest`, `ConversationContinueResponse`.
   - `ConversationHistoryItem` (`Role`, `Author`, `Text`).
   - `GrammarCorrectionDto` already exists — reuse it.
   - `VocabularyAnalysisDto` — mirror the existing `VocabularyAnalysis` shape but with camelCase JSON property names.
5. **Auth**: Resolve `UserProfileId` from claims the same way `SpeechEndpoints.GetVoices` does. Return 401 if missing.
6. **Logging**: Use `ILoggerFactory.CreateLogger("ConversationEndpoints")` — match the existing pattern.
7. **Language normalization**: Lift `Bcp47ToLabel` from `SpeechEndpoints.cs` to a shared helper (or duplicate locally — your call; not blocking).
8. **Persistence**: This endpoint is **stateless** — do NOT call `LoadMemoryStateAsync` / `SaveMemoryStateAsync`. The Flutter client passes full history on every `/continue`. (The MAUI app uses the stateful memory APIs because it runs in-process; HTTP can't share that memory across requests without sessionization, which is out of scope.)

---

## Acceptance Tests

Add to `tests/` following the existing API test conventions (whatever pattern `SpeechEndpoints` / `ProfileEndpoints` already use).

1. `GET /api/v1/conversation/scenarios` (authed) returns 200 with at least one scenario when the DB is seeded.
2. `GET /api/v1/conversation/scenarios` (unauthed) returns 401.
3. `POST /api/v1/conversation/start` with valid `scenarioId` returns a non-empty `firstAssistantMessage`.
4. `POST /api/v1/conversation/start` with unknown `scenarioId` returns 404.
5. `POST /api/v1/conversation/continue` with a one-turn history returns a `ConversationContinueResponse` whose `comprehensionScore` is an integer in `[0, 100]`.
6. Wire-shape integration test: serialize a `ConversationContinueResponse` and assert the JSON contains the exact keys listed in *Wire Contract* (camelCase, no PascalCase keys leak).

---

## Deploy Verification

Per `docs/deploy-runbook.md`, after the endpoint lands in main and deploys to Azure Container Apps:

1. Hit `GET https://api.agreeablesky-76d2f81f.westus3.azurecontainerapps.io/api/v1/conversation/scenarios` with a valid bearer token — expect 200 + non-empty list.
2. Notify the Flutter team (or the `davidortinau/dx24-release-deploy` session) that the endpoint is live, so they can:
   - Remove the `LD-15 API_USE_FIXTURES` escape hatch comment in `lib/features/activities/conversation/data/conversation_repository.dart:13`.
   - Rebuild the DX24 release and verify end-to-end against production.

---

## Out of Scope (deferred)

- Per-bubble TTS Listen button → Flutter-side work, reuses `/api/v1/speech/synthesize`.
- Activity timer banner for plan-launched conversations → needs `ResourceId`/`SkillId`/`PlanItemId` query params; defer.
- Vocabulary progress scoring write-back (MAUI `ExtractAndScoreVocabularyAsync` with 1.2× weight, 0.8× penalty) → either a new `POST /api/v1/vocabulary/score-conversation-turn` endpoint or Flutter-side port of the scoring logic; defer until the basic flow works.
- Sessionized memory (`LoadMemoryStateAsync` / `SaveMemoryStateAsync`) over HTTP → would require a `conversationId` on every turn; explicitly out of scope per Implementation Note 8.

---

## Handoff Checklist for the Backend Agent

- [ ] Read `docs/ConversationAgentFramework.md` for service-internal expectations.
- [ ] Read `docs/deploy-runbook.md` end-to-end (mandatory pre-deploy steps).
- [ ] Confirm `IConversationAgentService` can be resolved from `SentenceStudio.Api`'s DI without dragging in AppLib (or import what's needed).
- [ ] Implement the three endpoints + DTOs + auth + logging.
- [ ] Add the acceptance tests above.
- [ ] Run the deploy runbook's pre-deploy safety checklist.
- [ ] Deploy to Azure Container Apps.
- [ ] Smoke-test the three URLs against the production host.
- [ ] Comment on the originating PR in `SentenceStudioFlutter` (`davidortinau/dx24-release-deploy` branch) that the backend is live.
