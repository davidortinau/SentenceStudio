using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using SentenceStudio.Shared.Services;
using Xunit;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// F1 regression guard: the Vocab Quiz round sampler must surface ALL pool words across
/// consecutive rounds before repeating (no front-bias starvation), vary the set per round,
/// and preserve pool order (SRS overdue priority) — the fix for "same 10 every round /
/// never see all 20" introduced by commit 750138b5's deterministic Take-from-front.
/// </summary>
public class RoundSamplerTests
{
    private static List<int> Pool(int n) => Enumerable.Range(0, n).ToList();

    [Fact]
    public void TwentyPool_TenPerRound_CoversAllAcrossTwoRounds_NoRepeatNoGap()
    {
        var pool = Pool(20);

        var (round1, cursor1) = RoundSampler.NextWindow(pool, 0, 10);
        var (round2, cursor2) = RoundSampler.NextWindow(pool, cursor1, 10);

        round1.Should().Equal(Enumerable.Range(0, 10), "round 1 takes the most-overdue front window");
        round2.Should().Equal(Enumerable.Range(10, 10), "round 2 takes the NEXT window — different words");
        round1.Should().NotIntersectWith(round2, "no word repeats before all are shown");
        round1.Concat(round2).Distinct().Should().HaveCount(20, "all 20 words covered, no gaps");
        cursor2.Should().Be(0, "cursor wraps after a full pass");
    }

    [Fact]
    public void ThirdRound_WrapsBackToStart()
    {
        var pool = Pool(20);
        var c = 0;
        (_, c) = RoundSampler.NextWindow(pool, c, 10);
        (_, c) = RoundSampler.NextWindow(pool, c, 10);
        var (round3, _) = RoundSampler.NextWindow(pool, c, 10);

        round3.Should().Equal(Enumerable.Range(0, 10), "after a full pass, rotation restarts from the front");
    }

    [Fact]
    public void PoolSmallerThanRound_ReturnsAll()
    {
        var pool = Pool(7);
        var (window, next) = RoundSampler.NextWindow(pool, 0, 10);
        window.Should().Equal(pool, "a pool smaller than the round size returns every word");
        next.Should().Be(0);
    }

    [Fact]
    public void CursorOutOfRange_ResetsToStart()
    {
        var pool = Pool(20);
        var (window, _) = RoundSampler.NextWindow(pool, 999, 10);
        window.Should().Equal(Enumerable.Range(0, 10), "an out-of-range cursor is clamped to 0");
    }

    [Fact]
    public void EmptyPool_ReturnsEmpty()
    {
        var (window, next) = RoundSampler.NextWindow(new List<int>(), 5, 10);
        window.Should().BeEmpty();
        next.Should().Be(0);
    }

    [Fact]
    public void OddPool_StillCoversAllBeforeRepeating()
    {
        // 15-word pool, 10 per round: round1 = 0..9, round2 wraps = 10..14 + 0..4.
        var pool = Pool(15);
        var (r1, c1) = RoundSampler.NextWindow(pool, 0, 10);
        var (r2, _) = RoundSampler.NextWindow(pool, c1, 10);
        r1.Concat(r2).Distinct().Should().HaveCount(15, "every word appears within two rounds");
    }
}
