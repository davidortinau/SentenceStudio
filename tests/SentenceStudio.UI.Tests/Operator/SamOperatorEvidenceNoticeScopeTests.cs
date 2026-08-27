using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.UI.Tests.Coach;
using SentenceStudio.WebApp.Operator;
using SamOpportunitiesPage = SentenceStudio.WebApp.Components.Pages.Operator.SamOpportunities;

namespace SentenceStudio.UI.Tests.Operator;

/// <summary>
/// Which message the operator page shows, and where, when a call is refused.
/// </summary>
/// <remarks>
/// <para>
/// A refusal on one row is not a statement about the surface. The page used to answer a refused
/// evidence reveal by putting "The operator surface is not available for this caller" in the page
/// banner — while the rollup, the row list and the row's own fields were on screen and working.
/// That sent a reviewer looking for an access problem that did not exist.
/// </para>
/// <para>
/// These are component tests rather than assertions on a string table: they render the real page
/// into an interactive renderer, click the real buttons, and read the resulting markup, so the
/// scope of each message is proven by where it appears rather than by which method produced it.
/// </para>
/// </remarks>
public class SamOperatorEvidenceNoticeScopeTests
{
    private const string CapabilityCode = "referent_lost_after_offer";
    private const string RevealButton = "Reveal learner content";
    private const string RowsButton = "Reviewable rows";
    private const string DetailButton = "Detail";

    private const string RollupJson = """
    [{"fingerprint":"a1b2c3d4e5f6a7b8","kind":"AmbiguousFollowUp","disposition":"Product",
      "capabilityCode":"referent_lost_after_offer","toolName":null,"failureCode":null,
      "offerLink":"PriorCoachQuestion","totalOccurrences":1,"distinctLearners":1,"rowCount":1,
      "firstObservedAtUtc":"2026-08-20T00:00:00Z","lastObservedAtUtc":"2026-08-20T00:00:00Z",
      "statuses":["New"]}]
    """;

    private const string RowsJson = """
    {"items":[{"id":"opp-1","kind":"AmbiguousFollowUp","disposition":"Product",
      "surface":"TurnOutcome","capabilityCode":"referent_lost_after_offer","toolName":null,
      "riskClass":null,"failureCode":null,"stopReason":"Completed",
      "offerLink":"PriorCoachQuestion","fingerprint":"a1b2c3d4e5f6a7b8",
      "dedupBucketDate":"2026-08-20","occurrenceCount":1,
      "firstObservedAtUtc":"2026-08-20T00:00:00Z","lastObservedAtUtc":"2026-08-20T00:00:00Z",
      "status":"New","reviewedAtUtc":null,"reviewerNoteCode":null,"linkedSpecPath":null,
      "hasEvidence":true,"evidenceRevealCount":0,"evidenceLastRevealedAtUtc":null,
      "schemaVersion":1}],"total":1,"skip":0,"take":50}
    """;

    // ------------------------------------------------------- a refused reveal

    /// <summary>
    /// A cross-owner or unknown row answers 404, and only the detail card says so.
    /// </summary>
    /// <remarks>
    /// The server answers a cross-owner refusal with the same 404 it answers "no such row" with,
    /// so this is the shape both take. The assertions are deliberately on all three things at
    /// once: the evidence sentence appears, the surface sentence does not, and the row list is
    /// still rendered — because the bug was that the third stayed true while the page claimed
    /// otherwise.
    /// </remarks>
    [Fact]
    public async Task ARefusedEvidenceRevealScopesTheMessageToTheDetailAndLeavesTheListIntact()
    {
        using var harness = await OperatorPageHarness.OpenDetailAsync(
            evidenceStatus: HttpStatusCode.NotFound);

        await harness.ClickAsync(RevealButton);

        var markup = harness.Markup;

        markup.Should().Contain(SamOpportunityNotices.EvidenceUnavailable,
            "the reveal was refused, and that is what the reviewer needs to be told");
        markup.Should().NotContain(SamOpportunityNotices.SurfaceUnavailable,
            "the surface is demonstrably available — its own row list is on screen behind this "
            + "message");
        markup.Should().Contain(CapabilityCode,
            "the row list must survive a refused reveal");

        harness.Unhandled.Should().BeEmpty();
    }

    /// <summary>
    /// The refusal is worded identically whoever owns the row.
    /// </summary>
    /// <remarks>
    /// The API collapses "no such row" and "exists, not yours" into one 404 so the identifier
    /// space cannot be probed. Wording them differently in the browser would rebuild that oracle
    /// one layer up, so the collapse is asserted rather than assumed.
    /// </remarks>
    [Fact]
    public void ACrossOwnerRefusalAndAnUnknownRowReadIdentically() =>
        SamOpportunityNotices.Evidence(SamOpportunityClientStatus.CrossOwnerRefused)
            .Should().Be(
                SamOpportunityNotices.Evidence(SamOpportunityClientStatus.NotAvailable),
                "a distinguishable message would confirm that an identifier names a real row "
                + "owned by somebody else");

    // ------------------------------------------------------- stale error clearing

    /// <summary>Closing the detail card drops the refusal it was showing.</summary>
    [Fact]
    public async Task ClosingTheDetailClearsTheEvidenceMessage()
    {
        using var harness = await OperatorPageHarness.OpenDetailAsync(
            evidenceStatus: HttpStatusCode.NotFound);

        await harness.ClickAsync(RevealButton);
        harness.Markup.Should().Contain(SamOpportunityNotices.EvidenceUnavailable);

        await harness.ClickCloseAsync();

        harness.Markup.Should().NotContain(SamOpportunityNotices.EvidenceUnavailable,
            "the card that carried the message is gone; keeping it would attach one row's "
            + "refusal to whatever is opened next");
    }

    /// <summary>Switching view reloads the rows and drops the refusal.</summary>
    [Fact]
    public async Task SwitchingViewClearsTheEvidenceMessage()
    {
        using var harness = await OperatorPageHarness.OpenDetailAsync(
            evidenceStatus: HttpStatusCode.NotFound);

        await harness.ClickAsync(RevealButton);
        harness.Markup.Should().Contain(SamOpportunityNotices.EvidenceUnavailable);

        await harness.ClickAsync("Rollup");

        harness.Markup.Should().NotContain(SamOpportunityNotices.EvidenceUnavailable,
            "a reload re-reads the rows, so a refusal recorded against the previous read is "
            + "stale by definition");
    }

    /// <summary>A freshly loaded page shows no refusal from a previous one.</summary>
    /// <remarks>
    /// The browser-level equivalent of a tab reload: a new component instance over the same
    /// services and the same stub. Nothing about the previous instance's refusal may survive it.
    /// </remarks>
    [Fact]
    public async Task AReloadedPageShowsNoEvidenceMessage()
    {
        using var harness = await OperatorPageHarness.OpenDetailAsync(
            evidenceStatus: HttpStatusCode.NotFound);

        await harness.ClickAsync(RevealButton);
        harness.Markup.Should().Contain(SamOpportunityNotices.EvidenceUnavailable);

        var reloaded = await harness.ReloadAsync();

        reloaded.Should().NotContain(SamOpportunityNotices.EvidenceUnavailable);
        reloaded.Should().NotContain(SamOpportunityNotices.SurfaceUnavailable);
        reloaded.Should().Contain(CapabilityCode, "the reloaded page reads the rollup again");
    }

    // ------------------------------------------------------- the surface really being off

    /// <summary>
    /// When the operator API itself refuses, the banner still says the surface is unavailable.
    /// </summary>
    /// <remarks>
    /// The correction must not go too far the other way. A caller outside the cohort gets 404 on
    /// the rollup, and that genuinely is a statement about the surface — narrowing every message
    /// to the detail card would leave that reviewer with an empty page and no explanation.
    /// </remarks>
    [Fact]
    public async Task AnUnavailableOperatorApiStillUsesTheSurfaceMessage()
    {
        using var harness = await OperatorPageHarness.RenderAsync(
            rollupStatus: HttpStatusCode.NotFound);

        var markup = harness.Markup;

        markup.Should().Contain(SamOpportunityNotices.SurfaceUnavailable,
            "the rollup itself was refused, so the surface is what is unavailable");
        markup.Should().NotContain(SamOpportunityNotices.EvidenceUnavailable,
            "no reveal was attempted");
    }

    /// <summary>A 401 on the rollup reads the same as a 404, as it does on the wire.</summary>
    [Fact]
    public async Task AnUnauthorizedRollupAlsoUsesTheSurfaceMessage()
    {
        using var harness = await OperatorPageHarness.RenderAsync(
            rollupStatus: HttpStatusCode.Unauthorized);

        harness.Markup.Should().Contain(SamOpportunityNotices.SurfaceUnavailable);
    }

    // ------------------------------------------------------------------ harness

    /// <summary>
    /// The real page in an interactive renderer, over a canned operator API.
    /// </summary>
    private sealed class OperatorPageHarness : IDisposable
    {
        private readonly IServiceProvider _services;
        private readonly InteractiveTestRenderer _renderer;
        private int _componentId;

        private OperatorPageHarness(IServiceProvider services, InteractiveTestRenderer renderer)
        {
            _services = services;
            _renderer = renderer;
        }

        public string Markup => _renderer.RenderedText(_componentId);

        public IReadOnlyList<Exception> Unhandled => _renderer.Unhandled;

        public static async Task<OperatorPageHarness> RenderAsync(
            HttpStatusCode rollupStatus = HttpStatusCode.OK,
            HttpStatusCode listStatus = HttpStatusCode.OK,
            HttpStatusCode evidenceStatus = HttpStatusCode.OK)
        {
            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton<IWebHostEnvironment>(new DevelopmentEnvironment());
            services.AddSingleton(_ => new SamOpportunityOperatorClient(
                new HttpClient(new CannedOperatorApi(rollupStatus, listStatus, evidenceStatus))
                {
                    BaseAddress = new Uri("https://operator.test")
                },
                NullLogger<SamOpportunityOperatorClient>.Instance));

            var provider = services.BuildServiceProvider();
            var renderer = new InteractiveTestRenderer(provider, NullLoggerFactory.Instance);
            var harness = new OperatorPageHarness(provider, renderer);

            harness._componentId = await renderer.RenderAsync<SamOpportunitiesPage>();
            return harness;
        }

        /// <summary>Renders the page, switches to the row list, and opens the first row.</summary>
        public static async Task<OperatorPageHarness> OpenDetailAsync(
            HttpStatusCode evidenceStatus)
        {
            var harness = await RenderAsync(evidenceStatus: evidenceStatus);
            await harness.ClickAsync(RowsButton);
            await harness.ClickAsync(DetailButton);
            return harness;
        }

        public Task ClickAsync(string text) => _renderer.ClickButtonAsync(_componentId, text);

        /// <summary>Clicks the detail card's close button, which carries no text of its own.</summary>
        public Task ClickCloseAsync() => _renderer.ClickButtonByIdAsync(_componentId, "operator-detail-close");

        /// <summary>Renders a second instance over the same services, as a tab reload would.</summary>
        public async Task<string> ReloadAsync()
        {
            var reloadedId = await _renderer.RenderAsync<SamOpportunitiesPage>();
            return _renderer.RenderedText(reloadedId);
        }

        public void Dispose() => (_services as IDisposable)?.Dispose();
    }

    /// <summary>An operator API that answers from a script rather than a network.</summary>
    private sealed class CannedOperatorApi(
        HttpStatusCode rollupStatus,
        HttpStatusCode listStatus,
        HttpStatusCode evidenceStatus) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            var (status, body) = path.EndsWith("/evidence", StringComparison.Ordinal)
                ? (evidenceStatus, "{}")
                : path.EndsWith("/rollup", StringComparison.Ordinal)
                    ? (rollupStatus, RollupJson)
                    : (listStatus, RowsJson);

            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.OK)
            {
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            return Task.FromResult(response);
        }
    }

    /// <summary>The environment the page's third gate reads.</summary>
    private sealed class DevelopmentEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "SentenceStudio.WebApp";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
