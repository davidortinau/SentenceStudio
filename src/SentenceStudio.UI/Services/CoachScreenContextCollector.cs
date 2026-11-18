using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Builds the screen context sent with a typed Sam message: which screen a bug report filed through
/// Sam was about, and what "this" means when the learner asks Sam about the screen. Used by the web
/// app and the MAUI heads (shared Razor UI).
/// </summary>
/// <remarks>
/// Best effort: a failed snapshot sends a turn without context rather than delaying or failing it.
/// The route template and control ids are public-safe identifiers; the concrete route and query go
/// in the private part, which the server never publishes.
/// </remarks>
public sealed class CoachScreenContextCollector : IAsyncDisposable
{
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromMilliseconds(250);

    private readonly NavigationManager _nav;
    private readonly IJSRuntime _js;
    private readonly ILogger<CoachScreenContextCollector> _logger;
    private readonly CoachScreenSubject? _subject;
    private IJSObjectReference? _module;

    public CoachScreenContextCollector(
        NavigationManager nav, IJSRuntime js, ILogger<CoachScreenContextCollector> logger,
        CoachScreenSubject? subject = null)
    {
        _nav = nav;
        _js = js;
        _logger = logger;
        _subject = subject;
    }

    public async Task<CoachClientScreenContext?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var relative = _nav.ToBaseRelativePath(_nav.Uri);
            var path = relative.Split('?', '#')[0];
            var isWebView = BlazorHostKind.IsWebViewHost(_nav.BaseUri);

            // The page describes itself while the browser snapshot runs: neither waits on the
            // other, so a send is held up by at most one timeout.
            var describing = DescribePageAsync(cancellationToken);

            Snapshot? snapshot = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(SnapshotTimeout);
                // Imported once per circuit, not per message.
                _module ??= await _js.InvokeAsync<IJSObjectReference>(
                    "import", timeout.Token, "./_content/SentenceStudio.UI/js/app.js");
                snapshot = await _module.InvokeAsync<Snapshot>("coachScreenSnapshot", timeout.Token);
            }
            catch (Exception ex) when (ex is JSException or TaskCanceledException or OperationCanceledException
                                           or InvalidOperationException or JSDisconnectedException)
            {
                _logger.LogDebug("Sam screen snapshot unavailable: {ExceptionType}", ex.GetType().Name);
            }

            var state = new List<CoachClientScreenStateEntry>();
            if (snapshot?.Viewport is { Length: > 0 } viewport)
            {
                state.Add(new CoachClientScreenStateEntry { Key = "viewport", Value = viewport });
            }

            var template = Template(path);
            var page = await describing.ConfigureAwait(false);

            return new CoachClientScreenContext
            {
                Items = Items(page?.Items ?? [], RouteItems(template, relative)),
                AnswerHidden = page?.AnswerHidden ?? false,
                ScreenId = "/" + template,
                Platform = isWebView ? NativePlatform() : "web",
                AppVersion = AppVersion(),
                OsVersion = isWebView ? OsName() + " " + Environment.OSVersion.Version : null,
                Device = isWebView ? null : snapshot?.Browser,
                Locale = CultureInfo.CurrentUICulture.Name,
                Theme = snapshot?.Theme,
                VisibleControlIds = snapshot?.ControlIds,
                Route = "/" + relative,
                State = state,
                Facts = page?.Facts?.Where(fact => fact.Value is not null).ToArray()
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Sam screen context could not be built; sending the turn without it.");
            return null;
        }
    }

    /// <summary>The open page's own description; a page that fails to describe itself sends none.</summary>
    private async Task<CoachScreenDescription?> DescribePageAsync(CancellationToken cancellationToken)
    {
        if (_subject is null)
            return null;
        try
        {
            // Bounded like the browser snapshot: a busy page must not hold up the message.
            return await _subject.DescribeAsync().WaitAsync(SnapshotTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("The open page did not describe itself to Sam in time; sending the screen without it.");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The open page could not describe itself to Sam; sending the screen without it.");
            return null;
        }
    }

    /// <summary>The page's items first, then route items it did not already name.</summary>
    public static IReadOnlyList<CoachClientScreenItem> Items(
        IReadOnlyList<CoachClientScreenItem> page, IEnumerable<CoachClientScreenItem> route)
    {
        var items = page.ToList();
        var hasFocus = items.Any(item => item.Focus);
        foreach (var item in route)
        {
            if (items.Any(known => known.Kind == item.Kind && item.Id is not null && known.Id == item.Id))
                continue;
            items.Add(hasFocus && item.Focus ? new CoachClientScreenItem
            {
                Kind = item.Kind, Id = item.Id, Label = item.Label, Detail = item.Detail, Focus = false
            } : item);
            hasFocus |= item.Focus;
        }

        return items;
    }

    /// <summary>Activities by route template, so "this activity" names the one open.</summary>
    private static readonly HashSet<string> ActivityRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "cloze", "conversation", "flashcard-activity", "how-do-you-say", "numberdrill", "reading",
        "scene", "shadowing", "translation", "video-watching", "vocab-matching", "vocab-quiz",
        "word-association", "writing", "minimal-pairs/session/{Id}", "diary", "diary/{Id}", "diary/today"
    };

    /// <summary>
    /// The things the route itself names: the word, resource, skill, pair or channel in the path,
    /// and the resources and skill an activity was opened with (its query).
    /// </summary>
    public static IEnumerable<CoachClientScreenItem> RouteItems(string template, string relative)
    {
        var parts = relative.Split('#')[0].Split('?', 2);
        var segments = parts[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var id = segments.Length > 0 ? Uri.UnescapeDataString(segments[^1]) : null;

        var kind = template.ToLowerInvariant() switch
        {
            "vocabulary/edit/{id}" => CoachClientScreenItemKinds.VocabularyWord,
            "resources/edit/{id}" => CoachClientScreenItemKinds.LearningResource,
            "skills/edit/{id}" => CoachClientScreenItemKinds.Skill,
            "minimal-pairs/session/{id}" => CoachClientScreenItemKinds.MinimalPair,
            "import/channel/{id}" or "media-import/channel/{id}" => CoachClientScreenItemKinds.YouTubeChannel,
            "diary/{id}" => CoachClientScreenItemKinds.Text,
            _ => null
        };
        if (kind is not null && !string.IsNullOrWhiteSpace(id))
            yield return CoachScreenDescription.Item(kind, id, null, focus: true);

        var query = parts.Length > 1 ? Query(parts[1]) : [];
        foreach (var (key, value) in query)
        {
            var queryKind = key.ToLowerInvariant() switch
            {
                "resourceid" or "resourceids" => CoachClientScreenItemKinds.LearningResource,
                "skillid" => CoachClientScreenItemKinds.Skill,
                _ => null
            };
            if (queryKind is null) continue;
            foreach (var one in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(5))
                yield return CoachScreenDescription.Item(queryKind, one, null);
        }

        // The activity is what "this" means when the page names nothing more specific; Items
        // drops this focus when the page marked its own, as native does.
        if (ActivityRoutes.Contains(template))
            yield return CoachScreenDescription.Item(CoachClientScreenItemKinds.Activity, template.Split('/')[0], null, focus: true);
    }

    private static IEnumerable<(string Key, string Value)> Query(string query) =>
        query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[1].Length > 0)
            .Select(pair => (Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1].Replace('+', ' '))));

    /// <summary>
    /// The app route template the path matches, for example <c>media-import/channel/{Id}</c>.
    /// Published, so it comes from the app's own <c>@page</c> declarations and never from the URL:
    /// learner text in a path (a search term, a word) can only ever appear as a parameter name.
    /// An unmatched path is reported as <c>unknown</c>.
    /// </summary>
    public static string Template(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? best = null;
        var bestLiterals = -1;
        foreach (var template in RouteTemplates.Value)
        {
            var parts = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (!Matches(parts, segments, out var literals) || literals <= bestLiterals) continue;
            best = string.Join('/', parts.Take(Math.Max(segments.Length, RequiredCount(parts))).Select(Normalize));
            bestLiterals = literals;
        }

        return best ?? (segments.Length == 0 ? string.Empty : "unknown");
    }

    private static readonly Lazy<string[]> RouteTemplates = new(() =>
        typeof(CoachScreenContextCollector).Assembly.GetTypes()
            .SelectMany(type => type.GetCustomAttributes<RouteAttribute>(inherit: false))
            .Select(route => route.Template.Trim('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());

    private static bool Matches(string[] template, string[] path, out int literals)
    {
        literals = 0;
        if (path.Length > template.Length || path.Length < RequiredCount(template)) return false;
        for (var i = 0; i < path.Length; i++)
        {
            var part = template[i];
            if (IsParameter(part)) continue;
            if (!string.Equals(part, path[i], StringComparison.OrdinalIgnoreCase)) return false;
            literals++;
        }

        return true;
    }

    private static int RequiredCount(string[] template) =>
        template.Count(part => !(IsParameter(part) && (part.Contains('?') || part.StartsWith("{*", StringComparison.Ordinal))));

    private static bool IsParameter(string part) => part.StartsWith('{') && part.EndsWith('}');

    /// <summary><c>{Id:int?}</c> becomes <c>{Id}</c>: constraints and markers are not identifier-shaped.</summary>
    private static string Normalize(string part)
    {
        if (!IsParameter(part)) return part;
        var name = part.Trim('{', '}', '*').Split(':')[0].TrimEnd('?');
        return "{" + name + "}";
    }

    private static string NativePlatform() =>
        OperatingSystem.IsIOS() ? "maui-ios"
        : OperatingSystem.IsAndroid() ? "maui-android"
        : OperatingSystem.IsMacCatalyst() ? "maui-maccatalyst"
        : OperatingSystem.IsWindows() ? "maui-windows"
        : "maui";

    private static string OsName() =>
        OperatingSystem.IsIOS() ? "iOS"
        : OperatingSystem.IsAndroid() ? "Android"
        : OperatingSystem.IsMacCatalyst() ? "macOS"
        : OperatingSystem.IsWindows() ? "Windows"
        : "OS";

    private static string AppVersion()
    {
        var asm = typeof(CoachScreenContextCollector).Assembly;
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString() ?? "unknown";
        var plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];
#if DEBUG
        return version + " (Debug)";
#else
        return version;
#endif
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try
        {
            await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The circuit is already gone; nothing to release.
        }
    }

    private sealed class Snapshot
    {
        public List<string>? ControlIds { get; set; }
        public string? Theme { get; set; }
        public string? Browser { get; set; }
        public string? Viewport { get; set; }
    }
}
