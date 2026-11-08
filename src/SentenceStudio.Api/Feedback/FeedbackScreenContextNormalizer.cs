using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using System.Text.RegularExpressions;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Feedback;

/// <summary>
/// Bounds and splits a client's screen context into what may be published and what is kept private.
/// </summary>
/// <remarks>
/// <para>
/// The public half is published verbatim in a public GitHub issue, so every public field must look
/// like an identifier or a version string. Anything else is dropped rather than escaped: a value
/// that does not match the shape is not trusted to be free of learner text.
/// </para>
/// <para>
/// The private half (route and screen state) is stored in the feedback database only, behind a
/// reference id the issue names.
/// </para>
/// </remarks>
public static class FeedbackScreenContextNormalizer
{
    // The control list is published in one card line, which holds 400 characters.
    public const int MaxControlListLength = 360;
    public const int MaxStateEntries = 20;
    public const int MaxRouteLength = 300;
    public const int MaxStateKeyLength = 40;
    public const int MaxStateValueLength = 200;

    /// <summary>
    /// The private JSON is kept well under the snapshot column and the write ledger's argument
    /// limit. Over budget, state entries and then control ids are dropped whole — never sliced,
    /// so what is stored always parses.
    /// </summary>
    public const int MaxPrivateJsonLength = 8 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    // Screen ids, platforms, themes, locales and automation ids: identifier characters only.
    private static readonly Regex IdentifierShape = new(
        @"^[A-Za-z0-9._:/{}\-]{1,80}$", RegexOptions.CultureInvariant, Timeout);

    // Versions, OS names and device models: identifier characters plus space and parentheses.
    private static readonly Regex VersionShape = new(
        @"^[A-Za-z0-9 ._(),\-]{1,40}$", RegexOptions.CultureInvariant, Timeout);

    private static readonly JsonSerializerOptions PrivateJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        // Stored for a person to read when they look a reference up, never rendered as HTML, so
        // Korean is kept as Korean rather than \u escapes.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>The sanitised context. <see cref="IsEmpty"/> when the client sent nothing usable.</summary>
    public sealed record NormalizedScreenContext(
        string? ScreenId,
        string? Platform,
        string? AppVersion,
        string? OsVersion,
        string? Device,
        string? Locale,
        string? Theme,
        IReadOnlyList<string> VisibleControlIds,
        string? Route,
        IReadOnlyDictionary<string, string> State)
    {
        [JsonIgnore]
        public bool IsEmpty =>
            ScreenId is null && Platform is null && AppVersion is null && OsVersion is null
            && Device is null && Locale is null && Theme is null && VisibleControlIds.Count == 0
            && Route is null && State.Count == 0;

        [JsonIgnore]
        public bool HasPrivateDetail => Route is not null || State.Count > 0;

        /// <summary>
        /// The published screen facts, one line each. The issue body and the approval card both
        /// use these exact lines, so the learner approves what is published. Every value is in a
        /// code span, so nothing in it can render as a link or markup.
        /// </summary>
        public IReadOnlyList<string> PublicLines(bool markdown = false)
        {
            // The card shows plain values; the issue wraps the same values in code spans so a
            // value can never render as a link or markup. Same facts, same order, either way.
            string V(string value) => markdown ? $"`{value}`" : value;

            var lines = new List<string>(4)
            {
                $"Screen: {V(ScreenId ?? "unknown")}"
            };

            var build = new List<string>(4);
            if (Platform is not null) build.Add(V(Platform));
            if (AppVersion is not null) build.Add("app " + V(AppVersion));
            if (OsVersion is not null) build.Add(V(OsVersion));
            if (Device is not null) build.Add(V(Device));
            if (build.Count > 0) lines.Add("Build: " + string.Join(" \u00b7 ", build));

            var look = new List<string>(2);
            if (Locale is not null) look.Add("locale " + V(Locale));
            if (Theme is not null) look.Add("theme " + V(Theme));
            if (look.Count > 0) lines.Add("Display: " + string.Join(" \u00b7 ", look));

            if (VisibleControlIds.Count > 0)
            {
                lines.Add("Controls on screen: " + string.Join(", ", VisibleControlIds.Select(V)));
            }

            return lines;
        }

        /// <summary>The public markdown section for the GitHub issue.</summary>
        public string PublicMarkdown(string? privateReference)
        {
            var sb = new StringBuilder();
            sb.AppendLine("### Where");
            foreach (var line in PublicLines(markdown: true))
            {
                var colon = line.IndexOf(':');
                sb.AppendLine($"- **{line[..colon]}:**{line[(colon + 1)..]}");
            }

            if (privateReference is not null)
            {
                sb.AppendLine($"- **Private screen state:** reference `{privateReference}` (kept in the feedback database, not published)");
            }

            return sb.ToString();
        }

        /// <summary>The private record: everything, including route and state.</summary>
        public string ToPrivateJson() => JsonSerializer.Serialize(this, PrivateJson);
    }

    private static readonly NormalizedScreenContext Empty = new(
        null, null, null, null, null, null, null, [], null, new Dictionary<string, string>());

    public static NormalizedScreenContext Normalize(CoachClientScreenContext? context)
    {
        if (context is null)
        {
            return Empty;
        }

        var controls = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var listLength = 0;
        foreach (var id in context.VisibleControlIds ?? [])
        {
            if (Identifier(id) is not { } clean || !seen.Add(clean)) continue;
            if (listLength + clean.Length + 4 > MaxControlListLength) break;
            listLength += clean.Length + 4;
            controls.Add(clean);
        }

        var state = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in context.State ?? [])
        {
            if (state.Count >= MaxStateEntries) break;
            var k = Clean(entry?.Key, MaxStateKeyLength);
            if (k is null) continue;
            state[k] = Clean(entry!.Value, MaxStateValueLength) ?? string.Empty;
        }

        var normalized = new NormalizedScreenContext(
            Identifier(context.ScreenId),
            Identifier(context.Platform)?.ToLowerInvariant(),
            Version(context.AppVersion),
            Version(context.OsVersion),
            Version(context.Device),
            Identifier(context.Locale),
            Identifier(context.Theme)?.ToLowerInvariant(),
            controls,
            Clean(context.Route, MaxRouteLength),
            state);

        return FitPrivateBudget(normalized);
    }

    private static NormalizedScreenContext FitPrivateBudget(NormalizedScreenContext context)
    {
        while (context.ToPrivateJson().Length > MaxPrivateJsonLength)
        {
            if (context.State.Count > 0)
            {
                var trimmed = new SortedDictionary<string, string>(
                    context.State.Take(context.State.Count - 1).ToDictionary(e => e.Key, e => e.Value),
                    StringComparer.Ordinal);
                context = context with { State = trimmed };
            }
            else if (context.VisibleControlIds.Count > 0)
            {
                context = context with { VisibleControlIds = context.VisibleControlIds.Take(context.VisibleControlIds.Count - 1).ToList() };
            }
            else
            {
                return context with { Route = null };
            }
        }

        return context;
    }

    private static string? Identifier(string? value) => Match(IdentifierShape, value?.Trim());

    private static string? Version(string? value) => Match(VersionShape, value?.Trim());

    private static string? Match(Regex shape, string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return shape.IsMatch(value) ? value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var chars = value.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        var text = new string(chars).Trim();
        if (text.Length == 0) return null;
        return text.Length <= max ? text : text[..max];
    }
}
