using System.Globalization;
using System.Runtime.CompilerServices;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Builds a Compose effect key from several values. The pinned Compose facade boxes effect keys
/// to Java and accepts only null, strings, primitives and Java objects; a tuple, record or other
/// .NET object throws NotSupportedException during composition and closes the app. Every
/// <c>LaunchedEffect</c> in this head goes through <c>AndroidComposeEffects.Effect</c>, whose
/// overloads accept only supported key types; a key made of several values is built here.
/// </summary>
internal static class AndroidEffectKey
{
    /// <summary>
    /// One stable string for the parts, or null (no effect) when any part is null. Strings and
    /// numbers keep their value; any other object is identified by reference, so a new instance
    /// restarts the effect and the same instance does not.
    /// </summary>
    internal static string? Of(params object?[] parts)
    {
        var text = new string[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            text[index] = parts[index] switch
            {
                null => null!,
                string value => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("|", "\\|", StringComparison.Ordinal),
                bool value => value ? "true" : "false",
                IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
                var value => "#" + RuntimeHelpers.GetHashCode(value).ToString(
                    CultureInfo.InvariantCulture),
            };
            if (text[index] is null)
                return null;
        }
        return string.Join('|', text);
    }
}
