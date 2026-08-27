using System.Collections.Concurrent;
using System.Reflection;

namespace SentenceStudio.Contracts.Wire;

/// <summary>
/// Resolves the declared unknown-value fallback for a wire enum.
/// </summary>
/// <remarks>
/// Shared by the tolerant converter and by the architecture test so the two cannot disagree about
/// what "annotated" means. A type that is missing the attribute, or names a member that does not
/// exist, fails here rather than degrading quietly — the whole point of the policy is that the
/// decision is made deliberately, and a silent miss would restore exactly the behaviour it
/// replaces.
/// </remarks>
public static class WireEnumFallback
{
    private static readonly ConcurrentDictionary<Type, WireEnumFallbackDescriptor?> Cache = new();

    /// <summary>
    /// The declared fallback for <paramref name="enumType"/>, or <see langword="null"/> when the
    /// type carries no policy.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The attribute is present but names a member the enum does not have.
    /// </exception>
    public static WireEnumFallbackDescriptor? TryDescribe(Type enumType)
    {
        ArgumentNullException.ThrowIfNull(enumType);

        return Cache.GetOrAdd(enumType, static type =>
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            if (!underlying.IsEnum)
            {
                return null;
            }

            if (underlying.GetCustomAttribute<WireEnumFallbackAttribute>(inherit: false) is not { } attribute)
            {
                return null;
            }

            if (!Enum.TryParse(underlying, attribute.MemberName, ignoreCase: false, out var value)
                || value is null
                || !Enum.IsDefined(underlying, value))
            {
                throw new InvalidOperationException(
                    $"{underlying.FullName} declares the wire fallback '{attribute.MemberName}', which is not one of its members.");
            }

            return new WireEnumFallbackDescriptor(underlying, value, attribute.Kind, attribute.Rationale);
        });
    }

    /// <summary>The declared fallback for <paramref name="enumType"/>.</summary>
    /// <exception cref="InvalidOperationException">The type carries no policy.</exception>
    public static WireEnumFallbackDescriptor Describe(Type enumType) =>
        TryDescribe(enumType)
        ?? throw new InvalidOperationException(
            $"{enumType.FullName} crosses the API/client boundary but declares no [WireEnumFallback]. "
            + "Add one naming the member an unreadable value must collapse to.");

    /// <summary>True when <paramref name="enumType"/> declares a fallback.</summary>
    public static bool IsAnnotated(Type enumType) => TryDescribe(enumType) is not null;
}

/// <summary>The resolved fallback for one wire enum.</summary>
/// <param name="EnumType">The enum the policy belongs to.</param>
/// <param name="Value">The member an unknown value collapses to, boxed.</param>
/// <param name="Kind">How the member was chosen.</param>
/// <param name="Rationale">Why the member is safe.</param>
public sealed record WireEnumFallbackDescriptor(
    Type EnumType,
    object Value,
    WireEnumFallbackKind Kind,
    string Rationale)
{
    /// <summary>The canonical name of the fallback member.</summary>
    public string MemberName { get; } = Enum.GetName(EnumType, Value)!;
}
