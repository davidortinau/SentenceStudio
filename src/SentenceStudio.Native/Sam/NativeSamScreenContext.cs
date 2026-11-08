using System.Globalization;
using System.Reflection;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Native;

/// <summary>
/// Builds the screen context a native head sends with a typed Sam message, so a bug report filed
/// through Sam says where it happened. The head supplies the screen id and state; this fills in
/// the build facts the same way on every platform.
/// </summary>
public static class NativeSamScreenContext
{
    /// <param name="platform"><c>ios</c>, <c>android</c> or <c>macos</c>.</param>
    /// <param name="screenId">A stable id such as <c>vocabulary.word-form</c>; shared names across heads.</param>
    /// <param name="headAssembly">The app head's assembly, for its version.</param>
    /// <param name="state">Private key/value state (destination, tab, open sheet, selected ids).</param>
    public static CoachClientScreenContext Build(
        string platform,
        string screenId,
        Assembly headAssembly,
        string? osVersion,
        string? device,
        string? theme,
        bool debugBuild,
        IEnumerable<(string Key, string? Value)> state) => new()
    {
        ScreenId = "native." + screenId,
        Platform = platform,
        AppVersion = Version(headAssembly) + (debugBuild ? " (Debug)" : string.Empty),
        OsVersion = osVersion,
        Device = device,
        Locale = CultureInfo.CurrentUICulture.Name,
        Theme = theme,
        Route = screenId,
        State = state
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .Select(entry => new CoachClientScreenStateEntry { Key = entry.Key, Value = entry.Value })
            .ToArray()
    };

    /// <summary>The tab names both heads use: <c>study</c>, <c>library</c>, <c>settings</c>.</summary>
    public const string StudyTab = "study";
    public const string LibraryTab = "library";
    public const string SettingsTab = "settings";

    /// <summary>
    /// The screen name for a shell destination when no feature page is open. Shared so the same
    /// screen has the same name on every head.
    /// </summary>
    public static string ForDestination(NativeDestination destination, string tab) => destination switch
    {
        NativeDestination.Dashboard or NativeDestination.TodaysPlan => tab,
        NativeDestination.VocabularyQuiz => "vocabulary-quiz",
        NativeDestination.QuizSummary => "quiz-summary",
        NativeDestination.Resources => "resources.list",
        NativeDestination.ResourceDetail => "resources.detail",
        NativeDestination.ResourceEdit => "resources.form",
        NativeDestination.Activity => "activity",
        NativeDestination.SignIn => "sign-in",
        NativeDestination.Onboarding => "onboarding",
        _ => destination.ToString().ToLowerInvariant()
    };

    private static string Version(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        var plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }
}
