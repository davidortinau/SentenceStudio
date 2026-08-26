using FluentAssertions;
using SentenceStudio.Api.Coach.Application;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Deterministic copy for latest-study answers: correct content, no PII,
/// correct language routing (display language, NOT target language),
/// date-only formatting, days-since singular/plural grammar.
/// </summary>
public sealed class CoachLatestStudyCopyTests
{
    // ── With data: EN display ───────────────────────────────────────────────

    [Fact]
    public void En_display_with_date_and_days_since_plural()
    {
        var result = CoachDeterministicCopy.ComposeLatestStudyAnswer(
            new DateOnly(2026, 8, 20), daysSince: 5, isCorrection: false, "en-US");

        result.Should().Contain("2026-08-20");
        result.Should().Contain("5 days ago");
        result.Should().NotContain("day(s)");
        result.Should().StartWith("Your most recent practice");
    }

    [Fact]
    public void En_display_with_date_today()
    {
        var result = CoachDeterministicCopy.ComposeLatestStudyAnswer(
            new DateOnly(2026, 8, 25), daysSince: 0, isCorrection: false, "en-US");

        result.Should().Contain("2026-08-25");
        result.Should().Contain("today");
        result.Should().NotContain("days ago");
        result.Should().NotContain("day ago");
    }

    [Fact]
    public void En_display_1_day_singular()
    {
        var result = CoachDeterministicCopy.ComposeLatestStudyAnswer(
            new DateOnly(2026, 8, 24), daysSince: 1, isCorrection: false, "en-US");

        result.Should().Contain("1 day ago");
        result.Should().NotContain("1 days ago");
        result.Should().NotContain("day(s)");
        result.Should().NotContain("today");
    }

    [Fact]
    public void En_display_2_days_plural()
    {
        var result = CoachDeterministicCopy.ComposeLatestStudyAnswer(
            new DateOnly(2026, 8, 23), daysSince: 2, isCorrection: false, "en-US");

        result.Should().Contain("2 days ago");
        result.Should().NotContain("day(s)");
    }

    // ── With data: KO display ───────────────────────────────────────────────

    [Fact]
    public void Ko_display_with_date_and_days_since()
    {
        var result = CoachDeterministicCopy.ComposeLatestStudyAnswer(
            new DateOnly(2026, 8, 20), daysSince: 5, isCorrection: false, "ko-KR");

        result.Should().Contain("2026-08-20");
        result.Should().Contain("5일 전");
        result.Should().Contain("최근 학습");
    }

    [Fact]
    public void Ko_display_with_date_today()
    {
        var result = CoachDeterministicCopy.ComposeLatestStudyAnswer(
            new DateOnly(2026, 8, 25), daysSince: 0, isCorrection: false, "ko-KR");

        result.Should().Contain("2026-08-25");
        result.Should().Contain("오늘");
    }

    // ── Display language != target language (the bug) ────────────────────────

    [Fact]
    public void En_display_with_Ko_target_produces_English()
    {
        // User's device is English, target language is Korean.
        // Display language governs the answer text, NOT target.
        var result = CoachDeterministicCopy.ComposeLatestStudyAnswer(
            new DateOnly(2026, 8, 24), daysSince: 1, isCorrection: false, "en-US");

        result.Should().StartWith("Your most recent practice");
        result.Should().NotContain("학습");
        result.Should().NotContain("전입니다");
    }

    [Fact]
    public void Ko_display_with_any_target_produces_Korean()
    {
        // User's device is Korean, target could be anything.
        var result = CoachDeterministicCopy.ComposeLatestStudyAnswer(
            new DateOnly(2026, 8, 24), daysSince: 1, isCorrection: false, "ko-KR");

        result.Should().Contain("최근 학습");
        result.Should().NotContain("Your most recent");
    }

    // ── No data ─────────────────────────────────────────────────────────────

    [Fact]
    public void En_no_data_is_honest()
    {
        var result = CoachDeterministicCopy.ComposeLatestStudyAnswer(
            null, null, isCorrection: false, "en-US");

        result.Should().Contain("don\u2019t have any practice records");
    }

    [Fact]
    public void Ko_no_data_is_honest()
    {
        var result = CoachDeterministicCopy.ComposeLatestStudyAnswer(
            null, null, isCorrection: false, "ko-KR");

        result.Should().Contain("학습 기록이 없습니다");
    }

    [Fact]
    public void En_no_data_preserves_display_language()
    {
        // Even with Korean target, no-data must be English when display is English.
        var result = CoachDeterministicCopy.ComposeLatestStudyAnswer(
            null, null, isCorrection: false, "en-US");

        result.Should().NotContain("없습니다");
        result.Should().Contain("don\u2019t have any practice records");
    }

    // ── Correction preamble preserves display language ───────────────────────

    [Fact]
    public void En_correction_prepends_preamble()
    {
        var result = CoachDeterministicCopy.ComposeLatestStudyAnswer(
            new DateOnly(2026, 8, 20), daysSince: 5, isCorrection: true, "en-US");

        result.Should().StartWith("Let me check again.");
    }

    [Fact]
    public void Ko_correction_prepends_preamble()
    {
        var result = CoachDeterministicCopy.ComposeLatestStudyAnswer(
            new DateOnly(2026, 8, 20), daysSince: 5, isCorrection: true, "ko-KR");

        result.Should().StartWith("다시 확인하겠습니다.");
    }

    [Fact]
    public void En_correction_preserves_display_language()
    {
        var result = CoachDeterministicCopy.ComposeLatestStudyAnswer(
            new DateOnly(2026, 8, 20), daysSince: 5, isCorrection: true, "en-US");

        result.Should().StartWith("Let me check again.");
        result.Should().NotContain("확인하겠습니다");
    }

    // ── No PII in output ────────────────────────────────────────────────────

    [Fact]
    public void Answer_text_contains_no_user_names_or_emails()
    {
        var result = CoachDeterministicCopy.ComposeLatestStudyAnswer(
            new DateOnly(2026, 8, 20), daysSince: 5, isCorrection: false, "en-US");

        result.Should().NotContainAny("@", "user", "email", "name");
    }

    // ── Answer codes are content-free snake_case ────────────────────────────

    [Fact]
    public void Answer_codes_are_valid_snake_case()
    {
        CoachDeterministicCopy.LatestStudyAnswerCode.Should().MatchRegex("^[a-z_]+$");
        CoachDeterministicCopy.LatestStudyNoDataCode.Should().MatchRegex("^[a-z_]+$");
        CoachDeterministicCopy.LatestStudyCorrectionCode.Should().MatchRegex("^[a-z_]+$");
    }
}
