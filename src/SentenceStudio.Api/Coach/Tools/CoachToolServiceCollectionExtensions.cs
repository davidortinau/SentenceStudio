using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SentenceStudio.Api.Coach.Tools.Observation;
using SentenceStudio.Api.Coach.Validation;

namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// Registers the read-only coach tools and the coach validators.
/// Every tool is scoped, so each request resolves its own user scope.
/// </summary>
public static class CoachToolServiceCollectionExtensions
{
    /// <summary>Adds the read-only coach tools, the tool registry, the tool factory, and the validators.</summary>
    /// <remarks>
    /// <para>
    /// The embargo contract runs here, at registration time, so a shape that could carry
    /// identity data or a due word stops the host at start-up instead of at the first turn.
    /// </para>
    /// <para>
    /// Two halves of the contract run at two different moments, because they need different
    /// things to exist. The assembly-discovered half — the model-visible graph and the public
    /// client contracts — needs only types, so it runs immediately. The registry-driven half needs
    /// a settled set of tools, so it runs inside the registry's own singleton factory, in a fixed
    /// order: construct, freeze, validate, expose. Validating before construction was the previous
    /// arrangement and it could not have worked; there was nothing to validate yet.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCoachReadOnlyTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        CoachOutputContract.EnsureValid();

        // The per-turn observation buffer. Registered here rather than in the coach persistence
        // extension because the seam that writes to it is built by CoachToolFactory, and a host
        // that has tools has the seam whether or not it has durable history.
        //
        // Scoped, so "the turn" is the request scope and there is no cross-turn state to leak or
        // to clear. Nothing else changes for a host that never reads it: the buffer is an empty
        // list and the stored outcome carries a null trace.
        services.AddCoachToolObservation();

        // Core five tools
        services.TryAddScoped<LearnerProfileSummaryTool>();
        services.TryAddScoped<PracticeBalanceTool>();
        services.TryAddScoped<VocabularyDueSummaryTool>();
        services.TryAddScoped<ResourceCatalogTool>();
        services.TryAddScoped<PreviewPracticePlanTool>();
        services.TryAddScoped<PracticeHistorySummaryTool>();

        // Sam read tools (scoped: each request resolves its own user scope)
        services.TryAddScoped<SamTools.VocabularySearchTool>();
        services.TryAddScoped<SamTools.VocabularyWordDetailTool>();
        services.TryAddScoped<SamTools.SkillListTool>();
        services.TryAddScoped<SamTools.SkillDetailTool>();
        services.TryAddScoped<SamTools.LearningResourceListTool>();
        services.TryAddScoped<SamTools.LearningResourceDetailTool>();
        services.TryAddScoped<SamTools.CurrentProfileSummaryTool>();
        services.TryAddScoped<SamTools.LearnerSettingsSummaryTool>();
        services.TryAddScoped<SamTools.CurrentPlanSummaryTool>();

        // Sam write tools. One tool class serves every propose_ function; the handlers are what
        // differ. All scoped, because the ownership guard they depend on holds a request-scoped
        // database context and each request must resolve its own learner.
        services.TryAddScoped<SamTools.SamWriteProposalTool>();
        services.TryAddScoped<Operations.CoachWriteTurnScope>();
        services.TryAddScoped<Operations.CoachWriteOwnership>();
        services.TryAddScoped<Operations.CoachWriteOperationService>();
        services.TryAddScoped<Operations.ICoachWriteProposer>(
            sp => sp.GetRequiredService<Operations.CoachWriteOperationService>());
        services.TryAddScoped<Operations.ICoachWriteHandlerCatalog, Operations.CoachWriteHandlerCatalog>();

        // Handlers are registered as the interface only. The catalog is the sole lookup, and it
        // refuses duplicates and mis-classified entries when it is built, so an accidental second
        // registration for one tool name is a startup failure rather than a coin toss at runtime.
        services.AddScoped<Operations.ICoachWriteHandler, Operations.Handlers.CoachVocabularyEntryHandler>();
        services.AddScoped<Operations.ICoachWriteHandler, Operations.Handlers.CoachVocabularyEditHandler>();
        services.AddScoped<Operations.ICoachWriteHandler, Operations.Handlers.CoachVocabularyLinkHandler>();
        services.AddScoped<Operations.ICoachWriteHandler, Operations.Handlers.CoachVocabularyRemovalHandler>();
        services.AddScoped<Operations.ICoachWriteHandler, Operations.Handlers.CoachSkillEntryHandler>();
        services.AddScoped<Operations.ICoachWriteHandler, Operations.Handlers.CoachSkillEditHandler>();
        services.AddScoped<Operations.ICoachWriteHandler, Operations.Handlers.CoachSkillArchiveHandler>();
        services.AddScoped<Operations.ICoachWriteHandler, Operations.Handlers.CoachResourceEntryHandler>();
        services.AddScoped<Operations.ICoachWriteHandler, Operations.Handlers.CoachResourceEditHandler>();
        services.AddScoped<Operations.ICoachWriteHandler, Operations.Handlers.CoachResourceRemovalHandler>();
        services.AddScoped<Operations.ICoachWriteHandler, Operations.Handlers.CoachPreferenceChangeHandler>();
        services.AddScoped<Operations.ICoachWriteHandler, Operations.Handlers.CoachYouTubeImportHandler>();

        services.TryAddScoped<Application.ICoachWriteApprovalService, Application.CoachWriteApprovalService>();

        services.TryAddScoped<ICoachPlanPreviewFailureAdapter, DefaultCoachPlanPreviewFailureAdapter>();
        services.TryAddScoped<ICoachToolFactory, CoachToolFactory>();

        services.TryAddSingleton<CoachEmbargoScanner>();
        services.TryAddSingleton<CoachIntentValidator>();

        // Registry: singleton, constructed from CoachOptions, used by allow list and factory.
        // The factory is the one place the lifecycle is enforced, so no caller can obtain a
        // registry that has not been through it.
        services.TryAddSingleton<ICoachToolRegistry>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Runtime.CoachOptions>>().Value;
            return BuildValidatedRegistry(options);
        });
        services.TryAddSingleton<CoachToolAllowList>(sp =>
            new CoachToolAllowList(sp.GetRequiredService<ICoachToolRegistry>()));

        // Capability manifest: built once from the frozen registry and cached for the process.
        // Singleton because it is immutable by construction and read on the turn path — rebuilding
        // it per turn would spend allocations to produce the same answer.
        services.TryAddSingleton<Capabilities.ICoachCapabilityManifest>(sp =>
            new Capabilities.CoachCapabilityManifest(sp.GetRequiredService<ICoachToolRegistry>()));
        services.TryAddSingleton<Capabilities.ICoachCapabilityResolver>(sp =>
            new Capabilities.CoachCapabilityResolver(
                sp.GetRequiredService<Capabilities.ICoachCapabilityManifest>()));

        services.TryAddScoped<CoachDueItemLeakValidator>();

        // W6 honesty rules. Registered beside the manifest and resolver they read, because those
        // two are the rules' only view of what this build can do — a claim rule with no manifest
        // cannot tell an over-claim from an ordinary offer.
        Validation.Claims.CoachClaimRuleServiceCollectionExtensions.AddCoachClaimRules(services);

        // Scoped: reads the embargoed terms and the owned resource ids for the trusted user.
        // Nothing it returns ever reaches agent context.
        services.TryAddScoped<ICoachValidationDataSource, CoachValidationDataSource>();

        // Startup validator: eagerly resolve the registry so envelope drift stops the host
        // before the first request, not on the first Sam turn.
        services.AddHostedService<CoachToolRegistryStartupValidator>();

        return services;
    }

    /// <summary>
    /// Constructs the registry, seals it, validates its coverage, and returns the frozen instance.
    /// Throws before returning if any registered tool returns an unapproved or unscannable shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is the point, and each step depends on the one before it.
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>Construct.</b> The constructor registers every core and Sam tool, which is the only
    /// place registrations are added. Nothing outside this method ever holds an unfrozen registry.
    /// </description></item>
    /// <item><description>
    /// <b>Freeze.</b> Sealing before validation is what makes the validation total. A later
    /// <c>Register</c> now throws, so "every registered shape passed" cannot decay into "every
    /// shape registered so far passed".
    /// </description></item>
    /// <item><description>
    /// <b>Validate.</b> Coverage and result shapes are checked against the sealed set. A failure
    /// throws out of the DI factory, which surfaces on the first resolve and stops the host rather
    /// than serving a turn with an unscanned tool.
    /// </description></item>
    /// <item><description>
    /// <b>Expose.</b> Only a frozen, validated registry is returned to the container, and the
    /// singleton lifetime means every consumer shares that one instance.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static ICoachToolRegistry BuildValidatedRegistry(Runtime.CoachOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var registry = new CoachToolRegistry(options);
        registry.Freeze();
        CoachOutputContract.ValidateRegistry(registry);
        return registry;
    }
}
