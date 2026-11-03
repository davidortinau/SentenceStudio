using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// Regression (2026-11-02): two quick Backs (Minimal Pairs session, then its list) left the
/// Dashboard on "Loading" until restart. Popping the activity re-pushed and re-rendered it while
/// its exit was pending; the render touched an alert SwiftUI had already disposed and threw
/// before the busy runner's try, so the busy flag was never cleared.
/// </summary>
public sealed class NativeActivityBackContractTests
{
    [Fact]
    public void BusyRunner_LogsARenderFailure_StillRunsTheAction_AndClearsBusy()
    {
        var runner = Member(Read("App/ApplicationController.cs"),
            "private async Task RunSafelyAsync(", "private void PublishLoggingFailure(");

        // The render before the action cannot throw out of the runner, and the action follows.
        Assert.Matches(new Regex(
            @"_busy\s*=\s*true;\s*PublishLoggingFailure\(\);\s*try\s*\{\s*await action\(\);"), runner);
        // The finally clears busy first, and its re-render is guarded too.
        Assert.Matches(new Regex(
            @"finally\s*\{\s*_busy\s*=\s*false;[^{}]*try\s*\{[\s\S]*Publish\(\);\s*\}\s*catch\s*\(Exception"),
            runner);

        var publish = Member(Read("App/ApplicationController.cs"),
            "private void PublishLoggingFailure(", "private static string Text(");
        Assert.Matches(new Regex(@"try\s*\{\s*Publish\(\);\s*\}\s*catch\s*\(Exception ex\)"), publish);
    }

    [Fact]
    public void PoppedActivity_IsNotPushedAgainWhileItsExitRuns()
    {
        var activities = Read("App/ApplicationController.Activities.cs");
        Assert.Matches(new Regex(
            @"internal bool IsActivityPopPending\s*=>\s*IsActivityOpen\s*&&\s*_activityPopPending;"),
            activities);

        var update = Member(Read("App/ApplicationScene.Activities.cs"),
            "private void UpdateActivity()", "private void UpdateActivityToast(");
        var push = update.IndexOf("_dashboardStack.Push(\"activity\"", StringComparison.Ordinal);
        var guard = Regex.Match(update,
            @"if\s*\(\s*_activitySlot is null\s*&&\s*_dashboardStack\.NavigationPath\.Count == 0\s*&&\s*!_app\.IsActivityPopPending\s*\)");
        Assert.True(guard.Success && push > guard.Index,
            "The activity page must not be pushed again while its pop is pending.");
    }

    [Fact]
    public void PoppedActivity_DropsTheAlertItPresentedOverThePage_OnceOnly()
    {
        var popped = Member(Read("App/ApplicationController.Activities.cs"),
            "internal void OnActivityPopped()", "private void HandlePoppedActivity(");
        // Duplicate or late pop notices return before the hook, so a re-pushed page keeps its alert.
        Assert.Matches(new Regex(
            @"if\s*\(\s*!IsActivityOpen\s*\|\|\s*_activityPopPending\s*\)\s*return;\s*_activity!\.OnPagePopped\(\);\s*_activityPopPending\s*=\s*true;"),
            popped);

        var screen = Code(Read("Features/Activities/MinimalPairsScreen.cs"));
        Assert.Matches(new Regex(@"public void OnPagePopped\(\)\s*=>\s*_deleteAlert\s*=\s*null;"), screen);
    }

    [Fact]
    public void FailedExit_StillReleasesTheScreen_AndReturnsToTheDashboard()
    {
        var exit = Member(Read("App/ApplicationController.Activities.cs"),
            "private async Task ExitActivityAsync()", "internal void OnActivityPopped()");
        Assert.Matches(new Regex(
            @"await screen\.ExitAsync\(_lifetime\.Token\);\s*\}\s*catch\s*\(Exception ex\)[\s\S]*finally\s*\{\s*ReleaseActivity\(\);\s*\}\s*await ReturnToDashboardAsync\(\);"),
            exit);
    }

    /// <summary>The member's code from <paramref name="start"/> up to <paramref name="end"/>, comments removed.</summary>
    private static string Member(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Missing {start}");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"Missing {end} after {start}");
        return Code(source[from..to]);
    }

    private static string Code(string source) =>
        Regex.Replace(Regex.Replace(source, @"///?[^\n]*", string.Empty), @"/\*[\s\S]*?\*/", string.Empty);

    private static string Read(string file) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS", file)));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
