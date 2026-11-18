namespace SentenceStudio.Services;

/// <summary>
/// The built-in conversation scenarios by English name, with words a learner might use for each,
/// so the server can match "getting directions" to Asking for Directions without the scenario
/// rows (built-ins never sync; each client seeds them with the same fixed id, see
/// <see cref="SyncIds.BuiltInScenario"/>). The names must match the seeded list in AppLib's
/// ScenarioService; a unit test holds them together.
/// </summary>
public static class BuiltInConversationScenarios
{
    public sealed record Entry(string Name, IReadOnlyList<string> Words)
    {
        public string Id => SyncIds.BuiltInScenario(Name);
    }

    public static IReadOnlyList<Entry> All { get; } =
    [
        new("First Meeting", ["first meeting", "meeting", "meet", "introduce", "introduction", "introductions", "new person", "getting acquainted", "self introduction", "만남", "소개", "자기소개"]),
        new("Ordering Coffee", ["coffee", "cafe", "café", "barista", "latte", "커피", "카페"]),
        new("Ordering Dinner", ["dinner", "restaurant", "ordering food", "order food", "meal", "bbq", "barbecue", "waiter", "저녁", "식당", "음식 주문"]),
        new("Asking for Directions", ["directions", "direction", "the way", "lost", "getting around", "find the station", "street", "길 찾기", "길찾기", "길을", "길 좀"]),
        new("Weekend Plans", ["weekend", "weekend plans", "free time", "주말"])
    ];

    /// <summary>
    /// The built-in scenario the learner's words describe, or null when none clearly does (no
    /// match, or two scenarios match equally). Whole-word matching, case-insensitive: "meal" does
    /// not match "oatmeal". A Korean word may carry a particle after it ("주말에"), so it needs a
    /// word start only. A keyword inside a longer matched one ("weekend" in "weekend plans") counts once.
    /// </summary>
    public static Entry? Match(string? words)
    {
        if (string.IsNullOrWhiteSpace(words))
            return null;
        var text = words.Trim().ToLowerInvariant();
        var scored = All
            .Select(entry => (entry, score: Score(entry, text)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ToList();
        if (scored.Count == 0 || scored.Count > 1 && scored[0].score == scored[1].score)
            return null;
        return scored[0].entry;
    }

    private static int Score(Entry entry, string text)
    {
        var matched = entry.Words.Where(word => Contains(text, word)).ToList();
        var distinct = matched.Count(word => !matched.Any(other =>
            other.Length > word.Length && other.Contains(word, StringComparison.Ordinal)));
        return distinct + (Contains(text, entry.Name.ToLowerInvariant()) ? 5 : 0);
    }

    private static bool Contains(string text, string word)
    {
        var hangul = word.Any(c => c is >= '\uAC00' and <= '\uD7A3');
        var pattern = @"(?<!\p{L})" + System.Text.RegularExpressions.Regex.Escape(word) + (hangul ? string.Empty : @"(?!\p{L})");
        return System.Text.RegularExpressions.Regex.IsMatch(text, pattern);
    }
}
