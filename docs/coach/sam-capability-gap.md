# Sam capability gap

What a learner can do in the app, set against what Sam can do for them today. Sam's abilities
are the tools in `src/SentenceStudio.Api/Coach/Tools/CoachToolRegistry.cs` (read tools 151-337,
approve-first `propose_*` write tools 367-560), the vocabulary-set flow
(`Coach/Application/CoachVocabularySetApplicationService.cs`) and the deterministic routes in
`Coach/Application/CoachSessionService.cs` (help list at 818, "when did I last study" at 802).
Snapshot of the `native-platform-heads` working tree on 2026-11-07, including the uncommitted
`propose_feedback` tool.

**Coverage key:** Full = a tool does it. Partial = a tool does part of it (the gap is in Notes).
None = Sam cannot do it. Excluded (policy) = kept out of Sam by
`docs/coach/coach-product-policy-v1.md` section 6. Every write tool shows a proposal card the
learner must approve; nothing is saved before that.

**Path shorthand:** `UI/` = `src/SentenceStudio.UI/Pages/` (WebApp and MAUI heads), `iOS/` =
`src/SentenceStudio.Native.iOS/Features/`, `And/` = `src/SentenceStudio.Native.Android/`,
`Coach/` = `src/SentenceStudio.Api/Coach/`. Registry line numbers refer to `CoachToolRegistry.cs`.

## Answer: does Sam's word count match the dashboard?

**No. Sam and the dashboard count different sets of words, so they will usually give different
numbers.** There are three counts in the app today:

| Where | Number shown | What it counts | Code |
|---|---|---|---|
| Dashboard "Total words" (web, MAUI, iOS, Android) | New + Learning + Familiar + Review + Known | Distinct words the learner owns that are **linked to at least one of their resources**, including words never practised (no progress row). A word not on any resource is left out. | Total: `UI/Index.razor:485`, `iOS/Dashboard/DashboardLearningProgressView.cs:152`, `And/AndroidSliceShell.Dashboard.Progress.Compose.cs:178`. Counts: `src/SentenceStudio.Shared/Data/VocabularyProgressRepository.cs:274-342` (`GetVocabSummaryCountsAsync`), reached through `ProgressService.GetVocabSummaryAsync` (`src/SentenceStudio.Shared/Services/Progress/ProgressService.cs:177`) and `src/SentenceStudio.Native/NativeDashboardPresenter.cs:475`. |
| Vocabulary screen "N of M" | M = all words | **Every word the learner owns**, linked to a resource or not. | `UI/Vocabulary.razor:52`, `:830`; `iOS/Vocabulary/VocabularyListView.cs:187-190`; `And/AndroidSliceShell.Vocabulary.Compose.cs:257`. Source: `LearningResourceRepository.GetAllVocabularyWordsWithResourcesAsync` (`src/SentenceStudio.Shared/Data/LearningResourceRepository.cs:1106`). |
| Sam `get_current_profile_summary` `TrackedWordCount` | one number | Owned words that have a **progress row** (a `VocabularyProgress` row for this learner), whether or not they are on a resource. Words added but never practised have no row and are not counted. | `Coach/Tools/SamTools/ProfileTools.cs:61` calls `CountTrackedWordsAsync` (`src/SentenceStudio.Shared/Data/VocabularyProgressRepository.Queries.cs:25-41`). |
| Sam `get_vocabulary_due_summary` `TrackedWordCount` | one number | Same set as above (progress rows), so the two Sam tools agree with each other. | `GetProgressFactsAsync` (`VocabularyProgressRepository.Queries.cs:43-66`), `rows.Count` at `Coach/Tools/VocabularyDueSummaryTool.cs:108`. |
| Sam `list_user_vocabularies` `TotalMatchCount` | one number | Progress rows **minus words due for review now** (by design, so due answers are never listed). Returns at most 25 rows. | `Coach/Tools/SamTools/VocabularySearchTool.cs:30`, `:64`; `SearchUndueWordsAsync` (`VocabularyProgressRepository.Queries.cs:97`); description at registry 231. |

What this means in practice:

- Sam's number equals the dashboard only when every owned word is on a resource **and** has
  been practised or marked at least once. Usually Sam reads lower, because newly added words
  have no progress row until the first attempt (`VocabularyProgressService.GetOrCreateProgressAsync`,
  `src/SentenceStudio.Shared/Services/VocabularyProgressService.cs:419`). Sam reads higher when
  practised words were later unlinked from every resource.
- A word Sam itself adds (`propose_vocabulary_entry`) does not raise Sam's own count: the
  handler creates the word and its link but no progress row
  (`Coach/Operations/Handlers/CoachVocabularyHandlers.cs`, no `VocabularyProgress` reference).
  The learner adds a word, asks "how many words do I have?", and gets the old number.
- `ProfileTools.cs:63-68` already states the rule for skills ("a count that disagreed with the
  skills screen would make Sam wrong about the learner's own account"); the word count does not
  follow it yet.
- Minor: the dashboard total can double-count a word that is both Known and Familiar or New
  (`VocabularyProgressRepository.cs:322-339`, the categories are not mutually exclusive), and
  the web dashboard reads a cached summary (`ProgressService.cs:180`).

## Vocabulary

| Action | Where in app | Sam today | Notes/gap |
|---|---|---|---|
| Add a word (to a resource) | `UI/Vocabulary.razor` (AddWord), `UI/ResourceEdit.razor` quick add; `iOS/LearningResources/LearningResourceQuickAddView.cs`; `And/AndroidSliceShell.Vocabulary.Compose.cs` | Full: `propose_vocabulary_entry` (registry 367) | Must name a resource (`CoachVocabularyEntryArgs.ResourceId`). No lemma, word type (word/phrase/sentence), language or mnemonic on create. |
| Add a themed set of ~10 words | none (Sam only) | Full: vocabulary-set flow, creates a "Vocabulary List" resource (`CoachVocabularySetApplicationService.cs:578-607`) | Sam-only capability; fixed at 10 terms (`RequiredTermCount`). |
| Edit a word (terms, tags, mnemonic) | `UI/VocabularyWordEdit.razor`; `iOS/Vocabulary/VocabularyWordEditView.cs`; `And/AndroidSliceShell.Vocabulary.Compose.cs` | Partial: `propose_vocabulary_edit` (registry 382) | Target, native, tags, mnemonic only. Not lemma, word type, language, learning status, constituents, mnemonic image. |
| Delete a word | `UI/VocabularyWordEdit.razor` (RequestDelete); native edit views | Full: `propose_vocabulary_removal` (registry 412) | Falls back to unlinking when the word is shared or used by Minimal Pairs answers (`CoachVocabularyHandlers.cs:600-650`). |
| Add an existing word to another resource | `UI/ResourceEdit.razor` (AddExistingVocabularyAsync); `iOS/LearningResources/LearningResourceVocabularyView.cs` | Full: `propose_vocabulary_link` (registry 397) | |
| Remove a word from one resource (keep the word) | `UI/ResourceEdit.razor` (RemoveVocabularyWordAsync) | None | Removal deletes the word or unlinks it from every resource; no single-resource unlink. |
| Find / search a word | `UI/Vocabulary.razor` search box; `src/SentenceStudio.Native/Features/Vocabulary/VocabularyFilterState.cs` | Partial: `list_user_vocabularies` (registry 225), `get_vocabulary_word_detail` (240) | Search hides due words (policy 9.3) and never-practised words (no progress row). Matches terms and lemma only, not tags. Max 25 rows. Detail needs a named word. |
| Filter by tag, resource, status, type | `UI/Vocabulary.razor:767-769`, `:206-209`; native `VocabularyFilterState.cs` | None | No filter arguments on `list_user_vocabularies`. |
| Sort by added / updated | `UI/Vocabulary.razor:58-66` | None | Sam's list is mastery order only. |
| Count words (total, by status) | `UI/Vocabulary.razor:52-56`; dashboard | Partial: `get_current_profile_summary`, `get_vocabulary_due_summary` (registry 303, 173) | Different population from both screens; see the answer above. |
| Bulk set status / language / add tags / delete | `UI/Vocabulary.razor:85-130`; `iOS/Vocabulary/VocabularyListView.cs:166` | None | One word per proposal; no status or language write at all. |
| Mark a word known / familiar (set status) | `UI/VocabularyWordEdit.razor`, Vocabulary bulk status | None | |
| Find and merge duplicates | `UI/Vocabulary.razor` (FindDuplicates, MergeDuplicateGroup); `iOS/Vocabulary/VocabularyDuplicatesView.cs`; `And/AndroidSliceShell.VocabularyDuplicates.Compose.cs` | None | |
| Clean-up tools: fix swapped languages, populate lemmas, assign orphaned words | `UI/Vocabulary.razor:32-35` | None | Web/MAUI only. |
| Edit constituents (parts of a phrase) | `UI/VocabularyWordEdit.razor` (AddConstituent); native edit views | None | |
| Play word audio | `UI/VocabularyWordEdit.razor` (PlayTermAudio) | None | UI mechanic; low value through chat. |

## Example sentences

| Action | Where in app | Sam today | Notes/gap |
|---|---|---|---|
| View a word's example sentences | `src/SentenceStudio.UI/Components/ReferenceSentencesSection.razor`; read-only on native (`iOS/Vocabulary/VocabularyWordEditView.cs:82`, `And/AndroidSliceShell.Vocabulary.Compose.cs:585`) | None | `get_vocabulary_word_detail` "returns no example sentences" (registry 247). |
| Add an example sentence by hand | `ReferenceSentencesSection.razor:32-51` (web/MAUI only) | None | The Captain's named example. Sam can write sentences in chat but cannot save them. |
| Generate example sentences (AI), keep or discard | `ReferenceSentencesSection.razor:14`, `:71-75` | None | |
| Edit or delete an example sentence | `ReferenceSentencesSection.razor:99-115` | None | |
| Harvest example sentences from resources | `UI/Settings.razor:151`, `:412` | None | |

## Lookups and "How do you say"

| Action | Where in app | Sam today | Notes/gap |
|---|---|---|---|
| Ask how to say a phrase | `UI/HowDoYouSay.razor`; `iOS/Activities/HowDoYouSayScreen.cs`; `And/AndroidSliceShell.HowDoYouSay.Compose.cs` | Partial: tutoring in chat | Sam answers in conversation; nothing saved to the phrase history, no audio. |
| View / delete phrase history | `UI/HowDoYouSay.razor` (DeleteHistoryItem) | None | |
| Recent lookups: view, add to vocabulary | `UI/RecentLookups.razor`; `iOS/Lookups/RecentLookupsView.cs`; `And/AndroidSliceShell.Lookups.Compose.cs` | None | Sam cannot read lookup history; it could add the word if the learner types it. |

## Learning resources

| Action | Where in app | Sam today | Notes/gap |
|---|---|---|---|
| List resources | `UI/Resources.razor`; `iOS/LearningResources/LearningResourcesView.cs`; `And/AndroidSliceShell.Resources.Compose.cs` | Full: `get_learning_resource_list`, `get_resource_catalog` (registry 278, 186) | Metadata only, paged. No filter or search by title/tag. |
| Count resources | same | Full: `get_current_profile_summary` `ResourceCount` | |
| View one resource | `UI/ResourceEdit.razor`; `iOS/LearningResources/LearningResourceView.cs` | Partial: `get_learning_resource_detail` (registry 291) | Never returns the transcript text or the word list on the resource. |
| Create a resource | `UI/ResourceAdd.razor`; native resource form | Partial: `propose_resource_entry` (registry 472) | Title, description, media type, language, tags. No transcript, URL, audio or file, no words in the same step. |
| Edit a resource | `UI/ResourceEdit.razor` (SaveResource) | Partial: `propose_resource_edit` (registry 487) | Title, description, tags only. Not transcript, media type, language, URL. |
| Delete a resource | `UI/ResourceEdit.razor` (RequestDelete) | Full: `propose_resource_removal` (registry 502) | |
| List words on a resource | `UI/ResourceEdit.razor`; `iOS/LearningResources/LearningResourceVocabularyView.cs` | None | Only a `VocabularyCount`. |
| Generate vocabulary from a resource's text (AI) | `UI/ResourceEdit.razor` (GenerateVocabulary) | None | |
| Bulk import words into a resource | `UI/ResourceAdd.razor`, `UI/ResourceEdit.razor` (ImportVocabulary, ToggleBulkImport) | None | |
| Create a starter resource | `UI/Resources.razor`, `UI/Index.razor` (CreateStarterResource) | None | The vocabulary-set flow is the nearest equivalent. |

## Import (YouTube, channels, content)

| Action | Where in app | Sam today | Notes/gap |
|---|---|---|---|
| Import one YouTube video's captions | `UI/MediaImport.razor`; `iOS/Import/YouTubeImportView.cs`; `And/AndroidSliceShell.YouTubeImport.Compose.cs` | Full: `propose_youtube_import` (registry 532) | No "polish transcript" step (`UI/MediaImport.razor` PolishTranscript). |
| View import history, retry a failed import | `UI/MediaImport.razor` (History tab, RetryImport) | None | |
| Add / edit a YouTube channel, check for new videos, import selected | `UI/ChannelDetail.razor`, `UI/MediaImport.razor`; `iOS/Import/YouTubeChannelView.cs` | None | |
| Import pasted content (vocabulary, phrases, transcript) | `UI/ImportContent.razor`; `iOS/Import/ImportContentView.cs`; `And/AndroidSliceShell.ImportContent.Compose.cs` | None | High value: "add these 20 words" today means 20 single proposals or none. |

## Skills

| Action | Where in app | Sam today | Notes/gap |
|---|---|---|---|
| List / count skills | `UI/Skills.razor`; `iOS/Skills/SkillsView.cs`; `And/AndroidSliceShell.Skills.Compose.cs` | Full: `get_skill_list` (registry 254), `get_current_profile_summary` `SkillCount` | Archived skills excluded on both sides. |
| View a skill | `UI/SkillEdit.razor` | Full: `get_skill_detail` (registry 266) | |
| Create a skill | `UI/SkillAdd.razor`; `iOS/Skills/SkillProfileFormView.cs` | Full: `propose_skill_entry` (registry 427) | |
| Edit a skill | `UI/SkillEdit.razor` | Full: `propose_skill_edit` (registry 442) | Title and description. |
| Delete a skill | `UI/SkillEdit.razor` (RequestDelete) | Partial: `propose_skill_archive` (registry 457) | Sam archives (hides) rather than deletes; undo only for a few minutes. |
| Skill progress / proficiency | `ProgressService` skill progress, dashboard | None | |

## Activities and practice

| Action | Where in app | Sam today | Notes/gap |
|---|---|---|---|
| Start an activity (quiz, flashcards, cloze, translation, writing, reading, shadowing, conversation, scene, word association, matching, number drill, video watching) | `UI/*.razor` per activity; `iOS/Activities/*.cs`, `iOS/Quiz/`; `And/AndroidSliceShell.*.Compose.cs` | None | Sam cannot launch anything. Its limitation cards can only point to Activity Log, Vocabulary, Settings, Skills, Writing and Feedback (`src/SentenceStudio.Contracts/Coach/CoachRouteCatalog.cs:40-91`). |
| Do the activity inside Sam (quiz me, correct my sentence, explain grammar) | n/a | Partial: tutoring in chat | Conversational only; answers are not graded or saved to progress, and due words stay hidden (policy 9.3). |
| Resume a previous quiz session | `UI/VocabQuiz.razor` (ResumePreviousSessionAsync) | None | |
| Minimal pairs: create, delete, start a session | `UI/MinimalPairs.razor`, `UI/MinimalPairCreate.razor`; `iOS/Activities/MinimalPairsScreen.cs`; `And/AndroidSliceShell.MinimalPairs.Compose.cs` | None | |
| Scene: add / delete images | `UI/Scene.razor` (AddImage, DeleteSelectedImages) | None | |

## Today's Plan and dashboard

| Action | Where in app | Sam today | Notes/gap |
|---|---|---|---|
| Read today's plan and completion | `UI/Index.razor`; `iOS/Dashboard/TodaysPlanView.cs`; `And/AndroidSliceShell.Dashboard.Compose.cs` | Full: `get_current_plan_summary` (registry 329) | No item text, by design. |
| Generate / regenerate today's plan | `UI/Index.razor` (GeneratePlanAsync, RegeneratePlanAsync) | Partial: `preview_practice_plan` (registry 197) and the Coach plan pane | Sam builds a preview and revises its own session plan; it never writes today's plan (no `DailyPlan` write handler exists, `Coach/Operations/CoachWriteEnums.cs:207` is unused by handlers). |
| Launch a plan item | `UI/Index.razor` (LaunchPlanItem) | None | |
| Switch to "choose my own" mode and pick a resource/skill | `UI/Index.razor` (SetMode); `iOS/Dashboard/ChooseOwnView.cs` | None | |
| Vocabulary stats tiles (New, Learning, Familiar, Review, Known, weekly accuracy) | `UI/Index.razor:474-525`; native dashboards | Partial: `get_vocabulary_due_summary` (registry 173) | Bands and lapse rate exist but over a different word population (see the answer above). No 7-day accuracy figure. |
| Due count | plan card `UI/Index.razor:388` | Partial: `DueNowCount` | Counts all progress rows due, not only resource-linked words. |

## Progress and history

| Action | Where in app | Sam today | Notes/gap |
|---|---|---|---|
| When did I last study | none on one screen | Full: deterministic route (`CoachSessionService.cs:802`), `get_practice_history_summary` (registry 210) | |
| Minutes by activity, input/output balance | `src/SentenceStudio.UI/Shared/PracticeBalanceBar.razor`, dashboard | Full: `get_practice_balance` (registry 161) | 7/14/30-day windows only. |
| Activity log: list, filter input/output, load more | `UI/ActivityLog.razor`; `iOS/ActivityLog/ActivityLogView.cs`; `And/AndroidSliceShell.ActivityLog.Compose.cs` | Partial | Totals only; no per-session list. Sam can route to the log. |
| Practice heat map | `ProgressService.GetPracticeHeatAsync` (dashboard) | Partial: `ActiveDayCount` in `get_practice_balance` | No per-day series. |
| Per-word history (attempts, accuracy) | quiz learning details, `iOS/Quiz/QuizLearningDetailsView.cs` | Full: `get_vocabulary_word_detail` | Named word only. |

## Diary

| Action | Where in app | Sam today | Notes/gap |
|---|---|---|---|
| List entries, open today's entry | `UI/Diary.razor`; `iOS/Activities/DiaryScreen.cs`; `And/AndroidSliceShell.Diary.Compose.cs` | None | Resource tools never return diary text (registry 285). |
| Write / save an entry, get a new prompt | `UI/DiaryEditor.razor` | None | |
| Get AI feedback on an entry | `UI/DiaryViewer.razor` (LoadFeedback) | Partial: tutoring in chat | Only if the learner pastes the text into Sam. |

## Settings and profile

| Action | Where in app | Sam today | Notes/gap |
|---|---|---|---|
| Read languages, level, session length | `UI/Profile.razor`, `UI/Settings.razor`; `iOS/Profile/ProfileView.cs`, `iOS/Settings/SettingsView.cs` | Full: `get_learner_settings_summary`, `get_current_profile_summary`, `get_learner_profile_summary` (registry 317, 303, 151) | Three overlapping tools for one record. |
| Change name, languages, display language, level, session length | `UI/Profile.razor` (SaveProfile) | None | `propose_preference_change` exists (registry 517) but declines every setting (`Coach/Operations/Handlers/CoachPreferenceHandler.cs:15-22`). |
| Quiz settings (prompt language, modalities, autoplay, mnemonics, auto-advance), voice | `UI/Settings.razor`; `iOS/Settings/QuizSettingsView.cs` | None | Not readable either: the settings summary has no quiz fields (`SamReadToolResults.cs` `LearnerSettingsSummary`). |
| Theme, light/dark, text size | `UI/Settings.razor`; `iOS/Settings/AppearanceSettingsView.cs` | None | Presentation; low value. |
| What's new / release notes | `UI/Settings.razor` (ShowReleaseNotes) | None | |
| Delete all Sam conversation history | `UI/Settings.razor` (AskDeleteCoachHistory); `src/SentenceStudio.Native/Sam/NativeSamPresenter.Manage.cs` | None | Sam can route to Settings. Irreversible; keep in the screen. |
| Sam conversations: new, rename, close, delete; memory panel | `src/SentenceStudio.UI/Shared/Coach/CoachConversationList.razor`, `CoachMemoryPanel.razor`; `iOS/Sam/SamView.cs` | None | Handled by Sam's own UI, not by chat. |

## Account

| Action | Where in app | Sam today | Notes/gap |
|---|---|---|---|
| Sign in, register, sign out, switch local profile | `UI/LoginPage.razor`, `UI/RegisterPage.razor`, `UI/Auth.razor`; `iOS/Authentication/` | Excluded (policy) | Policy section 6: credential actions. |
| Change password, forgot password | `UI/Profile.razor` (ChangePassword), `UI/ForgotPasswordPage.razor`; native profile views | Excluded (policy) | |
| Delete my account | `UI/Profile.razor` (RequestDelete); `iOS/Profile/ProfileView.cs`; `And/AndroidSliceShell.Profile.Compose.cs` | Excluded (policy) | |
| Change email | `UI/Profile.razor` | Excluded (policy) | `propose_preference_change` "cannot reach the email address" (registry 528). |
| Full account export | Settings (per `CoachRouteCatalog.cs` comment) | Excluded (policy) | |
| Onboarding (languages, level, starter content) | `UI/Onboarding.razor`; `iOS/Onboarding/OnboardingView.cs` | None | Runs before Sam is available. |

## Feedback

| Action | Where in app | Sam today | Notes/gap |
|---|---|---|---|
| File a bug or feature request | `UI/Feedback.razor` (web/MAUI); no feedback form on the native heads | Full: `propose_feedback` (registry 547, uncommitted) | Attaches the screen the learner was on. On native heads Sam is the only path. |
| Preview / AI-polish the report before sending | `UI/Feedback.razor` (PreviewFeedback) | Partial | Sam's proposal card is the preview; no separate polish step. |

## Suggested order

A suggestion for the Captain to rank, by likely value against effort:

1. **Make Sam's word count match the dashboard.** Point `get_current_profile_summary` and
   `get_vocabulary_due_summary` at the same population as `GetVocabSummaryCountsAsync`, or
   return both numbers with labels. Small change, fixes a visible wrong answer.
2. **Example sentences for a word:** read them in `get_vocabulary_word_detail` (non-due words
   only) and add `propose_example_sentence_entry` / edit / removal. Named by the Captain.
3. **Search filters on `list_user_vocabularies`:** tag, resource, status, and include
   never-practised words, so "find my food words" and "how many words are tagged X" work.
4. **Bulk word add:** a `propose_vocabulary_batch` (or reuse the Import Content parser) so a
   pasted list becomes one proposal instead of many.
5. **Set a word's status** (known / familiar / learning), single and bulk.
6. **Profile preferences:** approve the first settings in `CoachPreferenceHandler` (session
   length, target level, display language), plus quiz settings read.
7. **Launch an activity or plan item** from Sam (a route-catalog destination with a resource or
   word focus), so "quiz me on these" ends in the real quiz.
8. **Words on a resource:** list a resource's words (non-due) and unlink from one resource.
9. **Regenerate / adopt today's plan** from Sam's preview (needs a `DailyPlan` handler).
10. **Activity log and diary reads:** per-session list, and diary entry listing with an
    explicit privacy decision first.
