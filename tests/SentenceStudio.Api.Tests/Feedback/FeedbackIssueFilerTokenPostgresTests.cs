using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Feedback;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api.Tests.Feedback;

/// <summary>
/// A token problem happens before any issue request is sent, so the claim must close as "not
/// filed" — never sit in doubt, which would tell the learner to search GitHub for nothing.
/// </summary>
public sealed class FeedbackIssueFilerTokenPostgresTests : IAsyncLifetime
{
    private const string Owner = "user-filer-token";
    private FeedbackPostgresHarness _harness = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await FeedbackPostgresHarness.CreateAsync("filertoken");
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    private static FeedbackPreviewPayload Payload(string jti)
    {
        var now = DateTimeOffset.UtcNow;
        return new FeedbackPreviewPayload(jti, "Title", "Body", ["bug"], "bug", Owner,
            FeedbackRouteCategory.Coach, FeedbackPlatform.Native, "1.0.0",
            now.ToUnixTimeSeconds(), now.AddMinutes(10).ToUnixTimeSeconds());
    }

    private (FeedbackIssueFiler Filer, FeedbackDbContext Db) NewFiler(IGitHubTokenProvider tokens)
    {
        var db = _harness.NewContext();
        var filer = new FeedbackIssueFiler(
            tokens,
            new NoHttp(),
            _harness.NewRateLimiter(db),
            _harness.NewLedger(db),
            NullLogger<FeedbackIssueFiler>.Instance);
        return (filer, db);
    }

    [PostgresTheory]
    [InlineData(GitHubTokenFailure.Unavailable, FeedbackFileOutcomeKind.GitHubUnavailable)]
    [InlineData(GitHubTokenFailure.Unauthorized, FeedbackFileOutcomeKind.GitHubRefused)]
    [InlineData(GitHubTokenFailure.Unconfigured, FeedbackFileOutcomeKind.Unconfigured)]
    public async Task A_token_failure_closes_the_claim_as_not_filed(GitHubTokenFailure failure, FeedbackFileOutcomeKind expected)
    {
        var jti = $"tok-{failure}".ToLowerInvariant();
        var (filer, db) = NewFiler(new FixedTokens(GitHubToken.Fail(failure)));
        await using (db)
        {
            var outcome = await filer.FileAsync(Payload(jti), Owner, CancellationToken.None);
            outcome.Kind.Should().Be(expected);
        }

        await using var check = _harness.NewContext();
        check.FeedbackSubmissions.Single(s => s.Jti == jti).Status.Should().Be(FeedbackSubmissionStatus.Failed,
            "nothing was sent, so the row is closed rather than left in doubt");
    }

    [PostgresFact]
    public async Task A_cancelled_token_fetch_still_closes_the_claim()
    {
        const string jti = "tok-cancelled";
        var (filer, db) = NewFiler(new ThrowingTokens(new OperationCanceledException()));
        await using (db)
        {
            var act = () => filer.FileAsync(Payload(jti), Owner, CancellationToken.None);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        await using var check = _harness.NewContext();
        check.FeedbackSubmissions.Single(s => s.Jti == jti).Status.Should().Be(FeedbackSubmissionStatus.Failed);
    }

    [PostgresFact]
    public async Task An_unexpected_token_exception_is_reported_as_unavailable_and_closes_the_claim()
    {
        const string jti = "tok-throws";
        var (filer, db) = NewFiler(new ThrowingTokens(new FormatException()));
        await using (db)
        {
            (await filer.FileAsync(Payload(jti), Owner, CancellationToken.None)).Kind
                .Should().Be(FeedbackFileOutcomeKind.GitHubUnavailable);
        }

        await using var check = _harness.NewContext();
        check.FeedbackSubmissions.Single(s => s.Jti == jti).Status.Should().Be(FeedbackSubmissionStatus.Failed);
    }

    private sealed class FixedTokens(GitHubToken token) : IGitHubTokenProvider
    {
        public Task<GitHubToken> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult(token);
        public void Invalidate(string token) { }
    }

    private sealed class ThrowingTokens(Exception ex) : IGitHubTokenProvider
    {
        public Task<GitHubToken> GetTokenAsync(CancellationToken cancellationToken) => Task.FromException<GitHubToken>(ex);
        public void Invalidate(string token) { }
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("No issue request may be sent when there is no token.");
    }
}
