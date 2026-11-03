using SentenceStudio.Pages.VocabularyMatching;
using SentenceStudio.Shared.Models;
using VocabMatchingPage = SentenceStudio.WebUI.Pages.VocabMatching;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Backport B3: Vocabulary Matching read <c>active_profile_id</c> with an empty default and passed
/// an empty owner through to progress writes. It now refuses to load without an owner and only
/// records a match for the owner the board was loaded for.
/// </summary>
public sealed class VocabMatchingOwnerTests
{
    [Fact]
    public async Task NoActiveProfile_LoadsNoBoard()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"), ("하늘", "sky"));
        fixture.Preferences.ActiveId = string.Empty;

        var (_, page) = await fixture.Renderer.RenderAsync<VocabMatchingPage>();

        Members.Get<List<MatchingTile>>(page, "tiles").Should().BeEmpty();
        fixture.Toasts.Should().Contain(t => t.Text == "No active user profile. Please select a profile first.");
    }

    [Fact]
    public async Task Match_AfterTheProfileChanged_IsNotRecordedForAnyone()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var (_, words) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        var (_, page) = await fixture.Renderer.RenderAsync<VocabMatchingPage>();
        await SelectMatchingPairAsync(fixture, page, words[0]);
        fixture.Preferences.ActiveId = ActivityPageFixture.OwnerB;

        await fixture.Renderer.InvokeAsync(page, "CheckForMatch");

        (await fixture.RowsAsync<VocabularyProgress>()).Should().BeEmpty();
        Members.Get<int>(page, "matchedPairs").Should().Be(1, "the board still advances");
    }

    [Fact]
    public async Task Match_ForTheOwner_IsRecordedForThatOwner()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var (_, words) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        var (_, page) = await fixture.Renderer.RenderAsync<VocabMatchingPage>();
        await SelectMatchingPairAsync(fixture, page, words[0]);

        await fixture.Renderer.InvokeAsync(page, "CheckForMatch");

        var progress = (await fixture.RowsAsync<VocabularyProgress>()).Should().ContainSingle().Subject;
        progress.UserId.Should().Be(ActivityPageFixture.OwnerA);
        progress.VocabularyWordId.Should().Be(words[0].Id);
    }

    /// <summary>
    /// The page opened without a resource (no board), so this sets the loaded owner the way
    /// LoadVocabulary does and selects the native and target tiles of one word.
    /// </summary>
    private static Task SelectMatchingPairAsync(ActivityPageFixture fixture, VocabMatchingPage page, VocabularyWord word) =>
        fixture.Renderer.Dispatcher.InvokeAsync(() =>
        {
            Members.Get<string>(page, "_owner").Should().Be(ActivityPageFixture.OwnerA);
            var native = new MatchingTile { Id = 0, Text = word.NativeLanguageTerm!, Language = "native", VocabularyWordId = word.Id, Word = word };
            var target = new MatchingTile { Id = 1, Text = word.TargetLanguageTerm!, Language = "target", VocabularyWordId = word.Id, Word = word };
            Members.Set(page, "tiles", new List<MatchingTile> { native, target });
            Members.Set(page, "totalPairs", 1);
            Members.Get<List<MatchingTile>>(page, "selectedTiles").AddRange([native, target]);
        });
}
