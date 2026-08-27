using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Feedback;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Api.Tests.Coach;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api.Tests.Feedback;

/// <summary>
/// The per-owner limits, proven durable against a real PostgreSQL server.
/// </summary>
/// <remarks>
/// <para>
/// Every test that matters here runs the limiter through <em>separate</em>
/// <see cref="FeedbackDbContext"/> instances over separate connections. A limiter that kept its
/// counters in memory passes a single-instance test perfectly and multiplies every limit by the
/// replica count in production; using one context throughout would reproduce that blind spot in
/// the test suite.
/// </para>
/// <para>
/// The Retry-After assertions are about truthfulness, not about the presence of a header. A value
/// that is too short trains clients to hammer, and one that is too long is a worse experience than
/// silence — so the tests check that retrying <em>at</em> the stated time succeeds and retrying
/// one second before it does not.
/// </para>
/// </remarks>
public sealed class FeedbackRateLimitPostgresTests : IAsyncLifetime
{
    private const string Owner = "user-feedback-limits";
    private const string OtherOwner = "user-feedback-limits-other";

    private FeedbackPostgresHarness _harness = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await FeedbackPostgresHarness.CreateAsync("limits");
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------------ preview window

    /// <summary>Ten previews an hour, and the eleventh waits.</summary>
    [PostgresFact]
    public async Task Previews_are_capped_at_ten_per_rolling_hour()
    {
        var clock = FeedbackTestData.Clock();

        for (var i = 0; i < 10; i++)
        {
            (await ConsumeAsync(FeedbackRateKind.Preview, Owner, clock))
                .Allowed.Should().BeTrue($"preview {i + 1} is inside the allowance");
            clock.Advance(TimeSpan.FromSeconds(30));
        }

        var refused = await ConsumeAsync(FeedbackRateKind.Preview, Owner, clock);
        refused.Allowed.Should().BeFalse();
        refused.Reason.Should().Be(FeedbackFailureCodes.RateLimited);
    }

    /// <summary>
    /// The window is rolling, and the wait it reports is the moment the oldest event leaves it.
    /// </summary>
    /// <remarks>
    /// A counter that resets on a fixed boundary would satisfy "eleven is refused" and then admit
    /// eleven more the instant the boundary passed. Asserting on the reported wait — and then
    /// honouring it exactly — is what distinguishes a rolling window from a bucket.
    /// </remarks>
    [PostgresFact]
    public async Task The_preview_window_frees_a_slot_exactly_when_its_oldest_event_ages_out()
    {
        var clock = FeedbackTestData.Clock();

        for (var i = 0; i < 10; i++)
        {
            (await ConsumeAsync(FeedbackRateKind.Preview, Owner, clock)).Allowed.Should().BeTrue();
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var refused = await ConsumeAsync(FeedbackRateKind.Preview, Owner, clock);
        refused.Allowed.Should().BeFalse();

        var wait = refused.RetryAfter;
        wait.Should().BeGreaterThan(TimeSpan.Zero);

        // One second short of the stated wait: still refused, so the value is not padding.
        clock.Advance(wait - TimeSpan.FromSeconds(1));
        (await ConsumeAsync(FeedbackRateKind.Preview, Owner, clock))
            .Allowed.Should().BeFalse("retrying before the stated time cannot succeed");

        // At the stated wait: admitted, so the value is not pessimism either.
        clock.Advance(TimeSpan.FromSeconds(1));
        (await ConsumeAsync(FeedbackRateKind.Preview, Owner, clock))
            .Allowed.Should().BeTrue("the reported wait is when a slot actually frees");
    }

    // ------------------------------------------------------------------------- submit window

    /// <summary>Two submissions inside a minute: the second waits out the cooldown.</summary>
    [PostgresFact]
    public async Task Submissions_are_separated_by_a_sixty_second_cooldown()
    {
        var clock = FeedbackTestData.Clock();

        (await ConsumeAsync(FeedbackRateKind.Submit, Owner, clock)).Allowed.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(10));
        var refused = await ConsumeAsync(FeedbackRateKind.Submit, Owner, clock);

        refused.Allowed.Should().BeFalse();
        refused.RetryAfterSeconds.Should().Be(50, "fifty of the sixty seconds are left");

        clock.Advance(TimeSpan.FromSeconds(50));
        (await ConsumeAsync(FeedbackRateKind.Submit, Owner, clock)).Allowed.Should().BeTrue();
    }

    /// <summary>Three submissions a day, and the fourth waits for the first to age out.</summary>
    [PostgresFact]
    public async Task Submissions_are_capped_at_three_per_rolling_day()
    {
        var clock = FeedbackTestData.Clock();

        for (var i = 0; i < 3; i++)
        {
            (await ConsumeAsync(FeedbackRateKind.Submit, Owner, clock))
                .Allowed.Should().BeTrue($"submission {i + 1} is inside the allowance");
            clock.Advance(TimeSpan.FromHours(1));
        }

        var refused = await ConsumeAsync(FeedbackRateKind.Submit, Owner, clock);
        refused.Allowed.Should().BeFalse();

        // Three hours elapsed since the first of three, so twenty-one remain — not twenty-four,
        // and not the cooldown's sixty seconds.
        refused.RetryAfter.Should().BeCloseTo(TimeSpan.FromHours(21), TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// When both the cooldown and the daily cap block, the wait reported is the longer one.
    /// </summary>
    /// <remarks>
    /// Reporting the cooldown here would be a lie a client can detect by obeying it: it waits sixty
    /// seconds, retries into an exhausted day, and is refused again. Retry-After has to be a time
    /// at which the request could actually succeed.
    /// </remarks>
    [PostgresFact]
    public async Task A_blocked_submission_reports_the_longest_blocking_wait()
    {
        var clock = FeedbackTestData.Clock();

        for (var i = 0; i < 3; i++)
        {
            (await ConsumeAsync(FeedbackRateKind.Submit, Owner, clock)).Allowed.Should().BeTrue();
            clock.Advance(TimeSpan.FromSeconds(61));
        }

        var refused = await ConsumeAsync(FeedbackRateKind.Submit, Owner, clock);

        refused.Allowed.Should().BeFalse();
        refused.RetryAfter.Should().BeGreaterThan(
            TimeSpan.FromHours(23), "the daily cap outlasts the cooldown and is the real blocker");
    }

    // ---------------------------------------------------------------------------- durability

    /// <summary>
    /// The window survives a process restart, because it is a row rather than a field.
    /// </summary>
    /// <remarks>
    /// Each consume runs on a fresh context over a fresh connection, and the last one is evaluated
    /// after everything the earlier ones held has been disposed. An in-memory limiter passes every
    /// other test in this file and fails this one.
    /// </remarks>
    [PostgresFact]
    public async Task Limits_survive_a_restart_because_they_are_stored()
    {
        var clock = FeedbackTestData.Clock();

        for (var i = 0; i < 3; i++)
        {
            (await ConsumeAsync(FeedbackRateKind.Submit, Owner, clock)).Allowed.Should().BeTrue();
            clock.Advance(TimeSpan.FromMinutes(5));
        }

        // A brand-new limiter, new context, new connection — the "restarted replica".
        await using var restarted = _harness.NewContext();
        var limiter = _harness.NewRateLimiter(restarted, clock);

        var decision = await limiter.PeekAsync(Owner, FeedbackRateKind.Submit);
        decision.Allowed.Should().BeFalse("the day's allowance was spent before the restart");
    }

    /// <summary>One owner's spending never touches another's allowance.</summary>
    [PostgresFact]
    public async Task Limits_are_scoped_to_one_owner()
    {
        var clock = FeedbackTestData.Clock();

        for (var i = 0; i < 3; i++)
        {
            (await ConsumeAsync(FeedbackRateKind.Submit, Owner, clock)).Allowed.Should().BeTrue();
            clock.Advance(TimeSpan.FromMinutes(2));
        }

        (await ConsumeAsync(FeedbackRateKind.Submit, Owner, clock)).Allowed.Should().BeFalse();
        (await ConsumeAsync(FeedbackRateKind.Submit, OtherOwner, clock)).Allowed.Should().BeTrue();
    }

    /// <summary>
    /// An owner-less call is refused rather than exempted.
    /// </summary>
    /// <remarks>
    /// The multi-tenant rule in this codebase: an empty scope means "no data", never "all data".
    /// A limiter that read an empty owner as unmetered would turn any code path that lost the
    /// caller's identity into an unlimited one.
    /// </remarks>
    [PostgresFact]
    public async Task An_owner_less_call_is_refused()
    {
        var clock = FeedbackTestData.Clock();

        (await ConsumeAsync(FeedbackRateKind.Preview, string.Empty, clock))
            .Allowed.Should().BeFalse();

        await using var check = _harness.NewContext();
        (await check.FeedbackRateWindows.CountAsync(w => w.UserProfileId == string.Empty))
            .Should().Be(0, "a refusal must not create a window an unowned caller could then spend");
    }

    /// <summary>
    /// Concurrent consumes over separate connections never admit more than the limit.
    /// </summary>
    /// <remarks>
    /// The over-admission this catches is the classic count-then-insert interleaving: two replicas
    /// both read "one slot left" and both take it. The compare-and-swap is what makes the count of
    /// admissions equal to the count of recorded events.
    /// </remarks>
    [PostgresFact]
    public async Task Concurrent_consumes_never_exceed_the_limit()
    {
        var clock = FeedbackTestData.Clock();
        using var barrier = new Barrier(6);

        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            await using var db = _harness.NewContext();
            var limiter = _harness.NewRateLimiter(db, clock);
            barrier.SignalAndWait();
            return await limiter.TryConsumeAsync(Owner, FeedbackRateKind.Submit);
        })).ToArray();

        var decisions = await Task.WhenAll(tasks);

        decisions.Count(d => d.Allowed).Should().Be(
            1,
            "the cooldown admits one submission at a single instant, whatever the concurrency");

        await using var check = _harness.NewContext();
        var window = await check.FeedbackRateWindows.AsNoTracking()
            .SingleAsync(w => w.UserProfileId == Owner && w.Kind == FeedbackRateKind.Submit);

        FeedbackRateLimiter.Parse(window.RecentTicksCsv).Should().ContainSingle(
            "admissions and recorded events must be the same number");
    }

    // -------------------------------------------------------------------------- window state

    /// <summary>Stamps outside the window are pruned rather than accumulating.</summary>
    [PostgresFact]
    public async Task Aged_out_stamps_are_pruned_from_the_stored_window()
    {
        var clock = FeedbackTestData.Clock();

        for (var i = 0; i < 5; i++)
        {
            (await ConsumeAsync(FeedbackRateKind.Preview, Owner, clock)).Allowed.Should().BeTrue();
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        clock.Advance(TimeSpan.FromHours(2));
        (await ConsumeAsync(FeedbackRateKind.Preview, Owner, clock)).Allowed.Should().BeTrue();

        await using var check = _harness.NewContext();
        var window = await check.FeedbackRateWindows.AsNoTracking()
            .SingleAsync(w => w.UserProfileId == Owner && w.Kind == FeedbackRateKind.Preview);

        FeedbackRateLimiter.Parse(window.RecentTicksCsv).Should().ContainSingle(
            "the column is bounded by the limit, not by the account's lifetime");
    }

    /// <summary>A stamp from a skewed clock cannot hold the window open indefinitely.</summary>
    [PostgresFact]
    public async Task A_future_dated_stamp_is_discarded_rather_than_trusted()
    {
        var clock = FeedbackTestData.Clock();
        var future = clock.GetUtcNow().AddDays(30).UtcDateTime.Ticks;

        await using (var seed = _harness.NewContext())
        {
            seed.FeedbackRateWindows.Add(new FeedbackRateWindow
            {
                UserProfileId = Owner,
                Kind = FeedbackRateKind.Submit,
                RecentTicksCsv = string.Join(',', Enumerable.Repeat(future, 3)),
                UpdatedAtUtc = clock.GetUtcNow().UtcDateTime,
                Version = 1
            });
            await seed.SaveChangesAsync();
        }

        (await ConsumeAsync(FeedbackRateKind.Submit, Owner, clock))
            .Allowed.Should().BeTrue("a stamp ahead of now is not evidence of a past event");
    }

    private async Task<FeedbackRateDecision> ConsumeAsync(
        FeedbackRateKind kind, string owner, TestTimeProvider clock)
    {
        // A fresh context each time, so nothing is carried between calls in a change tracker.
        await using var db = _harness.NewContext();
        var limiter = _harness.NewRateLimiter(db, clock);
        return await limiter.TryConsumeAsync(owner, kind);
    }
}

/// <summary>
/// The Retry-After the endpoint actually puts on the wire.
/// </summary>
public sealed class FeedbackRetryAfterHeaderPostgresTests : IAsyncLifetime
{
    private const string Owner = "user-feedback-retryafter";

    private FeedbackPostgresHarness _harness = null!;
    private FeedbackApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await FeedbackPostgresHarness.CreateAsync("retryafter");
        _factory = new FeedbackApiFactory(_harness.ConnectionString);

        // Two previews an hour, so the third request is refused without a long test.
        _factory.Settings["Feedback:MaxPreviewsPerWindow"] = "2";
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    /// <summary>
    /// A refused preview answers 429 with a Retry-After header a client can act on.
    /// </summary>
    /// <remarks>
    /// The header, specifically. A wait mentioned only in the problem detail is invisible to every
    /// HTTP client's built-in retry handling, so it is advice nobody takes.
    /// </remarks>
    [PostgresFact]
    public async Task A_refused_preview_returns_429_with_a_truthful_retry_after_header()
    {
        using var client = _factory.CreateClientFor(Owner);

        for (var i = 0; i < 2; i++)
        {
            var ok = await client.PostAsJsonAsync("/api/v1/feedback/preview",
                new FeedbackRequest { Description = $"Report {i}." });
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var refused = await client.PostAsJsonAsync("/api/v1/feedback/preview",
            new FeedbackRequest { Description = "One too many." });

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        refused.Headers.RetryAfter.Should().NotBeNull("a limit without a wait is not actionable");
        var delta = refused.Headers.RetryAfter!.Delta;
        delta.Should().NotBeNull();
        delta!.Value.Should().BeGreaterThan(TimeSpan.Zero);
        delta.Value.Should().BeLessThanOrEqualTo(
            TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1),
            "the wait can never exceed the window that produced it");
    }

    /// <summary>A refused preview does not spend an AI call or mint a token.</summary>
    [PostgresFact]
    public async Task A_refused_preview_issues_no_token()
    {
        using var client = _factory.CreateClientFor(Owner);

        for (var i = 0; i < 2; i++)
        {
            await client.PostAsJsonAsync("/api/v1/feedback/preview",
                new FeedbackRequest { Description = $"Report {i}." });
        }

        var refused = await client.PostAsJsonAsync("/api/v1/feedback/preview",
            new FeedbackRequest { Description = "One too many." });

        var body = await refused.Content.ReadAsStringAsync();
        body.Should().NotContain("previewToken");
    }
}
