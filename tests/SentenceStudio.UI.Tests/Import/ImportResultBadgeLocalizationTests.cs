using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.UI.Tests.Activities;
using SentenceStudio.WebUI.Services;
using ImportContentPage = SentenceStudio.WebUI.Pages.ImportContent;

namespace SentenceStudio.UI.Tests.Import;

/// <summary>
/// Issue 340: each row of an import's results carries a type badge and a status badge. Their
/// labels were English literals, so a Korean interface showed "Failed" on a row under a count
/// card that said 실패. The page is rendered the way the app reaches it: a finished import is
/// held in <see cref="IImportResultStore"/> and opened through <c>?completed=&lt;key&gt;</c>.
/// </summary>
public sealed class ImportResultBadgeLocalizationTests
{
    private static readonly string[] EnglishLabels =
        ["Word", "Phrase", "Sentence", "Unknown", "Created", "Updated", "Skipped", "Failed"];

    [Theory]
    [InlineData(LexicalUnitType.Word, "단어")]
    [InlineData(LexicalUnitType.Phrase, "구문")]
    [InlineData(LexicalUnitType.Sentence, "문장")]
    [InlineData(LexicalUnitType.Unknown, "모름")]
    public async Task InKorean_ARowsTypeBadge_IsInKorean(LexicalUnitType type, string korean)
    {
        var text = await RenderResultsAsync("ko", Row(type, ImportItemStatus.Created));

        text.Should().Contain(korean);
        ShouldHaveNoEnglishLabel(text);
    }

    [Theory]
    [InlineData(ImportItemStatus.Created, "생성됨")]
    [InlineData(ImportItemStatus.Updated, "업데이트됨")]
    [InlineData(ImportItemStatus.Skipped, "건너뜀")]
    [InlineData(ImportItemStatus.Failed, "실패")]
    public async Task InKorean_ARowsStatusBadge_SaysWhatTheCountCardSays(ImportItemStatus status, string korean)
    {
        // Every count is zero, so no filter button for a status is drawn: the word is on the
        // count card and, once the row's badge is localized, on the row.
        var text = await RenderResultsAsync("ko", Row(LexicalUnitType.Word, status));

        Regex.Matches(text, Regex.Escape(korean)).Should().HaveCount(2,
            "the count card and the row's badge both say {0}", korean);
        ShouldHaveNoEnglishLabel(text);
    }

    [Fact]
    public async Task InKorean_AStatusThePageDoesNotKnow_IsUnknownInKorean()
    {
        var text = await RenderResultsAsync("ko", Row(LexicalUnitType.Word, (ImportItemStatus)99));

        text.Should().Contain("모름");
        ShouldHaveNoEnglishLabel(text);
    }

    [Fact]
    public async Task InEnglish_TheBadgesKeepTheirWords()
    {
        var text = await RenderResultsAsync("en",
            Row(LexicalUnitType.Word, ImportItemStatus.Created),
            Row(LexicalUnitType.Phrase, ImportItemStatus.Updated),
            Row(LexicalUnitType.Sentence, ImportItemStatus.Skipped),
            Row(LexicalUnitType.Unknown, ImportItemStatus.Failed));

        foreach (var label in EnglishLabels)
            text.Should().MatchRegex($@"\b{label}\b");
        // Count card and row: two each, as in Korean.
        Regex.Matches(text, @"\bFailed\b").Should().HaveCount(2);
    }

    /// <summary>
    /// The video import page's status badge had the same literal for a status it does not know.
    /// No status reaches that arm today, so the page cannot be made to draw it: the source is read.
    /// </summary>
    [Fact]
    public void TheVideoImportPagesStatusBadge_HasNoEnglishLiteralForAnUnknownStatus()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "SentenceStudio.UI", "Pages", "MediaImport.razor"));
        var start = source.IndexOf("RenderFragment RenderStatusBadge(", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the status badge helper is what this test reads");
        var helper = source[start..source.IndexOf("};", start, StringComparison.Ordinal)];

        helper.Should().Contain("Localize[\"Vocabulary_Unknown\"]");
        helper.Should().NotMatchRegex("\"bi-[a-z-]+\",\\s*\"", "every arm's label is read from the resources");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static void ShouldHaveNoEnglishLabel(string text)
    {
        foreach (var label in EnglishLabels)
            text.Should().NotMatchRegex($@"\b{label}\b", "a Korean interface shows no English badge label");
    }

    private static ContentImportItemResult Row(LexicalUnitType type, ImportItemStatus status) => new()
    {
        VocabularyWordId = status == ImportItemStatus.Failed ? null : "word-1",
        Lemma = "사과",
        NativeLanguageTerm = "apple",
        Type = type,
        Status = status
    };

    private static async Task<string> RenderResultsAsync(string culture, params ContentImportItemResult[] rows)
    {
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        var previousUiCulture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            var store = new SingleResultStore(new ContentImportResult { ResourceId = "resource-1", Items = rows });
            await using var fixture = await ActivityPageFixture.CreateAsync(services =>
            {
                services.AddSingleton<IContentImportService>(ThrowingStub<IContentImportService>.Create());
                services.AddSingleton<IImportResultStore>(store);
                services.AddSingleton<NavigationManager>(new CompletedImportNavigation(store.Key));
                services.AddSupplyValueFromQueryProvider();
            });
            fixture.Services.GetRequiredService<BlazorLocalizationService>().SetCulture(culture);

            var (id, _) = await fixture.Renderer.RenderAsync<ImportContentPage>();

            fixture.Renderer.Unhandled.Should().BeEmpty();
            var text = fixture.Renderer.RenderedText(id);
            text.Should().Contain("사과", "the results list is what was rendered");
            return text;
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
            System.Globalization.CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private sealed class SingleResultStore(ContentImportResult result) : IImportResultStore
    {
        public Guid Key { get; } = Guid.NewGuid();

        public Guid Save(ContentImportResult saved) => throw new NotSupportedException();

        public ContentImportResult? TryGet(Guid key) => key == Key ? result : null;
    }

    private sealed class CompletedImportNavigation : NavigationManager
    {
        public CompletedImportNavigation(Guid key) =>
            Initialize("http://localhost/", $"http://localhost/import-content?completed={key}");

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }
}
