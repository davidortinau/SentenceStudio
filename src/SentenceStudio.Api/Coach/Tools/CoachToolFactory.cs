using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Api.Coach.Operations.Handlers;

namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// Builds the read-only tool set for one coach request.
/// Every function is closed: it names its own arguments, refuses an unknown
/// argument, and takes no user identifier.
/// </summary>
public interface ICoachToolFactory
{
    /// <summary>
    /// Creates the tool set for one turn.
    /// </summary>
    /// <remarks>
    /// The returned functions are unbudgeted. The per-turn call cap is applied by the caller at
    /// the harness boundary through <see cref="CoachToolCallBudget.Apply"/>, so a factory
    /// substituted in a test is capped on the same terms as the production one.
    /// </remarks>
    IReadOnlyList<AIFunction> CreateTools();
}

/// <inheritdoc />
/// <remarks>
/// The embargo contract runs before the first tool set is handed out. A tool answer type or
/// public coach contract type that carries identity data, an entity, or an open member type
/// stops the coach here rather than at the boundary it would have crossed.
/// </remarks>
public sealed class CoachToolFactory : ICoachToolFactory
{
    /// <summary>
    /// The options every tool result is marshalled with.
    /// </summary>
    /// <remarks>
    /// Carries <c>CoachResultScopeCaptureConverter</c>, which is how the observation seam sees the
    /// real <c>CoachResultScope</c> object. <c>AIFunctionFactory</c> marshals a result to a
    /// <c>JsonElement</c> before any wrapper can look at it, and the marshalled projection omits the
    /// six foundation members a consumer needs — so the capture has to happen here, on the way
    /// through, rather than after. The converter changes no output.
    /// </remarks>
    private static readonly JsonSerializerOptions SerializerOptions = BuildSerializerOptions();

    private static JsonSerializerOptions BuildSerializerOptions()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        options.Converters.Add(new Observation.CoachResultScopeCaptureConverter(options));
        return options;
    }

    private static readonly AIJsonSchemaCreateOptions SchemaOptions = new()
    {
        IncludeSchemaKeyword = false,
        TransformOptions = new AIJsonSchemaTransformOptions
        {
            DisallowAdditionalProperties = true,
            ConvertBooleanSchemas = true,
            MoveDefaultKeywordToDescription = true
        }
    };

    private readonly LearnerProfileSummaryTool _profile;
    private readonly PracticeBalanceTool _balance;
    private readonly VocabularyDueSummaryTool _vocabulary;
    private readonly ResourceCatalogTool _resources;
    private readonly PreviewPracticePlanTool _preview;
    private readonly ICoachToolRegistry _registry;
    private readonly IServiceProvider _serviceProvider;

    public CoachToolFactory(
        LearnerProfileSummaryTool profile,
        PracticeBalanceTool balance,
        VocabularyDueSummaryTool vocabulary,
        ResourceCatalogTool resources,
        PreviewPracticePlanTool preview,
        ICoachToolRegistry registry,
        IServiceProvider serviceProvider)
    {
        _profile = profile;
        _balance = balance;
        _vocabulary = vocabulary;
        _resources = resources;
        _preview = preview;
        _registry = registry;
        _serviceProvider = serviceProvider;
    }
    /// <inheritdoc />
    public IReadOnlyList<AIFunction> CreateTools()
    {
        CoachOutputContract.EnsureValid();
        return BuildTools();
    }

    private IReadOnlyList<AIFunction> BuildTools()
    {
        var tools = new List<AIFunction>();

        // Always include the core five
        tools.AddRange(BuildCoreTools());

        // Add Sam read tools if enabled
        foreach (var reg in _registry.Enabled)
        {
            if (reg.RequiredFeatures.Count > 0 && reg.RequiredFeatures.Contains("SamReadTools"))
            {
                // Write tools are dispatched separately; they share a single implementation and
                // are keyed by risk class rather than by name.
                if (reg.RiskClass is CoachToolRiskClass.WriteSoft or CoachToolRiskClass.WriteHard)
                {
                    continue;
                }

                var samTool = BuildSamReadTool(reg);
                if (samTool is not null)
                    tools.Add(samTool);
            }
        }

        // Add Sam write tools if enabled. Each produces a proposal; none of them writes.
        foreach (var reg in _registry.Enabled)
        {
            if (reg.RiskClass is not (CoachToolRiskClass.WriteSoft or CoachToolRiskClass.WriteHard))
            {
                continue;
            }

            var writeTool = BuildSamWriteTool(reg);
            if (writeTool is not null)
                tools.Add(writeTool);
        }

        // Wrap after the set is assembled, so nothing can be added to the turn's tool list
        // without also being counted against the turn's budget.
        return Observe(tools);
    }

    /// <summary>
    /// Wraps every tool in the turn's observation seam.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applied here rather than in the two agent arms. Both <c>BaselineLearningCoach</c> and
    /// <c>HarnessLearningCoach</c> call <c>CoachToolCallBudget.Apply(CreateTools())</c>, so
    /// wrapping inside the factory puts the seam <em>inside</em> the budget wrapper. That
    /// ordering is deliberate: a budget refusal is raised by the outer wrapper before the inner
    /// delegate runs, so the seam never sees the call and emits no observation for it — it is
    /// counted once at the turn boundary instead of being double-counted here — and a future third
    /// arm is covered by construction rather than by somebody remembering to wrap it.
    /// </para>
    /// <para>
    /// <b>One seam, an explicit subscriber list.</b> Subscriber 1 is the opportunity ledger,
    /// composed here from the recorder already in the container so its position is a property of
    /// this method rather than of registration order in a file somebody may reorder. Subscriber 2
    /// is the turn buffer, present only once a host has registered one. Anything else registered as
    /// <c>ICoachToolCallObserver</c> follows, in container order.
    /// </para>
    /// <para>
    /// The registration is in hand at build time, so the tool name every observer records is a
    /// server-side constant and never a model-supplied string.
    /// </para>
    /// <para>
    /// A host with no observers at all — every test that constructs this factory by hand — gets the
    /// tools back untouched, so the allow-list contract and every existing tool test see exactly
    /// what they saw before.
    /// </para>
    /// </remarks>
    private IReadOnlyList<AIFunction> Observe(IReadOnlyList<AIFunction> tools)
    {
        var observers = BuildObservers();
        if (observers.Count == 0)
        {
            return tools;
        }

        // One sequence per tool set. Both arms build the set exactly once per turn, so this is the
        // turn's ordinal source and needs no scope, no lifetime, and no reset.
        var sequence = new Observation.CoachToolCallSequence();
        var observed = new List<AIFunction>(tools.Count);

        foreach (var tool in tools)
        {
            var registration = _registry.Find(tool.Name);

            // A tool with no registration is a deployment defect the allow-list contract will
            // refuse a moment from now. Passing it through unwrapped keeps that the failure the
            // caller sees, rather than replacing it with one from the seam.
            observed.Add(registration is null
                ? tool
                : new Observation.CoachObservedFunction(tool, registration, observers, sequence));
        }

        return observed;
    }

    /// <summary>The turn's subscribers, in the order they are notified.</summary>
    private IReadOnlyList<Observation.ICoachToolCallObserver> BuildObservers()
    {
        var observers = new List<Observation.ICoachToolCallObserver>(2);

        var recorder = _serviceProvider.GetService<Opportunities.ICoachOpportunityRecorder>();
        if (recorder is not null)
        {
            var turnScope = _serviceProvider.GetService<Operations.CoachWriteTurnScope>();
            observers.Add(new Opportunities.Detection.CoachOpportunityToolObserver(recorder, turnScope));
        }

        var sink = _serviceProvider.GetService<Observation.ICoachTurnObservationSink>();
        if (sink is not null)
        {
            observers.Add(new Observation.CoachTurnObservationCollector(sink));
        }

        observers.AddRange(_serviceProvider.GetServices<Observation.ICoachToolCallObserver>());

        return observers;
    }

    private IReadOnlyList<AIFunction> BuildCoreTools() =>
    [
        Create(
            (CancellationToken ct) => _profile.GetAsync(ct),
            CoachToolNames.GetLearnerProfileSummary,
            "Reads the languages, the display language, the preferred session length, and the level of the learner."),

        Create(
            (CoachPracticeWindow? window, CancellationToken ct) => _balance.GetAsync(
                window ?? throw new CoachToolException(
                    CoachToolFailureKind.InvalidArgument,
                    CoachToolNames.GetPracticeBalance,
                    "The window must be seven, fourteen, or thirty days."),
                ct),
            CoachToolNames.GetPracticeBalance,
            "Reads the input minutes and the output minutes over the last seven, fourteen, or thirty days."),

        Create(
            (int? maxCategoryTags = null, CancellationToken ct = default) => _vocabulary.GetAsync(
                maxCategoryTags ?? VocabularyDueSummaryTool.DefaultTagCount, ct),
            CoachToolNames.GetVocabularyDueSummary,
            "Reads the counts, the mastery bands, the lapse rate, and the category tags for due words. It returns no words."),

        Create(
            (int? maxResults = null, CancellationToken ct = default) => _resources.GetAsync(
                maxResults ?? ResourceCatalogTool.DefaultResults, ct),
            CoachToolNames.GetResourceCatalog,
            "Reads the resources the learner owns, as metadata only. It returns no transcript and no diary text."),

        Create(
            (CoachPlanPreviewArguments? constraints = null, CancellationToken ct = default) =>
                _preview.PreviewAsync(constraints ?? new CoachPlanPreviewArguments(), ct),
            CoachToolNames.PreviewPracticePlan,
            "Builds a read-only plan preview for the supplied constraints. The preview changes nothing.")
    ];

    private AIFunction? BuildSamReadTool(CoachToolRegistration reg)
    {
        return reg.Name switch
        {
            CoachToolNames.ListUserVocabularies =>
                Create(
                    (string? query = null, int? maxResults = null, CancellationToken ct = default) =>
                        _serviceProvider.GetRequiredService<SamTools.VocabularySearchTool>()
                            .SearchAsync(query, maxResults ?? 10, ct),
                    reg.Name, reg.Description),

            CoachToolNames.GetVocabularyWordDetail =>
                Create(
                    (string wordId, CancellationToken ct = default) =>
                        _serviceProvider.GetRequiredService<SamTools.VocabularyWordDetailTool>()
                            .GetAsync(wordId, ct),
                    reg.Name, reg.Description),

            CoachToolNames.GetSkillList =>
                Create(
                    (int? maxResults = null, CancellationToken ct = default) =>
                        _serviceProvider.GetRequiredService<SamTools.SkillListTool>()
                            .GetAsync(maxResults ?? 20, ct),
                    reg.Name, reg.Description),

            CoachToolNames.GetSkillDetail =>
                Create(
                    (string skillId, CancellationToken ct = default) =>
                        _serviceProvider.GetRequiredService<SamTools.SkillDetailTool>()
                            .GetAsync(skillId, ct),
                    reg.Name, reg.Description),

            CoachToolNames.GetLearningResourceList =>
                Create(
                    (int? maxResults = null, CancellationToken ct = default) =>
                        _serviceProvider.GetRequiredService<SamTools.LearningResourceListTool>()
                            .GetAsync(maxResults ?? 20, ct),
                    reg.Name, reg.Description),

            CoachToolNames.GetLearningResourceDetail =>
                Create(
                    (string resourceId, CancellationToken ct = default) =>
                        _serviceProvider.GetRequiredService<SamTools.LearningResourceDetailTool>()
                            .GetAsync(resourceId, ct),
                    reg.Name, reg.Description),

            CoachToolNames.GetCurrentProfileSummary =>
                Create(
                    (CancellationToken ct = default) =>
                        _serviceProvider.GetRequiredService<SamTools.CurrentProfileSummaryTool>()
                            .GetAsync(ct),
                    reg.Name, reg.Description),

            CoachToolNames.GetLearnerSettingsSummary =>
                Create(
                    (CancellationToken ct = default) =>
                        _serviceProvider.GetRequiredService<SamTools.LearnerSettingsSummaryTool>()
                            .GetAsync(ct),
                    reg.Name, reg.Description),

            CoachToolNames.GetCurrentPlanSummary =>
                Create(
                    (CancellationToken ct = default) =>
                        _serviceProvider.GetRequiredService<SamTools.CurrentPlanSummaryTool>()
                            .GetAsync(ct),
                    reg.Name, reg.Description),

            _ => null
        };
    }

    /// <summary>
    /// Builds the model-facing function for one write-intent registration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each case differs only in its argument type. The tool name comes from the registration and
    /// is closed over here, so it is a server-side constant by the time the ledger sees it — the
    /// model supplies domain fields and nothing else. That matters: the ledger looks the handler
    /// up by name, so a model-supplied name would be a way to run one domain's handler with
    /// another domain's approval.
    /// </para>
    /// <para>
    /// A registration with no case returns null and is simply absent from the turn. The registry's
    /// startup validation is what makes that loud rather than silent.
    /// </para>
    /// </remarks>
    private AIFunction? BuildSamWriteTool(CoachToolRegistration reg)
    {
        return reg.Name switch
        {
            CoachToolNames.ProposeVocabularyEntry => CreateWrite<CoachVocabularyEntryArgs>(reg),
            CoachToolNames.ProposeVocabularyEdit => CreateWrite<CoachVocabularyEditArgs>(reg),
            CoachToolNames.ProposeVocabularyLink => CreateWrite<CoachVocabularyLinkArgs>(reg),
            CoachToolNames.ProposeVocabularyRemoval => CreateWrite<CoachVocabularyRemovalArgs>(reg),
            CoachToolNames.ProposeSkillEntry => CreateWrite<CoachSkillEntryArgs>(reg),
            CoachToolNames.ProposeSkillEdit => CreateWrite<CoachSkillEditArgs>(reg),
            CoachToolNames.ProposeSkillArchive => CreateWrite<CoachSkillArchiveArgs>(reg),
            CoachToolNames.ProposeResourceEntry => CreateWrite<CoachResourceEntryArgs>(reg),
            CoachToolNames.ProposeResourceEdit => CreateWrite<CoachResourceEditArgs>(reg),
            CoachToolNames.ProposeResourceRemoval => CreateWrite<CoachResourceRemovalArgs>(reg),
            CoachToolNames.ProposePreferenceChange => CreateWrite<CoachPreferenceChangeArgs>(reg),
            CoachToolNames.ProposeYouTubeImport => CreateWrite<CoachYouTubeImportArgs>(reg),
            _ => null
        };
    }

    private AIFunction CreateWrite<TArgs>(CoachToolRegistration reg)
        where TArgs : class =>
        Create(
            (TArgs arguments, CancellationToken ct = default) =>
                _serviceProvider.GetRequiredService<SamTools.SamWriteProposalTool>()
                    .ProposeAsync(reg.Name, arguments, ct),
            reg.Name, reg.Description);

    private static AIFunction Create(Delegate method, string name, string description) =>
        AIFunctionFactory.Create(method, new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
            SerializerOptions = SerializerOptions,
            JsonSchemaCreateOptions = SchemaOptions
        });
}
