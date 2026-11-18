using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Api.Tests.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach.Validation;

/// <summary>
/// The leak scan's word list for one turn (Captain, 2026-11-17): due words stay withheld, except
/// the ones the learner asked to see and the side of the word on their screen they can already
/// read; the side the screen hides is withheld whether it is due or not.
/// </summary>
public class CoachTurnEmbargoTests
{
    private static readonly CoachDueItemLeakValidator Leak = new();

    [Fact]
    public async Task With_no_screen_and_nothing_listed_a_due_word_stays_withheld()
    {
        using var fixture = new CoachToolTestFixture();
        var fish = Due(fixture, "생선", "fish");

        var embargo = await Source(fixture).GetTurnEmbargoAsync(CoachToolTestFixture.UserA, CoachTurnEmbargo.None);

        Leak.ValidateMany(["오늘 생선을 먹었어요."], embargo).IsValid.Should().BeFalse();
        embargo.Should().Contain(item => item.WordId == fish.Id);
    }

    [Fact]
    public async Task A_due_word_the_learner_asked_to_see_may_be_named()
    {
        using var fixture = new CoachToolTestFixture();
        var fish = Due(fixture, "생선", "fish");

        var embargo = await Source(fixture).GetTurnEmbargoAsync(CoachToolTestFixture.UserA,
            new CoachTurnEmbargo(new HashSet<string>([fish.Id]), []));

        Leak.ValidateMany(["Your weakest word is 생선 (fish)."], embargo).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task The_quiz_word_shown_in_korean_may_be_used_but_its_meaning_stays_hidden()
    {
        using var fixture = new CoachToolTestFixture();
        var fish = Due(fixture, "생선", "fish");

        var embargo = await Source(fixture).GetTurnEmbargoAsync(CoachToolTestFixture.UserA,
            new CoachTurnEmbargo(new HashSet<string>(), [new CoachScreenWord(fish.Id, "생선", AnswerHidden: true)]));

        Leak.ValidateMany(["시장에서 싱싱한 생선을 골랐어요."], embargo).IsValid.Should().BeTrue();
        Leak.ValidateMany(["It means fish."], embargo).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task The_quiz_word_shown_in_english_keeps_the_korean_answer_hidden_even_when_not_due()
    {
        using var fixture = new CoachToolTestFixture();
        var apple = fixture.SeedWord("사과", "apple");

        var embargo = await Source(fixture).GetTurnEmbargoAsync(CoachToolTestFixture.UserA,
            new CoachTurnEmbargo(new HashSet<string>(), [new CoachScreenWord(apple.Id, "apple", AnswerHidden: true)]));

        Leak.ValidateMany(["사과를 먹어요."], embargo).IsValid.Should().BeFalse();
        Leak.ValidateMany(["Think of the fruit you see in autumn."], embargo).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task An_audio_prompt_shows_neither_side_so_both_stay_hidden()
    {
        using var fixture = new CoachToolTestFixture();
        var fish = fixture.SeedWord("생선", "fish");

        var embargo = await Source(fixture).GetTurnEmbargoAsync(CoachToolTestFixture.UserA,
            new CoachTurnEmbargo(new HashSet<string>(), [new CoachScreenWord(fish.Id, null, AnswerHidden: true)]));

        Leak.ValidateMany(["생선"], embargo).IsValid.Should().BeFalse();
        Leak.ValidateMany(["fish"], embargo).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Once_the_answer_shows_the_screen_word_may_be_named_even_when_due()
    {
        using var fixture = new CoachToolTestFixture();
        var fish = Due(fixture, "생선", "fish");

        var embargo = await Source(fixture).GetTurnEmbargoAsync(CoachToolTestFixture.UserA,
            new CoachTurnEmbargo(new HashSet<string>(), [new CoachScreenWord(fish.Id, "생선", AnswerHidden: false)]));

        Leak.ValidateMany(["생선 means fish."], embargo).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Another_learners_word_on_the_screen_adds_nothing()
    {
        using var fixture = new CoachToolTestFixture();
        var theirs = fixture.SeedWord("비밀", "secret", owner: CoachToolTestFixture.UserB);

        var embargo = await Source(fixture).GetTurnEmbargoAsync(CoachToolTestFixture.UserA,
            new CoachTurnEmbargo(new HashSet<string>(), [new CoachScreenWord(theirs.Id, null, AnswerHidden: true)]));

        embargo.Should().NotContain(item => item.WordId == theirs.Id);
    }

    private static Shared.Models.VocabularyWord Due(CoachToolTestFixture fixture, string target, string native)
    {
        var word = fixture.SeedWord(target, native);
        fixture.SeedProgress(CoachToolTestFixture.UserA, word.Id, nextReviewDate: fixture.Now.AddHours(-1));
        return word;
    }

    private static CoachValidationDataSource Source(CoachToolTestFixture fixture) =>
        new(fixture.Db, fixture.Scope, fixture.Dates, NullLogger<CoachValidationDataSource>.Instance);
}
