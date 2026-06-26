using FluentAssertions;
using SentenceStudio.Services;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// Pure unit tests for <see cref="HtmlReadability.Reduce"/> — no IO, no network.
/// All tests use fixture HTML strings.
/// </summary>
public sealed class WebArticleReadabilityTests
{
    // ---------------------------------------------------------------------------
    // Helper
    // ---------------------------------------------------------------------------

    private static (string text, string? title, string? warning) Reduce(string html)
    {
        var text = HtmlReadability.Reduce(html, out var title, out var warning);
        return (text, title, warning);
    }

    // ---------------------------------------------------------------------------
    // Script / style / noscript stripping
    // ---------------------------------------------------------------------------

    [Fact]
    public void ScriptBlock_IsStripped_ContentNotInOutput()
    {
        const string html = """
            <html><body>
            <p>Good content</p>
            <script type="text/javascript">var secret = "doNotLeak";</script>
            <p>More text</p>
            </body></html>
            """;

        var (text, _, _) = Reduce(html);

        text.Should().Contain("Good content");
        text.Should().Contain("More text");
        text.Should().NotContain("doNotLeak");
        text.Should().NotContain("<script");
    }

    [Fact]
    public void StyleBlock_IsStripped_ContentNotInOutput()
    {
        const string html = """
            <html><body>
            <style>.hidden { display: none; }</style>
            <p>Visible text</p>
            </body></html>
            """;

        var (text, _, _) = Reduce(html);

        text.Should().Contain("Visible text");
        text.Should().NotContain(".hidden");
        text.Should().NotContain("<style");
    }

    [Fact]
    public void NoscriptBlock_IsStripped_ContentNotInOutput()
    {
        const string html = """
            <html><body>
            <noscript>Please enable JavaScript.</noscript>
            <p>Main article text here.</p>
            </body></html>
            """;

        var (text, _, _) = Reduce(html);

        text.Should().Contain("Main article text");
        text.Should().NotContain("Please enable JavaScript");
    }

    [Fact]
    public void NavHeaderFooter_AreStripped()
    {
        const string html = """
            <html><body>
            <nav><a href="/">Home</a><a href="/about">About</a></nav>
            <header><h1>Site Header</h1></header>
            <main><p>This is the article body with Korean: 안녕하세요.</p></main>
            <footer><p>Copyright 2024</p></footer>
            </body></html>
            """;

        var (text, _, _) = Reduce(html);

        text.Should().Contain("article body");
        text.Should().Contain("안녕하세요");
        text.Should().NotContain("Site Header");
        text.Should().NotContain("Copyright 2024");
        text.Should().NotContain("Home");
    }

    // ---------------------------------------------------------------------------
    // Title extraction
    // ---------------------------------------------------------------------------

    [Fact]
    public void TitleTag_IsExtracted_NotPresentInBodyText()
    {
        const string html = """
            <html>
            <head><title>My Article Title</title></head>
            <body>
              <p>This is a long enough body paragraph so the thin-page threshold is not reached and the title fallback does not fire.</p>
            </body>
            </html>
            """;

        var (text, title, _) = Reduce(html);

        title.Should().Be("My Article Title");
        text.Should().Contain("long enough body paragraph");
        // Title was inside <head> which is stripped — it should not appear in body text
        text.Should().NotContain("My Article Title");
    }

    [Fact]
    public void TitleTag_WithHtmlEntities_IsDecoded()
    {
        const string html = """
            <html>
            <head><title>Korean &amp; English Guide</title></head>
            <body><p>Text.</p></body>
            </html>
            """;

        var (_, title, _) = Reduce(html);

        title.Should().Be("Korean & English Guide");
    }

    // ---------------------------------------------------------------------------
    // Meta description extraction / thin-page fallback
    // ---------------------------------------------------------------------------

    [Fact]
    public void MetaDescription_NameFirst_UsedForThinPageFallback()
    {
        // Page body is empty — only title and meta description
        const string html = """
            <html>
            <head>
              <title>Word of the Day</title>
              <meta name="description" content="Learn Korean vocabulary every day.">
            </head>
            <body></body>
            </html>
            """;

        var (text, title, warning) = Reduce(html);

        title.Should().Be("Word of the Day");
        text.Should().Contain("Word of the Day");
        text.Should().Contain("Learn Korean vocabulary");
        warning.Should().Contain("thin page");
    }

    [Fact]
    public void MetaDescription_ContentFirst_AlsoExtracted()
    {
        // content= comes before name= in the tag
        const string html = """
            <html>
            <head>
              <title>Test</title>
              <meta content="Description comes first." name="description">
            </head>
            <body></body>
            </html>
            """;

        var (text, _, warning) = Reduce(html);

        text.Should().Contain("Description comes first");
        warning.Should().Contain("thin page");
    }

    [Fact]
    public void ThinPage_WhenBothTitleAndDescriptionAbsent_NoWarning()
    {
        // Body is empty, no title, no meta desc — nothing to fall back to
        const string html = "<html><body></body></html>";

        var (text, title, warning) = Reduce(html);

        text.Should().BeEmpty();
        title.Should().BeNull();
        warning.Should().BeNull();
    }

    // ---------------------------------------------------------------------------
    // HTML entity decoding
    // ---------------------------------------------------------------------------

    [Fact]
    public void HtmlEntities_AreDecoded_AmpersandAndQuotes()
    {
        const string html = """
            <html><body>
            <p>Tom &amp; Jerry are &quot;famous&quot; &mdash; even in Korea.</p>
            </body></html>
            """;

        var (text, _, _) = Reduce(html);

        text.Should().Contain("Tom & Jerry");
        text.Should().Contain("\"famous\"");
        text.Should().Contain("\u2014"); // em dash
    }

    [Fact]
    public void HtmlEntities_NumericDecimalKorean_AreDecoded()
    {
        // &#48148; is '바' in decimal NCR; &#49444; is '설'
        const string html = "<html><body><p>&#48148;&#49444;</p></body></html>";

        var (text, _, _) = Reduce(html);

        text.Should().Contain("바설");
    }

    [Fact]
    public void HtmlEntities_NumericHexKorean_AreDecoded()
    {
        // &#xBC14; is '바'; &#xC124; is '설'
        const string html = "<html><body><p>&#xBC14;&#xC124;</p></body></html>";

        var (text, _, _) = Reduce(html);

        text.Should().Contain("바설");
    }

    // ---------------------------------------------------------------------------
    // Whitespace collapsing
    // ---------------------------------------------------------------------------

    [Fact]
    public void ExcessiveWhitespace_IsCollapsed()
    {
        const string html = """
            <html><body>
            <p>Word1    Word2	Word3</p>
            <p>Line after three blank lines</p>
            </body></html>
            """;

        var (text, _, _) = Reduce(html);

        text.Should().NotContain("    "); // no multi-space runs
        text.Should().NotContain("\t");   // no tabs
        text.Should().NotMatchRegex(@"\n{3,}"); // at most 2 consecutive newlines
    }

    // ---------------------------------------------------------------------------
    // Length cap
    // ---------------------------------------------------------------------------

    [Fact]
    public void OutputLength_IsCappedAt8000Characters()
    {
        // Build a page with far more than 8000 chars of body text
        var bigText = string.Concat(Enumerable.Repeat("한국어 vocabulary word ", 500)); // ~11 000 chars
        var html = $"<html><body><p>{bigText}</p></body></html>";

        var (text, _, warning) = Reduce(html);

        text.Length.Should().BeLessOrEqualTo(8_000);
        warning.Should().Contain("truncated");
    }

    // ---------------------------------------------------------------------------
    // Overall pipeline — typical article
    // ---------------------------------------------------------------------------

    [Fact]
    public void TypicalArticle_ExtractsBodyTextCleanly()
    {
        const string html = """
            <!DOCTYPE html>
            <html lang="ko">
            <head>
              <meta charset="UTF-8">
              <title>Korean Grammar Guide</title>
              <meta name="description" content="Learn Korean grammar step by step.">
              <style>body { font-family: sans-serif; }</style>
              <script>console.log("init");</script>
            </head>
            <body>
              <header><nav><a href="/">Home</a></nav></header>
              <article>
                <h1>Introduction to Korean</h1>
                <p>Korean (한국어) is spoken by over 80 million people.</p>
                <p>The alphabet is called Hangul (한글) and has 24 basic letters.</p>
              </article>
              <footer>Copyright 2024 KoreanGuide</footer>
            </body>
            </html>
            """;

        var (text, title, warning) = Reduce(html);

        title.Should().Be("Korean Grammar Guide");
        text.Should().Contain("Korean (한국어)");
        text.Should().Contain("Hangul (한글)");
        text.Should().NotContain("console.log");
        text.Should().NotContain("font-family");
        text.Should().NotContain("Copyright 2024");
        warning.Should().BeNull(); // clean extraction, no truncation or thin-page
    }

    [Fact]
    public void EmptyHtml_ReturnsEmptyString()
    {
        var (text, title, warning) = Reduce("");

        text.Should().BeEmpty();
        title.Should().BeNull();
        warning.Should().BeNull();
    }
}
