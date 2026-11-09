using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// The "what can you do?" answer, built from the tools this deployment has enabled.
/// </summary>
/// <remarks>
/// <para>
/// Each group of actions names the registered tools behind it. A group is listed only when at
/// least one of its tools is enabled, and its approval wording appears only when one of its
/// <c>propose_</c> tools is enabled, so the answer cannot promise a change Sam is not allowed to
/// propose. Language teaching has no tool: it is what Sam always does.
/// </para>
/// <para>
/// English and Korean copy lives here, like <see cref="CoachDeterministicCopy"/>, because the
/// server composes the answer before any client localizes anything.
/// </para>
/// </remarks>
public static class CoachHelpCatalog
{
    /// <summary>The facts about this deployment and turn that decide what is listed.</summary>
    public sealed record HelpContext(
        IReadOnlyCollection<string> EnabledTools,
        bool MemoryEnabled,
        bool VocabularyReviewAvailable,
        string DisplayLanguageTag);

    /// <summary>One listed group: a short title and one sentence about it.</summary>
    public sealed record HelpItem(string Title, string Body);

    private sealed record Copy(string En, string Ko)
    {
        public string For(bool ko) => ko ? Ko : En;
    }

    private static readonly Copy Intro = new(
        "Here is what I can help with. Ask in your own words.",
        "제가 도와드릴 수 있는 일이에요. 편하게 말해 주세요.");

    /// <summary>Builds the ordered help items for <paramref name="context"/>.</summary>
    public static IReadOnlyList<HelpItem> Items(HelpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var ko = IsKorean(context.DisplayLanguageTag);
        var enabled = context.EnabledTools as IReadOnlySet<string>
            ?? new HashSet<string>(context.EnabledTools, StringComparer.Ordinal);
        bool Any(params string[] names) => names.Any(enabled.Contains);

        var items = new List<HelpItem>(7);

        // 1. Teaching. Always available: it is the coach itself, not a tool.
        items.Add(new HelpItem(
            new Copy("Language questions", "언어 질문").For(ko),
            context.VocabularyReviewAvailable
                ? new Copy(
                    "Explain grammar, words and usage, check sentences you write, and make a 10-word review on any topic.",
                    "문법, 단어, 표현을 설명하고, 직접 쓴 문장을 고쳐 드리고, 원하는 주제로 단어 10개 복습을 만들어 드려요.").For(ko)
                : new Copy(
                    "Explain grammar, words and usage, and check sentences you write.",
                    "문법, 단어, 표현을 설명하고, 직접 쓴 문장을 고쳐 드려요.").For(ko)));

        // 2. Saved vocabulary.
        // Example sentences belong to words, so they are listed with them rather than as a
        // group of their own (the answer is held to eight parts).
        if (Any(CoachToolNames.ListUserVocabularies, CoachToolNames.GetVocabularyWordDetail,
                CoachToolNames.GetVocabularyDueSummary, CoachToolNames.ProposeVocabularyEntry,
                CoachToolNames.ProposeVocabularyEdit, CoachToolNames.ProposeVocabularyLink,
                CoachToolNames.ProposeVocabularyRemoval, CoachToolNames.GetVocabularyExampleSentences,
                CoachToolNames.ProposeExampleSentenceEntry, CoachToolNames.ProposeExampleSentenceEdit,
                CoachToolNames.ProposeExampleSentenceRemoval))
        {
            var canWrite = Any(CoachToolNames.ProposeVocabularyEntry, CoachToolNames.ProposeVocabularyEdit,
                CoachToolNames.ProposeVocabularyLink, CoachToolNames.ProposeVocabularyRemoval);
            var sentencesRead = Any(CoachToolNames.GetVocabularyExampleSentences);
            var sentencesWrite = Any(CoachToolNames.ProposeExampleSentenceEntry,
                CoachToolNames.ProposeExampleSentenceEdit, CoachToolNames.ProposeExampleSentenceRemoval);
            // Never promise more than is enabled: the full wording needs both write families.
            var body = (canWrite && sentencesWrite, canWrite, sentencesRead) switch
            {
                (true, _, _) => new Copy(
                    "Count and find your saved words and their example sentences, and add, edit or remove words and example sentences once you approve.",
                    "저장한 단어와 예문을 세고 찾아 드리고, 승인하시면 단어와 예문을 추가, 수정, 삭제해 드려요."),
                (false, true, _) => new Copy(
                    "Count and find your saved words and what is due for review, and add, edit or remove words once you approve.",
                    "저장한 단어와 복습할 단어를 세고 찾아 드리고, 승인하시면 단어를 추가, 수정, 삭제해 드려요."),
                (false, false, true) => new Copy(
                    "Count and find your saved words and show their example sentences.",
                    "저장한 단어를 세고 찾아 드리고 예문을 보여 드려요."),
                _ => new Copy(
                    "Find your saved words and tell you how many are due for review.",
                    "저장한 단어를 찾아 드리고 복습할 단어가 몇 개인지 알려 드려요.")
            };
            items.Add(new HelpItem(new Copy("Your vocabulary", "내 어휘").For(ko), body.For(ko)));
        }

        // 3. Learning resources, including YouTube import.
        if (Any(CoachToolNames.GetResourceCatalog, CoachToolNames.GetLearningResourceList,
                CoachToolNames.GetLearningResourceDetail, CoachToolNames.ProposeResourceEntry,
                CoachToolNames.ProposeResourceEdit, CoachToolNames.ProposeResourceRemoval,
                CoachToolNames.ProposeYouTubeImport))
        {
            var canEdit = Any(CoachToolNames.ProposeResourceEntry, CoachToolNames.ProposeResourceEdit,
                CoachToolNames.ProposeResourceRemoval);
            var canImport = Any(CoachToolNames.ProposeYouTubeImport);
            var body = (canEdit, canImport) switch
            {
                (true, true) => new Copy(
                    "Find your resources, add, edit or remove them, and import a YouTube video, once you approve.",
                    "학습 자료를 찾아 드리고, 승인하시면 자료를 추가, 수정, 삭제하거나 YouTube 영상을 가져와 드려요."),
                (true, false) => new Copy(
                    "Find your resources, and add, edit or remove them once you approve.",
                    "학습 자료를 찾아 드리고, 승인하시면 자료를 추가, 수정, 삭제해 드려요."),
                (false, true) => new Copy(
                    "Find your resources, and import a YouTube video once you approve.",
                    "학습 자료를 찾아 드리고, 승인하시면 YouTube 영상을 가져와 드려요."),
                _ => new Copy(
                    "Find your learning resources and tell you what is in them.",
                    "학습 자료를 찾아 드리고 어떤 내용인지 알려 드려요.")
            };
            items.Add(new HelpItem(new Copy("Learning resources", "학습 자료").For(ko), body.For(ko)));
        }

        // 4. Skills and practice history.
        var skillsRead = Any(CoachToolNames.GetSkillList, CoachToolNames.GetSkillDetail);
        var skillsWrite = Any(CoachToolNames.ProposeSkillEntry, CoachToolNames.ProposeSkillEdit,
            CoachToolNames.ProposeSkillArchive);
        var progress = Any(CoachToolNames.GetPracticeHistorySummary, CoachToolNames.GetPracticeBalance,
            CoachToolNames.GetLearnerProfileSummary);
        if (skillsRead || skillsWrite || progress)
        {
            var parts = new List<Clause>(3);
            if (progress)
            {
                parts.Add(new Clause("tell you when you last practiced and how your recent practice time was spent",
                    "마지막으로 공부한 날과 최근 공부 시간을 알려 드리고", "마지막으로 공부한 날과 최근 공부 시간을 알려 드려요"));
            }

            if (skillsRead || skillsWrite)
            {
                parts.Add(new Clause("show the skills you are working on", "연습 중인 기술을 보여 드리고", "연습 중인 기술을 보여 드려요"));
            }

            if (skillsWrite)
            {
                parts.Add(new Clause("add, edit or archive a skill once you approve",
                    "승인하시면 기술을 추가, 수정, 보관해 드리고", "승인하시면 기술을 추가, 수정, 보관해 드려요"));
            }

            items.Add(new HelpItem(new Copy("Progress and skills", "학습 기록과 기술").For(ko), Join(parts, ko)));
        }

        // 5. Today's Plan: one capability among the others, listed after them.
        if (Any(CoachToolNames.PreviewPracticePlan, CoachToolNames.GetCurrentPlanSummary))
        {
            items.Add(new HelpItem(
                new Copy("Today's Plan", "오늘의 계획").For(ko),
                new Copy(
                    "Show Today's Plan and change its time, activities, energy or word focus when you ask.",
                    "오늘의 계획을 보여 드리고, 요청하시면 시간, 활동, 에너지, 단어 초점을 바꿔 드려요.").For(ko)));
        }

        // 6. Profile, settings and memory.
        var settingsRead = Any(CoachToolNames.GetCurrentProfileSummary, CoachToolNames.GetLearnerSettingsSummary);
        var settingsWrite = Any(CoachToolNames.ProposePreferenceChange);
        if (settingsRead || settingsWrite || context.MemoryEnabled)
        {
            var parts = new List<Clause>(3);
            if (settingsRead)
            {
                parts.Add(new Clause("show your profile and settings", "프로필과 설정을 보여 드리고", "프로필과 설정을 보여 드려요"));
            }

            if (settingsWrite)
            {
                parts.Add(new Clause("change a preference once you approve", "승인하시면 설정을 바꿔 드리고", "승인하시면 설정을 바꿔 드려요"));
            }

            if (context.MemoryEnabled)
            {
                parts.Add(new Clause("remember something when you ask me to", "기억해 달라고 하신 것을 기억해 드리고", "기억해 달라고 하신 것을 기억해 드려요"));
            }

            items.Add(new HelpItem(
                new Copy("Settings and memory", "설정과 기억").For(ko),
                Join(parts, ko)));
        }

        // 7. Feedback: bug reports and requests filed on GitHub with the current screen.
        if (Any(CoachToolNames.ProposeFeedback))
        {
            items.Add(new HelpItem(
                new Copy("Feedback", "피드백").For(ko),
                new Copy(
                    "Log a bug or a request about the app, with the screen you are on, as a GitHub issue once you approve.",
                    "앱의 버그나 요청을 지금 화면 정보와 함께 기록하고, 승인하시면 GitHub 이슈로 올려 드려요.").For(ko)));
        }

        return items;
    }

    /// <summary>The full structured answer: one intro line, then one labelled part per group.</summary>
    public static CoachAnswerDto Compose(HelpContext context, string targetLanguageTag)
    {
        ArgumentNullException.ThrowIfNull(context);

        var ko = IsKorean(context.DisplayLanguageTag);
        // Plain text and blocks list the same groups, so a copy never says more than the screen.
        var items = Items(context).Take(CoachAnswerLimits.MaxBlocks - 1).ToList();
        var intro = Intro.For(ko);

        var blocks = new List<CoachAnswerBlockDto>(items.Count + 1)
        {
            Block(CoachAnswerBlockKind.Answer, label: null, intro, context.DisplayLanguageTag)
        };
        foreach (var item in items)
        {
            blocks.Add(Block(CoachAnswerBlockKind.Note, item.Title, item.Body, context.DisplayLanguageTag));
        }

        var plain = intro + "\n" + string.Join("\n", items.Select(i => $"- {i.Title}: {i.Body}"));

        return new CoachAnswerDto
        {
            Topic = CoachAnswerTopic.StudyStrategy,
            Blocks = blocks,
            PlainText = plain,
            TargetLanguageTag = targetLanguageTag,
            DisplayLanguageTag = context.DisplayLanguageTag,
            EndsWithRecallQuestion = false
        };
    }

    private static CoachAnswerBlockDto Block(CoachAnswerBlockKind kind, string? label, string text, string tag) => new()
    {
        Kind = kind,
        Label = label,
        Spans =
        [
            new CoachAnswerSpanDto
            {
                Text = text,
                Language = CoachLanguageRole.Display,
                LanguageTag = tag
            }
        ]
    };

    /// <summary>One clause of a joined sentence. Korean needs a joining form and an ending form.</summary>
    private sealed record Clause(string En, string KoJoined, string KoFinal);

    private static string Join(List<Clause> parts, bool ko)
    {
        if (ko)
        {
            var clauses = parts.Take(parts.Count - 1).Select(p => p.KoJoined).Append(parts[^1].KoFinal);
            return string.Join(", ", clauses) + ".";
        }

        var en = parts.Select(p => p.En).ToList();
        var sentence = en.Count switch
        {
            1 => en[0],
            2 => $"{en[0]} and {en[1]}",
            _ => string.Join(", ", en.Take(en.Count - 1)) + ", and " + en[^1]
        };
        return char.ToUpperInvariant(sentence[0]) + sentence[1..] + ".";
    }

    private static bool IsKorean(string? tag) =>
        tag is not null && tag.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
}
