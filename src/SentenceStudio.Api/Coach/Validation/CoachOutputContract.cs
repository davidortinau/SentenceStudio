using System.Reflection;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Coach.Validation;

/// <summary>
/// The active embargo contract over every shape the coach can emit.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CoachEmbargoScanner"/> is a rule; this is where the rule runs. The check
/// executes once per process, at registration time and again on the first tool build, and
/// throws when any tool answer type or public coach contract type carries an identity
/// member, an embargoed content member, a database entity, or an open-ended member type.
/// </para>
/// <para>
/// It fails closed on purpose. A shape that can carry a learner identifier or a due word is
/// not a runtime warning: it is a boundary defect, and the coach must not serve traffic with
/// one present.
/// </para>
/// </remarks>
public static class CoachOutputContract
{
    private const string ContractName = "coach output embargo contract";

    private static readonly Lazy<bool> Guard = new(RunScan, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Every read-only tool answer type from the core five.</summary>
    public static IReadOnlyList<Type> ToolResultTypes { get; } =
    [
        typeof(LearnerProfileSummary),
        typeof(PracticeBalanceSummary),
        typeof(VocabularyDueSummary),
        typeof(ResourceCatalogSummary),
        typeof(PlanPreviewSummary)
    ];

    /// <summary>
    /// The result envelopes approved to cross the tool boundary, and the scope each was
    /// approved under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the explicit approval list. A tool cannot invent a return shape and have it reach
    /// the model simply because the scanner happened not to object to its member names: the shape
    /// has to be named here first, by a human, with the scope it was reviewed under. A registered
    /// tool whose <see cref="CoachToolRegistration.ResultType"/> is absent from this list fails
    /// startup as an unapproved envelope.
    /// </para>
    /// <para>
    /// Only root envelopes are listed. The scanner walks member types transitively, so the nested
    /// entry records — <c>VocabularySearchEntry</c>, <c>SkillListEntry</c> and the rest — are
    /// reached and judged through their parents. Listing them again would imply they were an
    /// independently returnable surface, which they are not.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<Type, CoachEmbargoScope> ApprovedResultEnvelopes { get; } =
        new Dictionary<Type, CoachEmbargoScope>
        {
            // Core five. The strict scope: no learner content at all.
            [typeof(LearnerProfileSummary)] = CoachEmbargoScope.ModelVisible,
            [typeof(PracticeBalanceSummary)] = CoachEmbargoScope.ModelVisible,
            [typeof(VocabularyDueSummary)] = CoachEmbargoScope.ModelVisible,
            [typeof(ResourceCatalogSummary)] = CoachEmbargoScope.ModelVisible,
            [typeof(PlanPreviewSummary)] = CoachEmbargoScope.ModelVisible,

            // Sam read tools. The ToolResult scope permits explicit learner-requested content
            // (terms, examples, sentences) while still refusing identity, bulk content,
            // directives, credentials, entities, and open types.
            [typeof(VocabularySearchResult)] = CoachEmbargoScope.ToolResult,
            [typeof(VocabularyWordDetail)] = CoachEmbargoScope.ToolResult,
            [typeof(SkillListResult)] = CoachEmbargoScope.ToolResult,
            [typeof(SkillDetailResult)] = CoachEmbargoScope.ToolResult,
            [typeof(LearningResourceListResult)] = CoachEmbargoScope.ToolResult,
            [typeof(LearningResourceDetailResult)] = CoachEmbargoScope.ToolResult,
            [typeof(CurrentProfileSummary)] = CoachEmbargoScope.ToolResult,
            [typeof(LearnerSettingsSummary)] = CoachEmbargoScope.ToolResult,
            [typeof(CurrentPlanSummary)] = CoachEmbargoScope.ToolResult,

            // Sam write tools. Every proposal comes back in this one shape, so the review that
            // approved it covers all twelve. It carries a summary and detail lines the learner is
            // about to be shown, which is learner content and therefore ToolResult, not the
            // strict scope. What it deliberately does not carry is the confirmation secret, the
            // owner identifier, or any key beyond the opaque operation id — the model can ask for
            // a change and describe it, and that is the whole of its reach.
            [typeof(CoachWriteProposalResult)] = CoachEmbargoScope.ToolResult
        };

    /// <summary>
    /// The namespace holding the typed intent the model produces.
    /// </summary>
    private const string IntentNamespace = "SentenceStudio.Contracts.Coach.Intent";

    /// <summary>
    /// The namespace holding the contracts the API sends to a client.
    /// </summary>
    private const string ContractsNamespace = "SentenceStudio.Contracts.Coach";

    /// <summary>
    /// Every shape the model can see or produce: the tool answers and the typed intent graph.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the surface the strict embargo was written for. A type earns a place here by being
    /// reachable from the model, not by living in a particular assembly, which is why the intent
    /// namespace is enumerated and the wider contracts namespace is not.
    /// </para>
    /// <para>
    /// The turn request assembled for a run is deliberately absent. The word embargo governs the
    /// shapes the coach emits, where a member name is the thing that leaks; the request is
    /// internal plumbing whose risk is what gets attached to it, which a name check cannot see
    /// either way. It is held instead by the structural isolation tests, which walk the real type
    /// graph and refuse any durable-history contract reachable from it. Scanning it here as well
    /// would add one true constraint and one false alarm: <c>UserLocalDate</c> is a calendar date
    /// used for planning and identifies nobody, and the only ways to satisfy the identity rule
    /// would be a ninety-site rename across files other work owns, or a carve-out of exactly the
    /// kind this split exists to remove.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Type> ModelVisibleTypes { get; } = ToolResultTypes
        .Concat(typeof(CoachTurnIntent).Assembly
            .GetTypes()
            .Where(t => t.IsPublic
                        && t is { IsClass: true, IsAbstract: false }
                        && string.Equals(t.Namespace, IntentNamespace, StringComparison.Ordinal)))
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// Every contract the API can send to a client and the model never sees. Discovered by
    /// namespace so a new DTO is covered the moment it is added.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Discovery is by exact namespace rather than by prefix, so the intent types sitting one level
    /// down are not swept in and quietly downgraded to the bounded rules. A type is in exactly one
    /// scope, and moving it between them is a visible edit.
    /// </para>
    /// <para>
    /// These are checked, not exempted. The bounded scope still refuses identity, directives,
    /// credentials, database entities, maps, and open-ended member types, and adds a rule the
    /// strict scope has no reason to carry: no ciphertext, nonce, key id, lease, or idempotency
    /// digest may ride out on a shape a client reads.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Type> PublicClientContractTypes { get; } = typeof(CoachTurnResponse).Assembly
        .GetTypes()
        .Where(t => t.IsPublic
                    && t is { IsClass: true, IsAbstract: false }
                    && string.Equals(t.Namespace, ContractsNamespace, StringComparison.Ordinal))
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// Runs the contract once per process. Throws
    /// <see cref="CoachContractViolationException"/> on the first failure and on every later
    /// call, so a defect cannot be skipped by retrying.
    /// </summary>
    /// <remarks>
    /// This is the registry-independent half: the model-visible graph and the public client
    /// contracts, both discovered from the assembly. It says nothing about tool coverage. Startup
    /// must additionally call <see cref="ValidateRegistry"/> against the frozen registry, which is
    /// what proves every tool the coach can call returns an approved, scanned shape.
    /// </remarks>
    public static void EnsureValid()
    {
        if (!Guard.Value)
        {
            // Unreachable: RunScan either returns true or throws. Kept so a future change to
            // the guard cannot turn a failure into a silent pass.
            throw new CoachContractViolationException(ContractName, CoachValidationResult.From(
                [new CoachViolation(CoachViolationKind.Embargo, "scan_failed", "The embargo scan did not complete.")]));
        }
    }

    /// <summary>
    /// Validates the frozen tool registry against the approved envelopes and scans every
    /// registered result shape under the scope its tool declared. Throws on any failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This closes the gap that a hand-maintained type list left open. Previously the scanned set
    /// and the registered set were two lists that happened to agree; a tool added with a result
    /// type nobody remembered to append was simply never examined, and the embargo failed open in
    /// exactly the case it existed for. Here the scanned set <em>is</em> the registered set, so
    /// forgetting is not a reachable state.
    /// </para>
    /// <para>
    /// Four things must hold, and each is a startup failure rather than a warning:
    /// the registry is frozen; every registration names an approved envelope; every registration
    /// declares the scope that envelope was approved under; and every approved envelope is claimed
    /// by some registration. The last one keeps the approval list honest — a stale entry for a
    /// deleted tool would otherwise sit there implying a review that no longer applies to
    /// anything.
    /// </para>
    /// </remarks>
    /// <param name="registry">The frozen registry to validate.</param>
    public static void ValidateRegistry(ICoachToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var result = ScanRegistry(registry);
        if (!result.IsValid)
        {
            throw new CoachContractViolationException(ContractName, result);
        }
    }

    /// <summary>Runs the registry coverage check and result scan without throwing, for tests.</summary>
    /// <param name="registry">The frozen registry to check.</param>
    /// <param name="approvedEnvelopes">
    /// The approval table to check against. Defaults to <see cref="ApprovedResultEnvelopes"/>,
    /// which is what startup uses. A test passes its own table to exercise a disagreement between
    /// the approvals and the registry that the shipped pair does not have.
    /// </param>
    public static CoachValidationResult ScanRegistry(
        ICoachToolRegistry registry,
        IReadOnlyDictionary<Type, CoachEmbargoScope>? approvedEnvelopes = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var approved = approvedEnvelopes ?? ApprovedResultEnvelopes;
        var violations = new List<CoachViolation>();

        if (!registry.IsFrozen)
        {
            violations.Add(new CoachViolation(
                CoachViolationKind.Embargo,
                "registry_not_frozen",
                "The coach tool registry must be frozen before its coverage can be validated. " +
                "Scanning an open registry proves nothing, because a later registration could add " +
                "an unscanned result shape."));

            // Everything below reasons about a settled set of tools. Reporting per-tool findings
            // against a registry that can still change would be noise, so stop here.
            return CoachValidationResult.From(violations);
        }

        var claimed = new HashSet<Type>();

        foreach (var registration in registry.All)
        {
            claimed.Add(registration.ResultType);

            if (registration.EmbargoScope == CoachEmbargoScope.PublicClient)
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.Embargo,
                    "tool_scope_not_model_facing",
                    $"Tool '{registration.Name}' declared the PublicClient embargo scope. That scope " +
                    "describes shapes the server sends to the authenticated owner and the model never " +
                    "sees. A tool result is model-visible by definition, so this would silently relax " +
                    "the content rules."));
                continue;
            }

            if (!approved.TryGetValue(registration.ResultType, out var approvedScope))
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.Embargo,
                    "unapproved_result_envelope",
                    $"Tool '{registration.Name}' returns '{registration.ResultType.FullName}', which is not " +
                    "an approved result envelope. Add it to CoachOutputContract.ApprovedResultEnvelopes " +
                    "with the scope it was reviewed under, or change the tool to return an approved shape."));
                continue;
            }

            if (approvedScope != registration.EmbargoScope)
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.Embargo,
                    "result_envelope_scope_mismatch",
                    $"Tool '{registration.Name}' declared the {registration.EmbargoScope} embargo scope for " +
                    $"'{registration.ResultType.FullName}', which was approved under {approvedScope}. " +
                    "The declared scope and the approved scope must agree, or the shape would be judged " +
                    "under rules it was never reviewed against."));
            }

            // A read that cannot say what it looked at is the shape the model over-claims from:
            // twenty rows read as the whole shelf, ten words read as the whole vocabulary. The
            // requirement is on reads only — a write tool answers with a proposal, which describes
            // an intended change rather than a population.
            if (registration.RiskClass == CoachToolRiskClass.Read
                && !typeof(ICoachScopedResult).IsAssignableFrom(registration.ResultType))
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.Embargo,
                    "missing_result_scope",
                    $"Read tool '{registration.Name}' returns '{registration.ResultType.FullName}', which does " +
                    $"not implement {nameof(ICoachScopedResult)}. Every read must state its coverage, its " +
                    "order, its filters, and what it withheld, or the model has no way to tell a complete " +
                    "answer from a page of one and will describe the learner's account wrongly while " +
                    "reporting only true rows."));
            }
        }

        foreach (var (envelope, scope) in approved)
        {
            if (!claimed.Contains(envelope))
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.Embargo,
                    "orphaned_result_envelope",
                    $"'{envelope.FullName}' is approved under {scope} but no registered tool returns it. " +
                    "Remove the approval, or register the tool that was meant to use it."));
            }
        }

        // Scan the registered shapes, grouped by declared scope. A fresh scanner per group so a
        // type reachable from two scopes is judged independently under each.
        foreach (var group in registry.All
                     .Where(r => r.EmbargoScope != CoachEmbargoScope.PublicClient)
                     .GroupBy(r => r.EmbargoScope))
        {
            var roots = group.Select(r => r.ResultType).Distinct().ToList();
            violations.AddRange(new CoachEmbargoScanner().ScanTypes(roots, group.Key).Violations);
        }

        return CoachValidationResult.From(violations);
    }

    /// <summary>Runs the scan without caching, for tests and diagnostics.</summary>
    public static CoachValidationResult Scan()
    {
        // Two assembly-discovered passes: ModelVisible (strict) over the tool answers plus the
        // typed intent graph, and PublicClient (bounded) over the client contracts. Separate
        // scanner instances so a type reached from multiple surfaces is judged under each scope
        // independently. Registered tool results are covered by ScanRegistry.
        return CoachValidationResult.From(
            new CoachEmbargoScanner()
                .ScanTypes(ModelVisibleTypes, CoachEmbargoScope.ModelVisible).Violations
                .Concat(new CoachEmbargoScanner()
                    .ScanTypes(PublicClientContractTypes, CoachEmbargoScope.PublicClient).Violations));
    }

    private static bool RunScan()
    {
        var result = Scan();
        return result.IsValid
            ? true
            : throw new CoachContractViolationException(ContractName, result);
    }

    /// <summary>
    /// Reflection helper for the contract tests: the members a scan actually walked.
    /// </summary>
    internal static IEnumerable<PropertyInfo> PublicProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
}
