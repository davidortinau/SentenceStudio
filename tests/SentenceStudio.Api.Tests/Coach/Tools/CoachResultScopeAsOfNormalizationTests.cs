using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// The one normalizer every scope's "as of" instant passes through.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the reads are handed <c>IPlanDateContext.UtcNow</c>, which is
/// <see cref="DateTime.UtcNow"/> and therefore carries sub-second ticks that
/// <c>System.Text.Json</c> renders in full. Eight characters, on every scope, on every one of a
/// turn's twenty tool calls, describing a precision no read is accurate to — the reads are computed
/// from calendar days, review dates and completion rows.
/// </para>
/// <para>
/// The tests below are about the two properties that make the normalization safe rather than merely
/// cheap: it never moves an instant forwards, and it cannot be bypassed.
/// </para>
/// </remarks>
public sealed class CoachResultScopeAsOfNormalizationTests
{
    private static readonly DateTime Whole =
        new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    private static CoachResultScope ScopeAt(DateTime asOf) => new()
    {
        Coverage = CoachScopeCoverage.DerivedProjection,
        Order = CoachScopeOrder.NotApplicable,
        OrderHonored = true,
        Filters = CoachScopeFilters.OwnerScoped,
        AsOfUtc = asOf,
        ReturnedCount = 0
    };

    // ------------------------------------------------------------------ truncation

    /// <summary>
    /// The sub-second component is dropped, not rounded.
    /// </summary>
    /// <remarks>
    /// Rounding a 12:00:00.7 read up to 12:00:01 would place the answer's stated instant after the
    /// data it was computed from — a scope claiming the answer was true at a moment that had not
    /// happened yet. The only direction "as of" may be moved is backwards.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(4_821_593)]
    [InlineData(5_000_000)]
    [InlineData(9_999_999)]
    public void Sub_second_ticks_are_truncated_toward_the_past(long ticks)
    {
        var normalized = CoachResultScope.NormalizeAsOf(Whole.AddTicks(ticks));

        normalized.Should().Be(Whole);
        normalized.Should().BeBefore(Whole.AddTicks(ticks));
    }

    [Fact]
    public void A_whole_second_instant_is_left_alone()
    {
        CoachResultScope.NormalizeAsOf(Whole).Should().Be(Whole);
    }

    [Fact]
    public void Normalization_is_idempotent()
    {
        var once = CoachResultScope.NormalizeAsOf(Whole.AddTicks(4_821_593));

        CoachResultScope.NormalizeAsOf(once).Should().Be(once);
    }

    /// <summary>The whole seconds are preserved exactly; only the fraction goes.</summary>
    [Fact]
    public void Nothing_above_the_second_is_disturbed()
    {
        var odd = new DateTime(2026, 2, 28, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_999_999);

        CoachResultScope.NormalizeAsOf(odd)
            .Should().Be(new DateTime(2026, 2, 28, 23, 59, 59, DateTimeKind.Utc));
    }

    // ------------------------------------------------------------------------ kind

    /// <summary>
    /// A local instant is converted rather than relabelled, so the moment survives.
    /// </summary>
    /// <remarks>
    /// A <see cref="DateTimeKind.Local"/> value would otherwise serialize with an offset instead of
    /// <c>Z</c> — a different number of characters and, worse, a different claim from the one the
    /// member's name makes.
    /// </remarks>
    [Fact]
    public void A_local_instant_is_converted_to_utc()
    {
        var local = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Local);

        var normalized = CoachResultScope.NormalizeAsOf(local);

        normalized.Kind.Should().Be(DateTimeKind.Utc);
        normalized.Should().Be(
            CoachResultScope.NormalizeAsOf(local.ToUniversalTime()),
            "the instant is preserved; only its representation changes");
    }

    /// <summary>An unspecified kind is read as UTC, which is what the member name already says.</summary>
    [Fact]
    public void An_unspecified_instant_is_read_as_utc()
    {
        var unspecified = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Unspecified);

        var normalized = CoachResultScope.NormalizeAsOf(unspecified);

        normalized.Kind.Should().Be(DateTimeKind.Utc);
        normalized.Should().Be(Whole);
    }

    // -------------------------------------------------------------- cannot be bypassed

    /// <summary>
    /// Constructing a scope normalizes, whatever the caller passes.
    /// </summary>
    /// <remarks>
    /// The reason the normalizer lives on the <c>init</c> accessor rather than in a factory: all
    /// fourteen registered reads build their scope with an object initializer, and a factory is a
    /// convention the fifteenth can decline to follow without anything failing.
    /// </remarks>
    [Fact]
    public void A_scope_built_by_object_initializer_is_normalized()
    {
        var scope = ScopeAt(Whole.AddTicks(4_821_593));

        scope.AsOfUtc.Should().Be(Whole);
        (scope.AsOfUtc.Ticks % TimeSpan.TicksPerSecond).Should().Be(0);
    }

    /// <summary>
    /// A <c>with</c> expression normalizes too.
    /// </summary>
    /// <remarks>
    /// Records copy backing fields directly, so a normalizer implemented as a constructor step
    /// rather than as an accessor would be silently skipped here — and <c>with</c> is exactly how a
    /// decorator or a test helper would adjust an instant.
    /// </remarks>
    [Fact]
    public void A_scope_produced_by_a_with_expression_is_normalized()
    {
        var scope = ScopeAt(Whole) with { AsOfUtc = Whole.AddTicks(9_999_999) };

        scope.AsOfUtc.Should().Be(Whole);
    }

    /// <summary>
    /// Two reads a fraction of a second apart state the same instant.
    /// </summary>
    /// <remarks>
    /// The model-facing consequence, and the reason this is a content decision rather than a
    /// formatting one: within a single turn, twenty tool calls now agree on when "now" was instead
    /// of disagreeing by microseconds the model might try to interpret as ordering.
    /// </remarks>
    [Fact]
    public void Two_reads_in_the_same_second_state_the_same_instant()
    {
        var first = ScopeAt(Whole.AddTicks(12));
        var second = ScopeAt(Whole.AddTicks(9_999_999));

        first.AsOfUtc.Should().Be(second.AsOfUtc);
    }

    /// <summary>
    /// Equality still distinguishes different seconds.
    /// </summary>
    /// <remarks>
    /// The counter-case to the one above: normalization must collapse noise, not information. A
    /// read taken a second later is a different answer and has to say so.
    /// </remarks>
    [Fact]
    public void Reads_in_different_seconds_remain_distinguishable()
    {
        var first = ScopeAt(Whole.AddTicks(9_999_999));
        var second = ScopeAt(Whole.AddSeconds(1));

        first.AsOfUtc.Should().NotBe(second.AsOfUtc);
        second.AsOfUtc.Should().Be(Whole.AddSeconds(1));
    }
}
