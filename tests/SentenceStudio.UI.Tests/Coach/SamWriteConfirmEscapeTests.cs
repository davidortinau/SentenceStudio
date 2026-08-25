using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Sam;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Escape inside a protected change's confirmation step backs out of the step, and does not also
/// take the panel down with it.
/// </summary>
/// <remarks>
/// <para>
/// Two handlers claim Escape while the step is open: the alert dialog's own, and the overlay's
/// <c>document</c>-level listener that collapses the panel. Both fire for a single keypress unless
/// one of them stops. Collapsing loses the only visible route back to the change the learner was
/// deciding on, so the outcome cannot be left to which listener was registered first.
/// </para>
/// <para>
/// <c>SamOverlayEscape</c> already encodes the intended precedence, and
/// <c>SamOverlayEscapeTests</c> pins it — but that rule depends on the two components agreeing
/// about state at the instant of a keypress. The attribute asserted here does not: the event never
/// reaches <c>document</c>, so the ordering question does not arise.
/// </para>
/// </remarks>
public class SamWriteConfirmEscapeTests
{
    private const string Conversation = "conversation-1";
    private const string Operation = "op-hard";

    private static async Task<(CoachWorkspaceState State, Microsoft.Extensions.DependencyInjection.ServiceProvider Provider)> ConfirmingAsync()
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
                IsSamWriteAvailable = true
            }
        };

        client.AddConversation(Conversation);
        var write = client.AddWrite(Conversation, Operation, requiresConfirmation: true, isReversible: false);
        client.Seed(Conversation, CoachMessageRole.Learner, "delete my archived skill");
        client.Seed(Conversation, CoachMessageRole.Coach, "This one cannot be undone.", writeOperation: write);

        var flags = new CoachFeatureFlags(client);
        await flags.EnsureLoadedAsync();

        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client, flags), flags);
        await state.RefreshAvailabilityAsync();
        await state.OpenConversationAsync(CoachPresentation.Overlay, Conversation);
        await state.BeginWriteConfirmationAsync(Operation);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        // The coach's name comes from the learner's study language, so every component that
        // names it needs the resolver. The all-optional constructor makes this a one-liner:
        // with no language source it answers with the default persona.
        services.AddScoped<CoachPersona>();
        services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());
        services.AddScoped(_ => state);

        return (state, services.BuildServiceProvider());
    }

    [Fact]
    public async Task The_confirmation_step_stops_the_keypress_reaching_the_document_handler()
    {
        var (state, provider) = await ConfirmingAsync();
        await using var _ = provider;

        state.ConfirmingWriteOperationId.Should().Be(Operation, "the step has to be open to be tested");

        using var renderer = new InteractiveTestRenderer(
            provider, provider.GetRequiredService<ILoggerFactory>());

        var id = await renderer.RenderAsync<SamWriteCard>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(SamWriteCard.Operation)] = state.ActiveWriteOperation
            }));

        var attributes = renderer.AttributesOfElementWithId(
            id, SamElementIds.WriteConfirmStep(Operation));

        attributes.Should().NotBeEmpty("the confirmation step must be on screen");
        attributes.Should().Contain("onkeydown", "the step handles Escape itself");
        attributes.Should().Contain(
            "__internal_stopPropagation_onkeydown",
            "and the keypress must not bubble to the overlay's document listener, "
            + "so the outcome does not depend on which handler was registered first");

        renderer.Unhandled.Should().BeEmpty();
    }

    /// <summary>
    /// The handler itself still does the backing out — stopping propagation is not a substitute
    /// for cancelling.
    /// </summary>
    [Fact]
    public async Task Escape_cancels_the_confirmation_without_touching_the_proposal()
    {
        var (state, provider) = await ConfirmingAsync();
        await using var _ = provider;

        state.ConfirmingWriteOperationId.Should().Be(Operation);
        state.ConfirmationExpiresAtUtc.Should().NotBeNull();

        // The handler bound to the alert dialog, invoked directly: the same call the keypress makes.
        state.CancelWriteConfirmation();

        state.ConfirmingWriteOperationId.Should().BeNull();
        state.ConfirmationExpiresAtUtc.Should().BeNull("the one-use value is dropped, not parked");
        state.ActiveWriteOperation.Should().NotBeNull("backing out of a step does not decline the change");
        state.ActiveWriteOperation!.Status.Should().Be(CoachWriteStatus.Proposed);
    }

    /// <summary>
    /// The overlay's own rule stays as the second line of defence, so a surface that reaches the
    /// document handler another way still stands down while a step is open.
    /// </summary>
    [Fact]
    public void The_overlay_still_defers_while_a_confirmation_is_open()
    {
        SamOverlayEscape
            .Resolve(SamOverlayVisualState.Expanded, isConfirmingWrite: true, isReportPanelOpen: false)
            .Should().Be(SamOverlayEscapeAction.DeferToPanelContent);

        SamOverlayEscape
            .Resolve(SamOverlayVisualState.Expanded, isConfirmingWrite: false, isReportPanelOpen: false)
            .Should().Be(SamOverlayEscapeAction.Collapse);
    }
}
