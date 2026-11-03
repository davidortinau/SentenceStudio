using Microsoft.AspNetCore.Components;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Shared;
using ReadingPage = SentenceStudio.WebUI.Pages.Reading;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Backports B8 (the Reading dictionary lookup cached each AI translation as a VocabularyWord with
/// the native and target terms swapped and no owner), B29 (the "audio generation failed" message
/// disappeared as soon as generation stopped) and B30 (the single-click sentence handler never fired).
/// </summary>
public sealed class ReadingLookupAndAudioTests
{
    [Fact]
    public async Task Lookup_AiTranslation_IsShownRecordedForTheOwnerAndNotSavedAsAWord()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenAsync(fixture);
        var wordsBefore = (await fixture.RowsAsync<VocabularyWord>()).Count;
        fixture.Ai.Respond = _ => "sea";

        await fixture.Renderer.InvokeAsync(page, "HandleWordClicked", "바다,");

        Members.Get<string>(page, "selectedWord").Should().Be("바다");
        Members.Get<string>(page, "wordDefinition").Should().Be("sea");
        (await fixture.RowsAsync<VocabularyWord>()).Should().HaveCount(wordsBefore,
            "the lookup must not store an inverted, unowned word");
        var activity = (await fixture.RowsAsync<UserActivity>()).Should().ContainSingle().Subject;
        activity.Input.Should().Be("Dictionary lookup: 바다");
        activity.UserProfileId.Should().Be(ActivityPageFixture.OwnerA);
    }

    [Fact]
    public async Task Lookup_AiFails_ShowsUnavailableAndRecordsNoLookup()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenAsync(fixture);
        fixture.Ai.Respond = _ => throw new HttpRequestException("AI unavailable");

        await fixture.Renderer.InvokeAsync(page, "HandleWordClicked", "바다");

        Members.Get<bool>(page, "translationUnavailable").Should().BeTrue();
        (await fixture.RowsAsync<UserActivity>()).Should().BeEmpty("a failed lookup is not a lookup");
    }

    [Fact]
    public async Task Lookup_UsesOnlyTheOwnersVocabulary()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        await fixture.SeedResourceAsync(ActivityPageFixture.OwnerB, ("바다", "B's private gloss"));
        var page = await OpenAsync(fixture);
        fixture.Ai.Respond = _ => "sea";

        await fixture.Renderer.InvokeAsync(page, "HandleWordClicked", "바다");

        Members.Get<string>(page, "wordDefinition").Should().Be("sea");
        fixture.Ai.Prompts.Should().ContainSingle();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lookup_SupersededByAnotherTap_DoesNotShowTheLateDefinition(bool closePanelFirst)
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var page = await OpenAsync(fixture);
        // The second word is in the passage vocabulary, so only the first lookup asks the AI.
        await fixture.Renderer.Dispatcher.InvokeAsync(() => Members.Set(page, "vocabularyWords",
            new List<VocabularyWord> { new() { Id = "sky", TargetLanguageTerm = "하늘", NativeLanguageTerm = "sky" } }));
        fixture.Ai.Respond = _ => "sea";
        fixture.Ai.Hold();

        var lookup = fixture.Renderer.InvokeAsync(page, "HandleWordClicked", "바다");
        await fixture.Ai.Started;
        if (closePanelFirst)
            await fixture.Renderer.InvokeAsync(page, "CloseWordPanel");
        await fixture.Renderer.InvokeAsync(page, "HandleWordClicked", "하늘");
        Members.Get<bool>(page, "isTranslating").Should().BeFalse("the tapped word is already known");
        fixture.Ai.Release();
        await lookup;

        Members.Get<string>(page, "selectedWord").Should().Be("하늘");
        Members.Get<string>(page, "wordDefinition").Should().Be("sky",
            "a late answer for another word must not be shown, or saved, under this word");
        Members.Get<bool>(page, "isTranslating").Should().BeFalse();
    }

    [Fact]
    public async Task AudioGenerationFailure_StaysVisibleAfterGenerationStops()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var (id, page) = await fixture.Renderer.RenderAsync<ReadingPage>();
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
        {
            Members.Set(page, "resource", new LearningResource
            {
                Id = Guid.NewGuid().ToString(), Title = "Passage", Transcript = "바다가 파래요.",
                UserProfileId = ActivityPageFixture.OwnerA
            });
            Members.Set(page, "sentences", new List<string> { "바다가 파래요." });
        });

        await fixture.Renderer.InvokeAsync(page, "InitializeAudioAsync");

        Members.Get<bool>(page, "isGeneratingAudio").Should().BeFalse();
        Members.Get<bool>(page, "audioFailed").Should().BeTrue();
        fixture.Renderer.RenderedText(id).Should().Contain("Audio error:",
            "the learner sees why there is no audio after generation stops");
    }

    [Fact]
    public async Task InteractiveText_SingleClickOnASentence_RaisesSentenceClicked()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        var clicked = new List<int>();
        var receiver = new object();
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(InteractiveText.Sentences)] = new List<string> { "첫 문장.", "둘째 문장." },
            [nameof(InteractiveText.OnSentenceClicked)] = EventCallback.Factory.Create<int>(receiver, clicked.Add),
        });
        var (id, _) = await fixture.Renderer.RenderAsync<InteractiveText>(parameters);

        await fixture.Renderer.ClickElementAsync(id, "data-sentence", "1");

        clicked.Should().Equal(1);
    }

    /// <summary>Opens Reading for owner A without a resource (the lookup does not need one).</summary>
    private static async Task<ReadingPage> OpenAsync(ActivityPageFixture fixture)
    {
        var (_, page) = await fixture.Renderer.RenderAsync<ReadingPage>();
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
            Members.Set(page, "_owner", ActivityPageFixture.OwnerA));
        return page;
    }
}
