using SentenceStudio.Native.Features.Settings;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Settings > About > What's New (2026-11-15): the embedded release notes, chosen by the app's
/// major.minor (newest when none match), become sections of headline-and-text lines.
/// </summary>
public sealed class NativeReleaseNotesTests
{
    [Theory]
    [InlineData("1.2.0", "1.2")]
    [InlineData("1.2", "1.2")]
    [InlineData("1.1.0+build.20261115.120000", "1.1")]
    public async Task TheAppVersionChoosesItsNotes(string appVersion, string expected)
    {
        var notes = await NativeReleaseNotes.ForAppVersionAsync(new ReleaseNotesService(), appVersion);

        Assert.NotNull(notes);
        Assert.Equal(expected, notes.Version);
    }

    [Theory]
    [InlineData("99.0.0")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AVersionWithoutNotesGetsTheNewest(string? appVersion)
    {
        var service = new ReleaseNotesService();
        var newest = (await service.GetAllNotesAsync())
            .MaxBy(note => ReleaseNotesService.ParseVersion(note.Version));

        var notes = await NativeReleaseNotes.ForAppVersionAsync(service, appVersion);

        Assert.NotNull(notes);
        Assert.Equal(newest!.Version, notes.Version);
    }

    [Fact]
    public async Task TheEmbeddedNotesBecomeSectionsOfHeadlinesWithoutMarkdown()
    {
        var notes = await NativeReleaseNotes.ForAppVersionAsync(new ReleaseNotesService(), "1.1.0");

        Assert.NotNull(notes);
        Assert.Equal("YouTube Import & Channel Monitoring", notes.Title);
        Assert.Equal("2026-03-21", notes.Date);
        Assert.Equal(["New Features", "Improvements", "Developer Experience"],
            notes.Sections.Select(section => section.Title));
        var first = notes.Sections[0].Items[0];
        Assert.Equal("YouTube Channel Monitoring", first.Headline);
        Assert.StartsWith("Subscribe to YouTube channels", first.Text);
        Assert.DoesNotContain(notes.Sections.SelectMany(section => section.Items),
            item => (item.Headline + item.Text).Contains("**", StringComparison.Ordinal));
    }

    [Fact]
    public void LinesWithoutAHeadlineAndLinksStayPlainText()
    {
        var notes = NativeReleaseNotes.From(new ReleaseNote
        {
            Version = "2.0",
            MarkdownContent = "## Big Release\n\nIntro with a [link](https://example.invalid).\n\n"
                + "### Fixes\n\n- A plain fix\n- **Bold** — with text\n"
                + "#### Deeper\n\n1. **Colon:** headline\n",
        });

        Assert.Equal("Big Release", notes.Title);
        Assert.Equal(string.Empty, notes.Sections[0].Title);
        Assert.Equal(new NativeReleaseNoteItem(null, "Intro with a link."), notes.Sections[0].Items[0]);
        Assert.Equal("Fixes", notes.Sections[1].Title);
        Assert.Equal([new NativeReleaseNoteItem(null, "A plain fix"),
            new NativeReleaseNoteItem("Bold", "with text")], notes.Sections[1].Items);
        Assert.Equal("Deeper", notes.Sections[2].Title);
        Assert.Equal([new NativeReleaseNoteItem("Colon", "headline")], notes.Sections[2].Items);
    }
}
