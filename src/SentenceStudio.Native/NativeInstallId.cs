using SentenceStudio.Abstractions;

namespace SentenceStudio.Native;

/// <summary>
/// Identifies this app install in a saved quiz state (<c>VocabQuizSavedState.SavedBy</c>), so a
/// graded answer synced to another device is scored only by the install that graded it
/// (docs/activity-session-format.md). Kept in preferences; a new install gets a new ID.
/// </summary>
/// <remarks>
/// A device backup restored onto a second device copies preferences, so both would share the
/// ID; then either could score the other's graded-but-unconfirmed answer. That needs a restore
/// while the old device is still in use and a quiz left on its feedback screen.
/// </remarks>
internal static class NativeInstallId
{
    private const string Key = "native.installId";
    private static readonly object Gate = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPreferencesService, string> Cache = new();

    public static string Get(IPreferencesService preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (Gate)
        {
            if (Cache.TryGetValue(preferences, out var cached))
                return cached;
            var id = preferences.Get(Key, string.Empty);
            if (!Guid.TryParseExact(id, "N", out _))
            {
                id = Guid.NewGuid().ToString("N");
                preferences.Set(Key, id);
            }
            var installId = "native:" + id;
            Cache.AddOrUpdate(preferences, installId);
            return installId;
        }
    }
}
