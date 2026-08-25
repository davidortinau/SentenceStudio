# Coach client wire tolerance (foundation W1)

**Owner:** Zoe (contracts / client compatibility)
**Status:** implemented — seam only, no behaviour promotion
**Scope:** `SentenceStudio.Contracts.Coach`, `SentenceStudio.Contracts.LearnerMemory`,
`SentenceStudio.Contracts.AppOperation` (reserved, currently empty)

---

## 1. The failure this prevents

Before this change, every coach enum was serialized with a type-level
`[JsonConverter(typeof(JsonStringEnumConverter))]`, which **throws** on a value it cannot name. The
client's read path funnelled through `ReadFromJsonAsync<T>()` with default options, so the moment a
newer server appended a member to any coach enum, an already-installed app threw
`JsonException` inside the deserializer — before any client code ran.

The visible symptom is not "one card is missing". It is:

- a turn that fails with the learner's own message stuck in `Pending`;
- a history page that will not load at all, so the whole conversation looks empty;
- an availability probe that throws, so the entry point disappears.

Nothing in a normal review catches it, because the diff that breaks the client contains only a new
enum member, and the client contains no code at all.

## 2. The two defences, and why both exist

| Layer | What it does | Where it lives |
|---|---|---|
| **Client-adoption gate** (primary) | Server withholds a new enum value from clients older than a stated wire revision, sending a documented downgrade instead. | `WireValueGate` / `WireValueGateRegistry` — **registry is empty today** |
| **Tolerant converter** (fail-safe) | Client reads an unknown value as a declared fallback member instead of throwing. | `TolerantWireEnumConverterFactory`, installed in `WireJson.Client` |

Either alone leaves a hole. A gate that is forgotten sends the value anyway. Tolerance alone means
every skew degrades to an unavailable card even when the server could have avoided it. Together, the
common case is correct and the uncommon case is survivable.

## 3. Where tolerance is installed — and where it deliberately is not

`System.Text.Json` resolves converters in this order:

1. `[JsonConverter]` on a **property**
2. a converter in **`JsonSerializerOptions.Converters`**
3. `[JsonConverter]` on the **type**

Because (2) beats (3), adding `TolerantWireEnumConverterFactory` to one options instance makes one
client tolerant and changes nothing else. **No enum declaration was made tolerant.** That matters,
because the same enums are parsed in three places that must stay strict:

- **Model output.** The coach parses `SentenceStudio.Contracts.Coach.Intent` shapes out of
  structured model responses. A model inventing `"DeletePlan"` has to be refused, not read as "no
  change". The architecture test asserts no client DTO references an intent enum, so client
  tolerance cannot leak into this path.
- **API request binding.** An unrecognised value in a learner's request body is still a bad request.
- **Entity Framework.** Several of these enums are stored as ordinals
  (`HasConversion<int>()`) and never touch `System.Text.Json` at all. **No persisted enum handling
  was changed. There is no migration.**

`CoachEnumContractTests.An_unknown_enum_name_is_refused` (strict) and
`CoachWireToleranceTests` (tolerant) both pass, and
`CoachWireEnumPolicyTests.Client_tolerance_does_not_loosen_the_strict_parsers` asserts the
coexistence explicitly.

## 4. The policy

Every enum reachable from a shipped Coach wire DTO declares, on the type, what an unrecognised value
degrades to:

```csharp
[WireEnumFallback(nameof(CoachWriteStatus.Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown already means 'honest unavailable card, never an action'. ...")]
```

The attribute is **inert metadata**. It registers nothing, changes no ordinal, and is invisible to
EF and to the strict serializers. It is read only by the tolerant converter and by the tests.

### Fallback categories

| Category | Meaning |
|---|---|
| `SafeZero` | The zero member is already the documented fail-closed value (`Unknown`, `None`, `Disabled`, `Expired`, `Failed`, `NoChange`, `NotApplicable`, `Unchanged`, `Candidate`, `Unspecified`). Unknown lands where unset already lands. |
| `NeutralMember` | Zero is meaningful; a **different existing** member is the honest neutral landing spot (`Other`, `Unreadable`, `Note`, `Count`, `AlreadyReported`). |
| `DeliberateNeutral` | Zero is meaningful and no member is neutral, but collapsing is safe because the value drives no control, no write, and no learner-visible label of its own. The rationale must say why. Paired with the version gate. |
| `AppendedSentinel` | The client must be able to **tell** the value is unknown to render honestly. A sentinel is **appended**, never inserted, so stored ordinals keep their meaning. |

### Why not `Unknown = 0` everywhere

Because most of these enums have a **meaningful zero**, and several are stored as ordinals.
`CoachMessage.Kind` is `HasConversion<int>()`, so adding `Unknown = 0` to `CoachMessageKind` would
have silently relabelled every stored `Text` row. Appending is the only ordinal-safe shape, and it
was used for exactly one enum.

---

## 5. Enum audit

**Reachable from shipped Coach wire DTOs — 31 enums, all annotated.** Enforced by
`CoachWireEnumPolicyTests` (namespace walk) and `CoachClientWireSurfaceTests` (walk from
`ICoachApiClient`'s own signatures).

### `SentenceStudio.Contracts.Coach`

| Enum | Zero member | Fallback | Category | Why it is safe |
|---|---|---|---|---|
| `CoachAvailabilityState` | `Disabled` | `Disabled` | SafeZero | Documented unset value; never opens an entry point. |
| `CoachSessionStatus` | `Expired` | `Expired` | SafeZero | Never accepts a turn. |
| `CoachTurnStatus` | `Failed` | `Failed` | SafeZero | Never renders as success. |
| `CoachStopReason` | `Failed` | `Failed` | SafeZero | Never claims the turn finished as planned. Client-side only — the stored ordinal is untouched. |
| `CoachTurnInputKind` | `Text` | `Text` | DeliberateNeutral | Request-side only; least-privileged member (no chip identity, no constraint action). |
| `CoachMessageRole` | `Coach` | `Coach` | DeliberateNeutral | Server-authored content must never be attributed to the learner. |
| **`CoachMessageKind`** | `Text` | **`Unrecognized` (appended)** | **AppendedSentinel** | The one enum where the client must *tell*. `Text` would print a proposal or consent prompt as prose, stripped of its controls. Appended because `CoachMessage.Kind` is a stored ordinal. |
| `CoachConstraintField` | `AvailableMinutes` | `AvailableMinutes` | DeliberateNeutral | Client never renders the field name; receipts carry server-localized copy. A sentinel would reach the server telemetry tag map and deterministic receipt copy. |
| `CoachSkillEmphasis` | `Listening` | `Listening` | DeliberateNeutral | Advisory display state; changes no plan and triggers no write. Plan items are titled from server copy. |
| `CoachEnergyLevel` | `Normal` | `Normal` | DeliberateNeutral | `Normal` is the no-op; `Low` can shorten a session. Fail toward no change. |
| `CoachPlanItemChangeKind` | `Unchanged` | `Unchanged` | SafeZero | Renders no change marker at all. |
| `CoachRevisionSource` | `DirectRequest` | `DirectRequest` | DeliberateNeutral | Captions a history row whose text the server localized; gates no control. Least presumptuous of the three. |
| `CoachEvidenceKind` | `PracticeBalance` | `PracticeBalance` | DeliberateNeutral | `CoachEvidenceDto` carries server-localized `Label`/`Summary`; the enum is a grouping key in a read-only panel. |
| `CoachEvidenceUnit` | `Minutes` | **`Count`** | NeutralMember | `Count` is unitless — asserts a quantity without asserting of what. `Minutes` would print "5 minutes" beside a number that might be attempts. |
| `CoachPlanActivityType` | `VocabularyReview` | `VocabularyReview` | DeliberateNeutral | Picks an icon and a route; the row's title and minutes come from server copy. A sentinel would break the parity contract with Today's Plan activity names. |
| `CoachAnswerTopic` | `Vocabulary` | **`Other`** | NeutralMember | `Other` is literally "fits none of the above". |
| `CoachAnswerBlockKind` | `Answer` | **`Note`** | NeutralMember | `Note` is "a short aside". `Answer` is the worst landing spot — it is rendered unlabelled in the lead position, so an unknown block would be promoted into the direct answer. |
| `CoachLanguageRole` | `Display` | `Display` | DeliberateNeutral | Inherits the answer's display tag, matching what `CoachAnswer` already does for an unresolved span. `Target` would switch a screen-reader voice. |
| `CoachConversationTitleOrigin` | `Generated` | **`Unreadable`** | NeutralMember | `Unreadable` already means "render a placeholder". `Generated` is "safe to replace silently" — collapsing there lets the client overwrite a learner-typed title. |
| `CoachTurnOperationState` | `Failed` | `Failed` | SafeZero | Keeps the learner's message, claims no result. `Completed` would read an absent `Result`; `Running` would poll forever. |
| `CoachExportFormat` | `Json` | `Json` | DeliberateNeutral | Request-side only; the lossless format. |
| `CoachVocabularyFocusStatus` | `Unchanged` | `Unchanged` | SafeZero | "The change did not touch the focus" — no claim about the word list. |
| `CoachResponseReportReason` | `DidNotAnswer` | **`Other`** | NeutralMember | Never attributes a specific complaint to a learner who did not make it. |
| `CoachResponseReportState` | `Recorded` | **`AlreadyReported`** | NeutralMember | The no-op reading; suppresses a second submit. `Recorded` would claim this request wrote something. |
| `CoachWriteRiskClass` | `Unknown` | `Unknown` | SafeZero | Already means "render no approval control". |
| `CoachWriteStatus` | `Unknown` | `Unknown` | SafeZero | Already means "honest unavailable card, never an action". |
| `CoachWriteChangeKind` | `Unknown` | `Unknown` | SafeZero | Already resolves to the generic heading in `SamWritePresentation`. |
| `CoachWriteTargetKind` | `None` | `None` | SafeZero | Never claims to point at a row. |

### `SentenceStudio.Contracts.LearnerMemory`

| Enum | Zero member | Fallback | Category | Why it is safe |
|---|---|---|---|---|
| `CoachMemoryKind` | `PersistentStudyGoal` | `PersistentStudyGoal` | DeliberateNeutral | No `Other` bucket by design; ordinal is persisted through the memory store. The card shows the learner's own `DisplayText`; a mismatched branch is already refused, and an edit echoing the collapsed kind is rejected server-side (kind changes are refused outright). |
| `CoachMemoryStatus` | `Candidate` | `Candidate` | SafeZero | Documented as never entering a prompt. |
| `CoachMemoryProvenance` | `UserExplicit` | `UserExplicit` | SafeZero | The weaker claim. `UserConfirmed` asserts an approval that must never be invented. |
| `CoachMemoryScope` | `TargetLanguage` | `TargetLanguage` | SafeZero | `Global` is documented as "must be chosen explicitly, never inferred". |
| `CoachMemoryExplanationDepth` | `Concise` | `Concise` | SafeZero | Minimal reading; never shows the learner asking for more than they did. |
| `CoachMemoryCorrectionTiming` | `Immediate` | `Immediate` | SafeZero | Display-only; neither member is unsafe. |
| `CoachMemoryExampleRegister` | `NeutralPolite` | `NeutralPolite` | SafeZero | The neutral register by name and definition. |
| `CoachMemoryTurnCategory` | `Unspecified` | `Unspecified` | SafeZero | "Nothing is known about the turn; only the safest kinds are considered." |

### Deliberately **not** annotated

| Enum | Why |
|---|---|
| `CoachMemoryValueRejection` | Server-internal validation result; never appears on a client DTO (a refusal comes back as an RFC 7807 problem). The only zero member is `None`, which says the value was *accepted* — the one claim a client must never make on a refusal. If it ever reaches a wire DTO, `CoachWireEnumPolicyTests` fails and the decision gets made deliberately. |
| `CoachIntentKind`, `CoachAcceptanceState`, `CoachProposed*` (5) | `SentenceStudio.Contracts.Coach.Intent` — model-facing, must stay strict. `CoachWireEnumPolicyTests.The_model_facing_intent_namespace_is_excluded_and_unreachable` asserts no client DTO drags one into the wire graph. |
| `ThemeMode`, `ThemeModeBehavior`, `Feedback*` | Different wire surfaces, out of W1's scope. Not touched. |

---

## 6. Client-side JSON audit

Every read/write on a Coach path now goes through `WireJson.Client`
(`JsonSerializerDefaults.Web` + `TolerantWireEnumConverterFactory` — identical property naming and
number handling to the `System.Net.Http.Json` defaults it replaces, plus enum tolerance).

| Location | Before | After |
|---|---|---|
| `CoachApiClient.ReadAsync<T>` (single read funnel — every response) | `ReadFromJsonAsync<T>(cancellationToken:)` | `ReadFromJsonAsync<T>(WireJson.Client, ...)` |
| `CoachApiClient` — 5 × `PostAsJsonAsync`, 1 × `PutAsJsonAsync` | no options | `WireJson.Client` |
| `CoachApiClient` — 3 × `JsonContent.Create` | no options | `options: WireJson.Client` |

Audited and **correctly left alone**:

| Location | Finding |
|---|---|
| `SamOpportunityOperatorClient` (WebApp, `/api/v1/coach/operator/...`) | Coach route, but its DTOs type `Kind`/`Disposition`/`OfferLink` as **strings**. No wire enum crosses it, so it is already tolerant by construction. |
| `IdentityAuthService`, `AiApiClient`, `PlansApiClient`, `SpeechApiClient`, `FeedbackApiClient`, `AiGatewayClient` | Not Coach paths. Out of W1 scope; unchanged. |
| `WebPreferencesService`, `ElevenLabsSpeechService` | Local storage / cache, not the API wire. No Coach DTO is persisted client-side (the only Coach preference is a `bool`). |
| MAUI heads (iOS / Android / MacCatalyst / MacOS / Windows) | No direct JSON call on a Coach path; all coach traffic goes through `CoachApiClient`. |

## 7. Neutral rendering for unknown message kinds

`CoachMessageKind.Unrecognized` → `CoachTimelineKind.UnsupportedMessage` →
a dimmed placeholder in `CoachChatPane` with **no text, no Copy, no report flag, no actions**.

- `CoachTimelineEntry.KindFor(message)` is the single mapping, used by both the live-turn and the
  durable-history construction sites. It checks unsupported **before** role, so the placeholder wins
  regardless of which side of the thread the bubble would have been on.
- **The placeholder still names who wrote it.** Withholding the content is not licence to
  misattribute it, and the first cut captioned every unsupported message with the persona's name —
  so a learner's own message appeared over Sam's. `KindFor` deliberately erases the *slot*
  distinction, so the pane reads the role off the message instead (`IsLearnerAuthored`). That is
  sound because only `Kind` degraded: `CoachMessageRole` has its own `DeliberateNeutral` fallback to
  `Coach`, so a message whose author this build cannot name is attributed to the server rather than
  having words put in the learner's mouth. Pinned by `CoachUnsupportedMessageRenderTests`.
- The slot is kept rather than dropped: a transcript that quietly omits a turn rewrites what
  happened.
- Distinct from `UnreadableMessage`, which means the stored payload failed to decode. Different
  facts, different copy: `Coach_UnsupportedMessage` = "This message needs a newer version of the
  app." (en + ko added).
- `IsConversational` is false for the new kind, so the existing positive-match guards
  (`== CoachMessage` / `== LearnerMessage`) already exclude it from evidence attachment, answer
  pairing, write-card anchoring, copy, and reporting. `CoachResponseReportability.IsReportableKind`
  gained an explicit `Unrecognized => false` arm.

**No new action-card capability was added.** This is the compatibility seam only.

## 8. Client-adoption / version gate seam

| Piece | Value today |
|---|---|
| `WireProtocolVersion.Current` | `1` — the tolerance foundation |
| `WireProtocolVersion.Unknown` | `0` — assumed for a request with no header, so a gate protects exactly the clients it was built for |
| `WireHeaders.ClientProtocolVersion` | `X-SentenceStudio-Wire-Version`, sent by `CoachApiClient` on **every** request |
| `WireValueGateRegistry.All` | **empty** — no value is gated, `Project(...)` is identity |

**How the server will suppress a new enum value.** Bump `WireProtocolVersion.Current`, add one entry
to `WireValueGateRegistry.All` naming the enum, the member, the minimum client revision, and an
**honest** downgrade member, then call `WireValueGateRegistry.Project(value, clientVersion)` in the
projection that builds the DTO — with `clientVersion` read from the request header via
`WireValueGateRegistry.ParseClientProtocolVersion`. Registering the first entry is the moment the
server starts making per-client decisions, and it is a reviewable change to one list rather than a
condition buried in a projection.

**Nothing is promoted now.** No new enum member is emitted by the server, and the server does not
yet read the header. The client half ships first because a gate can only hold a value back from
clients that announced themselves.

## 9. Tests

| File | Project | What it holds |
|---|---|---|
| `Coach/CoachWireEnumPolicyTests.cs` | UnitTests (net10.0) | 11 tests. Reachability walk over the client wire namespaces; every reachable enum annotated, fallback names a real member, rationale is substantive, converter covers plain + nullable, appended sentinels really are last, AppOperation namespace in the policy while empty, intent namespace excluded **and** unreachable, strict parsers unchanged, canonical-name writes, `JsonStringEnumConverter` attribute retained. Also asserts no reachable enum has two members differing only in case — the converter's name lookup is case-insensitive and built in a static initializer, so a collision would surface as a `TypeInitializationException` on first parse. Carries an empty, documented `DocumentedExceptions` map so an exception is a reviewable entry rather than a missing annotation. |
| `Coach/CoachWireToleranceTests.cs` | UnitTests (net10.0) | 35 tests. Old-client simulation across turn / history / write / receipt / operation / availability / answer / memory DTOs; unknown values never actionable and never "applied"; known values round-trip; casing tolerated; numeric ordinals read; explicit null degrades; nullable keeps null distinct; **malformed JSON still fails**; structurally wrong enum position still fails; wrongly typed neighbour still fails; gate registry empty and version parsing fails safe. |
| `Coach/CoachWireToleranceClientTests.cs` | UI.Tests (net11.0) | 13 tests. Real `CoachApiClient` over a stub handler: unknown kinds survive the transport, malformed body still throws, the wire-version header is on every request; `KindFor` mapping for unsupported/known messages; unsupported entry is not conversational; unsupported ≠ unreadable. |
| `Coach/CoachUnsupportedMessageRenderTests.cs` | UI.Tests (net11.0) | 15 tests. The same slot, rendered to HTML. Four withholdings — the original text, Copy, the report flag, and any action affordance — and one obligation: a truthful role label. Every negative assertion is paired with a positive control in the same thread, because "no Copy button on screen" passes trivially if the pane rendered nothing. Added in the W1 follow-up alongside the fix below. |
| `Coach/CoachClientWireSurfaceTests.cs` | UI.Tests (net11.0) | 3 tests. Walks `ICoachApiClient`'s own signatures — closes the gap where a parsed type lives outside the policy namespaces. Fails when somebody adds a *method*, where the namespace test fails when somebody adds an *enum*. |

Existing `CoachEnumContractTests.An_unknown_enum_name_is_refused` was left intact and given a
cross-reference explaining why strictness and tolerance are complements, not a contradiction.
`CoachResourceStringTests` gained one test pinning the unsupported-message copy: it must differ from
the unreadable-message copy, name the remedy, and not be phrased as a question (the placeholder
carries no controls, so a question would ask something the learner cannot answer there).

## 10. Results

**What these numbers are.** Each row is a reading taken at a stated moment, not a standing
guarantee. This worktree carries several workstreams at once, so the shared suites move under
concurrent edits: a total recorded here can be correct when written and wrong an hour later without
anything in W1 having changed. Read the "added by W1" column as the durable claim and the totals as
context.

### W1 review pass

| Suite / build | Reading |
|---|---|
| `SentenceStudio.UnitTests` | 1703 passed, 0 failed — 47 added by W1 (11 policy + 35 compatibility + 1 resource copy), counting each `InlineData` case |
| `SentenceStudio.UI.Tests` | 1820 passed, 0 failed — 16 added by W1 (13 client + 3 surface) |
| `SentenceStudio.Contracts` / `.AppLib` / `.UI` / `.WebApp` / `.Api` builds | succeeded |

### W1 follow-up pass (2026-08-21)

Three review follow-ups: the truthful role label on the unsupported placeholder, the render test
that pins it, and this correction.

| Suite / build | Reading |
|---|---|
| `SentenceStudio.UnitTests` | 1703 passed, 0 failed, 0 skipped |
| `SentenceStudio.UI.Tests` | 1861 passed, 0 failed, 0 skipped — 15 added by this pass (`CoachUnsupportedMessageRenderTests`) |
| `SentenceStudio.UI.Tests`, filtered to the new file | 15 passed, 0 failed |
| `SentenceStudio.Api.Tests` | see below — **not a W1 claim** |
| `SentenceStudio.MacOS` (net11.0-macos) | **blocked, cause identified, not W1's** — see below |

The UI total moved from 1820 to 1861 across the two passes while W1 added 15 tests. The difference
is other workstreams landing in the same worktree, which is the reason this section no longer
presents a bare total as a result.

### The API suite is not W1's to certify

The earlier revision of this section recorded `SentenceStudio.Api.Tests` as **2972 passed, 0 failed,
261 skipped** with no qualification, which read as "the API suite is green because of this work". It
should not have. Two things are wrong with that claim:

- **W1 changed no API code.** This workstream is client-side wire tolerance: contracts annotations,
  a tolerant converter, the client transport, and the chat pane. The API suite passing or failing
  says nothing about it either way, and it was never a gate on this work.
- **The suite is shared with W2, which is in flight.** A re-run on 2026-08-21 read **2991 passed, 0
  failed, 269 skipped** — a different total and a different skip count from the recorded one,
  without W1 touching anything. Recording a moving number as a settled result is how a doc starts
  vouching for work it has not seen.

Note also that "0 failed" there covers 269 tests that did not execute: the API suite skips its
PostgreSQL- and network-gated cases in this environment. Whoever owns W2 should state the API
result, from a run that includes those.

### The macOS head — restore solved, one pre-existing compile error remains

The earlier revision said the head was "not run" because `NU1301` made restore impossible. That is
now only half right, and the half that changed is worth recording.

**Restore is solvable offline.** `nuget.org` is unreachable from this environment (a plain `curl` of
the service index returns nothing), and because `NuGet.config` maps `*` to it, NuGet fails the whole
restore at service-index resolution before it works out which packages it actually needs. Pointing
restore at the two local sources instead completes it with no errors:

```
dotnet restore src/SentenceStudio.MacOS/SentenceStudio.MacOS.csproj \
  --source "$(pwd)/lib/coresync" --source /Users/davidortinau/work/LocalNuGets
dotnet build   src/SentenceStudio.MacOS/SentenceStudio.MacOS.csproj -f net11.0-macos --no-restore
```

> Side effect worth knowing: this rewrites `project.assets.json` for every project in the graph with
> the restricted source list, so sibling projects then re-restore and hit `NU1301` in turn. Restore
> the test projects the same way, or restore normally once the network is back.

**What the build then reports** is a single error, and it is not this workstream's:

```
src/SentenceStudio.MacOS/MacOSBlazorApp.cs(7,31):
error CS0118: 'Application' is a namespace but is used like a type
```

`MacOSBlazorApp.cs` is unchanged from `HEAD`. The collision is between
`Microsoft.Maui.Controls.Application` and a namespace arriving from the
`Microsoft.Maui.Platforms.MacOS.*` packages that the head's own `.csproj` selects — and that
`.csproj`, along with `Main.cs`, `MauiMacOSApp.cs` and a new `Platform/` folder, is being edited by
another workstream in this worktree. Reverting W1's only change reachable from the head
(`CoachChatPane.razor`) and rebuilding produces the identical single error, so the head's failure is
independent of this work. It still needs fixing by whoever owns the macOS head before a release
build; it is not a wire-tolerance defect.

No migration. No persisted enum handling changed. No theme, feedback, or tool-query implementation
file touched.
