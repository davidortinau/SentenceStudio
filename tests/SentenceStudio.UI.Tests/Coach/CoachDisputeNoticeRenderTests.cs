using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The dispute notice: the receipt a learner gets for correcting the coach.
/// </summary>
/// <remarks>
/// <para>
/// The defect is not that the coach was wrong. It is that a learner who corrected it had no way to
/// tell whether the correction landed — Case D repeated a disputed list more confidently, and from
/// the learner's side that is indistinguishable from not having spoken. Everything asserted here is
/// about a signal being present and true, or absent.
/// </para>
/// <para>
/// Host parity is asserted by rendering under both base URIs. The notice reads neither the base URI
/// nor the navigation manager, so parity is a property of the file; these tests are what stop it
/// quietly becoming a property of the current implementation.
/// </para>
/// </remarks>
public sealed class CoachDisputeNoticeRenderTests
{
    private const string WebBaseUri = "https://sentencestudio.example/";
    private const string WebViewBaseUri = "app://0.0.0.0/";
    private const string MessageId = "3f1c9a44-0d3e-4c1b-9a5e-77b2c1d0e912";

    private static CoachDisputeDto Open(
        CoachDisputeSignal signal = CoachDisputeSignal.DifferentCohort) => new()
        {
            Signal = signal,
            Status = CoachDisputeStatus.Open,
            DisputedMessageId = MessageId
        };

    // ── Both hosts ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WebBaseUri)]
    [InlineData(WebViewBaseUri)]
    public async Task An_open_dispute_renders_in_both_hosts(string baseUri)
    {
        var html = await RenderAsync(Open(), baseUri: baseUri);

        html.Should().Contain("data-coach-dispute=\"Open\"");
        html.Should().Contain("data-coach-dispute-signal=\"DifferentCohort\"");
        html.Should().Contain("Noted");
        html.Should().Contain("You named a different set of words.");
    }

    [Fact]
    public async Task Both_hosts_render_identical_markup()
    {
        foreach (var dispute in new[]
                 {
                     Open(),
                     new CoachDisputeDto
                     {
                         Signal = CoachDisputeSignal.WrongClaim,
                         Status = CoachDisputeStatus.ResolvedByReRead,
                         DisputedMessageId = MessageId
                     }
                 })
        {
            var web = await RenderAsync(dispute, baseUri: WebBaseUri);
            var webView = await RenderAsync(dispute, baseUri: WebViewBaseUri);

            web.Should().NotBeEmpty();
            webView.Should().Be(
                web,
                "the notice is host-agnostic; a divergence means somebody added a platform branch "
                + "to a surface whose whole job is telling the learner they were heard");
        }
    }

    // ── Each state renders its own line ──────────────────────────────────────

    [Theory]
    [InlineData(CoachDisputeStatus.Open, "Noted")]
    [InlineData(CoachDisputeStatus.ResolvedByReRead, "The coach looked again.")]
    [InlineData(CoachDisputeStatus.ResolvedByCorrection, "The coach corrected itself.")]
    [InlineData(CoachDisputeStatus.ResolvedByLimitation, "couldn\u2019t tell you")]
    [InlineData(CoachDisputeStatus.DismissedByLearner, "You dismissed this.")]
    public async Task Every_status_renders_its_own_line(CoachDisputeStatus status, string expected)
    {
        var html = await RenderAsync(new CoachDisputeDto
        {
            Signal = CoachDisputeSignal.WrongClaim,
            Status = status,
            DisputedMessageId = MessageId
        });

        html.Should().Contain(expected);
        html.Should().Contain($"data-coach-dispute=\"{status}\"");
    }

    [Theory]
    [InlineData(CoachDisputeSignal.MeantSomethingElse, "You said you meant something else.")]
    [InlineData(CoachDisputeSignal.NotWhatIAsked, "You said this wasn\u2019t what you asked.")]
    [InlineData(CoachDisputeSignal.WrongClaim, "You said this was wrong.")]
    [InlineData(CoachDisputeSignal.DifferentCohort, "You named a different set of words.")]
    public async Task Every_signal_renders_its_own_line(CoachDisputeSignal signal, string expected)
    {
        var html = await RenderAsync(Open(signal));

        html.Should().Contain(expected);
    }

    // ── Neutral on unknown ───────────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_status_renders_nothing()
    {
        var html = await RenderAsync(new CoachDisputeDto
        {
            Signal = CoachDisputeSignal.WrongClaim,
            Status = CoachDisputeStatus.Unknown
        });

        html.Trim().Should().BeEmpty(
            "showing an unresolved dispute the server never reported is its own lie, and guessing "
            + "a resolution would tell the learner their correction was handled");
    }

    [Fact]
    public async Task An_unknown_signal_renders_the_state_without_guessing_the_cause()
    {
        var html = await RenderAsync(new CoachDisputeDto
        {
            Signal = CoachDisputeSignal.Unknown,
            Status = CoachDisputeStatus.Open,
            DisputedMessageId = MessageId
        });

        html.Should().Contain("Noted", "the state line is true whichever correction was made");
        html.Should().NotContain(
            "You said this was wrong.",
            "naming a specific correction would claim the coach understood it more precisely than "
            + "it did");
    }

    [Fact]
    public async Task A_null_dispute_renders_nothing()
    {
        (await RenderAsync(null)).Trim().Should().BeEmpty();
    }

    // ── Content and accessibility ────────────────────────────────────────────

    /// <summary>
    /// The notice carries no learner text, because the DTO has nowhere to put any.
    /// </summary>
    [Fact]
    public async Task The_notice_renders_no_learner_text()
    {
        var html = await RenderAsync(Open());

        html.Should().NotContain(
            MessageId,
            "the identifier anchors the notice in code; rendering it would show the learner a GUID");
        html.Should().NotContain("I meant");
    }

    /// <summary>
    /// Status, not alert. A dispute is a state change the learner made deliberately.
    /// </summary>
    [Fact]
    public async Task The_notice_is_a_polite_live_region()
    {
        var html = await RenderAsync(Open());

        html.Should().Contain("role=\"status\"");
        html.Should().Contain("aria-live=\"polite\"");
        html.Should().NotContain(
            "role=\"alert\"",
            "an assertive region cuts across the answer the learner is reading, and they already "
            + "know they typed a correction");
        html.Should().Contain("aria-label=\"Correction status\"");
    }

    /// <summary>Internal codes stay in data attributes, never in prose or a class name.</summary>
    [Fact]
    public async Task No_internal_code_reaches_the_learner_facing_text()
    {
        var html = await RenderAsync(new CoachDisputeDto
        {
            Signal = CoachDisputeSignal.DifferentCohort,
            Status = CoachDisputeStatus.ResolvedByReRead,
            DisputedMessageId = MessageId
        });

        html.Should().Contain("data-coach-dispute=\"ResolvedByReRead\"", "diagnosable for a developer");
        html.Should().NotContain(
            "class=\"coach-dispute ResolvedByReRead",
            "the learner is not the audience for an enum member name");
        html.Should().Contain("coach-dispute-closed");
    }

    [Fact]
    public async Task The_dismiss_control_is_absent_when_no_handler_is_wired()
    {
        var html = await RenderAsync(Open());

        html.Should().NotContain(
            "coach-dispute-dismiss",
            "a host that has not wired dismissal must not show a button that does nothing");
    }

    [Fact]
    public async Task A_closed_dispute_offers_no_dismissal()
    {
        var html = await RenderAsync(
            new CoachDisputeDto
            {
                Signal = CoachDisputeSignal.WrongClaim,
                Status = CoachDisputeStatus.ResolvedByCorrection,
                DisputedMessageId = MessageId
            },
            withDismissHandler: true);

        html.Should().NotContain("coach-dispute-dismiss");
    }

    [Fact]
    public async Task An_open_dispute_offers_dismissal_when_a_handler_is_wired()
    {
        var html = await RenderAsync(Open(), withDismissHandler: true);

        html.Should().Contain("coach-dispute-dismiss");
        html.Should().Contain("Dismiss");
    }

    // ── Localization ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Korean_renders_translated_copy_with_the_same_structure()
    {
        var english = await RenderAsync(Open(), culture: "en");
        var korean = await RenderAsync(Open(), culture: "ko");

        korean.Should().Contain("data-coach-dispute=\"Open\"");
        korean.Should().Contain("data-coach-dispute-signal=\"DifferentCohort\"");

        korean.Should().NotBe(english, "the Korean notice must be translated, not fall back");
        korean.Should().NotContain(
            "You named a different set of words.",
            "a Korean learner reading an English receipt cannot tell whether they were understood");
        korean.Should().Contain("\uB2E4\uB978 \uB2E8\uC5B4 \uBAA9\uB85D");
    }

    [Fact]
    public async Task The_korean_accessible_label_is_translated()
    {
        var korean = await RenderAsync(Open(), culture: "ko");

        korean.Should().NotContain(
            "aria-label=\"Correction status\"",
            "a screen-reader label that falls back to English is the least visible localization "
            + "gap and the one that matters most to the learner who needs it");
    }

    private static async Task<string> RenderAsync(
        CoachDisputeDto? dispute,
        string culture = "en",
        string baseUri = WebBaseUri,
        bool withDismissHandler = false)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<BlazorLocalizationService>();
            services.AddSingleton<NavigationManager>(new StubNavigationManager(baseUri));

            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var parameters = new Dictionary<string, object?>
                {
                    [nameof(CoachDisputeNotice.Dispute)] = dispute
                };

                if (withDismissHandler)
                {
                    parameters[nameof(CoachDisputeNotice.OnDismiss)] =
                        EventCallback.Factory.Create(new object(), () => { });
                }

                var output = await renderer.RenderComponentAsync<CoachDisputeNotice>(
                    ParameterView.FromDictionary(parameters));

                return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
            });
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>
    /// Supplies the base URI the two hosts are distinguished by, so a notice that read it would
    /// render differently and the parity test would catch it.
    /// </summary>
    private sealed class StubNavigationManager : NavigationManager
    {
        public StubNavigationManager(string baseUri) => Initialize(baseUri, baseUri);

        protected override void NavigateToCore(string uri, bool forceLoad) =>
            throw new InvalidOperationException("The dispute notice never navigates.");
    }
}
