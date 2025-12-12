using Xunit;
using FluentAssertions;
using SentenceStudio.Shared.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// [T063] Unit tests for SearchQueryParser - GitHub-style search syntax parsing.
/// Tests parsing, filter detection, reconstruction, and filter manipulation.
/// </summary>
public class SearchQueryParserTests
{
    private readonly SearchQueryParser _sut; // System Under Test

    public SearchQueryParserTests()
    {
        _sut = new SearchQueryParser();
    }

    #region Parse Tests

    [Fact]
    public void Parse_EmptyString_ReturnsEmptyParsedQuery()
    {
        // Act
        var result = _sut.Parse("");

        // Assert
        result.Should().NotBeNull();
        result.Filters.Should().BeEmpty();
        result.FreeTextTerms.Should().BeEmpty();
        result.HasContent.Should().BeFalse();
    }

    [Fact]
    public void Parse_NullString_ReturnsEmptyParsedQuery()
    {
        // Act
        var result = _sut.Parse(null!);

        // Assert
        result.Should().NotBeNull();
        result.HasContent.Should().BeFalse();
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsEmptyParsedQuery()
    {
        // Act
        var result = _sut.Parse("   \t\n  ");

        // Assert
        result.HasContent.Should().BeFalse();
    }

    [Fact]
    public void Parse_SingleTagFilter_ExtractsTagCorrectly()
    {
        // Act
        var result = _sut.Parse("tag:nature");

        // Assert
        result.Filters.Should().HaveCount(1);
        result.Filters[0].Type.Should().Be("tag");
        result.Filters[0].Value.Should().Be("nature");
        result.FreeTextTerms.Should().BeEmpty();
    }

    [Fact]
    public void Parse_TagFilterWithKoreanValue_ExtractsCorrectly()
    {
        // Act
        var result = _sut.Parse("tag:자연");

        // Assert
        result.Filters.Should().HaveCount(1);
        result.Filters[0].Value.Should().Be("자연");
    }

    [Fact]
    public void Parse_QuotedFilterValue_ExtractsValueWithSpaces()
    {
        // Act
        var result = _sut.Parse("tag:\"multi word tag\"");

        // Assert
        result.Filters.Should().HaveCount(1);
        result.Filters[0].Value.Should().Be("multi word tag");
    }

    [Fact]
    public void Parse_ResourceFilter_ExtractsCorrectly()
    {
        // Act
        var result = _sut.Parse("resource:general");

        // Assert
        result.Filters.Should().HaveCount(1);
        result.Filters[0].Type.Should().Be("resource");
        result.Filters[0].Value.Should().Be("general");
    }

    [Fact]
    public void Parse_LemmaFilter_ExtractsCorrectly()
    {
        // Act
        var result = _sut.Parse("lemma:가다");

        // Assert
        result.Filters.Should().HaveCount(1);
        result.Filters[0].Type.Should().Be("lemma");
        result.Filters[0].Value.Should().Be("가다");
    }

    [Fact]
    public void Parse_StatusFilter_ExtractsCorrectly()
    {
        // Act
        var result = _sut.Parse("status:learning");

        // Assert
        result.Filters.Should().HaveCount(1);
        result.Filters[0].Type.Should().Be("status");
        result.Filters[0].Value.Should().Be("learning");
    }

    [Fact]
    public void Parse_FreeTextOnly_ExtractsTerms()
    {
        // Act
        var result = _sut.Parse("hello world 단풍");

        // Assert
        result.Filters.Should().BeEmpty();
        result.FreeTextTerms.Should().HaveCount(3);
        result.FreeTextTerms.Should().Contain("hello");
        result.FreeTextTerms.Should().Contain("world");
        result.FreeTextTerms.Should().Contain("단풍");
    }

    [Fact]
    public void Parse_MixedFiltersAndFreeText_ExtractsAllComponents()
    {
        // Act
        var result = _sut.Parse("tag:nature 단풍 resource:general");

        // Assert
        result.Filters.Should().HaveCount(2);
        result.TagFilters.Should().Contain("nature");
        result.ResourceFilters.Should().Contain("general");
        result.FreeTextTerms.Should().HaveCount(1);
        result.FreeTextTerms.Should().Contain("단풍");
    }

    [Fact]
    public void Parse_MultipleTagFilters_ExtractsAll()
    {
        // Act
        var result = _sut.Parse("tag:nature tag:season tag:weather");

        // Assert
        result.Filters.Should().HaveCount(3);
        result.TagFilters.Should().HaveCount(3);
        result.TagFilters.Should().Contain("nature");
        result.TagFilters.Should().Contain("season");
        result.TagFilters.Should().Contain("weather");
    }

    [Fact]
    public void Parse_CaseInsensitiveFilterType_NormalizesToLowercase()
    {
        // Act
        var result = _sut.Parse("TAG:nature RESOURCE:general");

        // Assert
        result.Filters.Should().HaveCount(2);
        result.Filters.All(f => f.Type == f.Type.ToLowerInvariant()).Should().BeTrue();
    }

    [Fact]
    public void Parse_DuplicateFilters_IgnoresDuplicates()
    {
        // Act
        var result = _sut.Parse("tag:nature tag:nature");

        // Assert
        result.Filters.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_ExceedsMaxFilters_TruncatesAtLimit()
    {
        // Arrange - create query with more than MaxFilters (10)
        var query = string.Join(" ", Enumerable.Range(1, 15).Select(i => $"tag:tag{i}"));

        // Act
        var result = _sut.Parse(query);

        // Assert
        result.Filters.Should().HaveCount(ParsedQuery.MaxFilters);
    }

    [Fact]
    public void Parse_InvalidFilterType_IgnoresInvalidFilter()
    {
        // Act
        var result = _sut.Parse("invalid:value tag:nature");

        // Assert - "invalid:value" should be treated as free text
        result.Filters.Should().HaveCount(1);
        result.Filters[0].Type.Should().Be("tag");
    }

    [Fact]
    public void Parse_ComplexQuery_HandlesAllFilterTypes()
    {
        // Act
        var result = _sut.Parse("tag:nature resource:textbook lemma:가다 status:learning 단풍");

        // Assert
        result.Filters.Should().HaveCount(4);
        result.TagFilters.Should().Contain("nature");
        result.ResourceFilters.Should().Contain("textbook");
        result.LemmaFilters.Should().Contain("가다");
        result.StatusFilters.Should().Contain("learning");
        result.FreeTextTerms.Should().Contain("단풍");
    }

    #endregion

    #region IsValidFilterToken Tests

    [Theory]
    [InlineData("tag", "nature", true)]
    [InlineData("resource", "general", true)]
    [InlineData("lemma", "가다", true)]
    [InlineData("status", "learning", true)]
    [InlineData("status", "known", true)]
    [InlineData("status", "unknown", true)]
    [InlineData("status", "invalid", false)]
    [InlineData("invalid", "value", false)]
    [InlineData("tag", "", false)]
    [InlineData("", "value", false)]
    public void IsValidFilterToken_ReturnsExpectedResult(string type, string value, bool expected)
    {
        // Act
        var result = _sut.IsValidFilterToken(type, value);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void IsValidFilterToken_ValueTooLong_ReturnsFalse()
    {
        // Arrange
        var longValue = new string('a', 101);

        // Act
        var result = _sut.IsValidFilterToken("tag", longValue);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region DetectActiveFilter Tests

    [Fact]
    public void DetectActiveFilter_TagPrefixAtEnd_DetectsTagFilter()
    {
        // Arrange
        var text = "tag:nat";
        var cursorPosition = text.Length;

        // Act
        var (filterType, partialValue) = _sut.DetectActiveFilter(text, cursorPosition);

        // Assert
        filterType.Should().Be("tag");
        partialValue.Should().Be("nat");
    }

    [Fact]
    public void DetectActiveFilter_ResourcePrefix_DetectsResourceFilter()
    {
        // Arrange
        var text = "resource:gen";
        var cursorPosition = text.Length;

        // Act
        var (filterType, partialValue) = _sut.DetectActiveFilter(text, cursorPosition);

        // Assert
        filterType.Should().Be("resource");
        partialValue.Should().Be("gen");
    }

    [Fact]
    public void DetectActiveFilter_EmptyPartialValue_DetectsFilterType()
    {
        // Arrange
        var text = "tag:";
        var cursorPosition = text.Length;

        // Act
        var (filterType, partialValue) = _sut.DetectActiveFilter(text, cursorPosition);

        // Assert
        filterType.Should().Be("tag");
        partialValue.Should().Be("");
    }

    [Fact]
    public void DetectActiveFilter_MidQueryFilterPrefix_DetectsFilter()
    {
        // Arrange
        var text = "some text tag:nat";
        var cursorPosition = text.Length;

        // Act
        var (filterType, partialValue) = _sut.DetectActiveFilter(text, cursorPosition);

        // Assert
        filterType.Should().Be("tag");
        partialValue.Should().Be("nat");
    }

    [Fact]
    public void DetectActiveFilter_NoFilterPrefix_ReturnsNull()
    {
        // Arrange
        var text = "just some text";
        var cursorPosition = text.Length;

        // Act
        var (filterType, partialValue) = _sut.DetectActiveFilter(text, cursorPosition);

        // Assert
        filterType.Should().BeNull();
        partialValue.Should().BeNull();
    }

    [Fact]
    public void DetectActiveFilter_QuotedPartial_ExtractsQuotedValue()
    {
        // Arrange
        var text = "tag:\"multi word";
        var cursorPosition = text.Length;

        // Act
        var (filterType, partialValue) = _sut.DetectActiveFilter(text, cursorPosition);

        // Assert
        filterType.Should().Be("tag");
        partialValue.Should().Be("multi word");
    }

    [Fact]
    public void DetectActiveFilter_CursorNotAtEnd_UsesTextBeforeCursor()
    {
        // Arrange
        var text = "tag:nature extra";
        var cursorPosition = 10; // After "tag:nature"

        // Act
        var (filterType, partialValue) = _sut.DetectActiveFilter(text, cursorPosition);

        // Assert
        filterType.Should().Be("tag");
        partialValue.Should().Be("nature");
    }

    [Fact]
    public void DetectActiveFilter_EmptyString_ReturnsNull()
    {
        // Act
        var (filterType, partialValue) = _sut.DetectActiveFilter("", 0);

        // Assert
        filterType.Should().BeNull();
    }

    [Fact]
    public void DetectActiveFilter_InvalidCursorPosition_ReturnsNull()
    {
        // Act
        var (filterType, partialValue) = _sut.DetectActiveFilter("tag:test", -1);

        // Assert
        filterType.Should().BeNull();
    }

    #endregion

    #region Reconstruct Tests

    [Fact]
    public void Reconstruct_EmptyQuery_ReturnsEmptyString()
    {
        // Arrange
        var query = new ParsedQuery();

        // Act
        var result = _sut.Reconstruct(query);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Reconstruct_SingleFilter_ReturnsFormattedString()
    {
        // Arrange
        var query = new ParsedQuery
        {
            Filters = new List<FilterToken> { new("tag", "nature") }
        };

        // Act
        var result = _sut.Reconstruct(query);

        // Assert
        result.Should().Be("tag:nature");
    }

    [Fact]
    public void Reconstruct_FilterWithSpaces_QuotesValue()
    {
        // Arrange
        var query = new ParsedQuery
        {
            Filters = new List<FilterToken> { new("tag", "multi word") }
        };

        // Act
        var result = _sut.Reconstruct(query);

        // Assert
        result.Should().Be("tag:\"multi word\"");
    }

    [Fact]
    public void Reconstruct_FiltersAndFreeText_ReturnsCompleteQuery()
    {
        // Arrange
        var query = new ParsedQuery
        {
            Filters = new List<FilterToken>
            {
                new("tag", "nature"),
                new("resource", "general")
            },
            FreeTextTerms = new List<string> { "단풍", "autumn" }
        };

        // Act
        var result = _sut.Reconstruct(query);

        // Assert
        result.Should().Contain("tag:nature");
        result.Should().Contain("resource:general");
        result.Should().Contain("단풍");
        result.Should().Contain("autumn");
    }

    [Fact]
    public void Reconstruct_ParsedQuery_RoundTripsCorrectly()
    {
        // Arrange
        var originalQuery = "tag:nature resource:general 단풍";

        // Act
        var parsed = _sut.Parse(originalQuery);
        var reconstructed = _sut.Reconstruct(parsed);
        var reparsed = _sut.Parse(reconstructed);

        // Assert
        reparsed.Filters.Should().BeEquivalentTo(parsed.Filters);
        reparsed.FreeTextTerms.Should().BeEquivalentTo(parsed.FreeTextTerms);
    }

    #endregion

    #region RemoveFilter Tests

    [Fact]
    public void RemoveFilter_RemovesSpecifiedFilter()
    {
        // Arrange
        var query = "tag:nature tag:season";
        var filterToRemove = new FilterToken("tag", "nature");

        // Act
        var result = _sut.RemoveFilter(query, filterToRemove);

        // Assert
        var parsed = _sut.Parse(result);
        parsed.Filters.Should().HaveCount(1);
        parsed.Filters[0].Value.Should().Be("season");
    }

    [Fact]
    public void RemoveFilter_PreservesFreeText()
    {
        // Arrange
        var query = "tag:nature 단풍";
        var filterToRemove = new FilterToken("tag", "nature");

        // Act
        var result = _sut.RemoveFilter(query, filterToRemove);

        // Assert
        var parsed = _sut.Parse(result);
        parsed.Filters.Should().BeEmpty();
        parsed.FreeTextTerms.Should().Contain("단풍");
    }

    [Fact]
    public void RemoveFilter_NonexistentFilter_ReturnsOriginalQuery()
    {
        // Arrange
        var query = "tag:nature";
        var filterToRemove = new FilterToken("tag", "nonexistent");

        // Act
        var result = _sut.RemoveFilter(query, filterToRemove);
        var parsed = _sut.Parse(result);

        // Assert
        parsed.Filters.Should().HaveCount(1);
        parsed.Filters[0].Value.Should().Be("nature");
    }

    #endregion

    #region InsertFilter Tests

    [Fact]
    public void InsertFilter_EmptyQuery_AddsFilter()
    {
        // Act
        var result = _sut.InsertFilter("", 0, "tag", "nature");

        // Assert
        result.Should().Be("tag:nature");
    }

    [Fact]
    public void InsertFilter_ExistingQuery_AppendsFilter()
    {
        // Act
        var result = _sut.InsertFilter("단풍", 2, "tag", "nature");

        // Assert
        result.Should().Contain("tag:nature");
        result.Should().Contain("단풍");
    }

    [Fact]
    public void InsertFilter_ReplacesPartialFilter()
    {
        // Arrange
        var query = "tag:nat";
        var cursorPosition = query.Length;

        // Act
        var result = _sut.InsertFilter(query, cursorPosition, "tag", "nature");

        // Assert
        result.Trim().Should().Be("tag:nature");
    }

    [Fact]
    public void InsertFilter_ValueWithSpaces_QuotesValue()
    {
        // Act
        var result = _sut.InsertFilter("", 0, "tag", "multi word");

        // Assert
        result.Should().Be("tag:\"multi word\"");
    }

    [Fact]
    public void InsertFilter_MidQuery_ReplacesActiveFilter()
    {
        // Arrange
        var query = "some text tag:nat";
        var cursorPosition = query.Length;

        // Act
        var result = _sut.InsertFilter(query, cursorPosition, "tag", "nature");

        // Assert
        var parsed = _sut.Parse(result);
        parsed.TagFilters.Should().Contain("nature");
        parsed.FreeTextTerms.Should().Contain("some");
        parsed.FreeTextTerms.Should().Contain("text");
    }

    #endregion

    #region ParsedQuery Property Tests

    [Fact]
    public void ParsedQuery_TagFilters_ReturnsOnlyTagValues()
    {
        // Arrange
        var query = _sut.Parse("tag:nature tag:season resource:general");

        // Act & Assert
        query.TagFilters.Should().HaveCount(2);
        query.TagFilters.Should().Contain("nature");
        query.TagFilters.Should().Contain("season");
        query.TagFilters.Should().NotContain("general");
    }

    [Fact]
    public void ParsedQuery_CombinedFreeText_JoinsTermsWithSpace()
    {
        // Arrange
        var query = _sut.Parse("hello world");

        // Act & Assert
        query.CombinedFreeText.Should().Be("hello world");
    }

    [Fact]
    public void ParsedQuery_IsValid_ReturnsTrueForValidQuery()
    {
        // Arrange
        var query = _sut.Parse("tag:nature 단풍");

        // Act & Assert
        query.IsValid.Should().BeTrue();
    }

    #endregion
}
