# Coach UI Outcome Inventory v1

**Status:** Pending Review
**Inventory date:** 2026-09-02
**Scope:** Current learner-visible UI outcomes and the directly supporting maintenance,
operator, and presentation actions needed to define a closed boundary.

## How to read this inventory

This inventory counts **user outcomes**, not HTTP endpoints, repository methods, or
model functions. One outcome may coordinate several services and transports; several UI
surfaces may also implement the same outcome. Broad UI/Coach parity therefore means that
approved user-meaningful outcomes share typed application handlers. It does **not** expose
raw endpoints, arbitrary service methods, route strings, identity fields, confirmations,
or persistence commands.

Current paths are evidence of the implementation as reviewed, not the proposed capability
boundary. A severe finding below is an evidence-backed current-state risk. It is not a
claim that the risk has been exploited. No sensitive values are reproduced.

### Codes

| Dimension | Values |
|---|---|
| Classification | **Capability**, **PresentationOnly**, **Internal**, **AbsentByDesign** |
| Authority | **NativeLocal** (offline/native data authority), **Server**, **External**, **Client**; a slash lists current host variants, never dual execution |
| Effect | **Read**, **Write**, **Launch**, **ExternalEffect**, **Composite**, **Presentation** |
| Sensitivity | **OwnerContent**, **Aggregate**, **ClientOnly**, **Public**, **Secret**, **Prohibited** |
| Confirmation | **None**, **Gesture**, **Accept**, **ProtectedConfirm** |
| Offline need | **Required**, **Cached**, **OnlineOnly**, **N/A** |
| Coach eligibility | **AllowedCandidate**, **NeedsReview**, **Denied**; **BLOCKED** is an overriding ineligibility until the cited defect is repaired |
| Automation eligibility | **Allowed**, **PolicyRequired**, **GestureOnly**, **Denied** |

## Counted outcomes

- **73** unique numbered outcomes.
- Classification: **59 Capability**, **4 PresentationOnly**, **7 Internal**,
  **3 AbsentByDesign**.
- Base Coach eligibility: **14 AllowedCandidate**, **39 NeedsReview**, **20 Denied**.
- Automation eligibility: **7 Allowed**, **29 PolicyRequired**, **16 GestureOnly**,
  **21 Denied**.
- Blockers: **8** rows. A blocker modifier does not change the base classification or
  eligibility census; it prevents Coach exposure until repair and re-review.

## Plans, learner context, preferences, and account data

| # | Capability code / family | User outcome | Current UI source | Current execution path | Classification | Authority | Effect / sensitivity | Confirmation | Offline | Coach | Automation | Migration dependencies | Safety risks |
|---:|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | `plan.today.read@1` / `plan-and-practice-history` | View today's plan, rationale, and preserved progress. | `src/SentenceStudio.UI/Pages/Index.razor:330-407,930-955` | `IProgressService.GetCachedPlanAsync` | Capability | NativeLocal / Server | Read / Aggregate | None | Required | AllowedCandidate | Allowed | Shared read handler; user-local date projection. | Redact due-answer detail from Coach observations. |
| 2 | `plan.today.generate@1`, `plan.today.regenerate@1` / `plan-and-practice-history` | Generate or replace today's plan. | `src/SentenceStudio.UI/Pages/Index.razor:419-425,957-1009` | `ProgressService` → `ILlmPlanGenerationService` → `ApiPlanGenerationService` → `PlansApiClient` | Capability | Server | Composite / OwnerContent | Accept | OnlineOnly | NeedsReview | PolicyRequired | Untouched-remainder proposal, expected versions, receipt. | Current regeneration clears cached completion rows; preserve started/completed work. |
| 3 | `plan.item.progress@1`, `plan.item.complete@1` / `activity-session-and-progress` | Persist elapsed time and complete plan items. | `src/SentenceStudio.UI/Pages/VocabQuiz.razor:2353-2484`; supporting paths `src/SentenceStudio.Shared/Services/Timer/ActivityTimerService.cs:547-589` and `src/SentenceStudio.Shared/Services/Progress/ProgressService.cs:565-900` | Timer → `IProgressService.UpdatePlanItemProgressAsync` | Capability | NativeLocal / Server | Write / Aggregate | Gesture | Required | NeedsReview | PolicyRequired | Explicit user, plan-item version, idempotent completion. | Most current pages start the legacy timer with an empty user identifier. |
| 4 | `progress.summary.read@1`, `progress.activity-log.read@1` / `activity-session-and-progress` | View vocabulary and number summaries and historical practice. | `src/SentenceStudio.UI/Pages/Index.razor:1207-1257`; `src/SentenceStudio.UI/Pages/ActivityLog.razor:138-175` | `IProgressService`; dashboard number summary directly queries `Db.NumberMasteryProgresses` | Capability | NativeLocal / Server | Read / Aggregate | None | Required | AllowedCandidate | Allowed | Move number query behind an owner-scoped application port. | Preserve user filtering on every query. |
| 5 | `learner.starter-content.create@1` / `learner-profile-preferences` | Create starter profile, skill, vocabulary, and resource. | `src/SentenceStudio.UI/Pages/Index.razor:774-872`; `src/SentenceStudio.UI/Pages/Onboarding.razor:331-400`; `src/SentenceStudio.UI/Pages/Resources.razor:221-247` | Profile, resource, and skill repositories | Capability | NativeLocal / Server | Composite / OwnerContent | Accept | Required | NeedsReview | PolicyRequired | Replace three implementations with one atomic, idempotent handler. | Partial retries must not duplicate starter data. |
| 6 | `learner.profile.read@1` / `learner-profile-preferences` | View learner profile and language settings. | `src/SentenceStudio.UI/Pages/Profile.razor:240-255` | `UserProfileRepository.GetAsync` | Capability | NativeLocal / Server | Read / OwnerContent | None | Required | AllowedCandidate | Allowed | Promote existing `ILearnerProfileQueries` into the shared application boundary. | Observation must expose only required profile fields. |
| 7 | `learner.profile.update@1` / `learner-profile-preferences` | Update name, contact email, native language, level, and session duration. | `src/SentenceStudio.UI/Pages/Profile.razor:272-285` | Whole-object `ProfileRepo.SaveAsync` | Capability | NativeLocal / Server | Write / OwnerContent | Gesture or Accept | Required | NeedsReview | PolicyRequired | Split credential contact data from learner metadata; expected-version write. | Whole-object saves can overwrite concurrent language/profile edits. |
| 8 | `learner.active-language.change@1` / `learner-profile-preferences` | Change the studied language and resume the originating request. | `src/SentenceStudio.UI/Pages/Profile.razor:279-292` | Whole-profile save → `Persona.ApplyStudyLanguage` | Capability | NativeLocal / Server | Composite / OwnerContent | ProtectedConfirm | Required | NeedsReview | PolicyRequired | Narrow compare-and-swap operation plus one-resume continuation. | Preserve other language settings; stale confirmation performs no effect. |
| 9 | `learner.display-language.change@1` / `learner-profile-preferences` | Change the UI display language. | `src/SentenceStudio.UI/Pages/Profile.razor:281,294-320` | Profile save → cookie endpoint or process-culture update | Capability | NativeLocal / Server | Composite / ClientOnly | Gesture or Accept | Required | NeedsReview | PolicyRequired | Separate persisted preference from client-only cookie/DOM application. | Never let Coach supply culture routes or cookie values. |
| 10 | `preference.appearance.update@1` / `learner-profile-preferences` | Persist theme, display mode, and text scale. | `src/SentenceStudio.UI/Pages/Settings.razor:322-360` | `ThemeService` or preference store → client DOM application | Capability | NativeLocal / Server | Write / ClientOnly | Gesture | Required | NeedsReview | PolicyRequired | Shared preference handler; retain DOM application as PresentationOnly. | Validate bounded values and host-specific availability. |
| 11 | `preference.vocabulary-review.update@1` / `learner-profile-preferences` | Persist quiz direction, modality, autoplay, and photo/text behavior. | `src/SentenceStudio.UI/Pages/Settings.razor:363-414`; `src/SentenceStudio.UI/Pages/VocabQuiz.razor:2661-2680` | `VocabularyQuizPreferences`; profile repository for photo text | Capability | NativeLocal / Server | Write / ClientOnly | Gesture or Accept | Required | NeedsReview | PolicyRequired | Consolidate duplicate stores; qualify the complete learning-value matrix. | Enforce a non-empty target-language modality and prevent answer leakage. |
| 12 | `preference.speech-voice.update@1` / `learner-profile-preferences` | Choose a preferred speech voice by language. | `src/SentenceStudio.UI/Pages/Settings.razor:376-414` | `IVoiceDiscoveryService` → `SpeechVoicePreferences` | Capability | NativeLocal / Server | Write / ClientOnly | Gesture | Cached | NeedsReview | PolicyRequired | Persist stable preference with deterministic unavailable-voice fallback. | External voice inventory is mutable. |
| 13 | `learner.onboarding.complete@1` / `learner-profile-preferences` | Finish initial profile and optional starter setup. | `src/SentenceStudio.UI/Pages/Onboarding.razor:261-415` | Direct repositories, authentication, preferences, and cache | Capability | NativeLocal / Server | Composite / OwnerContent | Gesture or Accept | Required | NeedsReview | PolicyRequired | Consolidate with starter-content capability; idempotent checkpoints. | Authentication and starter-data effects must not partially commit. |
| 14 | `account.data.complete-export@1` / `account` | Download a complete account-data archive. | `src/SentenceStudio.UI/Pages/Profile.razor:331-338`; `src/SentenceStudio.UI/Pages/Settings.razor:422-428` | `DataExportService.ExportAllDataAsZipAsync` | AbsentByDesign | Server | Composite / Prohibited | ProtectedConfirm | OnlineOnly | Denied — **BLOCKED** | Denied | Dedicated authenticated UI redesign; owner-complete export and migration validation. | **BLOCKER — current-state evidence:** `src/SentenceStudio.Shared/Services/DataExportService.cs:89-125` exports unscoped data and a stored API-key field. Coach-ineligible until repaired. |

## Vocabulary, resources, skills, and diary

| # | Capability code / family | User outcome | Current UI source | Current execution path | Classification | Authority | Effect / sensitivity | Confirmation | Offline | Coach | Automation | Migration dependencies | Safety risks |
|---:|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 15 | `vocabulary.catalog.read@1` / `vocabulary-management` | Search, filter, sort, and list owned vocabulary. | `src/SentenceStudio.UI/Pages/Vocabulary.razor:790-1077` | `LearningResourceRepository` + `VocabularyProgressService` | Capability | NativeLocal / Server | Read / OwnerContent | None | Required | AllowedCandidate | Allowed | Paged owner-scoped query; keep UI sorting presentation-local. | Due-answer projections must remain redacted. |
| 16 | `vocabulary.word.upsert@1` / `vocabulary-management` | Create or edit terms, type, tags, language, resource links, and constituents. | `src/SentenceStudio.UI/Pages/VocabularyWordEdit.razor:867-978` | `ResourceRepo.SaveWordAsync`; direct `PhraseConstituents` DbSet | Capability | NativeLocal / Server | Write / OwnerContent | Gesture or Accept | Required | NeedsReview — **BLOCKED** | PolicyRequired | Atomic duplicate, owner, and version checks; move constituent writes behind handler. | **BLOCKER — current-state evidence:** constituent search/write at `src/SentenceStudio.UI/Pages/VocabularyWordEdit.razor:779-840,923-974` bypasses the handler and search is not owner-scoped. Coach-ineligible until repaired. |
| 17 | `vocabulary.word.delete@1`, `vocabulary.word.bulk-delete@1` / `vocabulary-management` | Remove one or many vocabulary words. | `src/SentenceStudio.UI/Pages/VocabularyWordEdit.razor:992-1008`; `src/SentenceStudio.UI/Pages/Vocabulary.razor:1173-1197` | Repository delete or bulk delete | Capability | NativeLocal / Server | Write / OwnerContent | ProtectedConfirm | Required | NeedsReview | PolicyRequired | Cascade preview, expected versions, receipt, and undo policy. | Deletion must remain owner-scoped and idempotent. |
| 18 | `vocabulary.word.metadata-update@1` / `vocabulary-management` | Set learner status or bulk-update language, tags, and status. | `src/SentenceStudio.UI/Pages/Vocabulary.razor:1218-1316`; `src/SentenceStudio.UI/Pages/VocabularyWordEdit.razor:1061-1070` | Progress service + repository bulk methods | Capability | NativeLocal / Server | Write / OwnerContent | Accept | Required | NeedsReview | PolicyRequired | Typed partial update with expected versions. | Keep learner-declared status separate from computed mastery. |
| 19 | `vocabulary.resource-membership.update@1` / `vocabulary-management` | Link or unlink vocabulary and resources. | `src/SentenceStudio.UI/Pages/ResourceEdit.razor:601-687`; `src/SentenceStudio.UI/Pages/VocabularyWordEdit.razor:912-920` | `AddVocabularyToResourceAsync` / `RemoveVocabularyFromResourceAsync` | Capability | NativeLocal / Server | Write / OwnerContent | Accept | Required | NeedsReview | PolicyRequired | Canonical relationship handler; migrate all duplicate surfaces. | Both entities must resolve to the same owner. |
| 20 | `vocabulary.duplicates.merge@1` / `vocabulary-management` | Merge duplicate terms while preserving associations and progress. | `src/SentenceStudio.UI/Pages/Vocabulary.razor:1514-1668` | `ResourceRepo.MergeVocabularyWordsAsync` | Capability | NativeLocal / Server | Composite / OwnerContent | ProtectedConfirm | Required | NeedsReview | PolicyRequired | Transactional merge, keeper preview, receipt, and undo decision. | Cross-owner candidates must fail closed. |
| 21 | `vocabulary.example-sentence.generate@1`, `vocabulary.example-sentence.manage@1` / `vocabulary-management` | Preview generated examples; keep, add, edit, mark, flag, or delete them. | `src/SentenceStudio.UI/Components/ReferenceSentencesSection.razor:225-352` | Generation service + service-located `ExampleSentenceRepository` | Capability | NativeLocal / Server | Composite / OwnerContent | Accept | Cached | NeedsReview | PolicyRequired | Split inert generation from persisted writes; inject an application handler. | Generated content and owner vocabulary require separate projections. |
| 22 | `vocabulary.repair.*` / `operator-maintenance` | Populate lemmas, repair swapped languages, assign orphans, and auto-merge. | `src/SentenceStudio.UI/Pages/Vocabulary.razor:1686-1944` | AI services + direct repository batch operations | Internal | NativeLocal / Server | Composite / OwnerContent | ProtectedConfirm | Required | Denied | Denied | Retain as globally bounded, audited maintenance utilities. | Never catalog as learner or Coach capabilities. |
| 23 | `resource.catalog.read@1` / `learning-resource-and-import` | List, search, and view owned learning resources. | `src/SentenceStudio.UI/Pages/Resources.razor:157-219`; `src/SentenceStudio.UI/Pages/ResourceEdit.razor:378-383` | `LearningResourceRepository` | Capability | NativeLocal / Server | Read / OwnerContent | None | Required | AllowedCandidate | Allowed | Promote `ILearningResourceQueries` into the application boundary. | Require owner scope before applying search criteria. |
| 24 | `resource.upsert@1` / `learning-resource-and-import` | Create or edit a learning resource. | `src/SentenceStudio.UI/Pages/ResourceAdd.razor:225-249`; `src/SentenceStudio.UI/Pages/ResourceEdit.razor:399-426` | `SaveResourceAsync` | Capability | NativeLocal / Server | Write / OwnerContent | Accept | Required | NeedsReview | PolicyRequired | Expected version; explicit smart-resource restrictions. | Validate owner and resource type on every update. |
| 25 | `resource.delete@1` / `learning-resource-and-import` | Delete a resource after confirmation. | `src/SentenceStudio.UI/Pages/ResourceEdit.razor:444-487` | `DeleteResourceAsync` | Capability | NativeLocal / Server | Write / OwnerContent | ProtectedConfirm | Required | NeedsReview | PolicyRequired | Effect preview for vocabulary mappings and activities; receipt/undo decision. | Destructive cascade must be owner-scoped. |
| 26 | `resource.starter.create@1` / `learning-resource-and-import` | Create or reuse a starter vocabulary resource. | `src/SentenceStudio.UI/Pages/Resources.razor:221-247` | Profile + resource repository | Capability | NativeLocal / Server | Composite / OwnerContent | Accept | Required | NeedsReview | PolicyRequired | Canonicalize with outcomes 5 and 13. | Retry must reuse the owner's existing starter resource. |
| 27 | `content-import.preview@1` / `learning-resource-and-import` | Classify and parse content and preview duplicates without committing. | `src/SentenceStudio.UI/Pages/ImportContent.razor:742-973` | `IContentImportService.Classify`, `Parse`, `EnrichPreview` | Capability | Server | Read / OwnerContent | None | OnlineOnly | AllowedCandidate | Allowed | Frozen preview digest; separate redacted Coach observation. | Preview remains inert and must not reveal another owner's matches. |
| 28 | `content-import.commit@1` / `learning-resource-and-import` | Import selected vocabulary and resources. | `src/SentenceStudio.UI/Pages/ImportContent.razor:974-1058` | `ContentImportService.CommitImportAsync` → DbContext and repositories | Capability | NativeLocal / Server | Composite / OwnerContent | Accept | Cached | NeedsReview | PolicyRequired | Transactional owner/version/idempotency checks and exact receipt. | Large partial commits and duplicate retries must fail safely. |
| 29 | `resource.transcript-vocabulary.generate@1` / `learning-resource-and-import` | Generate and attach vocabulary from a transcript. | `src/SentenceStudio.UI/Pages/ResourceEdit.razor:863-969` | AI generation → repository writes | Capability | Server | Composite / OwnerContent | Accept | OnlineOnly | NeedsReview | PolicyRequired | Preview, validate, and explicitly accept generated candidates. | Never persist model output before owner-bound acceptance. |
| 30 | `example-sentence.transcript-harvest` / `operator-maintenance` | Backfill derived examples from transcripts. | `src/SentenceStudio.UI/Pages/Settings.razor:440-472` | `ITranscriptSentenceHarvestService` | Internal | NativeLocal / Server | Composite / OwnerContent | ProtectedConfirm | Required | Denied | Denied | Keep as bounded, owner-scoped maintenance work. | Bulk backfill must not cross owner boundaries. |
| 31 | `skill.catalog.read@1` / `skills` | List and open owned skill profiles. | `src/SentenceStudio.UI/Pages/Skills.razor:71-90` | `SkillProfileRepository.ListAsync` | Capability | NativeLocal / Server | Read / OwnerContent | None | Required | AllowedCandidate | Allowed | Promote `ISkillProfileQueries` into the application boundary. | Empty owner scope returns no data. |
| 32 | `skill.upsert@1`, `skill.archive@1` / `skills` | Create, edit, archive, or reopen skills. | `src/SentenceStudio.UI/Pages/SkillAdd.razor:52-71`; `src/SentenceStudio.UI/Pages/SkillEdit.razor:141-166`; archive has no current UI (`src/SentenceStudio.Shared/Data/SkillProfileRepository.cs:84`) | Skill repository | Capability | NativeLocal / Server | Write / OwnerContent | Accept | Required | NeedsReview | PolicyRequired | Add archive UI through the shared handler; migrate create/edit. | Prefer reversible archive before destructive deletion. |
| 33 | `skill.delete@1` / `skills` | Permanently remove a skill. | `src/SentenceStudio.UI/Pages/SkillEdit.razor:178-208` | `SkillRepo.DeleteAsync` | Capability | NativeLocal / Server | Write / OwnerContent | ProtectedConfirm | Required | NeedsReview | PolicyRequired | Preview linked plans/history; receipt and archive-first policy. | Destructive delete requires owner scope and expected version. |
| 34 | `diary.entry.read@1` / `diary` | List and read private diary entries. | `src/SentenceStudio.UI/Pages/Diary.razor:103-125`; `src/SentenceStudio.UI/Pages/DiaryViewer.razor:127-165` | `DiaryEntryRepository` | Capability | NativeLocal / Server | Read / OwnerContent | None | Required | Denied | Denied | Owner-scoped query and highly redacted client projection only. | Highly sensitive content; default-deny Coach projection. |
| 35 | `diary.entry.upsert@1` / `diary` | Autosave or explicitly save today's diary entry. | `src/SentenceStudio.UI/Pages/DiaryEditor.razor:179-233,296-309` | `DiaryRepo.UpsertAsync` | Capability | NativeLocal / Server | Write / OwnerContent | Gesture | Required | Denied | Denied | Versioned draft/autosave contract with offline preservation. | Concurrent autosave must not overwrite newer drafts. |
| 36 | `diary.prompt-generate@1`, `diary.feedback-generate@1` / `diary` | Generate a writing prompt or feedback and save accepted content. | `src/SentenceStudio.UI/Pages/DiaryEditor.razor:247-260`; `src/SentenceStudio.UI/Pages/DiaryViewer.razor:136-146` | `DiaryService` → AI service; repository persistence | Capability | Server | Composite / OwnerContent | Accept | OnlineOnly | Denied | Denied | Separate inert generation from persistence and migration. | Content-sensitive; persistence requires explicit owner-bound acceptance. |

## Activities and media

| # | Capability code / family | User outcome | Current UI source | Current execution path | Classification | Authority | Effect / sensitivity | Confirmation | Offline | Coach | Automation | Migration dependencies | Safety risks |
|---:|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 37 | `activity.vocabulary-review.prepare@1`, `.launch@1`, `.resume@1`, `.attempt@1`, `.complete@1` / `activity-session-and-progress` | Run, resume, and complete vocabulary review. | `src/SentenceStudio.UI/Pages/VocabQuiz.razor:827-936,1152-1456,1493-1548,2353-2484` | Launch validator, repositories, `VocabularyProgressService`, `IActivitySessionService`, validated timer | Capability | NativeLocal / Server | Composite / OwnerContent | Gesture | Required after preparation | AllowedCandidate | GestureOnly | Reference contract for owned snapshots, resume, attempts, and completion. | Preserve snapshot ownership/context validation and due-answer protection. |
| 38 | `activity.vocabulary-matching.prepare@1`, `.launch@1`, `.attempt@1`, `.complete@1` / `activity-session-and-progress` | Run the vocabulary matching game. | `src/SentenceStudio.UI/Pages/VocabMatching.razor:131-199,321-418` | Repositories + vocabulary progress + legacy timer | Capability | NativeLocal / Server | Composite / OwnerContent | Gesture | Required | NeedsReview | GestureOnly | Add durable session, attempt, and completion records. | Current completion is visual only; injected activity repository is unused. |
| 39 | `activity.cloze.prepare@1`, `.launch@1`, `.attempt@1`, `.complete@1` / `activity-session-and-progress` | Complete graded cloze challenges. | `src/SentenceStudio.UI/Pages/Cloze.razor:197-432` | `ClozureService`, activity repository, vocabulary progress, timer | Capability | NativeLocal / Server | Composite / OwnerContent | Gesture | Cached | NeedsReview | GestureOnly | Atomic owned generation, attempt, progress, and completion boundary. | No durable resume snapshot. |
| 40 | `activity.reading.prepare@1`, `.launch@1`, `.complete@1` / `activity-session-and-progress` | Read or listen, inspect words, and save discovered vocabulary. | `src/SentenceStudio.UI/Pages/Reading.razor:275-814` | Resource repository, speech/translation services, activity repository, timer | Capability | NativeLocal / Server | Composite / OwnerContent | Gesture; Accept for saved word | Cached | AllowedCandidate | GestureOnly | Split read launch from save-word write; add session snapshot. | Saving discovered vocabulary is a separate consequential effect. |
| 41 | `activity.listening.prepare@1`, `.launch@1`, `.complete@1` / `activity-session-and-progress` | Practice listening through the current shadowing surface. | `src/SentenceStudio.UI/Services/PlanActivityPresentation.cs:93-95`; `src/SentenceStudio.UI/Pages/Shadowing.razor:161-449` | Listening and shadowing share one route and service | Capability | NativeLocal / Server | Launch / OwnerContent | Gesture | Cached | AllowedCandidate | GestureOnly | Define a distinct listening modality contract. | Shared route must not collapse distinct pedagogical outcomes. |
| 42 | `activity.shadowing.prepare@1`, `.launch@1`, `.complete@1` / `activity-session-and-progress` | Practice sentence imitation with generated audio. | `src/SentenceStudio.UI/Pages/Shadowing.razor:161-449` | `ShadowingService`, speech service, timer | Capability | NativeLocal / Server | Composite / OwnerContent | Gesture | Cached | AllowedCandidate | GestureOnly | Add standardized attempt/completion/session state. | Current persistence records timer progress only. |
| 43 | `activity.translation.prepare@1`, `.launch@1`, `.attempt@1`, `.complete@1` / `activity-session-and-progress` | Translate, receive grading, and record progress. | `src/SentenceStudio.UI/Pages/Translation.razor:273-557` | Translation and teacher services, repositories, timer | Capability | NativeLocal / Server | Composite / OwnerContent | Gesture | Cached | NeedsReview | GestureOnly | Separate AI grading from local mutation; add resumable snapshot. | No current resume; grading and progress writes are interleaved. |
| 44 | `activity.writing.prepare@1`, `.launch@1`, `.attempt@1`, `.complete@1` / `activity-session-and-progress` | Write, optionally translate, receive grading, and record progress. | `src/SentenceStudio.UI/Pages/Writing.razor:216-435` | Teacher/translation services, repositories, timer | Capability | NativeLocal / Server | Composite / OwnerContent | Gesture | Cached | NeedsReview | GestureOnly | Split optional translation from grade and record operations. | Owner-authored content needs bounded observations and retention policy. |
| 45 | `activity.scene-description.prepare@1`, `.launch@1`, `.attempt@1`, `.complete@1` / `activity-session-and-progress` | Describe an image and receive grading. | `src/SentenceStudio.UI/Pages/Scene.razor:270-370,492-495` | Scene/teacher services, activity/progress repositories, timer | Capability | NativeLocal / Server | Composite / OwnerContent | Gesture | Cached | NeedsReview | GestureOnly | Resolve image ownership and disclosure policy; add session state. | Shared-image provenance and disclosure are unresolved. |
| 46 | `activity.conversation.prepare@1`, `.launch@1`, `.turn@1`, `.complete@1` / `activity-session-and-progress` | Hold a scenario-based target-language conversation. | `src/SentenceStudio.UI/Pages/Conversation.razor:250-458` | Scenario, conversation, speech, vocabulary-progress services, timer | Capability | Server | Composite / OwnerContent | Gesture | OnlineOnly | NeedsReview | GestureOnly | Add activity-session resume and isolate activity conversation from Coach history. | Current service resume exists without generic activity snapshot coverage. |
| 47 | `activity.video-watching.prepare@1`, `.launch@1`, `.complete@1` / `activity-session-and-progress` | Watch an imported video and transcript. | `src/SentenceStudio.UI/Pages/VideoWatching.razor:113-164` | Resource repository + timer | Capability | NativeLocal / Server | Launch / OwnerContent | Gesture | Cached | AllowedCandidate | GestureOnly | Add standardized completion and attempt records. | Current outcome records timer progress only. |
| 48 | `activity.number-drill.prepare@1`, `.launch@1`, `.attempt@1`, `.complete@1` / `activity-session-and-progress` | Practice number contexts and persist mastery. | `src/SentenceStudio.UI/Pages/NumberDrill.razor:551-921` | `NumberSessionService`; direct DbContext configuration; timer | Capability | NativeLocal / Server | Composite / OwnerContent | Gesture | Required | NeedsReview | GestureOnly | Move configuration behind owner/language-aware handler. | Current UI hardcodes the target-language code. |
| 49 | `activity.word-association.prepare@1`, `.launch@1`, `.attempt@1`, `.complete@1` / `activity-session-and-progress` | Generate clues, receive grading, and save score/progress. | `src/SentenceStudio.UI/Pages/WordAssociation.razor:229-428` | `WordAssociationService`, activity/progress repositories | Capability | NativeLocal / Server | Composite / OwnerContent | Gesture | Cached | NeedsReview | GestureOnly | Add generic resumable snapshot; move score persistence behind handler. | Service directly accesses DbContext for scores. |
| 50 | `activity.minimal-pairs.prepare@1`, `.launch@1`, `.attempt@1`, `.complete@1` / `activity-session-and-progress` | Select minimal pairs, run a session, record attempts, and end it. | `src/SentenceStudio.UI/Pages/MinimalPairs.razor:141-236`; `src/SentenceStudio.UI/Pages/MinimalPairSession.razor:148-341` | Minimal-pair repositories | Capability | NativeLocal / Server | Composite / OwnerContent | Gesture | Required | NeedsReview — **BLOCKED** | GestureOnly | Replace literal user input with resolved owner context; add owner predicates and versions. | **BLOCKER — current-state evidence:** UI supplies a literal user value and repository get/delete/end methods lack owner predicates (`src/SentenceStudio.Shared/Repositories/MinimalPairRepository.cs:28-145`; `src/SentenceStudio.Shared/Repositories/MinimalPairSessionRepository.cs:29-135`). Coach-ineligible until repaired. |
| 51 | `activity.how-do-you-say.launch@1`, `.synthesize@1`, `.history@1` / `activity-session-and-progress` | Hear a phrase and reuse or delete audio history. | `src/SentenceStudio.UI/Pages/HowDoYouSay.razor:118-329` | Speech service + stream-history repository/files | Capability | External / NativeLocal | Composite / OwnerContent | Gesture | Cached | AllowedCandidate | GestureOnly | Define history ownership, file lifecycle, and optional completion contract. | History and file deletion must be owner-scoped and idempotent. |
| 52 | `activity.flashcard.launch@1`, `.attempt@1`, `.complete@1` / `activity-session-and-progress` | Swipe plan-preview flashcards. | `src/SentenceStudio.UI/Pages/FlashcardActivity.razor:130-248` | Cached plan read; answer counts remain component memory | Capability | NativeLocal / Server | Launch / OwnerContent | Gesture | Required | NeedsReview | GestureOnly | Decide whether to add durable attempts/completion or reclassify as presentation. | Current UI persists no attempts, timer, completion, or resume. |
| 53 | `speech.synthesize@1` / `speech-and-audio` | Generate pronunciation audio. | `src/SentenceStudio.UI/Pages/Reading.razor:463-583`; supporting path `src/SentenceStudio.AppLib/Services/ElevenLabsSpeechService.cs:152-219` | Direct external speech SDK or speech API client | Capability | External | ExternalEffect / OwnerContent | Gesture | Cached | AllowedCandidate | PolicyRequired | Bounded text, cost, voice, cache-key, and receipt policy. | Text is disclosed externally; availability and cost are mutable. |
| 54 | — / `presentation` | Play, pause, seek, change speed, or autoplay audio. | `src/SentenceStudio.UI/Shared/AudioPlayer.razor:2-46`; `src/SentenceStudio.UI/Pages/Reading.razor:463-583` | JavaScript or native audio managers | PresentationOnly | Client | Presentation / ClientOnly | Gesture | N/A | Denied | Denied | None; keep outside the capability catalog. | Model must never control transport mechanics. |
| 55 | `media.scene-image.manage@1` / `media-channel-import` | Browse, add, select, or delete scene images. | `src/SentenceStudio.UI/Pages/Scene.razor:381-481` | `SceneImageService` → DbContext | Capability | NativeLocal / Server | Write / OwnerContent | Accept; ProtectedConfirm for delete | Required | NeedsReview — **BLOCKED** | PolicyRequired | Define shared-versus-owned image model and owner-scoped handler before migration. | **BLOCKER — current-state evidence:** `src/SentenceStudio.Shared/Services/SceneImageService.cs` is globally scoped while ownership policy is unresolved. Coach-ineligible until repaired. |
| 56 | `media.transcript.fetch@1`, `.polish@1`, `.save-resource@1` / `media-channel-import` | Fetch an external transcript, polish it, and save it as a resource. | `src/SentenceStudio.UI/Pages/MediaImport.razor:609-747` | External transcript/formatting services → resource repository | Capability | External / Server / NativeLocal | Composite / OwnerContent | Accept before save | OnlineOnly | NeedsReview | PolicyRequired | Split fetch, inert polish, and persisted owner-bound receipt. | External content/provenance and generated edits require preview. |
| 57 | `channel.read@1`, `.upsert@1`, `.monitor-toggle@1`, `.delete@1` / `media-channel-import` | Browse, create, edit, pause, resume, or delete monitored channels. | `src/SentenceStudio.UI/Pages/MediaImport.razor:380-429`; `src/SentenceStudio.UI/Pages/ChannelDetail.razor:278-423` | `ChannelMonitorService` | Capability | External / Server | Composite / OwnerContent | Accept | OnlineOnly | NeedsReview — **BLOCKED** | PolicyRequired | Add owner-bound IDs, versions, delete UI, and effect receipts. | **BLOCKER — current-state evidence:** get/update/delete are ID-only and unscoped at `src/SentenceStudio.Shared/Services/ChannelMonitorService.cs:44-107`. Coach-ineligible until repaired. |
| 58 | `media.video-import.start@1`, `.retry@1` / `media-channel-import` | Import selected videos or retry failed/stuck work. | `src/SentenceStudio.UI/Pages/ChannelDetail.razor:448-631`; `src/SentenceStudio.UI/Pages/MediaImport.razor:435-521` | `VideoImportPipelineService` | Capability | External | ExternalEffect / OwnerContent | Accept | OnlineOnly | NeedsReview — **BLOCKED** | PolicyRequired | Owner-bound import IDs, idempotency, one-use retry, and receipts. | **BLOCKER — current-state evidence:** get-by-ID and retry are unscoped at `src/SentenceStudio.Shared/Services/VideoImportPipelineService.cs:72-108`. Coach-ineligible until repaired. |
| 59 | `media.import.polling-cleanup` / `operator-maintenance` | Poll import status and mark stale imports failed. | `src/SentenceStudio.UI/Pages/MediaImport.razor:348-359,536-568` | Pipeline polling → `CleanupStaleImportsAsync` | Internal | Server | Composite / Aggregate | None | OnlineOnly | Denied — **BLOCKED** | Denied | Move cleanup to bounded operator/background authority with tenant scope. | **BLOCKER — current-state evidence:** a UI visit can initiate all-tenant cleanup at `src/SentenceStudio.Shared/Services/VideoImportPipelineService.cs:114-140`. Coach-ineligible until repaired. |

## Feedback, Coach management, account, operator, and presentation

| # | Capability code / family | User outcome | Current UI source | Current execution path | Classification | Authority | Effect / sensitivity | Confirmation | Offline | Coach | Automation | Migration dependencies | Safety risks |
|---:|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 60 | `feedback.issue.preview@1` / `feedback-reporting` | Produce a reviewable issue draft. | `src/SentenceStudio.UI/Pages/Feedback.razor:192-243` | `FeedbackApiClient.PreviewAsync` → current feedback preview endpoint | Capability | Server | Read / OwnerContent | None | OnlineOnly | NeedsReview | PolicyRequired | Frozen preview token/digest and bounded public projection. | Owner text can become public; preview remains inert. |
| 61 | `feedback.issue.submit@1` / `feedback-reporting` | Submit exactly one public issue. | `src/SentenceStudio.UI/Pages/Feedback.razor:246-280`; `src/SentenceStudio.AppLib/Services/Api/FeedbackApiClient.cs:59-90` | Server receipt ledger → external issue API | Capability | External | ExternalEffect / Public | ProtectedConfirm | OnlineOnly | NeedsReview | Denied | Preserve one-use receipt ledger and in-doubt settlement. | Irreversible; never retry an uncertain submission. |
| 62 | `feedback.coach-response.report@1` / `feedback-reporting` | Report a specific Coach answer. | `src/SentenceStudio.UI/Shared/Coach/CoachReportControl.razor:393-417`; `src/SentenceStudio.UI/Services/CoachWorkspaceState.Reports.cs:213-253` | `CoachApiClient.ReportResponseAsync` | Capability | Server | Write / OwnerContent | Gesture | OnlineOnly | Denied | Denied | Preserve owner, conversation, and message binding. | Coach must not report or approve reports about itself. |
| 63 | `coach.turn` / `coach-runtime` | Ask Coach and receive teaching content. | `src/SentenceStudio.UI/Pages/Coach.razor:261-302`; `src/SentenceStudio.UI/Services/CoachWorkspaceState.Durable.cs:286-309` | `CoachApiClient` → legacy Coach runtime | Internal | Server | Composite / OwnerContent | Gesture | OnlineOnly | Denied | Denied | Migrate history and checkpoints separately from the application catalog. | Runtime behavior is not an ApplicationCapability. |
| 64 | `coach-conversation.manage@1` / `coach-compatibility` | Create, list, open, rename, close, reopen, delete, or export Coach conversations. | `src/SentenceStudio.UI/Shared/Coach/CoachConversationList.razor:257-410` | `CoachConversationDirectory` → `CoachApiClient` | Capability | Server | Composite / OwnerContent | Accept; ProtectedConfirm for delete/export | OnlineOnly | Denied | Denied | Preserve IDs, order, owner/deletion/export coverage, and historical encryption purposes. | Coach cannot self-administer conversation deletion or export. |
| 65 | `coach-memory.manage@1` / `coach-compatibility` | View, approve, reject, edit, or forget remembered facts. | `src/SentenceStudio.UI/Shared/Coach/CoachMemoryPanel.razor:154-298` | `CoachMemoryDirectory` → Coach memory APIs | Capability | Server | Composite / OwnerContent | Accept; ProtectedConfirm for forget-all | OnlineOnly | Denied | Denied | Preserve product behavior and migrate owner-bound memory records additively. | Coach cannot self-authorize memory writes or deletion. |
| 66 | `application-operation.lifecycle` / `application-operation` | Accept, reject, confirm, undo, or cancel a pending effect. | `src/SentenceStudio.UI/Pages/Coach.razor:261-302`; supporting path `src/SentenceStudio.UI/Services/CoachWorkspaceState.Writes.cs:240-639` | Legacy Coach write APIs | Internal | Server | Composite / OwnerContent | Accept or ProtectedConfirm | OnlineOnly | Denied | Denied | Replace with neutral `ApplicationOperation` ledger and settle legacy proposals. | This is execution authority, not a domain capability or model tool. |
| 67 | `auth.credentials.*` / `account` | Log in, register, log out, recover, reset, or change credentials. | `src/SentenceStudio.UI/Pages/LoginPage.razor:102-165`; `src/SentenceStudio.UI/Pages/RegisterPage.razor:157-189`; `src/SentenceStudio.UI/Pages/ForgotPasswordPage.razor:88-99`; `src/SentenceStudio.UI/Pages/Profile.razor:351-389` | `IAuthService` → identity endpoints | AbsentByDesign | Server | Write / Secret | ProtectedConfirm | OnlineOnly | Denied | Denied | Keep on dedicated authenticated surfaces; preserve existing identity migration. | Never model eligible; never project secrets. |
| 68 | `account.delete` / `account` | Permanently delete an account. | `src/SentenceStudio.UI/Pages/Profile.razor:392-411`; `src/SentenceStudio.WebApp/Auth/AccountEndpoints.cs:268-311` | Current destructive GET endpoint → identity store and DbContext | AbsentByDesign | Server | Composite / Prohibited | ProtectedConfirm | OnlineOnly | Denied — **BLOCKED** | Denied | Redesign as authenticated non-GET operation with strong owner binding, receipt, and export/deletion validation. | **BLOCKER — current-state evidence:** destructive GET and explicit-profile fallback lack strong owner binding at the cited endpoint. Coach-ineligible until repaired. |
| 69 | `operator.debug-health` / `operator-maintenance` | Inspect database provider, migrations, counts, and API health. | `src/SentenceStudio.UI/Pages/DebugHealth.razor:169-257` | Direct DbContext + HTTP health request | Internal | NativeLocal / Server | Read / Prohibited | None | N/A | Denied | Denied | Development/operator gate only; no catalog migration. | Protected operational evidence must not enter Coach observations. |
| 70 | `operator.capability-opportunities` / `operator-maintenance` | Review capability-gap telemetry and reveal protected evidence. | `src/SentenceStudio.WebApp/Components/Pages/Operator/SamOpportunities.razor:520-628` (legacy filename) | Operator client → operator endpoints | Internal | Server | Composite / Prohibited | ProtectedConfirm for evidence reveal | OnlineOnly | Denied | Denied | Rename to neutral capability-gap telemetry during cutover; audited operator access. | Operator evidence is never learner- or Coach-visible. |
| 71 | — / `presentation` | Navigate/back, construct query strings, and resolve import route aliases. | `src/SentenceStudio.UI/Pages/Index.razor:1048-1089,1273-1319`; `src/SentenceStudio.UI/Layout/NavMenu.razor:85-111` | `NavigationManager` → route mapping | PresentationOnly | Client | Presentation / ClientOnly | Gesture | N/A | Denied | Denied | Capabilities return closed client actions; no route capability. | Model never supplies routes or identifiers. |
| 72 | — / `presentation` | Open panels/dialogs; manage focus, scroll, fullscreen, sidebar, and modal state. | `src/SentenceStudio.UI/Layout/MainLayout.razor:413-466`; `src/SentenceStudio.UI/Shared/Sam/SamOverlayHost.razor` (legacy filename); `src/SentenceStudio.UI/Pages/VocabQuiz.razor:2502-2652` | Component and JavaScript state | PresentationOnly | Client | Presentation / ClientOnly | Gesture | N/A | Denied | Denied | No capability migration. | Explicitly excluded from Coach control. |
| 73 | — / `presentation` | Change transient filters, sorting, view mode, toasts, loading/sync state, and update overlays. | `src/SentenceStudio.UI/Pages/Vocabulary.razor:863-1071,1335-1383`; `src/SentenceStudio.UI/Layout/MainLayout.razor:237-357`; `src/SentenceStudio.UI/Components/UpdateAvailableBanner.razor:24-40` | Local component, preference, and JavaScript state | PresentationOnly | Client | Presentation / ClientOnly | Gesture | N/A | Denied | Denied | Persisted preference portions migrate through outcomes 10-12 only. | Transient mechanics stay outside the catalog. |

## Direct UI bypass summary

The review found **37 Razor pages/components** that directly inject or service-locate a
repository or DbContext instead of going through an application handler:

- Shell/account: `src/SentenceStudio.UI/Layout/MainLayout.razor`,
  `src/SentenceStudio.UI/Pages/Auth.razor`,
  `src/SentenceStudio.UI/Pages/Onboarding.razor`,
  `src/SentenceStudio.UI/Pages/Profile.razor`.
- Dashboard/debug: `src/SentenceStudio.UI/Pages/Index.razor`,
  `src/SentenceStudio.UI/Pages/DebugHealth.razor`.
- Vocabulary/resources/import: `src/SentenceStudio.UI/Pages/Vocabulary.razor`,
  `src/SentenceStudio.UI/Pages/VocabularyWordEdit.razor`,
  `src/SentenceStudio.UI/Pages/Resources.razor`,
  `src/SentenceStudio.UI/Pages/ResourceAdd.razor`,
  `src/SentenceStudio.UI/Pages/ResourceEdit.razor`,
  `src/SentenceStudio.UI/Pages/ImportContent.razor`,
  `src/SentenceStudio.UI/Components/ReferenceSentencesSection.razor`.
- Skills/diary: `src/SentenceStudio.UI/Pages/Skills.razor`,
  `src/SentenceStudio.UI/Pages/SkillAdd.razor`,
  `src/SentenceStudio.UI/Pages/SkillEdit.razor`,
  `src/SentenceStudio.UI/Pages/Diary.razor`,
  `src/SentenceStudio.UI/Pages/DiaryEditor.razor`,
  `src/SentenceStudio.UI/Pages/DiaryViewer.razor`.
- Media: `src/SentenceStudio.UI/Pages/MediaImport.razor`,
  `src/SentenceStudio.UI/Pages/ChannelDetail.razor`.
- Activities: `src/SentenceStudio.UI/Pages/Cloze.razor`,
  `src/SentenceStudio.UI/Pages/Conversation.razor`,
  `src/SentenceStudio.UI/Pages/HowDoYouSay.razor`,
  `src/SentenceStudio.UI/Pages/MinimalPairs.razor`,
  `src/SentenceStudio.UI/Pages/MinimalPairCreate.razor`,
  `src/SentenceStudio.UI/Pages/MinimalPairSession.razor`,
  `src/SentenceStudio.UI/Pages/NumberDrill.razor`,
  `src/SentenceStudio.UI/Pages/Reading.razor`,
  `src/SentenceStudio.UI/Pages/Scene.razor`,
  `src/SentenceStudio.UI/Pages/Shadowing.razor`,
  `src/SentenceStudio.UI/Pages/Translation.razor`,
  `src/SentenceStudio.UI/Pages/VideoWatching.razor`,
  `src/SentenceStudio.UI/Pages/VocabMatching.razor`,
  `src/SentenceStudio.UI/Pages/VocabQuiz.razor`,
  `src/SentenceStudio.UI/Pages/WordAssociation.razor`,
  `src/SentenceStudio.UI/Pages/Writing.razor`.

Confirmed direct DbContext/service-location examples are dashboard number mastery
(`src/SentenceStudio.UI/Pages/Index.razor:1232-1254`), number-drill configuration
(`src/SentenceStudio.UI/Pages/NumberDrill.razor:594-599`), phrase-constituent
search/write (`src/SentenceStudio.UI/Pages/VocabularyWordEdit.razor:779-840,923-974`),
debug database inspection (`src/SentenceStudio.UI/Pages/DebugHealth.razor:176-227`),
and example-sentence repository location
(`src/SentenceStudio.UI/Components/ReferenceSentencesSection.razor:205-352`).

## Duplicate outcomes

1. Starter content appears in Dashboard, Onboarding, and Resources (outcomes 5, 13, 26).
2. Complete data export appears in Profile and Settings (outcome 14).
3. Resource/vocabulary membership appears in Resource Edit, Vocabulary Word Edit, and
   import commit (outcome 19).
4. Activity launch is independently assembled by plan cards and Choose My Own flows.
5. Vocabulary-review preferences appear in Settings and the in-activity profile toggle
   (outcome 11).
6. Profile creation/update overlaps Onboarding and Profile (outcomes 7 and 13).
7. Resource/vocabulary import overlaps Resource Add, Resource Edit, Import Content, and
   Media Import (outcomes 24, 27-29, 56).
8. Import and media-import routes, including channel aliases, are presentation aliases,
   not separate outcomes.
9. Listening and shadowing share one route but remain distinct pedagogical outcomes
   (outcomes 41 and 42).
10. Coach deletion scopes must remain distinct: current session, one conversation, and
    all Coach history.

## Coverage gaps and blockers

- Only vocabulary review has durable activity snapshot/resume/abandon/complete behavior.
- Most activities rely on legacy timer progress; several only show a completion screen.
- Flashcards persist no attempt or completion, and vocabulary matching injects an activity
  repository without writing it.
- Shadowing, video watching, and activity conversation lack standardized completion records.
- Skill archive and channel delete exist below the UI but have no current UI outcome.
- No diary-delete UI exists.
- The topical-vocabulary direct UI is not present yet; it remains the first proving slice.
- Shared Razor components conflate Server and NativeLocal implementations. Each catalog
  operation must bind exactly one execution authority.
- Outcomes **14, 16, 50, 55, 57, 58, 59, and 68** are blockers. Their current
  owner-scope/security defects must be repaired and independently re-reviewed before Coach
  exposure; this inventory does not attempt to fix them.

## Proposed family order after topical vocabulary and active language

1. `learner-profile-preferences` — stabilize owner and language context.
2. `vocabulary-management` — CRUD, status, examples, membership, and safe merges.
3. `learning-resource-and-import` — required by most activities.
4. `activity-session-and-progress` — one prepare/launch/snapshot/attempt/complete contract.
5. `plan-and-practice-history` — untouched-plan revision after activity/progress semantics.
6. `skills`.
7. Remaining activity families: Reading, Listening/Shadowing, Translation/Writing, then games.
8. `diary`, under content-sensitive default deny.
9. `media-channel-import`, after external-effect and idempotency policy.
10. `feedback-reporting`.
11. Coach conversation/memory compatibility migration, never self-administered model tools.
12. Exclude account credentials, deletion/export, operator, and presentation families from
    Coach discovery.

## Exact next reads

1. `src/SentenceStudio.Shared/Services/Progress/ProgressService.cs:230-900`,
   `src/SentenceStudio.Shared/Services/Plans/PlanService.cs`, and
   `src/SentenceStudio.Api/Plans/PlanEndpoints.cs`.
2. `src/SentenceStudio.UI/Pages/VocabQuiz.razor:827-1610,1760-2484`,
   `src/SentenceStudio.Shared/Services/ActivitySessionService.cs:22-165`, and
   `src/SentenceStudio.Shared/Services/Timer/ActivityTimerService.cs:73-608`.
3. `src/SentenceStudio.Shared/Data/LearningResourceRepository.cs:45-1705` and
   `src/SentenceStudio.Shared/Services/VocabularyProgressService.cs`.
4. `src/SentenceStudio.Shared/Services/ContentImportService.cs:1299-2000`.
5. `src/SentenceStudio.Shared/Services/ChannelMonitorService.cs:34-107` and
   `src/SentenceStudio.Shared/Services/VideoImportPipelineService.cs:61-180`.
6. `src/SentenceStudio.Shared/Services/DataExportService.cs:16-240` and
   `src/SentenceStudio.WebApp/Auth/AccountEndpoints.cs:268-311`.
7. `src/SentenceStudio.Shared/Repositories/MinimalPairRepository.cs:28-145` and
   `src/SentenceStudio.Shared/Repositories/MinimalPairSessionRepository.cs:29-135`.
8. Host DI registrations in
   `src/SentenceStudio.AppLib/ServiceCollectionExtentions.cs`,
   `src/SentenceStudio.WebApp/Program.cs`, and each MAUI head, to freeze NativeLocal/Server
   authority assignments.
9. EF models and migrations for activity sessions, daily-plan completion, minimal pairs,
   monitored channels, video imports, and ApplicationOperation additions.
10. Activity E2E references under `.claude/skills/e2e-testing/references/` to define
    launch/resume/complete acceptance coverage per outcome.

## Change control

This inventory is the parity denominator and the source for proactive coverage. Every
new or changed `ApplicationCapability` must map to a numbered inventory row and pass
classification and Coach-exposure review before it ships, even when it adds no new
learner-visible UI outcome. The same gate applies to every new or changed
learner-visible UI outcome. Before implementation or shipment, the change must:

1. Add exactly one numbered inventory row for a new outcome, or revise the existing
   row for a changed outcome or `ApplicationCapability`.
2. Classify the outcome as Capability, PresentationOnly, Internal, or AbsentByDesign.
3. Record authority, effect/sensitivity, confirmation, offline need, Coach and automation
   eligibility, migration dependencies, and safety risks.
4. Complete classification and Coach-exposure review before shipment. Missing metadata
   or an unresolved review means denied.
5. Add executable qualification for a Capability, or negative non-exposure evidence for
   every other classification.
6. Re-run the inventory census, duplicate review, blocker review, and relevant direct-UI/
   Coach parity gates.

No endpoint, service method, or UI control becomes Coach-visible merely because it exists.
