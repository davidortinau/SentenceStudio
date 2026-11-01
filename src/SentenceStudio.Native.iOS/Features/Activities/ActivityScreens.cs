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

    /// <summary>
    /// Optional: update the mounted tree in place instead of a full re-render
    /// (keeps a focused text field and its IME composition). False re-renders.
    /// </summary>
    bool TryUpdateInPlace(SourceUiStyle style) => false;
    /// True while the activity shows a second-level page (for example a Diary
    /// entry) that the scene pushes on the same NavigationStack above the
    /// activity page; system Back pops it to the activity page.
    /// </summary>
    bool HasChildPage => false;

    string ChildTitle => string.Empty;

    /// <summary>
    /// The scene re-renders the mounted child page only when this changes (or
    /// the appearance changes), so a screen can update a focused text editor's
    /// surroundings in place without interrupting IME composition.
    /// </summary>
    long ChildVersion => 0;

    SwiftView RenderChild(SourceUiStyle style) => Microsoft.SwiftUI.SwiftUI.VStack();

    /// <summary>The NavigationStack popped the child page (system Back or swipe).</summary>
    void OnChildPopped() { }

    /// <summary>
    /// Optional SwiftUI presentation (for example a confirmation alert) wrapped
    /// once around the activity page when the scene pushes it; the screen keeps
    /// the node and updates IsPresented from its renders. Not used with HasSheet.
    /// </summary>
    SwiftView PresentOver(SwiftView page) => page;
}

internal static class ActivityScreens
{
    /// <summary>One line per implemented activity route (NativeActivityCatalog).</summary>
    internal static IActivityScreen? Create(string route, ActivityScreenHost host) => route switch
    {
        NativeActivityCatalog.VocabularyMatching => new VocabularyMatchingScreen(host),
        NativeActivityCatalog.Cloze => new ClozeScreen(host),
        NativeActivityCatalog.Translation => new TranslationScreen(host),
        NativeActivityCatalog.NumberDrill => new NumberDrillScreen(host),
        NativeActivityCatalog.Writing => new WritingScreen(host),
        NativeActivityCatalog.Reading => new ReadingScreen(host),
        NativeActivityCatalog.Shadowing => new ShadowingScreen(host),
        NativeActivityCatalog.HowDoYouSay => new HowDoYouSayScreen(host),
        NativeActivityCatalog.WordAssociation => new WordAssociationScreen(host),
        NativeActivityCatalog.Conversation => new ConversationScreen(host),
        NativeActivityCatalog.Diary => new DiaryScreen(host),
        NativeActivityCatalog.MinimalPairs => new MinimalPairsScreen(host),
        _ => null
    };
}
