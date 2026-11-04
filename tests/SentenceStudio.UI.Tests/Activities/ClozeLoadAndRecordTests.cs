using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Services;
using ClozePage = SentenceStudio.WebUI.Pages.Cloze;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Backports B12 (any AI failure showed "No cloze sentences" because ClozureService returned an
/// empty list), B13 (a sentence whose word could not be blanked was shown with the answer
/// visible), B24 (review due dates are UTC instants) and B25 (the activity row is written in UTC
/// for its owner).
/// </summary>
public sealed class ClozeLoadAndRecordTests
{
    private const string NoSentences = "No cloze sentences available.";
    private const string GenerationFailed =
        "Cloze sentences could not be generated. Nothing was recorded. Go back and try again.";

    private static ClozureDto Sentence(string sentence, string word, string asUsed) => new()
    {
        SentenceText = sentence,
        RecommendedTranslation = "translation",
        VocabularyWord = word,
        VocabularyWordAsUsed = asUsed,
        VocabularyWordGuesses = [asUsed, "하늘", "구름", "나무", "산"]
    };

    [Fact]
    public async Task Load_AiFails_TellsTheLearnerItFailed_NotThatThereAreNoSentences()
    {
        await using var fixture = await CreateFixtureAsync();
        await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        fixture.Ai.Respond = _ => throw new HttpRequestException("AI unavailable");

        var (id, page) = await fixture.Renderer.RenderAsync<ClozePage>();

        fixture.Ai.Prompts.Should().ContainSingle();
        Members.Get<List<Challenge>>(page, "sentences").Should().BeEmpty();
        var text = fixture.Renderer.RenderedText(id);
        text.Should().Contain(GenerationFailed).And.NotContain(NoSentences);
        fixture.Toasts.Should().Contain(t => t.Text == GenerationFailed && t.Type == ToastType.Error);
        fixture.Toasts.Should().NotContain(t => t.Text.Contains("AI unavailable"), "raw exception text is not shown");
    }

    [Fact]
    public async Task Load_AiReturnsNothing_TellsTheLearnerItFailed()
    {
        await using var fixture = await CreateFixtureAsync();
        await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        fixture.Ai.Respond = _ => null;

        var (id, _) = await fixture.Renderer.RenderAsync<ClozePage>();

        fixture.Renderer.RenderedText(id).Should().Contain(GenerationFailed).And.NotContain(NoSentences);
    }

    [Fact]
    public async Task Load_NoEligibleVocabulary_ShowsNoSentences_WithoutAskingTheAi()
    {
        await using var fixture = await CreateFixtureAsync();

        var (id, _) = await fixture.Renderer.RenderAsync<ClozePage>();

        fixture.Ai.Prompts.Should().BeEmpty();
        fixture.Renderer.RenderedText(id).Should().Contain(NoSentences).And.NotContain(GenerationFailed);
    }

    [Fact]
    public async Task Load_DropsASentenceWhoseAnswerCannotBeBlanked()
    {
        await using var fixture = await CreateFixtureAsync();
        await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"), ("학교", "school"));
        fixture.Ai.Respond = _ => new ClozureResponse
        {
            Sentences =
            [
                // Neither 학교 nor 학교에 appears, so this sentence would show no blank.
                Sentence("나는 집에 가요.", "학교", "학교에"),
                Sentence("바다가 넓어요.", "바다", "바다가"),
            ]
        };

        var (_, page) = await fixture.Renderer.RenderAsync<ClozePage>();

        var sentences = Members.Get<List<Challenge>>(page, "sentences");
        sentences.Should().ContainSingle().Which.VocabularyWordAsUsed.Should().Be("바다가");
        Members.Get<string>(page, "displaySentence").Should().Be("______ 넓어요.");
    }

    [Fact]
    public async Task Load_NoSentenceCanBeBlanked_TellsTheLearnerItFailed()
    {
        await using var fixture = await CreateFixtureAsync();
        await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("학교", "school"));
        fixture.Ai.Respond = _ => new ClozureResponse
        {
            Sentences = [Sentence("나는 집에 가요.", "학교", "학교에")]
        };

        var (id, page) = await fixture.Renderer.RenderAsync<ClozePage>();

        Members.Get<List<Challenge>>(page, "sentences").Should().BeEmpty();
        fixture.Renderer.RenderedText(id).Should().Contain(GenerationFailed).And.NotContain("나는 집에 가요.");
    }

    [Fact]
    public async Task Answer_RecordsTheOwnersActivityInUtcAndScoresTheOwnersWord()
    {
        await using var fixture = await CreateFixtureAsync();
        var (_, words) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        fixture.Ai.Respond = _ => new ClozureResponse { Sentences = [Sentence("바다가 넓어요.", "바다", "바다가")] };
        var (_, page) = await fixture.Renderer.RenderAsync<ClozePage>();
        var before = DateTime.UtcNow.AddSeconds(-1);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => Members.Set(page, "userInput", "산이"));

        await fixture.Renderer.InvokeAsync(page, "GradeAnswer");

        var activity = (await fixture.RowsAsync<UserActivity>()).Should().ContainSingle().Subject;
        activity.UserProfileId.Should().Be(ActivityPageFixture.OwnerA);
        activity.Accuracy.Should().Be(0);
        activity.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow.AddSeconds(1),
            "streaks read UserActivity.CreatedAt as a UTC instant");
        var progress = (await fixture.RowsAsync<VocabularyProgress>()).Should().ContainSingle().Subject;
        progress.UserId.Should().Be(ActivityPageFixture.OwnerA);
        progress.VocabularyWordId.Should().Be(words[0].Id);
    }

    [Fact]
    public async Task Answer_AfterTheProfileChanged_RecordsNothing()
    {
        await using var fixture = await CreateFixtureAsync();
        await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        fixture.Ai.Respond = _ => new ClozureResponse { Sentences = [Sentence("바다가 넓어요.", "바다", "바다가")] };
        var (_, page) = await fixture.Renderer.RenderAsync<ClozePage>();
        fixture.Preferences.ActiveId = ActivityPageFixture.OwnerB;
        await fixture.Renderer.Dispatcher.InvokeAsync(() => Members.Set(page, "userInput", "바다가"));

        await fixture.Renderer.InvokeAsync(page, "GradeAnswer");

        (await fixture.RowsAsync<UserActivity>()).Should().BeEmpty();
        (await fixture.RowsAsync<VocabularyProgress>()).Should().BeEmpty();
    }

    [Fact]
    public async Task DueOnly_ComparesReviewDatesAsUtcInstants()
    {
        await using var fixture = await CreateFixtureAsync();
        var (_, words) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA,
            ("바다", "sea"), ("하늘", "sky"));
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            // One review came due a minute ago, the other comes due in a minute. A local-clock
            // comparison gets one of them wrong in every time zone but UTC.
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyProgresses.AddRange(
                new VocabularyProgress
                {
                    VocabularyWordId = words[0].Id, UserId = ActivityPageFixture.OwnerA,
                    TotalAttempts = 3, NextReviewDate = DateTime.UtcNow.AddMinutes(-1)
                },
                new VocabularyProgress
                {
                    VocabularyWordId = words[1].Id, UserId = ActivityPageFixture.OwnerA,
                    TotalAttempts = 3, NextReviewDate = DateTime.UtcNow.AddMinutes(1)
                });
            await db.SaveChangesAsync();
        }
        fixture.Ai.Respond = _ => new ClozureResponse { Sentences = [Sentence("바다가 넓어요.", "바다", "바다가")] };
        var clozure = fixture.Services.GetRequiredService<ClozureService>();

        var sentences = await clozure.GetSentences(string.Empty, 8, string.Empty, dueOnly: true);

        clozure.Words.Select(word => word.Id).Should().Equal(words[0].Id);
        sentences.Should().ContainSingle();
    }

    [Fact]
    public async Task FocusWordsGiveNoUsableSentence_TheRegularWordPoolIsTried()
    {
        await using var fixture = await CreateFixtureAsync();
        var (resource, words) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA,
            ("학교", "school"), ("바다", "sea"));
        var calls = 0;
        fixture.Ai.Respond = _ => ++calls == 1
            // The focus word's sentence cannot be blanked, so the focus attempt fails.
            ? new ClozureResponse { Sentences = [Sentence("나는 집에 가요.", "학교", "학교에")] }
            : new ClozureResponse { Sentences = [Sentence("바다가 넓어요.", "바다", "바다가")] };
        var clozure = fixture.Services.GetRequiredService<ClozureService>();

        var sentences = await clozure.GetSentences(resource.Id, 8, string.Empty,
            focusVocabularyIds: [words[0].Id]);

        calls.Should().Be(2, "the regular pool is asked after the focus words fail");
        sentences.Should().ContainSingle().Which.VocabularyWordAsUsed.Should().Be("바다가");
    }

    private static Task<ActivityPageFixture> CreateFixtureAsync() =>
        ActivityPageFixture.CreateAsync(services => services.AddSingleton<ClozureService>());
}
