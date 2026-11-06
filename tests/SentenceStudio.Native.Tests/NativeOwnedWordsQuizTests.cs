using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// The quiz's pool and the committer's check agree on the one ownership rule (Captain
/// 2026-11-06): with no resource chosen, every word recorded to the learner is quizzed, linked to
/// a resource or not (a word added on the web without a resource reaches the device and is
/// practised there). Before, the committer accepted only words an owned resource linked, so an
/// answer on such a word failed with "vocabulary outside its approved owner graph".
/// </summary>
public sealed class NativeOwnedWordsQuizTests
{
    [Fact]
    public async Task AllVocabularyQuiz_OnTheLearnersUnlinkedWords_ScoresTheAnswer()
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ResourceVocabularyMappings.RemoveRange(await db.ResourceVocabularyMappings
                .Where(mapping => mapping.ResourceId == fixture.ResourceId(NativeSliceFixture.UserA))
                .ToListAsync());
            await db.SaveChangesAsync();
        }
        fixture.Activate(NativeSliceFixture.UserA);
        var launch = await fixture.Provider.GetRequiredService<NativeQuizLaunchService>()
            .CreateChooseOwnAsync(NativeSliceFixture.UserA, [], skillId: null);
        Assert.NotNull(launch);

        using var quiz = fixture.Quiz;
        var question = await quiz.OpenAsync(launch);
        Assert.True(question.Phase == NativePhase.Ready, $"{question.Phase}: {question.ErrorKey}");
        Assert.StartsWith($"word-{NativeSliceFixture.UserA}-", question.Prompt!.WordId);
        var feedback = await quiz.AnswerAsync(question.Choices[0].Text);
        Assert.Equal(NativePhase.Feedback, feedback.Phase);
        var next = await quiz.NextAsync();
        Assert.True(next.Phase is NativePhase.Ready or NativePhase.Summary);

        Assert.Equal(1, await fixture.TotalAttemptsAsync(
            NativeSliceFixture.UserA, question.Prompt.WordId));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserB));
    }
}
