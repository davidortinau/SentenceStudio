using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Plugin.Maui.HelpKit.Localization;

/// <summary>
/// Lightweight flat-key localizer for HelpKit chrome. Reads embedded
/// <c>Strings.{lang}.json</c> files. Honors
/// <see cref="HelpKitOptions.Language"/>; falls back to English and
/// ultimately to the key itself when a lookup misses.
/// </summary>
public sealed class HelpKitLocalizer
{
    private static readonly Dictionary<string, Dictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _cacheLock = new();

    private readonly Dictionary<string, string> _active;
    private readonly Dictionary<string, string> _fallback;
    private readonly string _language;

    public HelpKitLocalizer(IOptions<HelpKitOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _language = string.IsNullOrWhiteSpace(options.Value.Language) ? "en" : options.Value.Language;
        _fallback = Load("en");
        _active = string.Equals(_language, "en", StringComparison.OrdinalIgnoreCase)
            ? _fallback
            : Load(_language);
    }

    /// <summary>Current language code actually in use.</summary>
    public string Language => _language;

    /// <summary>
    /// Returns the localized string for <paramref name="key"/>. Falls back
    /// to English, then to the key itself.
    /// </summary>
    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        if (_active.TryGetValue(key, out var v)) return v;
        if (_fallback.TryGetValue(key, out var f)) return f;
        return key;
    }

    private static Dictionary<string, string> Load(string lang)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(lang, out var cached))
                return cached;

            var dict = LoadFromManifest(lang) ?? new Dictionary<string, string>(StringComparer.Ordinal);
            _cache[lang] = dict;
            return dict;
        }
    }

    private static Dictionary<string, string>? LoadFromManifest(string lang)
    {
        var asm = typeof(HelpKitLocalizer).Assembly;
        var resourceName = $"{asm.GetName().Name}.Resources.Strings.{lang}.json";

        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(stream);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    result[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
