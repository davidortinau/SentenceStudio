using Plugin.Maui.HelpKit.Rag;
using Xunit;

namespace Plugin.Maui.HelpKit.Tests;

public class CitationValidatorTests
{
    private static HelpKitChunk Chunk(string path, string anchor, string heading = "Heading")
        => new(
            Id: $"{path}-{anchor}",
            SourcePath: path,
            HeadingPath: heading,
            SectionAnchor: anchor,
            Content: $"content for {path}#{anchor}");

    [Fact]
    public void Validate_ReturnsEmpty_ForNullOrEmptyInput()
    {
        var result = CitationValidator.Validate(string.Empty, Array.Empty<HelpKitChunk>());

        Assert.Equal(string.Empty, result.CleanedContent);
        Assert.Empty(result.ValidCitations);
        Assert.Empty(result.InvalidCitations);
    }

    [Fact]
    public void Validate_KeepsValidCitationMarker_InCleanedContent()
    {
        var chunks = new[] { Chunk("vocabulary/filtering.md", "adding-a-word") };
        var output = "See [cite:vocabulary/filtering.md#adding-a-word] for details.";

        var result = CitationValidator.Validate(output, chunks);

        Assert.Contains("[cite:vocabulary/filtering.md#adding-a-word]", result.CleanedContent);
        Assert.Single(result.ValidCitations);
        Assert.Empty(result.InvalidCitations);
        Assert.Equal("vocabulary/filtering.md", result.ValidCitations[0].SourcePath);
        Assert.Equal("adding-a-word", result.ValidCitations[0].SectionAnchor);
    }

    [Fact]
    public void Validate_ReplacesFabricatedCitation_WithUnverifiedMarker()
    {
        var chunks = new[] { Chunk("real/path.md", "real-anchor") };
        var output = "Answer [cite:fake/path.md#fake-anchor].";

        var result = CitationValidator.Validate(output, chunks);

        Assert.Contains(CitationValidator.UnverifiedMarker, result.CleanedContent);
        Assert.DoesNotContain("fake/path.md", result.CleanedContent);
        Assert.Empty(result.ValidCitations);
        Assert.Single(result.InvalidCitations);
    }

    [Fact]
    public void Validate_FallsBackToPathOnlyMatch_WhenAnchorWrong()
    {
        // Models sometimes cite the right doc but invent the anchor — path-only fallback accepts it.
        var chunks = new[] { Chunk("dashboard/overview.md", "correct-anchor") };
        var output = "See [cite:dashboard/overview.md#guessed-anchor].";

        var result = CitationValidator.Validate(output, chunks);

        Assert.Single(result.ValidCitations);
        Assert.Equal("dashboard/overview.md", result.ValidCitations[0].SourcePath);
        Assert.DoesNotContain(CitationValidator.UnverifiedMarker, result.CleanedContent);
    }

    [Fact]
    public void Validate_DeduplicatesCitations_ByPathAndAnchor()
    {
        var chunks = new[] { Chunk("a.md", "one") };
        var output = "[cite:a.md#one] and again [cite:a.md#one].";

        var result = CitationValidator.Validate(output, chunks);

        Assert.Single(result.ValidCitations);
    }

    [Fact]
    public void RenderForDisplay_StripsCitationMarkers_AndCollapsesWhitespace()
    {
        var chunks = new[] { Chunk("a.md", "one") };
        var validated = CitationValidator.Validate("See [cite:a.md#one] for details.", chunks);

        var rendered = CitationValidator.RenderForDisplay(validated);

        Assert.DoesNotContain("[cite:", rendered);
        Assert.DoesNotContain("[cite unverified]", rendered);
        Assert.DoesNotContain("  ", rendered);
        Assert.StartsWith("See", rendered);
    }

    [Fact]
    public void RenderForDisplay_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => CitationValidator.RenderForDisplay(null!));
    }

    [Fact]
    public void Validate_MixedValidAndInvalid_ProducesBothCollections()
    {
        var chunks = new[] { Chunk("real.md", "ok") };
        var output = "Good [cite:real.md#ok] and bad [cite:fake.md#bad].";

        var result = CitationValidator.Validate(output, chunks);

        Assert.Single(result.ValidCitations);
        Assert.Single(result.InvalidCitations);
        Assert.Contains("[cite:real.md#ok]", result.CleanedContent);
        Assert.Contains(CitationValidator.UnverifiedMarker, result.CleanedContent);
    }
}
