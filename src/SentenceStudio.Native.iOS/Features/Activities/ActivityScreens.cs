using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// What an activity screen needs from the application controller. The
/// controller owns navigation: Exit stops the activity and pops back to the
/// Dashboard; Publish re-renders; Run executes a serialized busy action.
/// </summary>
internal sealed record ActivityScreenHost(
    IServiceProvider Services,
    Func<string, string> Text,
    Action Publish,
    Action<Func<Task>> Run,
    Action Exit,
    ILogger? Logger,
    CancellationToken Lifetime);

/// <summary>A registered native activity rendered in SwiftUI and pushed on the Dashboard NavigationStack.</summary>
internal interface IActivityScreen : IDisposable
{
    string TitleKey { get; }

    /// <summary>True once the presenter has stopped its timer and handed control back to the Dashboard.</summary>
    bool HasExited { get; }

    Task OpenAsync(NativeActivityLaunch launch, CancellationToken cancellationToken);

    /// <summary>Back / Go Back / native pop: stop and save the timer, then return.</summary>
    Task ExitAsync(CancellationToken cancellationToken);

    SwiftView Render(SourceUiStyle style);

    /// <summary>True for a screen with a SwiftUI sheet (for example Reading's word lookup); the scene keeps one stable sheet node for it.</summary>
    bool HasSheet => false;

    bool IsSheetPresented => false;

    double SheetDetentFraction => 0.45;

    SwiftView RenderSheet(SourceUiStyle style) => Microsoft.SwiftUI.SwiftUI.VStack();

    /// <summary>The person swiped the sheet away.</summary>
    void OnSheetDismissed() { }
}

internal static class ActivityScreens
{
    /// <summary>One line per implemented activity route (NativeActivityCatalog).</summary>
    internal static IActivityScreen? Create(string route, ActivityScreenHost host) => route switch
    {
        NativeActivityCatalog.VocabularyMatching => new VocabularyMatchingScreen(host),
        NativeActivityCatalog.Reading => new ReadingScreen(host),
        _ => null
    };
}
