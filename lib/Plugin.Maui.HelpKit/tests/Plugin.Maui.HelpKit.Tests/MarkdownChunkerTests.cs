using Plugin.Maui.HelpKit.Rag;
using Xunit;

namespace Plugin.Maui.HelpKit.Tests;

public class MarkdownChunkerTests
{
    [Fact]
    public void Chunk_EmptyInput_ReturnsEmpty()
    {
        var result = MarkdownChunker.Chunk(string.Empty, "x.md");
        Assert.Empty(result);
    }

    [Fact]
    public void Chunk_WhitespaceOnly_ReturnsEmpty()
    {
        var result = MarkdownChunker.Chunk("   \n\n   ", "x.md");
        Assert.Empty(result);
    }

    [Fact]
    public void Chunk_ThrowsWhenChunkSizeNonPositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MarkdownChunker.Chunk("body", "x.md", 0, 0));
    }

    [Fact]
    public void Chunk_ThrowsWhenOverlapGreaterOrEqualToChunkSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MarkdownChunker.Chunk("body", "x.md", 100, 100));
    }

    [Fact]
    public void Chunk_SingleHeadingAndParagraph_ProducesOneChunkWithBreadcrumbAndAnchor()
    {
        const string md = "# Vocabulary\n\nAdd words using the Import button.";

        var chunks = MarkdownChunker.Chunk(md, "vocabulary/intro.md");

        Assert.Single(chunks);
        Assert.Equal("vocabulary/intro.md", chunks[0].SourcePath);
        Assert.Equal("Vocabulary", chunks[0].HeadingPath);
        Assert.Equal("vocabulary", chunks[0].SectionAnchor);
        Assert.Contains("Add words", chunks[0].Content);
        Assert.Contains("# Vocabulary", chunks[0].Content); // heading prepended
    }

    [Fact]
    public void Chunk_NestedHeadings_BuildBreadcrumb()
    {
        const string md = "# Vocabulary\n\nIntro text.\n\n## Adding a Word\n\nDetails.";

        var chunks = MarkdownChunker.Chunk(md, "vocab.md");

        // Chunks are flushed on heading boundaries so we expect >= 2.
        Assert.True(chunks.Count >= 2);
        Assert.Equal("Vocabulary", chunks[0].HeadingPath);
        var nested = chunks.FirstOrDefault(c => c.HeadingPath == "Vocabulary > Adding a Word");
        Assert.NotEqual(default, nested);
        Assert.Equal("adding-a-word", nested.SectionAnchor);
    }

    [Fact]
    public void Chunk_IdsAreStableForIdenticalContent()
    {
        const string md = "# A\n\nhello";

        var first = MarkdownChunker.Chunk(md, "a.md");
        var second = MarkdownChunker.Chunk(md, "a.md");

        Assert.Equal(first[0].Id, second[0].Id);
    }

    [Fact]
    public void Chunk_IdsDifferForDifferentSourcePaths()
    {
        const string md = "# A\n\nhello";
        var first = MarkdownChunker.Chunk(md, "a.md");
        var second = MarkdownChunker.Chunk(md, "b.md");

        Assert.NotEqual(first[0].Id, second[0].Id);
    }

    [Fact]
    public void Chunk_RespectsChunkSize_ProducesMultipleChunks()
    {
        // 100 paragraphs at ~50 chars each = ~5000 chars. At chunkSize 200 tokens = 800 chars,
        // we expect multiple chunks.
        var paragraphs = Enumerable.Range(0, 100)
            .Select(i => $"Paragraph number {i} with some stable text content here.");
        var md = string.Join("\n\n", paragraphs);

        var chunks = MarkdownChunker.Chunk(md, "long.md", chunkSizeTokens: 200, overlapTokens: 50);

        Assert.True(chunks.Count > 1);
    }

    [Theory]
    [InlineData("Adding a Word", "adding-a-word")]
    [InlineData("Title With (Special)! Chars?", "title-with-special-chars")]
    [InlineData("  Leading And Trailing  ", "leading-and-trailing")]
    [InlineData("한국어 Heading", "")] // current slugifier strips non-ASCII; asserts documented behavior
    [InlineData("", "")]
    public void Slugify_ProducesGitHubStyleSlugs(string input, string expected)
    {
        // For Korean-only headings current Slugify yields empty because SlugStripRegex
        // keeps only [a-z0-9\s-]. Known Alpha limitation — if this test fails after a
        // future i18n fix, update the expected value accordingly.
        var slug = MarkdownChunker.Slugify(input);
        Assert.Equal(expected, slug);
    }
}
