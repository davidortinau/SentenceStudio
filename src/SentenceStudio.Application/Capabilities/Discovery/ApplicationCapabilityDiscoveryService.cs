using System.Collections.ObjectModel;

namespace SentenceStudio.Application.Capabilities.Discovery;

public sealed class ApplicationCapabilityDiscoveryService
{
    private readonly ApplicationCapabilityDiscoveryIndex _index;
    private readonly IApplicationCapabilityLexicalScorer _lexicalScorer;
    private readonly IApplicationCapabilitySemanticScoreProvider? _semanticProvider;

    public ApplicationCapabilityDiscoveryService(
        ApplicationCapabilityDiscoveryIndex index,
        IApplicationCapabilityLexicalScorer lexicalScorer,
        IApplicationCapabilitySemanticScoreProvider? semanticProvider = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _lexicalScorer = lexicalScorer ?? throw new ArgumentNullException(nameof(lexicalScorer));
        _semanticProvider = semanticProvider;
    }

    public async ValueTask<ApplicationCapabilityDiscoveryResult> DiscoverAsync(
        ApplicationCapabilityDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var semantic = await GetSemanticScoresAsync(request.Query, cancellationToken);
        var scoredFamilies = ScoreFamilies(request, semantic.Scores);
        var suppressions = scoredFamilies
            .Where(family => family.HardNegative)
            .OrderBy(family => family.Family, StringComparer.Ordinal)
            .Select(family => new ApplicationCapabilityFamilySuppression(
                family.Family,
                Copy(family.Reasons.Where(reason => reason
                    is ApplicationCapabilityMatchReasonCode.HardNegativeExample
                    or ApplicationCapabilityMatchReasonCode.NegatedPositiveExample)),
                Copy(family.Members.Select(CreateDiscoveryMember))))
            .ToArray();

        var ranked = scoredFamilies
            .Where(family => !family.HardNegative && family.CombinedScore > 0)
            .OrderByDescending(family => family.EvidencePrecedence)
            .ThenByDescending(family => family.CombinedScore)
            .ThenBy(family => family.Family, StringComparer.Ordinal)
            .ToArray();
        AssignRanksAndNearTies(ranked, request.ScoringPolicy.NearTieTolerance);

        var maximumFamilies = request.Budgets.MaximumFamilyCandidates;
        var candidates = ranked.Take(maximumFamilies).ToArray();
        var beyondLimit = Math.Max(0, ranked.Length - candidates.Length);
        var boundaryNearTie = candidates.Length > 0
            && ranked.Length > candidates.Length
            && candidates[^1].NearTieGroup == ranked[candidates.Length].NearTieGroup;

        var remaining = request.Budgets.ToCosts();
        var matches = new List<ApplicationCapabilityFamilyMatch>();
        ApplicationCapabilityFamilyExceedsBudget? budgetFailure = null;
        foreach (var family in candidates)
        {
            if (!family.Costs.FitsWithin(remaining))
            {
                budgetFailure = new(
                    family.Family,
                    family.Rank,
                    ExceededDimensions(family.Costs, remaining),
                    family.Costs,
                    remaining);
                break;
            }

            matches.Add(new ApplicationCapabilityFamilyMatch(
                family.Family,
                family.Rank,
                family.NearTieGroup,
                family.LexicalScore,
                family.NegativePenalty,
                family.SemanticScore,
                family.CombinedScore,
                Copy(family.Reasons),
                Copy(family.Members.Select(CreateDiscoveryMember)),
                family.Costs));
            remaining = remaining.Subtract(family.Costs);
        }

        var state = budgetFailure is not null
            ? ApplicationCapabilityDiscoveryOutcomeState.NeedsExpansion
            : beyondLimit > 0
                ? ApplicationCapabilityDiscoveryOutcomeState.CandidateLimitReached
                : ApplicationCapabilityDiscoveryOutcomeState.Complete;

        return new ApplicationCapabilityDiscoveryResult(
            state,
            _index.Generation,
            semantic.Outcome,
            Copy(matches),
            Copy(suppressions),
            budgetFailure,
            beyondLimit,
            boundaryNearTie);
    }

    private async ValueTask<SemanticScoreSet> GetSemanticScoresAsync(
        string query,
        CancellationToken cancellationToken)
    {
        if (_semanticProvider is null)
        {
            return SemanticScoreSet.Failure(
                ApplicationCapabilitySemanticScoringState.Unavailable,
                ApplicationCapabilitySemanticScoringReason.ProviderNotConfigured);
        }

        ApplicationCapabilitySemanticScoringResult result;
        try
        {
            result = await _semanticProvider.ScoreAsync(
                new ApplicationCapabilitySemanticScoringRequest(
                    query,
                    _index.Generation,
                    _index.Documents),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return SemanticScoreSet.Failure(
                ApplicationCapabilitySemanticScoringState.Degraded,
                ApplicationCapabilitySemanticScoringReason.ProviderFailure);
        }

        if (result is null)
        {
            return SemanticScoreSet.Failure(
                ApplicationCapabilitySemanticScoringState.Degraded,
                ApplicationCapabilitySemanticScoringReason.InvalidProviderResult);
        }

        if (result.State != ApplicationCapabilitySemanticScoringState.Succeeded)
        {
            return SemanticScoreSet.Failure(result.State, result.Reason);
        }

        var expected = _index.Generation;
        if (result.Scores.Any(score =>
                !string.Equals(
                    score.DescriptorSetHash,
                    expected.DescriptorSetHash,
                    StringComparison.Ordinal)))
        {
            return SemanticScoreSet.Failure(
                ApplicationCapabilitySemanticScoringState.StaleGeneration,
                ApplicationCapabilitySemanticScoringReason.StaleDescriptorSet);
        }

        if (result.Scores.Any(score =>
                !string.Equals(
                    score.EmbeddingGeneration,
                    expected.EmbeddingGeneration,
                    StringComparison.Ordinal)
                || !string.Equals(
                    score.EmbeddingProvider,
                    expected.EmbeddingProvider,
                    StringComparison.Ordinal)
                || !string.Equals(
                    score.EmbeddingModel,
                    expected.EmbeddingModel,
                    StringComparison.Ordinal)))
        {
            return SemanticScoreSet.Failure(
                ApplicationCapabilitySemanticScoringState.StaleGeneration,
                ApplicationCapabilitySemanticScoringReason.StaleEmbeddingGeneration);
        }

        if (result.Scores.Any(score => score.EmbeddingDimensions != expected.EmbeddingDimensions)
            || result.Scores.Select(score => score.EmbeddingDimensions).Distinct().Count() > 1)
        {
            return SemanticScoreSet.Failure(
                ApplicationCapabilitySemanticScoringState.DimensionMismatch,
                ApplicationCapabilitySemanticScoringReason.MixedOrUnexpectedDimensions);
        }

        var known = _index.Documents
            .Select(document => new CapabilityKey(document.Code, document.MajorVersion))
            .ToHashSet();
        var scores = new Dictionary<CapabilityKey, double>();
        foreach (var score in result.Scores)
        {
            var key = new CapabilityKey(score.Code, score.MajorVersion);
            if (!known.Contains(key) || !scores.TryAdd(key, score.Score))
            {
                return SemanticScoreSet.Failure(
                    ApplicationCapabilitySemanticScoringState.Degraded,
                    ApplicationCapabilitySemanticScoringReason.InvalidProviderResult);
            }
        }

        return new(
            new ApplicationCapabilitySemanticOutcome(
                ApplicationCapabilitySemanticScoringState.Succeeded,
                ApplicationCapabilitySemanticScoringReason.None),
            scores);
    }

    private ScoredFamily[] ScoreFamilies(
        ApplicationCapabilityDiscoveryRequest request,
        IReadOnlyDictionary<CapabilityKey, double> semanticScores)
    {
        var families = new List<ScoredFamily>();
        foreach (var family in _index.Families.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var members = new List<ScoredMember>();
            foreach (var document in family.Value)
            {
                var lexical = _lexicalScorer.Score(request.Query, document);
                var vector = semanticScores.GetValueOrDefault(
                    new CapabilityKey(document.Code, document.MajorVersion));
                var combined = request.ScoringPolicy.LexicalWeight
                        * (lexical.Score - lexical.NegativePenalty)
                    + request.ScoringPolicy.SemanticWeight * vector;
                var memberReasons = new HashSet<ApplicationCapabilityMatchReasonCode>(
                    lexical.Reasons);
                if (vector > 0)
                {
                    memberReasons.Add(ApplicationCapabilityMatchReasonCode.SemanticScore);
                }

                var exactEvidence = lexical.Reasons.Any(reason => reason
                    is ApplicationCapabilityMatchReasonCode.ExactCode
                    or ApplicationCapabilityMatchReasonCode.ExactAlias
                    or ApplicationCapabilityMatchReasonCode.ExactFamily);
                var roundedCombined = Math.Round(combined, 12, MidpointRounding.ToEven);
                members.Add(new ScoredMember(
                    document,
                    lexical.Score,
                    lexical.NegativePenalty,
                    vector,
                    roundedCombined,
                    lexical.HardNegative,
                    !lexical.HardNegative && roundedCombined > 0,
                    request.ScoringPolicy.ExactAndAliasEvidencePrecedesVectorOnly
                        && exactEvidence
                            ? 1
                            : 0,
                    memberReasons.Order().ToArray()));
            }

            var candidates = members
                .Where(member => member.IsDiscoveryCandidate)
                .ToArray();
            var strongest = (candidates.Length > 0 ? candidates : members.ToArray())
                .OrderByDescending(member => member.CombinedScore)
                .ThenBy(member => member.Document.Code, StringComparer.Ordinal)
                .ThenBy(member => member.Document.MajorVersion)
                .First();
            var reasons = (candidates.Length > 0
                    ? candidates.SelectMany(member => member.Reasons)
                    : members.Where(member => member.HardNegative)
                        .SelectMany(member => member.Reasons))
                .Distinct()
                .Order()
                .ToArray();
            var costs = new ApplicationCapabilityDiscoveryCosts(
                family.Value.Sum(document => (long)document.Costs.SchemaTokens),
                family.Value.Sum(document => (long)document.Costs.Risk),
                family.Value.Sum(document => (long)document.Costs.Exposure),
                family.Value.Count);
            families.Add(new ScoredFamily(
                family.Key,
                members,
                costs,
                strongest.LexicalScore,
                strongest.NegativePenalty,
                strongest.SemanticScore,
                strongest.CombinedScore,
                candidates.Length == 0 && members.Any(member => member.HardNegative),
                candidates.Select(member => member.EvidencePrecedence).DefaultIfEmpty().Max(),
                reasons));
        }

        return families.ToArray();
    }

    private static void AssignRanksAndNearTies(
        IReadOnlyList<ScoredFamily> ranked,
        double tolerance)
    {
        var group = 0;
        var groupAnchor = double.NaN;
        var groupPrecedence = -1;
        for (var index = 0; index < ranked.Count; index++)
        {
            var family = ranked[index];
            family.Rank = index + 1;
            if (index == 0
                || family.EvidencePrecedence != groupPrecedence
                || Math.Abs(groupAnchor - family.CombinedScore) > tolerance)
            {
                group++;
                groupAnchor = family.CombinedScore;
                groupPrecedence = family.EvidencePrecedence;
            }

            family.NearTieGroup = group;
        }
    }

    private static ApplicationCapabilityBudgetDimension ExceededDimensions(
        ApplicationCapabilityDiscoveryCosts required,
        ApplicationCapabilityDiscoveryCosts available)
    {
        var dimensions = ApplicationCapabilityBudgetDimension.None;
        if (required.SchemaTokens > available.SchemaTokens)
        {
            dimensions |= ApplicationCapabilityBudgetDimension.SchemaTokens;
        }

        if (required.Risk > available.Risk)
        {
            dimensions |= ApplicationCapabilityBudgetDimension.Risk;
        }

        if (required.Exposure > available.Exposure)
        {
            dimensions |= ApplicationCapabilityBudgetDimension.Exposure;
        }

        if (required.ResultMembers > available.ResultMembers)
        {
            dimensions |= ApplicationCapabilityBudgetDimension.ResultMembers;
        }

        return dimensions;
    }

    private static IReadOnlyList<T> Copy<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>(values.ToArray());

    private static ApplicationCapabilityDiscoveryMember CreateDiscoveryMember(
        ScoredMember member) =>
        new(
            member.Document,
            member.LexicalScore,
            member.NegativePenalty,
            member.SemanticScore,
            member.CombinedScore,
            member.HardNegative,
            member.IsDiscoveryCandidate,
            Copy(member.Reasons));

    private readonly record struct CapabilityKey(string Code, int MajorVersion);

    private sealed class ScoredFamily(
        string family,
        IReadOnlyList<ScoredMember> members,
        ApplicationCapabilityDiscoveryCosts costs,
        double lexicalScore,
        double negativePenalty,
        double semanticScore,
        double combinedScore,
        bool hardNegative,
        int evidencePrecedence,
        IReadOnlyList<ApplicationCapabilityMatchReasonCode> reasons)
    {
        public string Family { get; } = family;
        public IReadOnlyList<ScoredMember> Members { get; } = members;
        public ApplicationCapabilityDiscoveryCosts Costs { get; } = costs;
        public double LexicalScore { get; } = lexicalScore;
        public double NegativePenalty { get; } = negativePenalty;
        public double SemanticScore { get; } = semanticScore;
        public double CombinedScore { get; } = combinedScore;
        public bool HardNegative { get; } = hardNegative;
        public int EvidencePrecedence { get; } = evidencePrecedence;
        public IReadOnlyList<ApplicationCapabilityMatchReasonCode> Reasons { get; } = reasons;
        public int Rank { get; set; }
        public int NearTieGroup { get; set; }
    }

    private sealed class ScoredMember(
        ApplicationCapabilityDiscoveryDocument document,
        double lexicalScore,
        double negativePenalty,
        double semanticScore,
        double combinedScore,
        bool hardNegative,
        bool isDiscoveryCandidate,
        int evidencePrecedence,
        IReadOnlyList<ApplicationCapabilityMatchReasonCode> reasons)
    {
        public ApplicationCapabilityDiscoveryDocument Document { get; } = document;
        public double LexicalScore { get; } = lexicalScore;
        public double NegativePenalty { get; } = negativePenalty;
        public double SemanticScore { get; } = semanticScore;
        public double CombinedScore { get; } = combinedScore;
        public bool HardNegative { get; } = hardNegative;
        public bool IsDiscoveryCandidate { get; } = isDiscoveryCandidate;
        public int EvidencePrecedence { get; } = evidencePrecedence;
        public IReadOnlyList<ApplicationCapabilityMatchReasonCode> Reasons { get; } = reasons;
    }

    private sealed class SemanticScoreSet(
        ApplicationCapabilitySemanticOutcome outcome,
        IReadOnlyDictionary<CapabilityKey, double> scores)
    {
        public ApplicationCapabilitySemanticOutcome Outcome { get; } = outcome;
        public IReadOnlyDictionary<CapabilityKey, double> Scores { get; } = scores;

        public static SemanticScoreSet Failure(
            ApplicationCapabilitySemanticScoringState state,
            ApplicationCapabilitySemanticScoringReason reason) =>
            new(
                new ApplicationCapabilitySemanticOutcome(state, reason),
                new Dictionary<CapabilityKey, double>());
    }
}
