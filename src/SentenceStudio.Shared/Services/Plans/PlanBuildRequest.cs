namespace SentenceStudio.Services.Plans;

/// <summary>
/// Typed request for deterministic plan generation. Replaces the bare
/// <c>userProfileId</c> parameter so constraints and write-suppression can be
/// expressed explicitly instead of via positional flags.
/// </summary>
/// <remarks>
/// The legacy <c>BuildPlanAsync(string?, CancellationToken)</c> /
/// <c>GenerateAsync(string?, CancellationToken)</c> overloads remain and map to
/// <c>new PlanBuildRequest { UserProfileId = ... }</c>, which is byte-identical
/// to today's behavior: no constraints, writes allowed.
/// </remarks>
public sealed record PlanBuildRequest
{
    /// <summary>
    /// Explicit, trusted user scope. Never populated from model output — the
    /// caller supplies it from <c>IUserScopeProvider</c> or the request
    /// principal. <c>null</c> falls back to the legacy active-profile path.
    /// </summary>
    public string? UserProfileId { get; init; }

    /// <summary>Validated session constraints. <c>null</c> means unconstrained.</summary>
    public PlanConstraints? Constraints { get; init; }

    /// <summary>
    /// A vocabulary focus set already resolved against the trusted user scope.
    /// </summary>
    /// <remarks>
    /// The planner treats these ids as authoritative and never re-derives them,
    /// so a preview and the apply that follows carry byte-identical words. Only
    /// <c>IVocabularyFocusResolver</c> output belongs here — never ids from
    /// model output or from a client request.
    /// </remarks>
    public IReadOnlyList<string>? FocusVocabularyWordIds { get; init; }

    /// <summary>
    /// When false the builder performs zero database writes — specifically it
    /// skips <c>UserProfileRepository.EnsureSmartResourcesAsync</c>, the only
    /// write on the generation path. Defaults to <c>true</c> so every existing
    /// caller keeps today's seeding behavior.
    /// </summary>
    public bool AllowWrites { get; init; } = true;

    /// <summary>
    /// Builds a pure-preview request: explicit user scope, optional
    /// constraints, and no writes. Intended for read-only plan previews such as
    /// the Learning Coach.
    /// </summary>
    public static PlanBuildRequest Preview(
        string userProfileId,
        PlanConstraints? constraints = null,
        IReadOnlyList<string>? focusVocabularyWordIds = null) =>
        new()
        {
            UserProfileId = userProfileId,
            Constraints = constraints,
            FocusVocabularyWordIds = focusVocabularyWordIds,
            AllowWrites = false
        };
}
