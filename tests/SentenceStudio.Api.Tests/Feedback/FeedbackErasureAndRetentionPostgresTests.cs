using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api.Tests.Feedback;

/// <summary>
/// Erasure and retention for the two user-scoped feedback tables.
/// </summary>
public sealed class FeedbackErasureAndRetentionPostgresTests : IAsyncLifetime
{
    private const string Owner = "user-feedback-erasure";
    private const string Neighbour = "user-feedback-neighbour";

    private FeedbackPostgresHarness _harness = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await FeedbackPostgresHarness.CreateAsync("erasure");
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    // -------------------------------------------------------------------------- erasure

    /// <summary>Erasure removes every row the learner owns, in both tables.</summary>
    /// <remarks>
    /// The rate window is included deliberately, even though removing it hands the account a fresh
    /// allowance. The account is being destroyed, so there is nobody left to hand an allowance to,
    /// and a row keyed on a profile id that no longer resolves is exactly the orphan an erasure
    /// request exists to prevent.
    /// </remarks>
    [PostgresFact]
    public async Task Erasure_removes_every_row_the_learner_owns()
    {
        await SeedSubmissionsAsync(Owner, 3);
        await SeedWindowAsync(Owner, FeedbackRateKind.Preview);
        await SeedWindowAsync(Owner, FeedbackRateKind.Submit);

        await using var db = _harness.NewContext();
        var report = await _harness.NewDeletionService(db).DeleteAllForOwnerAsync(Owner);

        report.Succeeded.Should().BeTrue();
        report.RowsDeleted.Should().Be(5);

        await using var check = _harness.NewContext();
        (await check.FeedbackSubmissions.CountAsync(s => s.UserProfileId == Owner)).Should().Be(0);
        (await check.FeedbackRateWindows.CountAsync(w => w.UserProfileId == Owner)).Should().Be(0);
    }

    /// <summary>Erasure never reaches another learner's rows.</summary>
    [PostgresFact]
    public async Task Erasure_leaves_other_learners_untouched()
    {
        await SeedSubmissionsAsync(Owner, 2);
        await SeedSubmissionsAsync(Neighbour, 2);
        await SeedWindowAsync(Neighbour, FeedbackRateKind.Submit);

        await using var db = _harness.NewContext();
        await _harness.NewDeletionService(db).DeleteAllForOwnerAsync(Owner);

        await using var check = _harness.NewContext();
        (await check.FeedbackSubmissions.CountAsync(s => s.UserProfileId == Neighbour)).Should().Be(2);
        (await check.FeedbackRateWindows.CountAsync(w => w.UserProfileId == Neighbour)).Should().Be(1);
    }

    /// <summary>
    /// An owner-less erasure deletes nothing rather than everything.
    /// </summary>
    /// <remarks>
    /// The filter would be vacuous with an empty scope, and a vacuous delete on a table like this
    /// takes every learner's rows. This is the same empty-scope rule the repositories follow, tested
    /// on the path where getting it wrong is unrecoverable.
    /// </remarks>
    [PostgresFact]
    public async Task An_owner_less_erasure_deletes_nothing()
    {
        await SeedSubmissionsAsync(Owner, 2);
        await SeedSubmissionsAsync(Neighbour, 2);

        await using var db = _harness.NewContext();
        var report = await _harness.NewDeletionService(db).DeleteAllForOwnerAsync(string.Empty);

        report.Succeeded.Should().BeFalse();
        report.RowsDeleted.Should().Be(0);
        report.FailureCode.Should().Be("no_owner");

        await using var check = _harness.NewContext();
        (await check.FeedbackSubmissions.CountAsync()).Should().Be(4);
    }

    /// <summary>Erasure is idempotent, so a retry after a partial failure finishes the job.</summary>
    [PostgresFact]
    public async Task Erasure_run_twice_succeeds_with_nothing_left_to_do()
    {
        await SeedSubmissionsAsync(Owner, 2);

        await using var db = _harness.NewContext();
        var deletion = _harness.NewDeletionService(db);

        (await deletion.DeleteAllForOwnerAsync(Owner)).Succeeded.Should().BeTrue();

        var second = await deletion.DeleteAllForOwnerAsync(Owner);
        second.Succeeded.Should().BeTrue();
        second.RowsDeleted.Should().Be(0);
    }

    /// <summary>An in-doubt row is erased too, not preserved for reconciliation.</summary>
    /// <remarks>
    /// Reconciliation is an operator convenience; erasure is the learner's right. What survives is
    /// the public GitHub issue, which the app cannot delete and which the learner asked for on
    /// screen — but nothing in our database links them to it afterwards.
    /// </remarks>
    [PostgresFact]
    public async Task Erasure_removes_in_doubt_rows_as_well()
    {
        await SeedSubmissionAsync(Owner, FeedbackSubmissionStatus.Committed);
        await SeedSubmissionAsync(Owner, FeedbackSubmissionStatus.Claimed);

        await using var db = _harness.NewContext();
        (await _harness.NewDeletionService(db).DeleteAllForOwnerAsync(Owner))
            .Succeeded.Should().BeTrue();

        await using var check = _harness.NewContext();
        (await check.FeedbackSubmissions.CountAsync(s => s.UserProfileId == Owner)).Should().Be(0);
    }

    // ------------------------------------------------------------------------ retention

    /// <summary>
    /// Retention prunes on token expiry, so a row is never removed while its token could still be
    /// presented.
    /// </summary>
    /// <remarks>
    /// The dangerous alternative is pruning on creation time. It looks equivalent and is not: it
    /// opens a window in which a live token's ledger entry is gone, and the exactly-once guarantee
    /// quietly lapses for exactly the tokens still in flight.
    /// </remarks>
    [PostgresFact]
    public async Task Retention_prunes_on_token_expiry_not_on_creation()
    {
        var clock = FeedbackTestData.Clock();
        var now = clock.GetUtcNow().UtcDateTime;

        await using (var seed = _harness.NewContext())
        {
            // Created long ago, but its token expires far in the future: must survive.
            seed.FeedbackSubmissions.Add(Row("old-created", now.AddDays(-400), now.AddDays(400)));

            // Expired ninety-one days ago: removable.
            seed.FeedbackSubmissions.Add(Row("aged-out", now.AddDays(-100), now.AddDays(-91)));

            // Expired yesterday: inside retention, must survive.
            seed.FeedbackSubmissions.Add(Row("recent", now.AddDays(-2), now.AddDays(-1)));

            await seed.SaveChangesAsync();
        }

        await using var db = _harness.NewContext();
        var result = await _harness.NewRetentionSweep(db, clock).RunAsync();

        result.SubmissionsRemoved.Should().Be(1);

        await using var check = _harness.NewContext();
        var remaining = await check.FeedbackSubmissions.AsNoTracking()
            .Select(s => s.Jti).ToListAsync();

        remaining.Should().BeEquivalentTo(["old-created", "recent"]);

        FeedbackSubmission Row(string jti, DateTime created, DateTime expires) => new()
        {
            Jti = jti,
            UserProfileId = Owner,
            Status = FeedbackSubmissionStatus.Submitted,
            ContentDigest = "digest",
            IssueNumber = 1,
            IssueUrl = "https://github.com/davidortinau/SentenceStudio/issues/1",
            IssueTitle = "Filed",
            RouteCategory = FeedbackRouteCategory.Activity,
            Platform = FeedbackPlatform.Web,
            AppVersion = "1.2.3",
            CreatedAtUtc = created,
            UpdatedAtUtc = created,
            TokenExpiresAtUtc = expires,
            Version = 1
        };
    }

    /// <summary>
    /// A rate window is only pruned once every stamp it could hold has aged out.
    /// </summary>
    /// <remarks>
    /// Deleting one early hands its owner a fresh allowance, which turns a retention job into a
    /// rate-limit bypass that runs on a timer.
    /// </remarks>
    [PostgresFact]
    public async Task Retention_does_not_prune_a_rate_window_that_could_still_be_holding_a_limit()
    {
        var clock = FeedbackTestData.Clock();
        var now = clock.GetUtcNow().UtcDateTime;

        await using (var seed = _harness.NewContext())
        {
            seed.FeedbackRateWindows.Add(new FeedbackRateWindow
            {
                UserProfileId = Owner,
                Kind = FeedbackRateKind.Submit,
                RecentTicksCsv = now.AddHours(-1).Ticks.ToString(),
                UpdatedAtUtc = now.AddHours(-1),
                Version = 1
            });

            seed.FeedbackRateWindows.Add(new FeedbackRateWindow
            {
                UserProfileId = Neighbour,
                Kind = FeedbackRateKind.Submit,
                RecentTicksCsv = string.Empty,
                UpdatedAtUtc = now.AddDays(-30),
                Version = 1
            });

            await seed.SaveChangesAsync();
        }

        await using var db = _harness.NewContext();
        var result = await _harness.NewRetentionSweep(db, clock).RunAsync();

        result.RateWindowsRemoved.Should().Be(1);

        await using var check = _harness.NewContext();
        var remaining = await check.FeedbackRateWindows.AsNoTracking()
            .Select(w => w.UserProfileId).ToListAsync();

        remaining.Should().BeEquivalentTo([Owner]);
    }

    /// <summary>Concurrent sweeps are harmless, which is why there is no lease.</summary>
    [PostgresFact]
    public async Task Concurrent_retention_sweeps_are_safe()
    {
        var clock = FeedbackTestData.Clock();
        var now = clock.GetUtcNow().UtcDateTime;

        await using (var seed = _harness.NewContext())
        {
            for (var i = 0; i < 20; i++)
            {
                seed.FeedbackSubmissions.Add(new FeedbackSubmission
                {
                    Jti = $"sweep-{i}",
                    UserProfileId = Owner,
                    Status = FeedbackSubmissionStatus.Submitted,
                    ContentDigest = "digest",
                    RouteCategory = FeedbackRouteCategory.Unknown,
                    Platform = FeedbackPlatform.Unknown,
                    AppVersion = "1.0.0",
                    CreatedAtUtc = now.AddDays(-200),
                    UpdatedAtUtc = now.AddDays(-200),
                    TokenExpiresAtUtc = now.AddDays(-199),
                    Version = 1
                });
            }

            await seed.SaveChangesAsync();
        }

        using var barrier = new Barrier(3);
        var tasks = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            await using var db = _harness.NewContext();
            var sweep = _harness.NewRetentionSweep(db, clock);
            barrier.SignalAndWait();
            return await sweep.RunAsync();
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        results.Sum(r => r.SubmissionsRemoved).Should().Be(
            20, "each row is deleted exactly once, whichever sweep gets to it");

        await using var check = _harness.NewContext();
        (await check.FeedbackSubmissions.CountAsync()).Should().Be(0);
    }

    // -------------------------------------------------------------------------- helpers

    private Task SeedSubmissionsAsync(string owner, int count) =>
        SeedManyAsync(owner, count, FeedbackSubmissionStatus.Submitted);

    private Task SeedSubmissionAsync(string owner, FeedbackSubmissionStatus status) =>
        SeedManyAsync(owner, 1, status);

    private async Task SeedManyAsync(string owner, int count, FeedbackSubmissionStatus status)
    {
        await using var seed = _harness.NewContext();
        var now = DateTime.UtcNow;

        for (var i = 0; i < count; i++)
        {
            seed.FeedbackSubmissions.Add(new FeedbackSubmission
            {
                Jti = $"{owner}-{status}-{i}-{Guid.NewGuid():N}"[..40],
                UserProfileId = owner,
                Status = status,
                ContentDigest = "digest",
                RouteCategory = FeedbackRouteCategory.Activity,
                Platform = FeedbackPlatform.Web,
                AppVersion = "1.2.3",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                TokenExpiresAtUtc = now.AddMinutes(10),
                Version = 1
            });
        }

        await seed.SaveChangesAsync();
    }

    private async Task SeedWindowAsync(string owner, FeedbackRateKind kind)
    {
        await using var seed = _harness.NewContext();
        seed.FeedbackRateWindows.Add(new FeedbackRateWindow
        {
            UserProfileId = owner,
            Kind = kind,
            RecentTicksCsv = DateTime.UtcNow.Ticks.ToString(),
            UpdatedAtUtc = DateTime.UtcNow,
            Version = 1
        });
        await seed.SaveChangesAsync();
    }
}
