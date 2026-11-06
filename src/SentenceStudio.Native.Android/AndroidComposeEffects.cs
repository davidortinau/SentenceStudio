using AndroidX.Compose;
using AndroidX.Compose.Runtime;

namespace SentenceStudio.Native.Android;

/// <summary>
/// The only way this head starts a Compose <c>LaunchedEffect</c>. The pinned facade accepts
/// effect keys of a few types only (null, string, primitives, Java objects) and throws
/// NotSupportedException for anything else during composition, so these overloads take only
/// those types; build a key from several values with <see cref="AndroidEffectKey.Of"/>.
/// </summary>
internal static class AndroidComposeEffects
{
    internal static void Effect(this IComposer composer, string? key,
        Func<CancellationToken, Task> effect) =>
        composer.LaunchedEffect(key, effect);

    internal static void Effect(this IComposer composer, long key,
        Func<CancellationToken, Task> effect) =>
        composer.LaunchedEffect(key, effect);

    internal static void Effect(this IComposer composer, Java.Lang.Object? key,
        Func<CancellationToken, Task> effect) =>
        composer.LaunchedEffect(key, effect);
}
