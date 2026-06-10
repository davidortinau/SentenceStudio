using FluentAssertions;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.UnitTests.PlanGeneration;

public class ActivityFocusVocabularyConsumerTests
{
    [Fact]
    public void VocabQuiz_PlanLaunch_UsesFocusVocabularyIds_NoReroll()
    {
        var words = CreateWords(6);
        var focusIds = new[] { "word-4", "word-2", "word-5" };

        var selected = FocusVocabularySelection.SelectFocusWords(words.OrderBy(_ => Guid.NewGuid()), focusIds);

        selected.Select(w => w.Id).Should().Equal(focusIds,
            "plan-launched quiz should use the ordered focus IDs instead of randomizing the full due pool");
    }

    [Fact]
    public void VocabMatching_PlanLaunch_UsesFocusVocabularyIds_DeterministicSubset()
    {
        var words = CreateWords(12);
        var focusIds = Enumerable.Range(1, 10).Select(i => $"word-{i}").ToList();

        var selected = FocusVocabularySelection.SelectFocusWords(words, focusIds)
            .Take(8)
            .Select(w => w.Id);

        selected.Should().Equal(focusIds.Take(8),
            "matching only has room for eight pairs and should take the first eight focus words deterministically");
    }

    [Fact]
    public void Cloze_PlanLaunch_IncludesFocusVocabularyAsRequiredWords()
    {
        var focusWords = CreateWords(3);
        var contextWords = CreateWords(6).Skip(3).ToList();

        var promptWords = FocusVocabularySelection.BuildRequiredFirstPromptVocabulary(focusWords, contextWords, maxVocabularyWords: 4);

        promptWords.Select(w => w.Id).Should().StartWith(focusWords.Select(w => w.Id),
            "cloze prompts must place required focus words before incidental context words");
        promptWords.Should().HaveCount(4);
    }

    [Fact]
    public void Writing_PlanLaunch_UsesFocusWordsForBlocksAndPrompts()
    {
        var resourceWords = CreateWords(8);
        var focusIds = new[] { "word-6", "word-1", "word-3", "word-8" };

        var writingBlocks = FocusVocabularySelection.SelectFocusWords(resourceWords, focusIds)
            .Take(4)
            .Select(w => w.Id);

        writingBlocks.Should().Equal(focusIds,
            "writing prompt chips should be sourced from the plan focus set when it is present");
    }

    [Fact]
    public void Translation_PlanLaunch_IncludesFocusWordsAndAllowsIncidentalResourceWords()
    {
        var focusWords = CreateWords(2);
        var contextWords = CreateWords(5).Skip(2).ToList();

        var promptWords = FocusVocabularySelection.BuildRequiredFirstPromptVocabulary(focusWords, contextWords, maxVocabularyWords: 5);

        promptWords.Select(w => w.Id).Should().Equal(new[] { "word-1", "word-2", "word-3", "word-4", "word-5" },
            "translation prompts should require focus words first while still allowing incidental resource context");
    }

    [Fact]
    public void Reading_PlanLaunch_ShowsFocusOverlapWhenResourceContainsFocusWords()
    {
        var resourceWords = CreateWords(5);
        var focusIds = new[] { "word-2", "word-9", "word-4" };

        var overlap = FocusVocabularySelection.SelectFocusWords(resourceWords, focusIds)
            .Select(w => w.Id);

        overlap.Should().Equal(new[] { "word-2", "word-4" },
            "reading stays resource-driven but marks the focus words that overlap the resource vocabulary");
    }

    [Fact]
    public void FlashcardPreview_UsesPlanFocusVocabularyIds()
    {
        var focusIds = new[] { "word-1", "word-2", "word-3" };
        var previewWords = focusIds
            .Select(id => new PlanPreviewWord(id, $"target-{id}", $"native-{id}"))
            .ToList();

        previewWords.Select(w => w.WordId).Should().Equal(focusIds,
            "flashcard preview words are a projection of the persisted plan focus IDs");
    }

    private static List<VocabularyWord> CreateWords(int count) => Enumerable.Range(1, count)
        .Select(i => new VocabularyWord
        {
            Id = $"word-{i}",
            TargetLanguageTerm = $"target-{i}",
            NativeLanguageTerm = $"native-{i}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        })
        .ToList();
}
