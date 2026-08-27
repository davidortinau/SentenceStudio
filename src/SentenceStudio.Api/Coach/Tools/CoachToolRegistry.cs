using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// The source of truth for which tools the coach may use.
/// Replaces the static <see cref="CoachToolNames.All"/> as the canonical registry.
/// Registrations are frozen after startup validation; late registration throws.
/// </summary>
public interface ICoachToolRegistry
{
    /// <summary>All registered tools, in registration order.</summary>
    IReadOnlyList<CoachToolRegistration> All { get; }

    /// <summary>
    /// The tools enabled for the current configuration.
    /// Filters <see cref="All"/> by feature flags in <see cref="CoachOptions"/>.
    /// </summary>
    IReadOnlyList<CoachToolRegistration> Enabled { get; }

    /// <summary>The enabled tool names, for allow-list checks.</summary>
    IReadOnlyList<string> EnabledNames { get; }

    /// <summary>True if the named tool is registered (regardless of feature flags).</summary>
    bool IsRegistered(string name);

    /// <summary>True if the named tool is enabled (registered and all feature requirements met).</summary>
    bool IsEnabled(string name);

    /// <summary>Returns the registration for the named tool, or null if not registered.</summary>
    CoachToolRegistration? Find(string name);

    /// <summary>
    /// True once the registry has been sealed and no further tool may be added.
    /// </summary>
    /// <remarks>
    /// Startup validation asserts this before it trusts the registry as the source of truth for
    /// embargo coverage. Scanning an open registry would prove nothing: any code that ran later
    /// could add a tool whose result shape was never examined.
    /// </remarks>
    bool IsFrozen { get; }
}

/// <inheritdoc />
public sealed class CoachToolRegistry : ICoachToolRegistry
{
    private readonly Dictionary<string, CoachToolRegistration> _byName;
    private readonly List<CoachToolRegistration> _all;
    private readonly List<CoachToolRegistration> _enabled;
    private readonly List<string> _enabledNames;
    private bool _frozen;

    public CoachToolRegistry(CoachOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _all = [];
        _byName = new Dictionary<string, CoachToolRegistration>(StringComparer.Ordinal);
        _enabled = [];
        _enabledNames = [];

        RegisterCoreTools();
        RegisterSamReadTools();
        RegisterSamWriteTools();

        Recompute(options);
    }

    public IReadOnlyList<CoachToolRegistration> All => _all;
    public IReadOnlyList<CoachToolRegistration> Enabled => _enabled;
    public IReadOnlyList<string> EnabledNames => _enabledNames;

    public bool IsRegistered(string name) => _byName.ContainsKey(name);
    public bool IsEnabled(string name) => _enabledNames.Contains(name);
    public CoachToolRegistration? Find(string name) => _byName.GetValueOrDefault(name);

    /// <inheritdoc />
    public bool IsFrozen => _frozen;

    /// <summary>
    /// Seals the registry. After this call, <see cref="Register"/> throws.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="CoachToolServiceCollectionExtensions"/> during startup, after every
    /// tool is registered and before <see cref="Validation.CoachOutputContract"/> validates the
    /// result shapes. The order matters: validation is only meaningful against a registry that
    /// can no longer grow, and freezing before validation is what turns "these shapes passed" into
    /// "every shape the coach can ever return passed". Freezing is idempotent.
    /// </remarks>
    public void Freeze() => _frozen = true;

    /// <summary>
    /// Adds a registration. Throws if already frozen or if the name is duplicate.
    /// </summary>
    public void Register(CoachToolRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (_frozen)
        {
            throw new InvalidOperationException(
                $"The coach tool registry is frozen. Cannot register '{registration.Name}' after startup validation.");
        }

        if (!_byName.TryAdd(registration.Name, registration))
        {
            throw new InvalidOperationException(
                $"The tool '{registration.Name}' is already registered.");
        }

        _all.Add(registration);
    }

    private void Recompute(CoachOptions options)
    {
        _enabled.Clear();
        _enabledNames.Clear();

        foreach (var reg in _all)
        {
            if (MeetsRequirements(reg, options))
            {
                _enabled.Add(reg);
                _enabledNames.Add(reg.Name);
            }
        }
    }

    private static bool MeetsRequirements(CoachToolRegistration reg, CoachOptions options)
    {
        foreach (var feature in reg.RequiredFeatures)
        {
            switch (feature)
            {
                case "SamOverlay" when !options.IsSamOverlayEnabled:
                case "SamReadTools" when !options.IsSamReadToolsEnabled:
                case "SamWriteTools" when !options.IsSamWriteToolsEnabled:
                    return false;
            }
        }

        return true;
    }

    private void RegisterCoreTools()
    {
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.GetLearnerProfileSummary,
            ResultType = typeof(LearnerProfileSummary),
            RiskClass = CoachToolRiskClass.Read,
            EffectClass = CoachCapabilityEffectClass.Read,
            Description = "Reads the learner's languages, display language, preferred session length and level. A settings "
                + "snapshot, not a list: one record with no order and no paging, so there is nothing to sort and no "
                + "count to interpret. Scoped to this learner, and nothing is withheld."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.GetPracticeBalance,
            ResultType = typeof(PracticeBalanceSummary),
            RiskClass = CoachToolRiskClass.Read,
            EffectClass = CoachCapabilityEffectClass.Read,
            Description = "Reads how many minutes the learner practised each activity type over the last seven, fourteen or "
                + "thirty days, most minutes first. Bounded to that window: practice outside it is absent, not "
                + "zero. Activity types with nothing logged in the window are withheld and reported as a count with "
                + "a reason, so matched equals returned plus withheld and there is no further page to fetch. The "
                + "counts are activity types; minutes are values on the rows."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.GetVocabularyDueSummary,
            ResultType = typeof(VocabularyDueSummary),
            RiskClass = CoachToolRiskClass.Read,
            EffectClass = CoachCapabilityEffectClass.Read,
            Description = "Reads counts of the learner's tracked words — due now, due this week, never practised, and the "
                + "total tracked — with mastery bands and lapse rate, plus the most frequent category tags found on "
                + "the due words, most frequent first. The word counts cover every tracked word, not only the due "
                + "ones. The scope's counts describe the tag breakdown rather than the words: the tag list is a "
                + "bounded page, so matched is how many distinct tags were found and truncation means more exist. A "
                + "word carrying two tags is counted under each. Returns counts only, never the words themselves."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.GetResourceCatalog,
            ResultType = typeof(ResourceCatalogSummary),
            RiskClass = CoachToolRiskClass.Read,
            EffectClass = CoachCapabilityEffectClass.Read,
            Description = "Lists the resources the learner owns, as metadata only — never their contents. Ordered by "
                + "how recently each was used, most recent first, with never-used resources last. Large catalogues "
                + "come back as one page: when that happens the result says so, and the total count is the whole "
                + "catalogue while the rows returned are only the page."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.PreviewPracticePlan,
            ResultType = typeof(PlanPreviewSummary),
            RiskClass = CoachToolRiskClass.Read,
            EffectClass = CoachCapabilityEffectClass.Read,
            Description = "Builds a read-only preview of a practice plan for the constraints given. The result is "
                + "computed, not stored: it is a projection of what a plan would look like, so nothing here has been "
                + "saved and asking twice may answer differently as the learner's data moves. Items come back in the "
                + "order they would be practised, highest priority first. The constraints supplied by the caller "
                + "are the only filter, and anything they exclude is simply not in the projection rather "
                + "than reported as withheld. Counts are planned items."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.GetPracticeHistorySummary,
            ResultType = typeof(PracticeHistorySummary),
            RiskClass = CoachToolRiskClass.Read,
            EffectClass = CoachCapabilityEffectClass.Read,
            Description = "Reads the date of the learner's most recent recorded practice and how many whole days "
                + "have passed since then. The read covers the learner's full history, not a window. Returns null "
                + "fields when the learner has never practised. It returns a date and a count only (no order applies), "
                + "never the content of what was practised."
        });
    }

    private void RegisterSamReadTools()
    {
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.ListUserVocabularies,
            ResultType = typeof(VocabularySearchResult),
            RiskClass = CoachToolRiskClass.Read,
            EffectClass = CoachCapabilityEffectClass.Read,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools"],
            Description = "Searches the learner's vocabulary, ordered by mastery, strongest first. Words currently due for "
                + "review are always excluded and reported as a withheld count with a reason, never as content; a "
                + "query, when supplied, narrows the search further. Matched, returned and withheld can all differ, "
                + "and returned plus withheld need not equal matched when the answer is also a page. Each match "
                + "carries its term, gloss, tags and mastery. Use get_vocabulary_due_summary for due counts, or "
                + "get_vocabulary_word_detail for one named word."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.GetVocabularyWordDetail,
            ResultType = typeof(VocabularyWordDetail),
            RiskClass = CoachToolRiskClass.Read,
            EffectClass = CoachCapabilityEffectClass.Read,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools"],
            Description = "Reads one vocabulary word the learner owns, named by the caller: term, gloss, tags, mastery, and "
                + "attempt counts. It returns no example sentences. A single item: no order, no paging, and no "
                + "count to interpret. The word is returned whether or not it is due, so this is the sanctioned way "
                + "past the due-word exclusion in list_user_vocabularies — for one word the learner has already "
                + "named, never for browsing."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.GetSkillList,
            ResultType = typeof(SkillListResult),
            RiskClass = CoachToolRiskClass.Read,
            EffectClass = CoachCapabilityEffectClass.Read,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools"],
            Description = "Lists the learner's skill profiles, most recently updated first. Archived skills are not "
                + "listed. Long lists come back as one page: when that happens the result says so, and the total "
                + "count is every skill the learner owns while the rows returned are only the page."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.GetSkillDetail,
            ResultType = typeof(SkillDetailResult),
            RiskClass = CoachToolRiskClass.Read,
            EffectClass = CoachCapabilityEffectClass.Read,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools"],
            Description = "Reads one skill profile the learner owns, named by the caller. A single item: no order, "
                + "no paging, and no count to interpret. The skill's description is returned when the "
                + "learner has set one."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.GetLearningResourceList,
            ResultType = typeof(LearningResourceListResult),
            RiskClass = CoachToolRiskClass.Read,
            EffectClass = CoachCapabilityEffectClass.Read,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools"],
            Description = "Lists the learner's learning resources as metadata only, most recently updated first. "
                + "Never returns transcript or diary text — the count is of resources, not of anything inside them. "
                + "Long lists come back as one page: when that happens the result says so, and the total count is "
                + "every resource the learner owns while the rows returned are only the page."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.GetLearningResourceDetail,
            ResultType = typeof(LearningResourceDetailResult),
            RiskClass = CoachToolRiskClass.Read,
            EffectClass = CoachCapabilityEffectClass.Read,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools"],
            Description = "Reads metadata for one learning resource the learner owns, named by the caller. A single "
                + "item: no order, no paging, and no count to interpret. Never returns the transcript; any item "
                + "count describes the resource's contents without disclosing them."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.GetCurrentProfileSummary,
            ResultType = typeof(CurrentProfileSummary),
            RiskClass = CoachToolRiskClass.Read,
            EffectClass = CoachCapabilityEffectClass.Read,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools"],
            Description = "Reads the learner's profile overview: languages, display language, level, preferred session "
                + "length, days since they started, and how many words, skills and resources they own. A settings "
                + "snapshot, not a list — one record with no order and no paging. Days since start is how long the "
                + "account has existed, not a practice streak. The word, skill and resource numbers are totals the "
                + "learner owns, not rows returned by this call."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.GetLearnerSettingsSummary,
            ResultType = typeof(LearnerSettingsSummary),
            RiskClass = CoachToolRiskClass.Read,
            EffectClass = CoachCapabilityEffectClass.Read,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools"],
            Description = "Reads the learner's app settings and preferences as they are now. A settings snapshot: "
                + "one record, no order, no paging, no count to interpret. Reading a setting here is not permission "
                + "to change it — no setting is currently approved for change."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.GetCurrentPlanSummary,
            ResultType = typeof(CurrentPlanSummary),
            RiskClass = CoachToolRiskClass.Read,
            EffectClass = CoachCapabilityEffectClass.Read,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools"],
            Description = "Reads today's plan: each item's activity type, whether it is done, and minutes planned against "
                + "minutes spent. Bounded to one calendar day in the learner's own time zone, so an empty answer "
                + "means no plan exists for today, not that the learner has never had one. Items carry no order the "
                + "caller may rely on. Counts are plan items. It returns no item text — an activity type is a "
                + "closed category, and the plan's strategy label is bounded plan metadata."
        });
    }

    /// <summary>
    /// Registers the write-intent tools.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every one of them requires <c>SamWriteTools</c> on top of the read-tool features, so the
    /// whole family disappears together when the flag is off, and the flag defaults to off.
    /// </para>
    /// <para>
    /// Each carries a write risk class. That class is not documentation: the allow-list reads it
    /// to decide whether a <c>propose_</c> name may keep a verb like "removal", and the ledger
    /// reads it to decide which approval channel the proposal needs. A registration that named a
    /// write tool but declared <c>Read</c> would be refused at startup by the allow-list rather
    /// than quietly accepted.
    /// </para>
    /// <para>
    /// The result type is the same for all of them. A write tool answers with a proposal, never
    /// with the thing it proposed to change.
    /// </para>
    /// </remarks>
    private void RegisterSamWriteTools()
    {
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.ProposeVocabularyEntry,
            ResultType = typeof(CoachWriteProposalResult),
            RiskClass = CoachToolRiskClass.WriteSoft,
            EffectClass = CoachCapabilityEffectClass.LearnerData,
            Reversal = CoachCapabilityReversal.LedgerUndo,
            Confirmation = CoachCapabilityConfirmation.Accept,
            ReceiptKind = CoachCapabilityReceiptKind.Ledger,
            Scope = CoachCapabilityScope.Account,
            RequiredStage = CoachCapabilityStage.Semantic,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools", "SamWriteTools"],
            Description = "Proposes adding a vocabulary word to one of the learner's resources. Nothing is saved until the learner accepts the proposal."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.ProposeVocabularyEdit,
            ResultType = typeof(CoachWriteProposalResult),
            RiskClass = CoachToolRiskClass.WriteSoft,
            EffectClass = CoachCapabilityEffectClass.LearnerData,
            Reversal = CoachCapabilityReversal.LedgerUndo,
            Confirmation = CoachCapabilityConfirmation.Accept,
            ReceiptKind = CoachCapabilityReceiptKind.Ledger,
            Scope = CoachCapabilityScope.Account,
            RequiredStage = CoachCapabilityStage.Semantic,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools", "SamWriteTools"],
            Description = "Proposes changing a vocabulary word the learner owns. Nothing is saved until the learner accepts the proposal."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.ProposeVocabularyLink,
            ResultType = typeof(CoachWriteProposalResult),
            RiskClass = CoachToolRiskClass.WriteSoft,
            EffectClass = CoachCapabilityEffectClass.LearnerData,
            Reversal = CoachCapabilityReversal.LedgerUndo,
            Confirmation = CoachCapabilityConfirmation.Accept,
            ReceiptKind = CoachCapabilityReceiptKind.Ledger,
            Scope = CoachCapabilityScope.Account,
            RequiredStage = CoachCapabilityStage.Semantic,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools", "SamWriteTools"],
            Description = "Proposes linking an existing vocabulary word to one of the learner's resources. Nothing is saved until the learner accepts the proposal."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.ProposeVocabularyRemoval,
            ResultType = typeof(CoachWriteProposalResult),
            RiskClass = CoachToolRiskClass.WriteHard,
            EffectClass = CoachCapabilityEffectClass.LearnerData,
            Reversal = CoachCapabilityReversal.LedgerUndo,
            Confirmation = CoachCapabilityConfirmation.Confirm,
            ReceiptKind = CoachCapabilityReceiptKind.Ledger,
            Scope = CoachCapabilityScope.Account,
            RequiredStage = CoachCapabilityStage.Semantic,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools", "SamWriteTools"],
            Description = "Proposes removing a vocabulary word from the learner's resources. Nothing is removed until the learner confirms the proposal."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.ProposeSkillEntry,
            ResultType = typeof(CoachWriteProposalResult),
            RiskClass = CoachToolRiskClass.WriteSoft,
            EffectClass = CoachCapabilityEffectClass.LearnerData,
            Reversal = CoachCapabilityReversal.LedgerUndo,
            Confirmation = CoachCapabilityConfirmation.Accept,
            ReceiptKind = CoachCapabilityReceiptKind.Ledger,
            Scope = CoachCapabilityScope.Account,
            RequiredStage = CoachCapabilityStage.Semantic,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools", "SamWriteTools"],
            Description = "Proposes creating a skill for the learner to practise. Nothing is saved until the learner accepts the proposal."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.ProposeSkillEdit,
            ResultType = typeof(CoachWriteProposalResult),
            RiskClass = CoachToolRiskClass.WriteSoft,
            EffectClass = CoachCapabilityEffectClass.LearnerData,
            Reversal = CoachCapabilityReversal.LedgerUndo,
            Confirmation = CoachCapabilityConfirmation.Accept,
            ReceiptKind = CoachCapabilityReceiptKind.Ledger,
            Scope = CoachCapabilityScope.Account,
            RequiredStage = CoachCapabilityStage.Semantic,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools", "SamWriteTools"],
            Description = "Proposes changing a skill the learner owns. Nothing is saved until the learner accepts the proposal."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.ProposeSkillArchive,
            ResultType = typeof(CoachWriteProposalResult),
            RiskClass = CoachToolRiskClass.WriteHard,
            EffectClass = CoachCapabilityEffectClass.LearnerData,
            Reversal = CoachCapabilityReversal.LedgerUndo,
            Confirmation = CoachCapabilityConfirmation.Confirm,
            ReceiptKind = CoachCapabilityReceiptKind.Ledger,
            Scope = CoachCapabilityScope.Account,
            RequiredStage = CoachCapabilityStage.Semantic,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools", "SamWriteTools"],
            Description = "Proposes archiving a skill the learner owns, which hides it from their skills list without deleting it. Archiving can only be undone in the few minutes right after it happens; the app has no archive view to restore from later. Nothing changes until the learner confirms the proposal."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.ProposeResourceEntry,
            ResultType = typeof(CoachWriteProposalResult),
            RiskClass = CoachToolRiskClass.WriteSoft,
            EffectClass = CoachCapabilityEffectClass.LearnerData,
            Reversal = CoachCapabilityReversal.LedgerUndo,
            Confirmation = CoachCapabilityConfirmation.Accept,
            ReceiptKind = CoachCapabilityReceiptKind.Ledger,
            Scope = CoachCapabilityScope.Account,
            RequiredStage = CoachCapabilityStage.Semantic,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools", "SamWriteTools"],
            Description = "Proposes creating a learning resource for the learner. Nothing is saved until the learner accepts the proposal."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.ProposeResourceEdit,
            ResultType = typeof(CoachWriteProposalResult),
            RiskClass = CoachToolRiskClass.WriteSoft,
            EffectClass = CoachCapabilityEffectClass.LearnerData,
            Reversal = CoachCapabilityReversal.LedgerUndo,
            Confirmation = CoachCapabilityConfirmation.Accept,
            ReceiptKind = CoachCapabilityReceiptKind.Ledger,
            Scope = CoachCapabilityScope.Account,
            RequiredStage = CoachCapabilityStage.Semantic,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools", "SamWriteTools"],
            Description = "Proposes changing a learning resource the learner owns. Nothing is saved until the learner accepts the proposal."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.ProposeResourceRemoval,
            ResultType = typeof(CoachWriteProposalResult),
            RiskClass = CoachToolRiskClass.WriteHard,
            EffectClass = CoachCapabilityEffectClass.LearnerData,
            Reversal = CoachCapabilityReversal.LedgerUndo,
            Confirmation = CoachCapabilityConfirmation.Confirm,
            ReceiptKind = CoachCapabilityReceiptKind.Ledger,
            Scope = CoachCapabilityScope.Account,
            RequiredStage = CoachCapabilityStage.Semantic,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools", "SamWriteTools"],
            Description = "Proposes deleting a learning resource the learner owns. Nothing is deleted until the learner confirms the proposal."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.ProposePreferenceChange,
            ResultType = typeof(CoachWriteProposalResult),
            RiskClass = CoachToolRiskClass.WriteHard,
            EffectClass = CoachCapabilityEffectClass.LearnerData,
            Reversal = CoachCapabilityReversal.LedgerUndo,
            Confirmation = CoachCapabilityConfirmation.Confirm,
            ReceiptKind = CoachCapabilityReceiptKind.Ledger,
            Scope = CoachCapabilityScope.Account,
            RequiredStage = CoachCapabilityStage.Semantic,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools", "SamWriteTools"],
            Description = "Proposes changing one of the learner's own settings. No setting is currently approved for change, so this tool declines every request and writes nothing — tell the learner to change it in the app's own settings screen instead. Cannot reach the email address, the account, or any stored key."
        });
        Register(new CoachToolRegistration
        {
            Name = CoachToolNames.ProposeYouTubeImport,
            ResultType = typeof(CoachWriteProposalResult),
            RiskClass = CoachToolRiskClass.WriteHard,
            EffectClass = CoachCapabilityEffectClass.ExternalEffect,
            Reversal = CoachCapabilityReversal.None,
            Confirmation = CoachCapabilityConfirmation.Confirm,
            ReceiptKind = CoachCapabilityReceiptKind.Ledger,
            Scope = CoachCapabilityScope.Account,
            RequiredStage = CoachCapabilityStage.External,
            EmbargoScope = CoachEmbargoScope.ToolResult,
            RequiredFeatures = ["SamOverlay", "SamReadTools", "SamWriteTools"],
            Description = "Proposes importing a YouTube video's captions as a learning resource. Accepts YouTube video addresses only. Nothing is fetched or saved until the learner confirms the proposal."
        });
    }
}
