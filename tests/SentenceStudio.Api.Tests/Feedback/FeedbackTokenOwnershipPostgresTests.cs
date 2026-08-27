using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Feedback;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api.Tests.Feedback;

/// <summary>
/// Who may redeem a preview token, and what happens to everyone else.
/// </summary>
public sealed class FeedbackTokenOwnershipPostgresTests : IAsyncLifetime
{
    private const string Owner = "user-feedback-owner";
    private const string Attacker = "user-feedback-attacker";

    private FeedbackPostgresHarness _harness = null!;
    private FeedbackApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await FeedbackPostgresHarness.CreateAsync("ownership");
        _factory = new FeedbackApiFactory(_harness.ConnectionString);
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
    /// A token presented by anybody other than its owner is refused, and files nothing.
    /// </summary>
    /// <remarks>
    /// The token is a bearer credential for one action, so a captured one — from a log, a shared
    /// screen, a proxy — must be useless to whoever captured it. The check is on the signed owner
    /// rather than on anything the caller supplies.
    /// </remarks>
    [PostgresFact]
    public async Task A_token_presented_by_another_learner_is_refused()
    {
        var token = await PreviewAsync(Owner, "The activity crashed.");

        using var attacker = _factory.CreateClientFor(Attacker);
        var response = await attacker.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = token });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.GitHub.Calls.Should().Be(0);

        await using var check = _harness.NewContext();
        (await check.FeedbackSubmissions.CountAsync()).Should().Be(
            0, "a refused token must not leave a ledger row that burns the owner's preview");
    }

    /// <summary>
    /// The rightful owner can still use a token somebody else tried to redeem.
    /// </summary>
    /// <remarks>
    /// If the failed attempt had claimed the jti, an attacker who merely observed a token could
    /// destroy it — a denial-of-service with no privilege at all. The refusal happens before any
    /// claim, so the owner is unaffected.
    /// </remarks>
    [PostgresFact]
    public async Task A_refused_attempt_does_not_burn_the_owners_token()
    {
        var token = await PreviewAsync(Owner, "The activity crashed.");

        using (var attacker = _factory.CreateClientFor(Attacker))
        {
            await attacker.PostAsJsonAsync(
                "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = token });
        }

        using var owner = _factory.CreateClientFor(Owner);
        var response = await owner.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = token });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.GitHub.Calls.Should().Be(1);
    }

    /// <summary>
    /// A wrong owner, an expired token, and a forged one all answer identically.
    /// </summary>
    /// <remarks>
    /// Distinguishable responses turn this route into an oracle: a caller holding a token they
    /// should not have could learn whether it is still live, and whose it is, by reading the
    /// difference. The status, the body, and the absence of a Retry-After header are all the same.
    /// </remarks>
    [PostgresFact]
    public async Task Every_token_refusal_looks_the_same_from_outside()
    {
        var live = await PreviewAsync(Owner, "A live report.");
        var forged = live[..^4] + "AAAA";

        using var attacker = _factory.CreateClientFor(Attacker);
        using var owner = _factory.CreateClientFor(Owner);

        var wrongOwner = await attacker.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = live });
        var tampered = await owner.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = forged });
        var garbage = await owner.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = "not-a-token" });

        var responses = new[] { wrongOwner, tampered, garbage };

        responses.Should().AllSatisfy(r => r.StatusCode.Should().Be(HttpStatusCode.BadRequest));

        var bodies = new List<string>();
        foreach (var response in responses)
        {
            bodies.Add(await response.Content.ReadAsStringAsync());
            response.Headers.RetryAfter.Should().BeNull();
        }

        bodies.Distinct(StringComparer.Ordinal).Should().ContainSingle(
            "the response must not tell a caller which kind of bad token they are holding");
    }

    /// <summary>An expired token is refused and files nothing.</summary>
    [PostgresFact]
    public async Task An_expired_token_is_refused()
    {
        using var factory = new FeedbackApiFactory(_harness.ConnectionString);
        factory.Settings["Feedback:TokenLifetime"] = "00:00:01";

        using var client = factory.CreateClientFor(Owner);

        var previewResponse = await client.PostAsJsonAsync("/api/v1/feedback/preview",
            new FeedbackRequest { Description = "Short-lived report.", FeedbackType = "bug" });
        var preview = (await previewResponse.Content.ReadFromJsonAsync<FeedbackPreviewResponse>())!;

        await Task.Delay(TimeSpan.FromSeconds(2));

        var submit = await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = preview.PreviewToken });

        submit.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.GitHub.Calls.Should().Be(0);
    }

    /// <summary>An unauthenticated caller reaches neither endpoint.</summary>
    [PostgresFact]
    public async Task Both_endpoints_require_authentication()
    {
        using var anonymous = _factory.CreateClient();

        var preview = await anonymous.PostAsJsonAsync("/api/v1/feedback/preview",
            new FeedbackRequest { Description = "Anonymous." });
        var submit = await anonymous.PostAsJsonAsync("/api/v1/feedback/submit",
            new FeedbackSubmitRequest { PreviewToken = "anything" });

        preview.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        submit.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.GitHub.Calls.Should().Be(0);
    }

    /// <summary>
    /// An authenticated caller with no profile claim is refused rather than treated as everybody.
    /// </summary>
    [PostgresFact]
    public async Task A_caller_without_a_profile_claim_is_refused()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                Infrastructure.TestJwtGenerator.GenerateToken(userProfileId: null));

        var preview = await client.PostAsJsonAsync("/api/v1/feedback/preview",
            new FeedbackRequest { Description = "No profile." });

        preview.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await using var check = _harness.NewContext();
        (await check.FeedbackRateWindows.CountAsync()).Should().Be(
            0, "an unowned caller must not create a window that would then be shared by everyone");
    }

    /// <summary>The description bounds are enforced before anything is spent.</summary>
    [PostgresTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_description_is_refused(string description)
    {
        using var client = _factory.CreateClientFor(Owner);

        var response = await client.PostAsJsonAsync("/api/v1/feedback/preview",
            new FeedbackRequest { Description = description });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [PostgresFact]
    public async Task An_over_long_description_is_refused()
    {
        using var client = _factory.CreateClientFor(Owner);

        var response = await client.PostAsJsonAsync("/api/v1/feedback/preview",
            new FeedbackRequest { Description = new string('x', 5001) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var check = _harness.NewContext();
        (await check.FeedbackRateWindows.CountAsync()).Should().Be(
            0, "a request refused for shape must not consume the caller's allowance");
    }

    /// <summary>
    /// The preview response body never contains the signing key or the GitHub credential.
    /// </summary>
    [PostgresFact]
    public async Task No_secret_appears_in_a_response()
    {
        using var client = _factory.CreateClientFor(Owner);

        var response = await client.PostAsJsonAsync("/api/v1/feedback/preview",
            new FeedbackRequest { Description = "A report." });

        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain("feedback-test-hmac-key");
        body.Should().NotContain("test-github-pat");
        body.Should().NotContain(Infrastructure.TestJwtGenerator.TestSigningKeyValue);
    }

    private async Task<string> PreviewAsync(string owner, string description)
    {
        using var client = _factory.CreateClientFor(owner);

        var response = await client.PostAsJsonAsync("/api/v1/feedback/preview",
            new FeedbackRequest { Description = description, FeedbackType = "bug" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await response.Content.ReadFromJsonAsync<FeedbackPreviewResponse>();
        return preview!.PreviewToken;
    }
}
