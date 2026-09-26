# Legacy Coach runtime growth freeze

## Status

The current Coach runtime is frozen while its clean replacement is built beside it. Existing
runtime behavior remains unchanged. The legacy runtime may receive only critical containment fixes:
changes required to stop or reduce an active production correctness, security, privacy, durability,
or availability failure. Read/decrypt compatibility and bounded settlement of operations created
before cutover may also be allowed when required by the replacement migration, but only after the
change is explicitly classified and approved as compatibility rather than legacy feature growth.

The freeze does not authorize a production feature flag, a new capability, a new mutation, or a new
architecture seam in the legacy runtime. New product work belongs in the replacement architecture.
No new mutation route or route alias may be added anywhere in host composition, including reporting,
review, or evidence-reveal verbs that change state.
`Sam` and `쌤` remain legacy localized product values only; new technical implementation names use
Coach or neutral Application terminology.

## Frozen architecture contracts

The contract test at
`tests/SentenceStudio.Api.Tests/Coach/Architecture/LegacyCoachGrowthFreezeTests.cs` pins meaningful
topology rather than implementation text, private methods, line counts, or whole-source hashes.
Harmless refactoring remains possible as long as the observable topology below does not grow.

### Registered tool and write-dispatch surface

The frozen registry contains **27 tools**: **15 reads** and **12 proposal-shaped writes**.
`CoachToolNames.AllRegistered` and `CoachToolNames.AllWrite` show the current registration-order
lists (`src/SentenceStudio.Api/Coach/Tools/CoachToolNames.cs:106-153`), while
`CoachToolRegistry` constructs the runtime registry
(`src/SentenceStudio.Api/Coach/Tools/CoachToolRegistry.cs:56-69`).

The 12 write tool names are:

- `propose_vocabulary_entry`
- `propose_vocabulary_edit`
- `propose_vocabulary_link`
- `propose_vocabulary_removal`
- `propose_skill_entry`
- `propose_skill_edit`
- `propose_skill_archive`
- `propose_resource_entry`
- `propose_resource_edit`
- `propose_resource_removal`
- `propose_preference_change`
- `propose_youtube_import`

The matching **12 `ICoachWriteHandler.ToolName` keys** and their **scoped lifetimes** are frozen as
well. Their closed DI registration is visible at
`src/SentenceStudio.Api/Coach/Tools/CoachToolServiceCollectionExtensions.cs:72-88`.
The test resolves the production handlers and inspects service descriptors. Adding a key, dropping
a key, or changing a handler lifetime fails the baseline; registration reorder and handler
implementation-class rename remain allowed.

### Production capability manifest

The production `CoachCapabilityManifest` contains **28 literal capability names**: the complete
**27-name registry surface** above plus the single standalone inert
`get_theme_metadata` declaration. The test constructs the production manifest and compares that
literal union in both directions. A manifest-only descriptor, a dropped registry descriptor, or a
new registry tool therefore fails even when the other source remains unchanged.

### Plan-shaped intent compatibility

`CoachIntentKind` is frozen at these names and ordinals:

| Member | Ordinal |
|---|---:|
| `NoChange` | 0 |
| `DirectConstraintChange` | 1 |
| `SuggestConstraintChange` | 2 |
| `AcceptPendingSuggestion` | 3 |
| `RejectPendingSuggestion` | 4 |
| `AskClarification` | 5 |
| `OffTopic` | 6 |
| `PedagogicalAnswer` | 7 |

The enum is defined at
`src/SentenceStudio.Contracts/Coach/Intent/CoachIntentEnums.cs:11-41`.
Its ordinal is persisted in `CoachPlanRevision.IntentKind`; the initial schema stores it as an
integer at
`src/SentenceStudio.Api/Coach/Persistence/Migrations/20260815030125_InitialCoachSchema.cs:20-24`.
Renaming, inserting, reordering, or appending a member is therefore a baseline change, not a
harmless refactor.

### External mutation surface

The frozen external surface contains **25 non-GET endpoint mappings**:

- **7** session mappings under `/api/v1/coach`
  (`src/SentenceStudio.Api/Coach/Endpoints/CoachEndpoints.cs:31-60`)
- **10** conversation and write-approval mappings under `/api/v1/coach/conversations`
  (`src/SentenceStudio.Api/Coach/Endpoints/CoachConversationEndpoints.cs:35-75`)
- **5** learner-memory mappings under `/api/v1/coach/memories`
  (`src/SentenceStudio.Api/Coach/Memory/Endpoints/CoachMemoryEndpoints.cs:36-46`)
- **1** response-report mapping under `/api/v1/coach/conversations`
  (`src/SentenceStudio.Api/Coach/Reports/Endpoints/CoachResponseReportEndpoints.cs:29-45`)
- **2** Development-only operator mappings under `/api/v1/coach/operator/opportunities`
  (`src/SentenceStudio.Api/Coach/Opportunities/Endpoints/CoachOpportunityOperatorEndpoints.cs:43-103`)

The host maps those endpoint groups at `src/SentenceStudio.Api/Program.cs:711-722`. The contract
test boots the real production host in Development, reads its `EndpointDataSource`, filters the
`/api/v1/coach` route namespace, normalizes trailing slashes, and compares exactly 25 literal
HTTP-method/route tuples. It does not pin endpoint display names, delegates, registration order, or
source order. Adding a mutation route or alias anywhere in `Program` composition fails the
baseline, including report, review, and evidence-reveal routes that mutate state.

### Absent presentation capability

The only declared presentation capability is `get_theme_metadata`. It remains non-tool-backed,
`PresentationState`, required at the `Presentation` stage, and capped at
`AbsentUnimplemented`; see
`src/SentenceStudio.Api/Coach/Capabilities/CoachCapabilityDeclarations.cs:41-60`.
The contract test also verifies that the tool registry does not register that name. This pins the
current absent/inert state without creating a capability.

## Changing an approved baseline

A baseline changes only with explicit architecture approval that identifies why a critical
containment fix must alter frozen topology. In the same change:

1. Classify the proposed work explicitly as critical containment, safe read/decrypt compatibility,
   bounded pre-cutover settlement, or prohibited legacy growth. Compatibility is not implicitly
   approved by this document.
2. Reject new mutation routes and aliases; the replacement architecture owns new product work.
3. Make the smallest approved production change.
4. Update the relevant count and explicit expected entries in
   `LegacyCoachGrowthFreezeTests.cs`; never derive the expectation from production data.
5. Update this document's count, list, and exact source citation.
6. Run the targeted architecture test and `git diff --check`.
7. Have the reviewer confirm that the change is approved containment or compatibility rather than
   legacy feature growth.

The tests include synthetic negative controls for tool growth, write-handler key and lifetime drift,
mutation-route aliases, manifest-only descriptors, and missing registry descriptors. They prove the
guards reject drift instead of merely reflecting over the current implementation and agreeing with
whatever it finds. Logging, telemetry, performance, and internal refactors remain permitted when
they do not alter these frozen contracts.
