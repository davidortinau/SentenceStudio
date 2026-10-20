using SentenceStudio.Native;
using SentenceStudio.Native.iOS.Features.Vocabulary;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class VocabularyFilterStateTests
{
    private static readonly NativeVocabularyResource StudyResource =
        new("resource-owned", "Study Notes", "Korean", false);

    private static readonly IReadOnlyList<NativeVocabularyEntry> Words = [
        new("word-one", "사과", "apple", LexicalUnitType.Word,
            "food,common", "사과", null, new DateTime(2026, 10, 1),
            new DateTime(2026, 10, 2), "Good", LearningStatus.Learning,
            false, [StudyResource]),
        new("word-two", "감사합니다", "thank you", LexicalUnitType.Sentence,
            "polite", "감사하다", "Korean", new DateTime(2026, 10, 2),
            new DateTime(2026, 10, 3), "Basic", LearningStatus.Unknown,
            false, [])
    ];

    [Fact]
    public void PhoneDraft_CancelApplyAndClearRetainOnlyCommittedQueryAfterRestoration()
    {
        var persisted = "status:learning";
        var filters = new VocabularyFilterState(persisted, next => persisted = next);
        Assert.Single(filters.Filter(Words, "added", descending: true));

        filters.OpenPhoneSheet();
        filters.ChangeFilter("status", "unknown", live: false);
        Assert.Equal("unknown", filters.FilterValue("status", useDraft: true));
        Assert.Equal("status:learning", filters.CommittedQuery);
        Assert.Equal("status:learning", persisted);
        Assert.Equal("word-one", Assert.Single(filters.Filter(
            Words, "added", descending: true)).Id);
        filters.CancelPhoneSheet();
        Assert.Equal("learning", filters.FilterValue("status"));

        filters.OpenPhoneSheet();
        filters.ChangeFilter("status", "unknown", live: false);
        filters.ApplyPhoneSheet();
        Assert.Equal("status:unknown", persisted);
        Assert.Equal("word-two", Assert.Single(filters.Filter(
            Words, "added", descending: true)).Id);

        filters.OpenPhoneSheet();
        filters.ChangeFilter("type", "word", live: false);
        filters.ClearAll();
        Assert.Equal(string.Empty, persisted);
        Assert.Equal(string.Empty, filters.VisibleQuery);
        filters.ChangeFilter("association", "orphaned", live: false);
        filters.CancelPhoneSheet();
        Assert.Equal(string.Empty, filters.CommittedQuery);
        Assert.Equal(2, filters.Filter(Words, "added", descending: true).Count);
        Assert.Equal(string.Empty, new VocabularyFilterState(persisted, _ => { })
            .CommittedQuery);
    }

    [Fact]
    public void PhoneSheet_LateOwnerChoicesPreserveDraftAndCommittedResultsUntilApply()
    {
        var persisted = "status:learning";
        var filters = new VocabularyFilterState(persisted, next => persisted = next);
        filters.OpenPhoneSheet();
        filters.ChangeFilter("status", "unknown", live: false);

        var loaded = new NativeVocabularyCatalog(true, Words, [StudyResource]);
        Assert.True(filters.PhoneSheetOpen);
        Assert.Equal("unknown", filters.FilterValue("status", useDraft: true));
        Assert.Equal("status:learning", filters.CommittedQuery);
        filters.ChangeFilter("status", "learning", live: false);
        filters.ChangeFilter("resource", loaded.Resources.Single().Title, live: false);
        filters.ChangeFilter("tag", "common", live: false);
        filters.ChangeFilter("lemma", Words[0].Lemma, live: false);
        filters.ChangeFilter("language", "Korean", live: false);
        Assert.Equal("Study Notes", filters.FilterValue("resource", useDraft: true));
        Assert.Equal("common", filters.FilterValue("tag", useDraft: true));
        Assert.Equal("사과", filters.FilterValue("lemma", useDraft: true));
        Assert.Equal("Korean", filters.FilterValue("language", useDraft: true));
        Assert.Equal("status:learning", persisted);
        Assert.Equal("word-one", Assert.Single(filters.Filter(
            loaded.Words, "added", descending: true)).Id);

        filters.CancelPhoneSheet();
        Assert.Equal(string.Empty, filters.FilterValue("resource"));
        Assert.Equal("status:learning", persisted);
        filters.OpenPhoneSheet();
        filters.ChangeFilter("resource", loaded.Resources.Single().Title, live: false);
        filters.ApplyPhoneSheet();
        Assert.Contains("resource:", persisted);
        Assert.Equal("word-one", Assert.Single(filters.Filter(
            loaded.Words, "added", descending: true)).Id);
    }

    [Fact]
    public void WideFiltersRemainLiveAfterDismissalAndSearchMatchesAllSourceFields()
    {
        var committed = string.Empty;
        var filters = new VocabularyFilterState(string.Empty,
            next => committed = next);
        filters.ChangeFilter("resource", "Study Notes", live: true);
        filters.ChangeFilter("language", "Korean", live: true);
        Assert.Equal("Study Notes", filters.FilterValue("resource"));
        Assert.Equal("word-one", Assert.Single(filters.Filter(
            Words, "updated", descending: true)).Id);
        filters.CancelPhoneSheet();
        Assert.Equal(committed, filters.CommittedQuery);

        filters.Search("tag:polite lemma:감사하다 encoding:basic type:sentence 감사합니다");
        Assert.Equal("word-two", Assert.Single(filters.Filter(
            Words, "added", descending: true)).Id);
        filters.Search("association:orphaned status:unknown thank");
        Assert.Equal("word-two", Assert.Single(filters.Filter(
            Words, "added", descending: true)).Id);
        filters.Search("association:associated status:known");
        Assert.Empty(filters.Filter(Words, "added", descending: true));
    }
}
