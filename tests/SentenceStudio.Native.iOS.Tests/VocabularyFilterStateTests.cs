using SentenceStudio.Native;
using SentenceStudio.Native.Features.Vocabulary;
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
    public void SearchBarChips_KeepMenuFiltersOutOfTheTypedText()
    {
        var persisted = string.Empty;
        var filters = new VocabularyFilterState(null, next => persisted = next);

        // The bar's typed words stay exactly as typed (a trailing space included).
        filters.SearchField("감사 ");
        Assert.Equal("감사 ", filters.FieldText);
        Assert.Empty(filters.ChipFilters);
        Assert.Equal("감사 ", persisted);

        // A menu choice becomes a chip; the typed words stay in the field.
        filters.ChangeFilter("status", "unknown", live: true);
        Assert.Equal(new FilterToken("status", "unknown"), Assert.Single(filters.ChipFilters));
        Assert.Equal("감사 ", filters.FieldText);
        Assert.Equal("status:unknown 감사 ", persisted);
        Assert.Equal("unknown", filters.FilterValue("status"));
        Assert.Equal("word-two", Assert.Single(filters.Filter(Words, "added", true)).Id);

        // Typing keeps the chip.
        filters.SearchField("감사합니다");
        Assert.Single(filters.ChipFilters);
        Assert.Equal("status:unknown 감사합니다", persisted);

        // A typed token of the same type gives way to the menu's choice.
        filters.SearchField("tag:polite");
        filters.ChangeFilter("tag", "food", live: true);
        Assert.Equal(string.Empty, filters.FieldText);
        Assert.Equal(2, filters.ChipFilters.Count);
        Assert.Equal("food", filters.FilterValue("tag"));

        // Removing a chip (its submenu's "All") leaves the rest.
        filters.ChangeFilter("status", null, live: true);
        Assert.Equal(new FilterToken("tag", "food"), Assert.Single(filters.ChipFilters));
        Assert.Equal("tag:food", persisted);

        // Clear All drops chips and text.
        filters.ClearAll();
        Assert.Empty(filters.ChipFilters);
        Assert.Equal(string.Empty, filters.FieldText);
        Assert.Equal(string.Empty, persisted);
    }

    [Fact]
    public void SearchBarChips_RestoredAndWholeQuerySearchesShowTheirFiltersAsChips()
    {
        var persisted = "status:learning tag:food 사과";
        var filters = new VocabularyFilterState(persisted, next => persisted = next);
        Assert.Equal(2, filters.ChipFilters.Count);
        Assert.Equal("사과", filters.FieldText);
        Assert.Equal("status:learning tag:food 사과", filters.CommittedQuery);

        // The wide page's field (and Android) set the whole query as typed; the bottom bar
        // still shows its filters as chips beside the rest.
        filters.Search("type:word 사과");
        Assert.Equal(new FilterToken("type", "word"), Assert.Single(filters.ChipFilters));
        Assert.Equal("사과", filters.FieldText);
        Assert.Equal("type:word 사과", persisted);
        Assert.Equal("word", filters.FilterValue("type"));
    }

    [Fact]
    public void SearchBarChips_RemoveOnlyTheTappedChipAndNeverRewriteTypedText()
    {
        var persisted = "tag:food tag:polite 사과";
        var filters = new VocabularyFilterState(persisted, next => persisted = next);
        Assert.Equal(2, filters.ChipFilters.Count);

        // A chip's x removes that chip alone.
        filters.RemoveFilter(new FilterToken("tag", "food"));
        Assert.Equal(new FilterToken("tag", "polite"), Assert.Single(filters.ChipFilters));
        Assert.Equal("tag:polite 사과", persisted);

        // A menu choice cuts only the token it replaces out of the typed text: repeats and
        // spacing stay as typed.
        filters.SearchField("go go tag:x  더 ");
        filters.ChangeFilter("tag", "food", live: true);
        Assert.Equal("go go  더 ", filters.FieldText);
        Assert.Equal(new FilterToken("tag", "food"), Assert.Single(filters.ChipFilters));
    }

    [Fact]
    public void SearchBarChips_FollowTheParsersTokensAndItsFilterLimit()
    {
        // A token the parser reads inside a word becomes a chip and leaves the field; removing
        // the chip removes the filter.
        var persisted = "사과tag:food";
        var filters = new VocabularyFilterState(persisted, next => persisted = next);
        Assert.Equal(new FilterToken("tag", "food"), Assert.Single(filters.ChipFilters));
        Assert.Equal("사과", filters.FieldText);
        filters.RemoveFilter(new FilterToken("tag", "food"));
        Assert.Empty(filters.CommittedFilters);
        Assert.Equal("사과", persisted);

        // No chip past the parser's limit (it would show without filtering).
        var full = string.Join(" ", Enumerable.Range(1, ParsedQuery.MaxFilters)
            .Select(index => $"tag:t{index}"));
        filters = new VocabularyFilterState(full, _ => { });
        Assert.Equal(ParsedQuery.MaxFilters, filters.ChipFilters.Count);
        filters.ChangeFilter("status", "learning", live: true);
        Assert.Equal(ParsedQuery.MaxFilters, filters.ChipFilters.Count);
        Assert.Equal(string.Empty, filters.FilterValue("status"));
    }

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
