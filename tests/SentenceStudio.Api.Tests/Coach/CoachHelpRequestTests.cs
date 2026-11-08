using FluentAssertions;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// "What can you do?" is answered by the server from the enabled tool registry, before the model.
/// </summary>
public sealed class CoachHelpRequestTests
{
    // ── classifier ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("help")]
    [InlineData("Help?")]
    [InlineData("/help")]
    [InlineData("help me")]
    [InlineData("What can you help with?")]
    [InlineData("what can you do")]
    [InlineData("Tell me what you can do")]
    [InlineData("tell me what you can do.")]
    [InlineData("What else can you help me with?")]
    [InlineData("hey Sam, what can you do?")]
    [InlineData("What are you able to do?")]
    [InlineData("How can you help me?")]
    [InlineData("what are your features")]
    [InlineData("What can I ask you?")]
    [InlineData("what can you do in this app?")]
    [InlineData("도움말")]
    [InlineData("도와주세요")]
    [InlineData("무엇을 도와줄 수 있어요?")]
    [InlineData("뭘 할 수 있어?")]
    [InlineData("쌤은 뭘 할 수 있어요?")]
    [InlineData("할 수 있는 게 뭐야?")]
    public void CapabilityQuestionsAreHelpRequests(string text)
        => CoachHelpRequestClassifier.IsHelpRequest(text).Should().BeTrue(text);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("help me with this sentence: 저는 학생이에요")]
    [InlineData("Can you help me understand -았-?")]
    [InlineData("How do I say 'what can you do' in Korean?")]
    [InlineData("주말에 뭘 할 수 있어요?")]
    [InlineData("When did I last practice?")]
    [InlineData("Make Today's Plan 10 minutes")]
    [InlineData("What does 할 수 있어요 mean?")]
    public void LanguageAndOtherRequestsAreNotHelpRequests(string? text)
        => CoachHelpRequestClassifier.IsHelpRequest(text).Should().BeFalse(text);

    [Fact]
    public void TheStarterPromptTextIsAHelpRequestInBothLanguages()
    {
        // The "What can you help with?" starter sends this exact text; it must take the help route.
        CoachHelpRequestClassifier.IsHelpRequest("What can you help with?").Should().BeTrue();
        CoachHelpRequestClassifier.IsHelpRequest("무엇을 도와줄 수 있어요?").Should().BeTrue();
    }

    // ── catalog ─────────────────────────────────────────────────────────────

    private static ICoachToolRegistry Registry(bool read, bool write) => new CoachToolRegistry(new CoachOptions
    {
        SamOverlay = new CoachFeatureSwitch { Enabled = read || write },
        SamReadTools = new CoachFeatureSwitch { Enabled = read || write },
        SamWriteTools = new CoachFeatureSwitch { Enabled = write }
    });

    private static CoachHelpCatalog.HelpContext Context(ICoachToolRegistry registry, string lang = "en",
        bool memory = true, bool review = true) =>
        new(registry.EnabledNames, memory, review, lang);

    [Fact]
    public void EveryEnabledToolBelongsToAListedGroup()
    {
        // A tool added to the registry must be reflected in the help list, or the answer silently
        // under-reports what Sam can do.
        var source = File.ReadAllText(FindSource("src/SentenceStudio.Api/Coach/Application/CoachHelpCatalog.cs"));
        foreach (var name in typeof(CoachToolNames).GetFields()
                     .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                     .Where(f => CoachToolNames.AllRegistered.Contains((string)f.GetRawConstantValue()!)))
        {
            source.Should().Contain("CoachToolNames." + name.Name,
                $"the help list must account for the registered tool {name.GetRawConstantValue()}");
        }
    }

    [Fact]
    public void WithEveryToolEnabledTheAnswerListsAllSevenGroupsIncludingFeedback()
    {
        var answer = CoachHelpCatalog.Compose(Context(Registry(read: true, write: true)), "ko");

        answer.Blocks.Should().HaveCount(8);
        answer.Blocks[0].Kind.Should().Be(CoachAnswerBlockKind.Answer);
        answer.Blocks.Skip(1).Should().OnlyContain(b => b.Kind == CoachAnswerBlockKind.Note && b.Label != null);
        answer.Blocks.Count.Should().BeLessThanOrEqualTo(CoachAnswerLimits.MaxBlocks);
        answer.Blocks.Select(b => b.Label).Should().Contain(["Your vocabulary", "Learning resources",
            "Progress and skills", "Today's Plan", "Settings and memory", "Feedback"]);
        answer.PlainText.Should().Contain("YouTube").And.Contain("once you approve");
    }

    [Fact]
    public void TodaysPlanIsOneGroupAmongTheOthersNotTheLead()
    {
        var labels = CoachHelpCatalog.Items(Context(Registry(read: true, write: true))).Select(i => i.Title).ToList();

        labels[0].Should().Be("Language questions");
        labels.IndexOf("Today's Plan").Should().BeGreaterThan(labels.IndexOf("Your vocabulary"));
    }

    [Fact]
    public void WithoutWriteToolsNothingPromisesAChange()
    {
        var items = CoachHelpCatalog.Items(Context(Registry(read: true, write: false), memory: false));

        string.Join(" ", items.Select(i => i.Body)).Should().NotContain("approve")
            .And.NotContain("YouTube");
    }

    [Fact]
    public void CoreOnlyListsOnlyWhatTheCoreToolsCanDo()
    {
        var titles = CoachHelpCatalog.Items(Context(Registry(read: false, write: false), memory: false))
            .Select(i => i.Title);

        titles.Should().NotContain(["Settings and memory", "Feedback"]);
        titles.Should().Contain(["Language questions", "Progress and skills", "Today's Plan"]);
    }

    [Fact]
    public void KoreanDisplayLanguageGetsKoreanCopy()
    {
        var answer = CoachHelpCatalog.Compose(Context(Registry(read: true, write: true), lang: "ko-KR"), "ko");

        answer.Blocks[0].Spans[0].Text.Should().Contain("도와드릴");
        answer.Blocks.Skip(1).Select(b => b.Label).Should().Contain("오늘의 계획");
        answer.PlainText.Should().NotContain("Today's Plan");
        answer.Blocks.SelectMany(b => b.Spans).Should().OnlyContain(s => s.LanguageTag == "ko-KR");
        // Last joined clause ends the sentence, never a dangling connective.
        answer.Blocks.Last().Spans[0].Text.Should().EndWith("요.");
    }

    [Fact]
    public void KoreanSettingsSentenceEndsCleanlyWithoutMemory()
    {
        var items = CoachHelpCatalog.Items(Context(Registry(read: true, write: true), lang: "ko", memory: false));

        items.Last().Body.Should().EndWith("드려요.");
    }

    [Fact]
    public void PrintsTheFullHelpAnswerForReview()
    {
        var path = Environment.GetEnvironmentVariable("SS_HELP_DUMP");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var (label, read, write) in new[] { ("all tools", true, true), ("read only", true, false), ("core only", false, false) })
        {
            foreach (var lang in new[] { "en", "ko" })
            {
                var answer = CoachHelpCatalog.Compose(Context(Registry(read, write), lang), "ko");
                sb.AppendLine($"=== {label} / {lang} ===");
                foreach (var block in answer.Blocks)
                {
                    sb.AppendLine(block.Label is null ? block.Spans[0].Text : $"[{block.Label}] {block.Spans[0].Text}");
                }
                sb.AppendLine();
            }
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static string FindSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull($"the repository root holding {relative} must be an ancestor of the test output");
        return Path.Combine(dir!.FullName, relative);
    }
}
