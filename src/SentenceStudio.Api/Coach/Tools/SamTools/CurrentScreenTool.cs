using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Tools.SamTools;

/// <summary>
/// Reads what the learner's app said it was showing when this message was sent, so "this word" or
/// "add it here" can mean the thing on screen.
/// </summary>
/// <remarks>
/// <para>
/// The answer comes from the turn's client screen (<see cref="CoachWriteTurnScope.ClientScreen"/>),
/// never from stored data, and lasts for the turn only. It is advisory: the identifiers it relays
/// are what the client sent, and every tool that takes one checks ownership again, so relaying an
/// identifier grants nothing.
/// </para>
/// <para>
/// Everything is bounded and stripped of control characters, because a label is learner content
/// and imported text can hold instructions; the model treats it as data.
/// </para>
/// </remarks>
public sealed class CurrentScreenTool : CoachToolBase
{
    public const int MaxItems = 12;
    public const int MaxFacts = 16;
    private const int MaxIdLength = 80;
    private const int MaxLabelLength = 160;
    private const int MaxNameLength = 40;
    private const int MaxValueLength = 120;
    private const int MaxScreenLength = 80;

    private readonly CoachWriteTurnScope _turn;
    private readonly IPlanDateContext _dates;

    public CurrentScreenTool(IUserScopeProvider userScope, CoachWriteTurnScope turn, IPlanDateContext dates)
        : base(userScope)
    {
        _turn = turn;
        _dates = dates;
    }

    public override string ToolName => CoachToolNames.GetCurrentScreen;

    public Task<CurrentScreenResult> GetAsync(CancellationToken ct = default)
    {
        // Owner first, as every read does, even though nothing here touches stored data: a
        // request with no learner has no screen worth describing.
        RequireUserProfileId();
        ct.ThrowIfCancellationRequested();

        var screen = _turn.ClientScreen;
        if (screen is null)
            return Task.FromResult(new CurrentScreenResult(
                Available: false, Screen: null, Platform: null, AnswerHidden: false,
                Items: [], Facts: [], Scope: Scope(0, 0)));

        var view = Read(screen, out var matched);
        return Task.FromResult(new CurrentScreenResult(
            Available: true,
            Screen: view.Screen,
            Platform: view.Platform,
            AnswerHidden: view.AnswerHidden,
            Items: view.Items,
            Facts: view.Facts,
            Scope: Scope(view.Items.Count + view.Facts.Count, matched)));
    }

    /// <summary>
    /// The screen as the model may see it: bounded, stripped of control characters, the main thing
    /// first, a hidden answer's details dropped, and items that name nothing left out. One reading
    /// for both the tool's answer and the screen note in every turn message
    /// (<see cref="Agents.CoachInstructions.BuildTurnMessage"/>), so the two never disagree.
    /// </summary>
    public static CurrentScreenView Read(CoachClientScreenContext screen) => Read(screen, out _);

    private static CurrentScreenView Read(CoachClientScreenContext screen, out int matched)
    {
        ArgumentNullException.ThrowIfNull(screen);

        // An item with neither an id nor a label points at nothing, so it is not reported.
        var reported = (screen.Items ?? [])
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Kind)
                && (!string.IsNullOrWhiteSpace(item.Id) || !string.IsNullOrWhiteSpace(item.Label)))
            .ToList();
        // The page's own facts first (they say what it shows), then the shell's state.
        var reportedState = (screen.Facts ?? []).Concat(screen.State ?? [])
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Key))
            .ToList();
        var items = reported
            // The main thing first, before the bound, so "this" is never the item cut off.
            .OrderByDescending(item => item.Focus)
            .Take(MaxItems)
            .Select(item => new CurrentScreenItem(
                Kind: KindOf(item.Kind),
                Id: Optional(item.Id, MaxIdLength),
                Label: Optional(item.Label, MaxLabelLength),
                Detail: screen.AnswerHidden ? null : Optional(item.Detail, MaxLabelLength),
                Focus: item.Focus))
            .ToList();

        var facts = reportedState
            .Take(MaxFacts)
            .Select(entry => new CurrentScreenFact(
                SanitizeMetadata(entry.Key, MaxNameLength),
                Optional(entry.Value, MaxValueLength)))
            .ToList();

        matched = reported.Count + reportedState.Count;
        return new CurrentScreenView(
            ScreenName(screen), Optional(screen.Platform, MaxNameLength), screen.AnswerHidden, items, facts);
    }

    /// <summary>
    /// The screen's stable name, never the concrete route: a web route carries its query (ids,
    /// filters) and would change with every visit. Native names drop their <c>native.</c> prefix and
    /// web templates their leading slash, so the same screen reads alike on every client.
    /// </summary>
    internal static string? ScreenName(CoachClientScreenContext screen)
    {
        var name = screen.ScreenId ?? screen.Route?.Split('?', '#')[0];
        if (string.IsNullOrWhiteSpace(name))
            return null;
        name = name.Trim();
        if (name.StartsWith("native.", StringComparison.Ordinal))
            name = name["native.".Length..];
        return Optional(name.TrimStart('/'), MaxScreenLength);
    }

    internal static CurrentScreenItemKind KindOf(string kind) => kind.Trim() switch
    {
        CoachClientScreenItemKinds.VocabularyWord => CurrentScreenItemKind.VocabularyWord,
        CoachClientScreenItemKinds.ExampleSentence => CurrentScreenItemKind.ExampleSentence,
        CoachClientScreenItemKinds.LearningResource => CurrentScreenItemKind.LearningResource,
        CoachClientScreenItemKinds.Skill => CurrentScreenItemKind.Skill,
        CoachClientScreenItemKinds.MinimalPair => CurrentScreenItemKind.MinimalPair,
        CoachClientScreenItemKinds.Activity => CurrentScreenItemKind.Activity,
        CoachClientScreenItemKinds.PlanItem => CurrentScreenItemKind.PlanItem,
        CoachClientScreenItemKinds.YouTubeChannel => CurrentScreenItemKind.YouTubeChannel,
        CoachClientScreenItemKinds.Text => CurrentScreenItemKind.Text,
        _ => CurrentScreenItemKind.Other
    };

    private static string? Optional(string? value, int maxLength)
    {
        var cleaned = SanitizeMetadata(value, maxLength);
        return cleaned.Length == 0 ? null : cleaned;
    }

    private CoachResultScope Scope(int returned, int matched) => new()
    {
        Coverage = CoachScopeCoverage.ClientScreenSnapshot,
        Order = CoachScopeOrder.NotApplicable,
        OrderHonored = true,
        // Owner-scoped as in "this learner's own turn": the read refuses without an owner. The
        // ids it relays are what the client sent, not rows checked against the owner; every tool
        // that takes one checks ownership itself (see the class remarks).
        Filters = CoachScopeFilters.OwnerScoped,
        AsOfUtc = _dates.UtcNow,
        ReturnedCount = returned,
        MatchedCount = matched,
        EligiblePopulationCount = matched,
        Truncated = returned < matched,
        DefinitionCode = CoachScopeDefinition.ClientReportedScreen,
        MinimumEvidence = CoachScopeMinimumEvidence.None,
        TieBreak = CoachScopeTieBreak.NotApplicable,
        ClockBasis = CoachScopeClockBasis.ServerUtcInstant,
        ReferenceMode = CoachScopeReferenceMode.AsOfInstant
    };
}

/// <summary>The cleaned screen, shared by the tool's answer and the per-turn screen note.</summary>
public sealed record CurrentScreenView(
    string? Screen,
    string? Platform,
    bool AnswerHidden,
    IReadOnlyList<CurrentScreenItem> Items,
    IReadOnlyList<CurrentScreenFact> Facts);
