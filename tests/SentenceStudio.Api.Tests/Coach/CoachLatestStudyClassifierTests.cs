using FluentAssertions;
using SentenceStudio.Api.Coach.Application;
using static SentenceStudio.Api.Coach.Application.CoachLatestStudyClassifier;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The server-owned classifier that detects "when did I last study" questions
/// and correction/dispute follow-ups, before the model executes.
/// </summary>
public sealed class CoachLatestStudyClassifierTests
{
    // ── EN query positives ──────────────────────────────────────────────────

    [Theory]
    [InlineData("when did I last study")]
    [InlineData("When did I last practice?")]
    [InlineData("last time I studied")]
    [InlineData("last time I practiced Korean")]
    [InlineData("most recent practice date")]
    [InlineData("most recent study session")]
    [InlineData("how long since I last studied")]
    [InlineData("how long since I last practiced")]
    [InlineData("When was my most recent study?")]
    [InlineData("When was my last practice session?")]
    public void En_query_phrases_match_as_Query(string input)
    {
        var match = Classify(input);
        match.Should().NotBeNull();
        match!.Kind.Should().Be(LatestStudyMatchKind.Query);
    }

    // ── KO query positives ──────────────────────────────────────────────────

    [Theory]
    [InlineData("마지막으로 공부한 게 언제야")]
    [InlineData("최근 학습 날짜")]
    [InlineData("마지막으로 연습한 게 언제야")]
    [InlineData("언제 마지막으로 공부했어")]
    [InlineData("최근 복습 날짜")]
    public void Ko_query_phrases_match_as_Query(string input)
    {
        var match = Classify(input);
        match.Should().NotBeNull();
        match!.Kind.Should().Be(LatestStudyMatchKind.Query);
    }

    // ── Captain's literal prompt ────────────────────────────────────────────

    [Fact]
    public void Captains_literal_prompt_matches()
    {
        var match = Classify("when did I last study");
        match.Should().NotBeNull();
        match!.Kind.Should().Be(LatestStudyMatchKind.Query);
    }

    // ── EN correction positives ─────────────────────────────────────────────

    [Theory]
    [InlineData("that's wrong, I practiced yesterday")]
    [InlineData("no, I studied on Monday")]
    [InlineData("that's not right, I practiced last week")]
    public void En_correction_phrases_match_as_Correction(string input)
    {
        var match = Classify(input);
        match.Should().NotBeNull();
        match!.Kind.Should().Be(LatestStudyMatchKind.Correction);
    }

    // ── KO correction positives ─────────────────────────────────────────────

    [Theory]
    [InlineData("틀렸어, 어제 공부했어")]
    [InlineData("아니, 월요일에 연습했어")]
    public void Ko_correction_phrases_match_as_Correction(string input)
    {
        var match = Classify(input);
        match.Should().NotBeNull();
        match!.Kind.Should().Be(LatestStudyMatchKind.Correction);
    }

    // ── Negatives: must not match ───────────────────────────────────────────

    [Theory]
    [InlineData("when should I study next")]
    [InlineData("how do I study Korean")]
    [InlineData("I want to practice vocabulary")]
    [InlineData("teach me a new word")]
    [InlineData("what is the Korean word for hello")]
    [InlineData("make a study plan for me")]
    [InlineData("how long should I study each day")]
    [InlineData("")]
    [InlineData("   ")]
    public void Non_study_timing_prompts_return_null(string input)
    {
        Classify(input).Should().BeNull();
    }

    [Fact]
    public void Null_input_returns_null()
    {
        Classify(null).Should().BeNull();
    }

    // ── Future-plan questions must not match ─────────────────────────────────

    [Theory]
    [InlineData("when should I study next")]
    [InlineData("when will I practice again")]
    [InlineData("plan my next study session")]
    public void Future_plan_questions_return_null(string input)
    {
        Classify(input).Should().BeNull();
    }
}
