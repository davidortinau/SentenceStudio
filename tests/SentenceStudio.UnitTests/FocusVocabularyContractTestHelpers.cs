using System.Collections;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.UnitTests;

internal static class FocusVocabularyContractTestHelpers
{
    public const string FocusVocabularyIdsProperty = "FocusVocabularyIds";

    public static readonly string[] RouteParameterKeys =
    {
        "FocusVocabularyIds",
        "focusVocabularyIds",
        "FocusVocabularyWordIds",
        "focusVocabularyWordIds",
    };

    private static readonly HashSet<string> VocabularyAlignedActivityTypes = new(StringComparer.Ordinal)
    {
        "VocabularyReview",
        "VocabularyGame",
        "Cloze",
        "Writing",
        "Translation",
        "Reading",
    };

    private static readonly HashSet<string> NonVocabularyActivityTypes = new(StringComparer.Ordinal)
    {
        "Listening",
        "VideoWatching",
        "Shadowing",
        "NumberDrill",
    };

    public static bool IsVocabularyAlignedActivity(string activityType) =>
        VocabularyAlignedActivityTypes.Contains(activityType);

    public static bool IsNonVocabularyActivity(string activityType) =>
        NonVocabularyActivityTypes.Contains(activityType);

    public static string GetActivityTypeName(object activity)
    {
        var prop = activity.GetType().GetProperty("ActivityType", BindingFlags.Instance | BindingFlags.Public);
        prop.Should().NotBeNull("plan activities must expose ActivityType for focus-vocabulary assertions");
        return prop!.GetValue(activity)?.ToString() ?? string.Empty;
    }

    public static List<string> GetRequiredFocusVocabularyIds(object instance, string because)
    {
        instance.Should().NotBeNull(because);
        var prop = GetFocusVocabularyProperty(instance);
        prop.Should().NotBeNull(
            "Phase 1 requires {0}.{1}; {2}",
            instance.GetType().Name,
            FocusVocabularyIdsProperty,
            because);

        var value = prop!.GetValue(instance);
        value.Should().NotBeNull(
            "{0}.{1} must be populated for vocabulary-aligned plan contract data; {2}",
            instance.GetType().Name,
            FocusVocabularyIdsProperty,
            because);

        return NormalizeIds(value, $"{instance.GetType().Name}.{FocusVocabularyIdsProperty}");
    }

    public static List<string> GetOptionalFocusVocabularyIds(object instance)
    {
        var prop = GetFocusVocabularyProperty(instance);
        if (prop is null)
        {
            return new List<string>();
        }

        var value = prop.GetValue(instance);
        return value is null
            ? new List<string>()
            : NormalizeIds(value, $"{instance.GetType().Name}.{FocusVocabularyIdsProperty}");
    }

    public static void SetRequiredFocusVocabularyIds(object instance, IEnumerable<string> ids, string because)
    {
        var prop = GetFocusVocabularyProperty(instance);
        prop.Should().NotBeNull(
            "Phase 1 requires {0}.{1}; {2}",
            instance.GetType().Name,
            FocusVocabularyIdsProperty,
            because);
        prop!.CanWrite.Should().BeTrue(
            "{0}.{1} must be settable by generators/converters; {2}",
            instance.GetType().Name,
            FocusVocabularyIdsProperty,
            because);

        var idList = ids.ToList();
        object value = BuildAssignableValue(prop.PropertyType, idList);
        prop.SetValue(instance, value);
    }

    public static List<string> GetPreviewWordIds(object? narrative)
    {
        if (narrative is null)
        {
            return new List<string>();
        }

        var vocabInsight = narrative.GetType().GetProperty("VocabInsight")?.GetValue(narrative);
        if (vocabInsight is null)
        {
            return new List<string>();
        }

        var previewWords = vocabInsight.GetType().GetProperty("PreviewWords")?.GetValue(vocabInsight);
        if (previewWords is null)
        {
            return new List<string>();
        }

        if (previewWords is not IEnumerable enumerable || previewWords is string)
        {
            return new List<string>();
        }

        var ids = new List<string>();
        foreach (var item in enumerable)
        {
            if (item is null)
            {
                continue;
            }

            var wordId = item.GetType().GetProperty("WordId")?.GetValue(item)?.ToString();
            if (!string.IsNullOrWhiteSpace(wordId))
            {
                ids.Add(wordId);
            }
        }

        return ids;
    }

    public static List<string> GetRequiredRouteFocusIds(
        Dictionary<string, object>? routeParameters,
        string activityType)
    {
        routeParameters.Should().NotBeNull("{0} must have route parameters", activityType);

        foreach (var key in RouteParameterKeys)
        {
            if (routeParameters!.TryGetValue(key, out var value))
            {
                return NormalizeIds(value, $"RouteParameters[{key}]");
            }
        }

        routeParameters!.Keys.Should().Contain(
            RouteParameterKeys,
            "{0} route parameters must carry the Phase 1 focus vocabulary IDs", activityType);
        return new List<string>();
    }

    public static List<string> GetOptionalRouteFocusIds(Dictionary<string, object>? routeParameters)
    {
        if (routeParameters is null)
        {
            return new List<string>();
        }

        foreach (var key in RouteParameterKeys)
        {
            if (routeParameters.TryGetValue(key, out var value))
            {
                return NormalizeIds(value, $"RouteParameters[{key}]");
            }
        }

        return new List<string>();
    }

    public static IProperty? FindFocusVocabularyEfProperty(IEntityType? entityType)
    {
        return entityType?.GetProperties().FirstOrDefault(p =>
            p.Name.Contains("Focus", StringComparison.OrdinalIgnoreCase)
            && p.Name.Contains("Vocabulary", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsEmptyContractResult(object? value)
    {
        if (value is null)
        {
            return true;
        }

        if (value is bool boolean)
        {
            return boolean == false;
        }

        if (value is int integer)
        {
            return integer == 0;
        }

        if (value is string text)
        {
            return string.IsNullOrWhiteSpace(text);
        }

        if (value is IEnumerable enumerable)
        {
            return !enumerable.Cast<object?>().Any();
        }

        return false;
    }

    private static PropertyInfo? GetFocusVocabularyProperty(object instance) =>
        instance.GetType().GetProperty(FocusVocabularyIdsProperty, BindingFlags.Instance | BindingFlags.Public);

    private static object BuildAssignableValue(Type targetType, List<string> ids)
    {
        if (targetType == typeof(string[]))
        {
            return ids.ToArray();
        }

        if (targetType.IsAssignableFrom(typeof(List<string>)))
        {
            return ids;
        }

        if (targetType.IsAssignableFrom(typeof(string[])))
        {
            return ids.ToArray();
        }

        if (targetType == typeof(IReadOnlyList<string>)
            || targetType == typeof(IReadOnlyCollection<string>)
            || targetType == typeof(IEnumerable<string>))
        {
            return ids;
        }

        if (targetType == typeof(string))
        {
            return string.Join(",", ids);
        }

        throw new InvalidOperationException(
            $"FocusVocabularyIds should be a string collection so ordering and identity survive the plan contract, but was {targetType.FullName}.");
    }

    private static List<string> NormalizeIds(object value, string owner)
    {
        if (value is string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new List<string>();
            }

            var trimmed = text.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<string>>(trimmed);
                    if (parsed is not null)
                    {
                        return parsed.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
                    }
                }
                catch (JsonException)
                {
                    // Fall through to comma splitting; the assertion below catches bad values.
                }
            }

            return trimmed
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();
        }

        if (value is IEnumerable<string> typed)
        {
            return typed.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        }

        if (value is IEnumerable enumerable)
        {
            return enumerable
                .Cast<object?>()
                .Select(item => item?.ToString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToList();
        }

        value.Should().BeAssignableTo<IEnumerable<string>>("{0} must be a focus vocabulary ID collection", owner);
        return new List<string>();
    }
}

internal sealed class CollectingLoggerProvider : ILoggerProvider
{
    private readonly List<CollectedLogEntry> _entries = new();

    public IReadOnlyList<CollectedLogEntry> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CollectingLogger(categoryName, _entries);

    public bool HasWarningContaining(params string[] terms) => _entries.Any(entry =>
        entry.Level >= LogLevel.Warning
        && terms.All(term => entry.Message.Contains(term, StringComparison.OrdinalIgnoreCase)));

    public void Dispose()
    {
    }

    private sealed class CollectingLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly List<CollectedLogEntry> _entries;

        public CollectingLogger(string categoryName, List<CollectedLogEntry> entries)
        {
            _categoryName = categoryName;
            _entries = entries;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add(new CollectedLogEntry(
                logLevel,
                _categoryName,
                formatter(state, exception),
                exception));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}

internal sealed record CollectedLogEntry(
    LogLevel Level,
    string Category,
    string Message,
    Exception? Exception);
