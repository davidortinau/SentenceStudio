namespace SentenceStudio.Api.Feedback;

/// <summary>
/// The complete set of labels this deployment will ever put on a public issue.
/// </summary>
/// <remarks>
/// <para>
/// Closed, and enforced twice: once when the preview is built, and again when the token is
/// redeemed. The second check looks redundant — the label array is inside the HMAC-covered
/// payload, so it cannot have been edited in transit — and it is kept anyway, because the threat
/// it answers is not tampering. It is the model. The label array originates in an LLM response to
/// learner-supplied text, and a prompt-injection payload that talks the model into emitting
/// <c>security</c>, <c>good first issue</c>, or an <c>@</c>-mention-shaped label would be signed
/// by us and applied by us. Filtering at the point of signature is what stops that; re-checking at
/// redemption is what stops a future refactor from moving the signing step somewhere the filter
/// is not.
/// </para>
/// <para>
/// The empty case matters as much as the invalid one. Filtering an all-invalid array leaves an
/// empty array, and an empty array posted to GitHub means "no labels" — the failure is silent and
/// looks like a triage oversight rather than a rejected model output. The fallback is the
/// feedback type, which is itself closed.
/// </para>
/// </remarks>
public static class FeedbackLabels
{
    /// <summary>The bug label, and one of the two accepted feedback types.</summary>
    public const string Bug = "bug";

    /// <summary>The enhancement label, and the other accepted feedback type.</summary>
    public const string Enhancement = "enhancement";

    private static readonly string[] AllowedValues = [Bug, Enhancement];

    /// <summary>Every label that may reach GitHub.</summary>
    public static IReadOnlyList<string> Allowed => AllowedValues;

    /// <summary>True when <paramref name="value"/> is one of the accepted labels, ordinally.</summary>
    public static bool IsAllowed(string? value) =>
        value is not null && Array.IndexOf(AllowedValues, value) >= 0;

    /// <summary>
    /// The accepted feedback type for <paramref name="value"/>, defaulting to
    /// <see cref="Enhancement"/>. Anything unrecognised — including null — is a feature request,
    /// because mislabelling a request as a bug is the more misleading of the two mistakes.
    /// </summary>
    public static string NormalizeType(string? value) =>
        value is Bug or Enhancement ? value : Enhancement;

    /// <summary>
    /// The labels that will actually be applied: the allowed subset of
    /// <paramref name="candidates"/>, de-duplicated, or the feedback type when that subset is
    /// empty.
    /// </summary>
    public static string[] Sanitize(IEnumerable<string?>? candidates, string feedbackType)
    {
        var type = NormalizeType(feedbackType);

        if (candidates is null)
        {
            return [type];
        }

        var kept = new List<string>(AllowedValues.Length);
        foreach (var candidate in candidates)
        {
            if (IsAllowed(candidate) && !kept.Contains(candidate!, StringComparer.Ordinal))
            {
                kept.Add(candidate!);
            }
        }

        return kept.Count == 0 ? [type] : kept.ToArray();
    }
}
