using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace SentenceStudio.Api.Coach.Telemetry;

/// <summary>
/// Reduces a provider or model failure to a small set of allow-listed, content-free facts.
/// </summary>
/// <remarks>
/// <para>
/// A provider exception is not a safe log record. The prompt is frequently echoed back inside
/// <see cref="Exception.Message"/> (content-filter and token-limit errors quote the offending
/// span), the request or response body is often attached to an inner exception or to
/// <see cref="Exception.Data"/>, and <see cref="Exception.ToString()"/> concatenates all of it.
/// Passing the exception object to <c>ILogger.LogError(ex, ...)</c> is enough to write learner
/// text and model output into whatever sink is configured.
/// </para>
/// <para>
/// So nothing is ever forwarded from the exception verbatim. The category comes from a closed
/// map keyed on type name, the status comes only from an integer-valued <c>Status</c> or
/// <c>StatusCode</c> member, and the provider error code is admitted only when it matches a
/// strict symbolic-token shape and is short. Everything else is dropped, including the message.
/// </para>
/// </remarks>
public static partial class CoachExceptionSanitizer
{
    /// <summary>Fallback used when no allow-listed category matches.</summary>
    public const string UnclassifiedCategory = "unclassified";

    /// <summary>The longest provider error code that will be forwarded.</summary>
    private const int MaxErrorCodeLength = 64;

    /// <summary>How far to walk the inner-exception chain when classifying.</summary>
    private const int MaxInnerDepth = 8;

    /// <summary>
    /// Type name to category. Matched against the exception type and its base types, so a
    /// provider-specific subclass still lands in the right bucket.
    /// </summary>
    private static readonly Dictionary<string, string> CategoriesByTypeName = new(StringComparer.Ordinal)
    {
        ["OperationCanceledException"] = "canceled",
        ["TaskCanceledException"] = "canceled",
        ["TimeoutException"] = "timeout",
        ["HttpRequestException"] = "http_transport",
        ["SocketException"] = "network",
        ["IOException"] = "io",
        ["JsonException"] = "serialization",
        ["NotSupportedException"] = "not_supported",
        ["InvalidOperationException"] = "invalid_operation",
        ["ArgumentException"] = "argument",
        ["ArgumentNullException"] = "argument",
        ["ArgumentOutOfRangeException"] = "argument",
        ["FormatException"] = "format",
        ["UnauthorizedAccessException"] = "unauthorized",
        ["AuthenticationFailedException"] = "provider_auth",
        ["CredentialUnavailableException"] = "provider_auth",
        ["RequestFailedException"] = "provider_request_failed",
        ["ClientResultException"] = "provider_request_failed",
        ["HttpOperationException"] = "provider_request_failed",
        ["AggregateException"] = "aggregate"
    };

    /// <summary>
    /// A provider error code is forwarded only when it looks like a symbolic token
    /// (<c>content_filter</c>, <c>RateLimitReached</c>). Anything with whitespace, punctuation,
    /// or free text is dropped, because that shape is how prompts leak.
    /// </summary>
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SymbolicCodePattern { get; }

    /// <summary>Reduces a failure to the facts that are safe to log.</summary>
    public static CoachSafeExceptionFacts Describe(Exception? exception)
    {
        if (exception is null)
        {
            return CoachSafeExceptionFacts.None;
        }

        var category = Categorize(exception);
        var (status, code) = ReadProviderSignals(exception);
        var depth = MeasureInnerDepth(exception);

        return new CoachSafeExceptionFacts(category, status, code, depth);
    }

    /// <summary>
    /// Maps the exception to an allow-listed category, walking base types and then the inner
    /// chain so a wrapped provider failure is still classified rather than falling through to
    /// <see cref="UnclassifiedCategory"/>.
    /// </summary>
    private static string Categorize(Exception exception)
    {
        var current = exception;

        for (var depth = 0; current is not null && depth <= MaxInnerDepth; depth++)
        {
            for (var type = current.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            {
                if (CategoriesByTypeName.TryGetValue(type.Name, out var category))
                {
                    // AggregateException on its own says nothing useful; prefer whatever it wraps.
                    if (category is not "aggregate")
                    {
                        return category;
                    }
                }
            }

            current = current.InnerException;
        }

        return UnclassifiedCategory;
    }

    /// <summary>
    /// Reads an HTTP-shaped status and a symbolic error code from the exception chain.
    /// </summary>
    /// <remarks>
    /// Reflection is used on purpose. The alternative is compiling against every provider SDK
    /// that can surface here (Azure.Core, System.ClientModel, and whatever a future provider
    /// brings), which couples a security helper to the model vendor list. Only members with the
    /// exact names <c>Status</c>/<c>StatusCode</c> and an integer-like value are read, so no
    /// string member can be picked up by accident.
    /// </remarks>
    private static (int? Status, string? ErrorCode) ReadProviderSignals(Exception exception)
    {
        var current = exception;
        int? status = null;
        string? code = null;

        for (var depth = 0; current is not null && depth <= MaxInnerDepth; depth++)
        {
            status ??= ReadStatus(current);
            code ??= ReadErrorCode(current);

            if (status is not null && code is not null)
            {
                break;
            }

            current = current.InnerException;
        }

        return (status, code);
    }

    private static int? ReadStatus(Exception exception)
    {
        foreach (var name in (string[])["Status", "StatusCode"])
        {
            var property = exception.GetType().GetProperty(
                name,
                BindingFlags.Public | BindingFlags.Instance);

            if (property is null || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            object? raw;
            try
            {
                raw = property.GetValue(exception);
            }
            catch (TargetInvocationException)
            {
                // A property that throws tells us nothing; treat it as absent.
                continue;
            }

            switch (raw)
            {
                case int value:
                    return Sanitize(value);
                // HttpStatusCode and friends.
                case Enum enumValue:
                    return Sanitize(Convert.ToInt32(enumValue, CultureInfo.InvariantCulture));
            }
        }

        return null;

        // Only plausible HTTP statuses are forwarded, so a coincidental integer member on some
        // future exception type cannot turn into a bogus telemetry dimension.
        static int? Sanitize(int value) => value is >= 100 and <= 599 ? value : null;
    }

    private static string? ReadErrorCode(Exception exception)
    {
        foreach (var name in (string[])["ErrorCode", "Code"])
        {
            var property = exception.GetType().GetProperty(
                name,
                BindingFlags.Public | BindingFlags.Instance);

            if (property is null
                || property.PropertyType != typeof(string)
                || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            string? raw;
            try
            {
                raw = property.GetValue(exception) as string;
            }
            catch (TargetInvocationException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxErrorCodeLength)
            {
                continue;
            }

            if (SymbolicCodePattern.IsMatch(raw))
            {
                return raw;
            }
        }

        return null;
    }

    private static int MeasureInnerDepth(Exception exception)
    {
        var depth = 0;
        var current = exception.InnerException;

        while (current is not null && depth < MaxInnerDepth)
        {
            depth++;
            current = current.InnerException;
        }

        return depth;
    }
}

/// <summary>
/// The content-free facts about a failure. Every member is safe to log, and there is
/// deliberately no member that can carry learner text, prompt text, or model output.
/// </summary>
/// <param name="Category">An allow-listed category such as <c>timeout</c>.</param>
/// <param name="ProviderStatus">An HTTP-shaped status from the provider, when present.</param>
/// <param name="ProviderErrorCode">A symbolic provider code such as <c>content_filter</c>.</param>
/// <param name="InnerDepth">How deep the inner-exception chain ran. Shape only.</param>
public readonly record struct CoachSafeExceptionFacts(
    string Category,
    int? ProviderStatus,
    string? ProviderErrorCode,
    int InnerDepth)
{
    /// <summary>The facts for "no exception".</summary>
    public static CoachSafeExceptionFacts None { get; } = new("none", null, null, 0);
}
