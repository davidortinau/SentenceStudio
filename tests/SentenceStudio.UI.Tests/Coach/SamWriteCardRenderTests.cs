using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Sam;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Renders <see cref="SamWriteCard"/> to real HTML and counts what it offers.
/// </summary>
/// <remarks>
/// <para>
/// The companion state tests pin what the workspace will act on. These pin the markup, because
/// the dangerous version of every bug in this feature lives there: a state service that correctly
/// refuses to undo, above a template that still draws an Undo button, produces a learner who
/// believes their word is recoverable and a button that does nothing.
/// </para>
/// <para>
/// Uses ASP.NET Core's own <see cref="HtmlRenderer"/>, matching the existing coach render tests,
/// so no package is added.
/// </para>
/// </remarks>
public class SamWriteCardRenderTests
{
    private const string Conversation = "conv-1";
    private static readonly Regex ButtonTag = new("<button\\b", RegexOptions.Compiled);

    private static FakeCoachApiClient NewClient(bool writeAvailable = true)
    {
        var client = new FakeCoachApiClient
        {
            DurableHistoryAvailable = true,
            Availability = new CoachAvailabilityResponse
            {
                IsAvailable = true,
                State = CoachAvailabilityState.Available,
                CanEditPlan = true,
                IsDurableHistoryAvailable = true,
                IsSamOverlayAvailable = true,
                IsSamWriteAvailable = writeAvailable
            }
        };

        client.AddConversation(Conversation);
        return client;
    }

    private static async Task<(string Html, CoachWorkspaceState State)> RenderAsync(
        FakeCoachApiClient client,
        CoachWriteOperationDto write,
        Func<CoachWorkspaceState, Task>? act = null)
    {
        client.Seed(Conversation, CoachMessageRole.Coach, "I can do that.", writeOperation: write);

        var flags = new CoachFeatureFlags(client);
        await flags.EnsureLoadedAsync();

        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client, flags), flags);
        await state.RefreshAvailabilityAsync();
        await state.OpenConversationAsync(CoachPresentation.Overlay, Conversation);

        if (act is not null)
        {
            await act(state);
        }

        var current = state.Timeline
            .Select(entry => entry.WriteOperation)
            .First(candidate => candidate?.OperationId == write.OperationId)!;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        // The coach's name comes from the learner's study language, so every component that
        // names it needs the resolver. The all-optional constructor makes this a one-liner:
        // with no language source it answers with the default persona.
        services.AddScoped<CoachPersona>();
        services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());
        services.AddScoped(_ => state);

        await using var provider = services.BuildServiceProvider();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        await using var renderer = new HtmlRenderer(provider, loggerFactory);

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<SamWriteCard>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(SamWriteCard.Operation)] = current
                }));

            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });

        return (html, state);
    }

    // ---------------------------------------------------------------- proposed

    [Fact]
    public async Task A_reversible_proposal_offers_exactly_apply_and_decline()
    {
        var client = NewClient();
        var (html, _) = await RenderAsync(client, client.AddWrite(Conversation, "op-1"));

        ButtonTag.Matches(html).Count.Should().Be(2, "a proposal offers one approval and one decline");
        html.Should().Contain("sam-write-op-1-accept").And.Contain("sam-write-op-1-decline");
        html.Should().NotContain("sam-write-op-1-confirm");
        html.Should().NotContain("sam-write-op-1-undo");
    }

    [Fact]
    public async Task A_proposal_says_nothing_has_changed_yet()
    {
        var client = NewClient();
        var (html, _) = await RenderAsync(client, client.AddWrite(Conversation, "op-1"));

        html.Should().Contain("Nothing has changed yet.");
        html.Should().Contain("Waiting for you");
        html.Should().NotContain("Applied", "a proposal has not applied anything");
    }

    [Fact]
    public async Task A_protected_proposal_offers_review_rather_than_a_direct_apply()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1", requiresConfirmation: true, isReversible: false);

        var (html, _) = await RenderAsync(client, write);

        html.Should().Contain("sam-write-op-1-review");
        html.Should().NotContain("sam-write-op-1-accept",
            "a protected change must not be one press away");
        html.Should().Contain("This cannot be undone.");
    }

    [Fact]
    public async Task An_irreversible_proposal_says_so_before_it_is_approved()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1", requiresConfirmation: true, isReversible: false);

        var (html, _) = await RenderAsync(client, write);

        html.Should().Contain("This cannot be undone.");
    }

    [Fact]
    public async Task A_reversible_proposal_says_it_can_be_taken_back()
    {
        var client = NewClient();
        var (html, _) = await RenderAsync(client, client.AddWrite(Conversation, "op-1"));

        html.Should().Contain("You can undo this afterwards.");
    }

    // ---------------------------------------------------------------- confirmation step

    [Fact]
    public async Task The_confirmation_step_is_an_alert_dialog_that_does_not_trap_focus()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1", requiresConfirmation: true, isReversible: false);

        var (html, _) = await RenderAsync(
            client, write, state => state.BeginWriteConfirmationAsync("op-1"));

        html.Should().Contain("role=\"alertdialog\"");
        html.Should().Contain("aria-modal=\"false\"",
            "the panel it lives in is deliberately non-modal, and claiming otherwise would tell a "
            + "screen reader the page behind Sam is unreachable when it is not");
        html.Should().Contain("sam-write-op-1-confirm-step");
        html.Should().Contain("sam-write-op-1-confirm");
        html.Should().Contain("sam-write-op-1-confirm-cancel");
    }

    /// <summary>
    /// The one-use value must never reach the DOM: not as text, not as an attribute, not as an id.
    /// </summary>
    [Fact]
    public async Task The_confirmation_step_never_renders_the_value_it_holds()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1", requiresConfirmation: true, isReversible: false);

        var (html, _) = await RenderAsync(
            client, write, s => s.BeginWriteConfirmationAsync("op-1"));

        // The guard, read from the render rather than from the state afterwards. The confirm step
        // only appears while the step is open, so its presence proves the assertions below are
        // about a card that was genuinely confirming — and unlike the state object, the markup
        // cannot have moved on by the time it is read.
        html.Should().Contain("sam-write-op-1-confirm-step", "the step is genuinely open");
        html.Should().NotContain("one-use-op-1");
        html.Should().NotContain("confirmationSecret");
    }

    // ---------------------------------------------------------------- applied and undo

    [Fact]
    public async Task An_applied_change_shows_its_receipt_and_an_undo()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1");

        var (html, _) = await RenderAsync(client, write, state => state.AcceptWriteAsync("op-1"));

        html.Should().Contain("Applied");
        html.Should().Contain("sam-write-op-1-undo");
        html.Should().NotContain("sam-write-op-1-accept", "there is nothing left to approve");
    }

    [Fact]
    public async Task An_applied_irreversible_change_offers_no_undo()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1", requiresConfirmation: true, isReversible: false);

        var (html, _) = await RenderAsync(client, write, async state =>
        {
            await state.BeginWriteConfirmationAsync("op-1");
            await state.ConfirmWriteAsync();
        });

        html.Should().Contain("Applied");
        html.Should().NotContain("sam-write-op-1-undo",
            "an undo the server would refuse reads as a promise that the original is recoverable");
    }

    [Fact]
    public async Task An_undone_change_says_undone_and_offers_nothing()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1");

        var (html, _) = await RenderAsync(client, write, async state =>
        {
            await state.AcceptWriteAsync("op-1");
            await state.UndoWriteAsync("op-1");
        });

        html.Should().Contain("Undone");
        ButtonTag.Matches(html).Count.Should().Be(0);
    }

    [Fact]
    public async Task A_declined_change_says_declined_and_offers_nothing()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1");

        var (html, _) = await RenderAsync(client, write, state => state.RejectWriteAsync("op-1"));

        html.Should().Contain("Declined");
        ButtonTag.Matches(html).Count.Should().Be(0);
    }

    // ---------------------------------------------------------------- stale and unreadable

    [Fact]
    public async Task A_proposal_past_its_window_offers_nothing_and_says_to_ask_again()
    {
        var client = NewClient();
        var write = client.AddWrite(
            Conversation, "op-1", expiresAtUtc: DateTime.UtcNow.AddMinutes(-1));

        var (html, _) = await RenderAsync(client, write);

        html.Should().Contain("Expired");
        html.Should().Contain("Ask again for a fresh proposal.");
        ButtonTag.Matches(html).Count.Should().Be(0,
            "a control that can only be refused is worse than no control");
    }

    [Fact]
    public async Task A_change_the_server_cannot_find_keeps_its_card_and_explains_itself()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1");

        var (html, _) = await RenderAsync(client, write, async state =>
        {
            client.Writes.Remove("op-1");
            await state.AcceptWriteAsync("op-1");
        });

        html.Should().Contain("That change is no longer available.");
        html.Should().Contain("sam-write-op-1-error");
        ButtonTag.Matches(html).Count.Should().Be(0);
    }

    /// <summary>
    /// A state the client cannot interpret renders as itself, not as a plausible guess.
    /// </summary>
    [Fact]
    public async Task A_malformed_state_offers_nothing()
    {
        var client = NewClient();

        var malformed = new CoachWriteOperationDto
        {
            OperationId = "op-1",
            ConversationId = Conversation,
            ChangeKind = CoachWriteChangeKind.VocabularyAdd,
            // The two fields disagree: a server this build does not understand wrote one of them.
            RiskClass = CoachWriteRiskClass.WriteHard,
            Status = CoachWriteStatus.Proposed,
            ApprovalMode = "accept",
            Summary = "Add a word",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
            RequiresConfirmation = false
        };

        client.Writes["op-1"] = malformed;
        var (html, _) = await RenderAsync(client, malformed);

        html.Should().Contain("Unavailable");
        html.Should().Contain("there is nothing to approve");
        ButtonTag.Matches(html).Count.Should().Be(0);
    }

    // ---------------------------------------------------------------- gating and accessibility

    [Fact]
    public async Task Nothing_renders_when_the_server_does_not_offer_the_write_surface()
    {
        var client = NewClient(writeAvailable: false);
        var (html, _) = await RenderAsync(client, client.AddWrite(Conversation, "op-1"));

        html.Trim().Should().BeEmpty(
            "a deployment with the write tools off shows a conversation, not disabled buttons");
    }

    [Fact]
    public async Task The_card_is_a_named_group_with_a_status_and_described_actions()
    {
        var client = NewClient();
        var (html, _) = await RenderAsync(client, client.AddWrite(Conversation, "op-1"));

        html.Should().Contain("role=\"group\"");
        html.Should().Contain("aria-labelledby=\"sam-write-op-1-title\"");
        html.Should().Contain("role=\"status\"");
        html.Should().Contain("aria-describedby=\"sam-write-op-1-summary\"");
        html.Should().Contain("aria-busy=\"false\"");
    }

    [Fact]
    public async Task A_refusal_is_announced_and_referenced_by_the_control_that_would_retry()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1");

        var (html, _) = await RenderAsync(client, write, async state =>
        {
            client.OnWriteRefusal = (verb, _) => verb == "accept"
                ? new SentenceStudio.Services.Api.CoachApiException(
                    System.Net.HttpStatusCode.UnprocessableEntity, null, null, null)
                : null;

            await state.AcceptWriteAsync("op-1");
        });

        html.Should().Contain("role=\"alert\"");
        html.Should().Contain("That change could not be applied.");
        html.Should().Contain("aria-describedby=\"sam-write-op-1-summary sam-write-op-1-error\"");
    }

    /// <summary>
    /// Every control carries the 44px floor at every breakpoint, matching the rest of the coach
    /// surface, which is enforced by the shared <c>coach-action</c> class.
    /// </summary>
    [Fact]
    public async Task Every_control_carries_the_shared_touch_target_class()
    {
        var client = NewClient();
        var (html, _) = await RenderAsync(client, client.AddWrite(Conversation, "op-1"));

        var buttons = Regex.Matches(html, "<button[^>]*>");
        buttons.Should().NotBeEmpty();
        buttons.Should().AllSatisfy(button =>
            button.Value.Should().Contain("coach-action").And.Contain("sam-write__action"));
    }

    [Fact]
    public async Task The_card_never_writes_the_persona_name_into_markup()
    {
        var client = NewClient();
        var (html, _) = await RenderAsync(client, client.AddWrite(Conversation, "op-1"));

        html.Should().NotContain(">Sam<", "the name is localized, never written into markup");
    }
}
