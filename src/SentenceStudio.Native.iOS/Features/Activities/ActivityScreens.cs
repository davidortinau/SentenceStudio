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

    /// <summary>Optional text under the title (for example a list's count); empty shows none.</summary>
    string Subtitle => string.Empty;

    /// <summary>
    /// Set when <see cref="Render"/> draws a browse page (a native List under the system search
    /// field): a host re-renders it only when <see cref="PageVersion"/> changes and updates it in
    /// place, so the search field keeps its focus.
    /// </summary>
    SentenceStudio.Native.iOS.Features.Browse.BrowserPageUpdate? BrowsePage => null;

    /// <summary>The browse page's search text, which an in-place update gives the mounted field.</summary>
    string SearchText => string.Empty;

    /// <summary>True once the presenter has stopped its timer and handed control back to the Dashboard.</summary>
    bool HasExited { get; }

    Task OpenAsync(NativeActivityLaunch launch, CancellationToken cancellationToken);

    /// <summary>Back / Go Back / native pop: stop and save the timer, then return.</summary>
    Task ExitAsync(CancellationToken cancellationToken);

    SwiftView Render(SourceUiStyle style);

    /// <summary>
    /// The newest success or info message in the screen's state. The scene shows
    /// it once as a transient toast over the page (NativeToastScheduler); errors
    /// and warnings stay inline as banners in <see cref="Render"/>.
    /// </summary>
    NativeMessage? Toast => null;

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

    /// <summary>
    /// Changes when the activity page itself shows something new; a host that keeps the
    /// page mounted re-renders it only then. Defaults to <see cref="ChildVersion"/>.
    /// </summary>
    long PageVersion => ChildVersion;

    SwiftView RenderChild(SourceUiStyle style) => Microsoft.SwiftUI.SwiftUI.VStack();

    /// <summary>The NavigationStack popped the child page (system Back or swipe).</summary>
    void OnChildPopped() { }

    /// <summary>
    /// Optional SwiftUI presentation (for example a confirmation alert) wrapped
    /// once around the activity page when the scene pushes it; the screen keeps
    /// the node and updates IsPresented from its renders. Not used with HasSheet.
    /// </summary>
    SwiftView PresentOver(SwiftView page) => page;

    /// <summary>
    /// The NavigationStack popped the activity page, which disposes the views pushed
    /// with it: drop any node kept from <see cref="PresentOver"/> before the exit renders.
    /// </summary>
    void OnPagePopped() { }
}

/// <summary>
/// The host's fallback when an activity's OpenAsync throws outside the screen (the screen
/// never leaves its loading state): the unexpected-error text and Go Back, for every activity.
/// </summary>
internal static class ActivityOpenFailureView
{
    internal static SwiftView Render(SourceUiStyle style, Func<string, string> text, Action goBack)
    {
        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var content = Microsoft.SwiftUI.SwiftUI.VStack(
                style.Text(Microsoft.SwiftUI.SwiftUI.Text(text(NativeText.UnexpectedError)),
                        SourceTextRole.Body1, SourceTextTone.Danger)
                    .WithAccessibilityIdentifier("native.activity.open-failed.message"),
                style.SecondaryButton(text("Common_GoBack"), goBack)
                    .WithAccessibilityIdentifier("native.activity.open-failed.go-back"))
            .WithPadding(16, gutter, 16, gutter)
            .WithFlexibleWidth();
        content.Spacing = SourceUiLayout.PageGap;
        return style.Page(Microsoft.SwiftUI.SwiftUI.Scroll(content)
            .WithAccessibilityIdentifier("native.activity.open-failed"));
    }
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
        NativeActivityCatalog.VideoWatching => new VideoWatchingScreen(host),
        NativeActivityCatalog.FlashcardPreview => new FlashcardScreen(host),
        NativeActivityCatalog.Scene => new SceneScreen(host),
        NativeActivityCatalog.MinimalPairs => new MinimalPairsScreen(host),
        _ => null
    };
}
