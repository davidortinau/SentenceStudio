using System.Xml.Linq;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// B34 leftovers and B50 (Vocabulary Quiz): hard-coded English on the shared Razor pages now uses
/// resx keys with natural Korean, and the Reading jump hint no longer tells a mouse user to double-tap.
/// </summary>
public sealed class BackportLocalizationTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    public static TheoryData<string, string, string> PageKeys => new()
    {
        { "MinimalPairCreate.razor", "MinimalPair_ContrastLabelPlaceholder", "e.g., ㅂ vs ㅃ" },
        { "HowDoYouSay.razor", "HowDoYouSay_AudioUnavailable", "\"Audio unavailable.\"" },
        { "Reading.razor", "Reading_JumpHintWeb", "Localize[\"Reading_JumpHint\"]" },
        { "Scene.razor", "Scene_GalleryEmpty", "No cached images yet." },
        // B50: Vocabulary Quiz hard-coded English and raw exception text.
        { "VocabQuiz.razor", "ErrorLoadingVocabulary", "Error loading vocabulary: {ex.Message}" },
        { "VocabQuiz.razor", "VocabQuiz_AllVocabularyMastered", "\"All vocabulary in this resource is mastered!\"" },
        { "VocabQuiz.razor", "VocabQuiz_WriteSentenceFirst", "\"Write at least one sentence first.\"" },
        { "VocabQuiz.razor", "VocabQuiz_SentenceGradeFailedNoChange", "No progress was changed. Please try again.\"" },
        { "VocabQuiz.razor", "VocabQuiz_MarkedCorrect", "Marked correct" + " \u2713" },
        { "VocabQuiz.razor", "NativeVocabQuiz_Details_TotalAttempts", ">TotalAttempts<" },
        { "VocabQuiz.razor", "NativeVocabQuiz_Details_WhyMode", "Why This Mode" },
    };

    [Theory]
    [MemberData(nameof(PageKeys))]
    public void ThePageUsesTheKey_NotTheLiteral(string page, string key, string literal)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src", "SentenceStudio.UI", "Pages", page));

        source.Should().Contain($"Localize[\"{key}\"]");
        source.Should().NotContain(literal);
    }

    [Theory]
    [MemberData(nameof(PageKeys))]
    public void TheKeyHasEnglishAndKoreanText(string page, string key, string literal)
    {
        _ = page;
        _ = literal;
        var english = Load("AppResources.resx");
        var korean = Load("AppResources.ko.resx");

        english.Should().ContainKey(key).WhoseValue.Should().NotBeNullOrWhiteSpace();
        korean.Should().ContainKey(key).WhoseValue.Should().NotBe(english[key]);
        korean[key].Should().MatchRegex("[가-힣]", "the Korean text is written in Korean");
    }

    [Fact]
    public void ReadingJumpHint_ForThePage_NamesDoubleClick()
    {
        Load("AppResources.resx")["Reading_JumpHintWeb"].Should().Contain("Double-click");
        Load("AppResources.ko.resx")["Reading_JumpHintWeb"].Should().Contain("더블클릭");
    }

    [Fact]
    public void NewKeys_ResolveThroughTheLocalizationService()
    {
        var localize = new BlazorLocalizationService();
        localize.SetCulture("en");
        localize["MinimalPair_ContrastLabelPlaceholder"].Should().Be("e.g., ㅂ vs ㅃ");
        localize.SetCulture("ko");
        localize["HowDoYouSay_AudioUnavailable"].Should().Be("오디오를 사용할 수 없습니다.");
    }

    private static Dictionary<string, string> Load(string fileName)
    {
        var path = Path.Combine(RepoRoot, "src", "SentenceStudio.Shared", "Resources", "Strings", fileName);
        return XDocument.Load(path).Root!
            .Elements("data")
            .Where(d => d.Attribute("name") is not null)
            .GroupBy(d => d.Attribute("name")!.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
