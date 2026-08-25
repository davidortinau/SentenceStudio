using System.Net;
using SentenceStudio.WebApp.Operator;
using SentenceStudio.WebApp.Tests.Infrastructure;

namespace SentenceStudio.WebApp.Tests.Operator;

/// <summary>
/// What the typed client makes of an operator answer, and which sentence each outcome earns.
/// </summary>
/// <remarks>
/// The component tests in <c>SentenceStudio.UI.Tests</c> prove where each message is rendered.
/// These prove the layer underneath over a real HTTP round trip: that a refused reveal arrives as
/// <see cref="SamOpportunityClientStatus.NotAvailable"/> rather than an exception, and that the
/// two notice scopes disagree about it on purpose — the surface is fine, the row is not.
/// </remarks>
public sealed class SamOpportunityEvidenceNoticeTests : IAsyncLifetime
{
    private StubOperatorApi _api = default!;
    private SamOpportunityOperatorClient _client = default!;
    private HttpClient _http = default!;

    public async Task InitializeAsync()
    {
        _api = await StubOperatorApi.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_api.BaseAddress) };
        _client = new SamOpportunityOperatorClient(
            _http, Microsoft.Extensions.Logging.Abstractions.NullLogger<SamOpportunityOperatorClient>.Instance);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _api.DisposeAsync();
    }

    [Fact]
    public async Task ARefusedRevealArrivesAsNotAvailableRatherThanAnException()
    {
        // The stub maps every unmapped route — which is what /evidence is — to 404, the same
        // answer the API gives for a cross-owner refusal and for a row that does not exist.
        var result = await _client.RevealEvidenceAsync("opp-does-not-exist");

        result.IsOk.Should().BeFalse();
        result.Status.Should().Be(
            SamOpportunityClientStatus.NotAvailable,
            "a refusal is normal traffic on a surface designed to be indistinguishable from "
            + "absent, so the client reports it rather than throwing");
    }

    [Fact]
    public void ARefusedRevealIsRowScopedAndNotSurfaceScoped()
    {
        var evidence = SamOpportunityNotices.Evidence(SamOpportunityClientStatus.NotAvailable);
        var surface = SamOpportunityNotices.Surface(SamOpportunityClientStatus.NotAvailable);

        evidence.Should().Be(SamOpportunityNotices.EvidenceUnavailable);
        surface.Should().Be(SamOpportunityNotices.SurfaceUnavailable);

        evidence.Should().NotBe(surface,
            "the same status means different things depending on which call returned it: the "
            + "rollup being refused is a statement about the caller, one row's evidence being "
            + "refused is a statement about that row");
    }

    [Fact]
    public void TheEvidenceMessageNeverClaimsTheSurfaceIsUnavailable()
    {
        var statuses = Enum.GetValues<SamOpportunityClientStatus>();

        foreach (var status in statuses)
        {
            SamOpportunityNotices.Evidence(status)
                .Should().NotBe(SamOpportunityNotices.SurfaceUnavailable,
                    "no reveal outcome may be worded as the whole surface being unavailable; "
                    + $"'{status}' was");
        }
    }

    [Fact]
    public void EveryRefusedRevealOutcomeSaysSomething()
    {
        foreach (var status in Enum.GetValues<SamOpportunityClientStatus>())
        {
            var message = SamOpportunityNotices.Evidence(status);

            if (status == SamOpportunityClientStatus.Success)
            {
                message.Should().BeNull("a successful reveal has nothing to explain");
                continue;
            }

            message.Should().NotBeNullOrWhiteSpace(
                $"'{status}' would otherwise fail the reveal silently");
        }
    }

    /// <summary>A working reveal leaves no message behind at either scope.</summary>
    [Fact]
    public void ASuccessfulRevealProducesNoNotice()
    {
        SamOpportunityNotices.Evidence(SamOpportunityClientStatus.Success).Should().BeNull();
        SamOpportunityNotices.Surface(SamOpportunityClientStatus.Success).Should().BeNull();
    }
}
