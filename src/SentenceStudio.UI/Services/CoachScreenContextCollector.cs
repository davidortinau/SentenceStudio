using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Builds the screen context sent with a typed Sam message, so a bug report filed through Sam can
/// say which screen it was about. Used by the web app and the MAUI heads (shared Razor UI).
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
    private IJSObjectReference? _module;

    public CoachScreenContextCollector(
        NavigationManager nav, IJSRuntime js, ILogger<CoachScreenContextCollector> logger)
    {
        _nav = nav;
        _js = js;
        _logger = logger;
    }

    public async Task<CoachClientScreenContext?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var relative = _nav.ToBaseRelativePath(_nav.Uri);
            var path = relative.Split('?', '#')[0];
            var isWebView = BlazorHostKind.IsWebViewHost(_nav.BaseUri);

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

            return new CoachClientScreenContext
            {
                ScreenId = "/" + Template(path),
                Platform = isWebView ? NativePlatform() : "web",
                AppVersion = AppVersion(),
                OsVersion = isWebView ? OsName() + " " + Environment.OSVersion.Version : null,
                Device = isWebView ? null : snapshot?.Browser,
                Locale = CultureInfo.CurrentUICulture.Name,
                Theme = snapshot?.Theme,
                VisibleControlIds = snapshot?.ControlIds,
                Route = "/" + relative,
                State = state
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Sam screen context could not be built; sending the turn without it.");
            return null;
        }
    }

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
