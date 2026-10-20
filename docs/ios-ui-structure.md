# iOS SwiftUI source organization

This is a source-only refactor of the existing iOS replacement head. `SwiftView`
is the Microsoft.SwiftUI C# node facade, **not** a native Swift `View` struct:
splitting these composers improves ownership/discovery but does not create
SwiftUI `@State`/`@Observable` invalidation boundaries or promise a performance
gain. The existing SwiftUI-rendered node tree, IDs, routes, appearance tokens,
effects, actions and data ownership remain the contract. The current resource
vocabulary-row appearance is still awaiting Captain's acceptance.

## Roles and file tree

`Program.cs` starts the SwiftUI app; `NativeServices.cs` retains DI/storage.
`Platform/`, `Native/`, `Resources/`, manifests and the project configuration
remain unchanged.

| Folder | Files and responsibility |
| --- | --- |
| `App/` | `ApplicationScene.cs`: tab/navigation/presentation hosts, mounted-node reconciliation, event subscriptions and disposal. `ApplicationController.cs` + `ApplicationController.Quiz.cs`: existing centralized presenter/startup/action state; the Quiz partial remains adjacent, not scattered into view composers. |
| `Features/Authentication/` | `SignInView.cs`: sign-in control composition. |
| `Features/Onboarding/` | `OnboardingView.cs`: onboarding steps and controls. |
| `Features/Dashboard/` | `DashboardView.cs`: page, mode switcher and toolbar; `DashboardWelcomeView.cs`: new-user choices; `TodaysPlanView.cs`: progress, phase and plan list; `PlanInsightView.cs`: narrative and categories; `PlanCardView.cs`: one plan card; `ChooseOwnView.cs`: activity/selection section; `ChooseOwnSelectorView.cs`: searchable selector content and Done toolbar; `DashboardLearningProgressView.cs`: vocabulary/numbers sections; `DashboardActions.cs`: callbacks supplied by the controller. These named sections have one caller; they are **not** advertised as reusable components. |
| `Features/LearningResources/` | `LearningResourcesView.cs`: browser controls, list/grid and toolbar; `LearningResourceRowView.cs`: one list/grid row; `LearningResourceView.cs`: combined Add/Edit/smart-read-only form and toolbar; `LearningResourceFieldsView.cs`: actual fields and picker/editor composition; `LearningResourceVocabularyView.cs`: vocabulary card/rows and appendable row builder; `LearningResourceQuickAddView.cs`: field and suggestion contents; `LearningResourcesCoordinator.cs`: existing **single** stateful presenter coordinator, registration, projections, guards, cancellation and incremental node updates; `LearningResourcesActions.cs`, `LearningResourceActions.cs`, `LearningResourceInputs.cs`: browser/form callbacks and handle-registration inputs; `LearningResourceViewState.cs`: form projection/word; `LearningResourceInputSelection.cs`: picker and Quick Add keyboard selection. No new view model or service layer. |
| `Features/Quiz/` | `QuizView.cs`: phase composition, fixed footer, toolbar/photo-cover content; `QuizQuestionView.cs`: question, choice and inputs; `QuizFeedbackView.cs`: correction/next controls; `QuizSentenceView.cs`: sentence editor and grades; `QuizSummaryView.cs`: results and statistics; `QuizLearningDetailsView.cs`: existing learning-details sheet body; `QuizActions.cs`, `QuizViewInputs.cs`: controller callbacks and transient input projection. |
| `Features/Settings/` | `SettingsView.cs`: settings page and feedback; `AppearanceSettingsView.cs`: theme/mode/text-size; `QuizSettingsView.cs`: quiz/voice preferences; `QuizSettingsDraft.cs`: existing mutable draft projection and input snapping. |
| `Styling/` | `SourceUiAppearance.cs`: CSS/theme colors; `SourceUiTypography.cs`: text roles and **unchanged** size/weight/line-height metrics; `SourceUiLayout.cs`: layout tokens; `SourceUiStyle.cs`: node styling. |

Namespaces follow `SentenceStudio.Native.iOS.App`,
`SentenceStudio.Native.iOS.Features.<Feature>` and
`SentenceStudio.Native.iOS.Styling`. Shared `Native*` domain/presenter/port
names and the assembly identity are not renamed.

**Residual debt:** `ApplicationController.cs` and its adjacent `.Quiz.cs`
partial remain large because they own existing app/presenter state and
orchestration. This extraction does not pretend to decompose that workflow or
scatter it into view files. `LearningResourcesCoordinator` likewise retains
its pre-existing guarded state machine in one place. Splitting either is a
separate, approved task only if a real behavior/ownership need emerges.

## Mounted-node and lifetime contract (before extraction)

| Node and exact shape | Creator / registrar | Retainer, reconciliation and disposal |
| --- | --- | --- |
| Root, tabs, Dashboard/Resources `NavigationStack`, selector `Searchable`/`Sheet` | `ApplicationScene.InstallTabs`; feature composers only supply content | Scene retains `_root`, `_tabs`, `_dashboardStack`, `_resourcesStack`, `_selectorSearch`, `_selectorSheet`. Destination switch removes and recursively disposes the old root child; scene disposal unsubscribes and disposes `_root`. Routes and pop callbacks do not move into composers. |
| Browser search `native.resources.search`, paired filters and view-mode group | `LearningResourcesView.Page` creates search as the **first direct content child** and the filters/toggle control row as the second child under `Toolbar(Page(Scroll(VStack(...))), actions)` in both Grid and List; scene `Find` registers search after mount. The control row is adaptive, but its nested two-filter HStack never stacks. Each filter is a native SwiftUI `Menu` whose source-styled **label** contains flexible, unlimited selected-value `Text` and a chevron, so the border can follow the text's intrinsic wrapped height without clipping or forcing a vertical filter fallback | `ApplicationScene.UpdateResources` retains the same Scroll viewport, mounted search and both control groups when toolbar, menu catalogs and control shapes match. It updates menu values, selected-option visuals/accessibility and toggle appearance, replaces/disposes only later content siblings, transfers next later siblings and disposes the unused next tree. Filter callbacks read the current presenter selection rather than capturing a stale other filter. List resources use a one-column native `LazyVGrid` with group border and dividers; Grid resources remain a responsive native `LazyVGrid`. Only browser titles in the shared row composer have the native SwiftUI `WithLineLimit(1)` modifier; metadata/date and all other Text remain unchanged. Loading has no search and requires full replacement. A breakpoint/theme replacement re-finds search and restores focus when applicable. No new wrapper between search and content. Runtime accessibility-extra-large geometry remains pending root review; static structure and a build are not acceptance. |
| Form fields, Save/import controls, Quick Add field `native.resources.edit.quick.search`, staged grid/count | `LearningResourceView`, `LearningResourceFieldsView`, `LearningResourceVocabularyView` and `LearningResourceQuickAddView` create existing nodes. `LearningResourceInputs` callbacks register **those same instances** in `LearningResourcesCoordinator.RenderForm` | Scene owns `_formSlot`; coordinator retains `_saveButtons`, `_importButtons`, `_quickSearchField`, `_stagedGrid`, `_stagedCount` and form-generation guards. A projection change replaces/disposes the mounted form child; a width breakpoint restores focus by control ID. Do not add a container that changes registered handles. |
| Vocabulary card `native.resources.form.vocabulary.card` with heading at child `0`, empty/rows as direct later children; rows container `native.resources.form.vocabulary.rows` | `LearningResourceVocabularyView` creates and registers the **card itself**, its direct rows container and row builder | Coordinator retains `_vocabularySection`; `TryAppendStagedQuickAdd` checks `Children[0]`, removes/disposes the empty child, finds/adds the direct rows container and appends one word; staged-grid removal similarly mutates direct children/count. Keep card, IDs, direct-child positions and object identity. |
| Quick Add suggestion listbox and overlay | `LearningResourceQuickAddView` creates listbox/options; `ApplicationScene.ResourceFormContent` creates `QuickAddSuggestions(content, fieldId, slot, dismiss)` | Scene retains `_quickSuggestions` + `_quickSuggestionSlot`; versioned `RefreshQuickSuggestions` transfers replacement listbox **children into the same slot** on main queue (identity check), disposing only detached children and unused next parent. Coordinator retains option instances and field focus; no form remount for a suggestion-only revision. |
| Quiz detail sheet/photo cover, resource file importer/delete confirmation/conflict alert, Settings slot | Named views create their **content/toolbars**; scene constructs/retains the presentation wrappers and navigation pushes; Quiz view returns its photo-cover handle to scene | Scene updates `.IsPresented`, delete pending, navigation titles and versioned slots, stops quiz progress on exit, drops stale form references on pop and guards delayed suggestion updates. Controller keeps underlying state/actions. Modal host callbacks, IDs, cancellation and recursive disposal stay as before. |

The read-only local Microsoft.SwiftUI binding source makes this structural
contract explicit: `SwiftUI.cs:454-463` attaches toolbar content as child `0`
before actions, `SwiftUI.cs:286-302` validates exactly one anchored text field
and attaches Quick Add content/slot, and `SwiftView.cs:1013-1055` requires a
detached child before disposal and recursively disposes attached descendants.
Detach a child before transferring it; dispose only an old detached tree or
the unused next tree. Never dispose a retained/mounted search, form control,
vocabulary section or suggestion slot. No wrapper introduced solely to change
a file boundary may alter this topology.

## Declaration and test relocation

| Old source declarations | New owner |
| --- | --- |
| `NativeSwiftUIScene` and root/tab/slot reconciliation | `App/ApplicationScene`; bottom-of-scene `NativeSwiftUIAppController.ResourcesSearchText` property rejoins `App/ApplicationController` |
| `NativeSwiftUIAppController` and `.Quiz` partial | `App/ApplicationController` and `App/ApplicationController.Quiz`; selector **UI body** moves to `Dashboard/ChooseOwnSelectorView`, Dashboard toolbar from scene moves to `Dashboard/DashboardView`, Quiz toolbar/photo content from scene moves to `Quiz/QuizView` |
| `NativeAuthViews.SignIn`, `NativeOnboardingViews.Page` | `Authentication/SignInView`, `Onboarding/OnboardingView` |
| `NativeFeatureViews.Dashboard`, `DashboardErrorText`, `DashboardPage`, `ModeButton` | `Dashboard/DashboardView`; new-user body to `DashboardWelcomeView`; phase/progress/card enumeration to `TodaysPlanView`; insight/category/icon body to `PlanInsightView`; card/icon body to `PlanCardView`; choose-own/activity body + icon to `ChooseOwnView`; vocabulary/numbers body, tiles and icons to `DashboardLearningProgressView` |
| `NativeFeatureViews.Quiz`, `QuizPage`, `RenderQuestion`, `QuizChoice`, `RenderFeedback`, `RenderSentence`, `Statistic` | `Quiz/QuizView`, `QuizQuestionView`, `QuizQuestionView`, `QuizFeedbackView`, `QuizSentenceView`, `QuizSummaryView` respectively; controller callbacks and input projection go to `QuizActions`/`QuizViewInputs` |
| `NativeQuizDetailsViews.Page/Value/StatusKey` | `Quiz/QuizLearningDetailsView` |
| `NativeResourceViews.Browser/BrowserHeader/Filter`, `Row` | `LearningResources/LearningResourcesView`, `LearningResourceRowView` |
| `NativeResourceViews.Form/FormPage/CardSection/FormatError`, `Field/Area/AreaHeight/Select`, `AddVocabulary/AddVocabularyRows/CreateEditVocabularyRows/AppendEditVocabularyRow/EditVocabularyRow/CreateStagedTable/RemoveWordButton`, `QuickAdd/QuickAddSuggestions` | `LearningResourceView`, `LearningResourceFieldsView`, `LearningResourceVocabularyView`, `LearningResourceQuickAddView` respectively |
| `ResourceBrowserActions`, `ResourceFormActions`, `ResourceFormInputs`, `ResourceFormWord/ResourceFormViewState`, `ResourceQuickAddKey/ResourceQuickAddNavigation/ResourceRequiredPicker` | `LearningResourcesActions`, `LearningResourceActions`, `LearningResourceInputs`, `LearningResourceViewState`, `LearningResourceInputSelection` |
| `IosSwiftUIResourcesCoordinator` | `LearningResourcesCoordinator` with all previous state/async operations and registered-node references intact |
| `NativeAppearanceSettingsViews.Page` | `Settings/SettingsView` composes actual `AppearanceSettingsView` and `QuizSettingsView` sections; `NativeQuizSettingsDraft`/`NativeSettingsInput` relocate to `QuizSettingsDraft` |
| `SourceUiAppearance` first 415 lines and `SourceUiTypography` from the same file; `SourceUiLayout`, `SourceUiStyle` | Four separately named files under `Styling/` with unchanged styling and metrics; no `Features/SourceUi*` duplicates |
| Test-only `IosDashboardNavigation` metadata | Actual two-tab setup in `ApplicationScene.InstallTabs`, asserted there instead of retaining a second, unmounted tab specification |

Update explicit test `<Compile Include>` links for relocated pure types. Source
contract assertions must read the **owning** file: scene lifetime/navigation
and search/suggestion reconciliation tests read `App/ApplicationScene`; resource
list/form/field/row/staged tests read their corresponding LearningResources
view files, and coordinator mutation guards read `LearningResourcesCoordinator`;
Dashboard tests read the specific welcome/plan/card/choose-own/progress
composition; Quiz tests read question/feedback/sentence/summary/chrome owners;
Settings tests read appearance/quiz sections and draft; style tests read the
four Styling files. Keep behavior/IDs/phase assertions, replace only assertions
invalidated by file adjacency. Add focused checks that the mounted first search
child, directly registered vocabulary section/staged controls and retained
suggestion slot survive reconciliation without extra wrappers.

Baseline before extraction: iOS contracts **273/276 passing**, three unrelated
stale assertions (`Views.Form` despite the existing scroll form; test-pinned
SwiftUI preview.20 vs existing preview.21, reported twice). Correct these
contract expectations to the already-shipped shape/pin without altering the
production UI or dependency. Runtime launch, focus/keyboard/suggestion and
isolated staged-word interaction remain the root coordinator's post-backup
acceptance gate; an iOS build alone does not accept them.
