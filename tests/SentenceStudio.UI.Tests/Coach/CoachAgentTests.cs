using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebApp.Coach;
using SentenceStudio.WebApp.Components.Coach;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

public sealed class CoachAgentTests
{
    [Fact]
    public async Task MountedSynchronizationTracksExternalWorkspaceChangesWithoutARenderedElement()
    {
        var workspace = new CoachWorkspaceState(new FakeCoachApiClient());
        using var client = new CoachWorkspaceChatClient(workspace);
        using var agent = new UIAgent(client, options =>
        {
            options.Thread = new CoachConversationThread(workspace);
            options.AddBlockHandler(new CoachTimelineBlockHandler());
        });
        using var context = new AgentContext(agent);
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(workspace);
        services.AddSingleton(client);
        services.AddScoped<BlazorLocalizationService>();
        services.AddScoped<CoachPersona>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent output = default;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment synchronization = builder =>
            {
                builder.OpenComponent<CoachAgentSynchronization>(0);
                builder.CloseComponent();
                builder.OpenComponent<CoachAgentMessages>(1);
                builder.CloseComponent();
            };
            output = await renderer.RenderComponentAsync<CascadingValue<AgentContext>>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = context,
                    ["ChildContent"] = synchronization
                }));
            await workspace.OpenAsync(CoachPresentation.Overlay);
            workspace.Draft = "Hello";
            await workspace.SendDraftAsync();
        });
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            context.Turns.SelectMany(t => t.RequestBlocks.Concat(t.ResponseBlocks))
                .OfType<CoachTimelineBlock>().Select(b => b.Entry).Should().Equal(workspace.Timeline);
            output.ToHtmlString().Should().Contain("Hello");
        });
        await renderer.Dispatcher.InvokeAsync(() => context.SendMessageAsync("Hello again"));
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            context.Turns.SelectMany(t => t.RequestBlocks.Concat(t.ResponseBlocks))
                .OfType<CoachTimelineBlock>().Select(b => b.Entry).Should().Equal(workspace.Timeline);
            output.ToHtmlString().Should().Contain("Hello again");
        });
        await renderer.Dispatcher.InvokeAsync(() => workspace.DeleteSessionAsync());
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            context.Turns.Should().BeEmpty();
            output.ToHtmlString().Should().NotContain("Hello");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LibraryInputIsPresentForSessionAndDurableConversations(bool durable)
    {
        var workspace = new CoachWorkspaceState(new FakeCoachApiClient { DurableHistoryAvailable = durable });
        using var agent = CreateAgent(workspace);
        using var context = new AgentContext(agent);
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(workspace);
        services.AddScoped<BlazorLocalizationService>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment input = builder =>
            {
                builder.OpenComponent<CoachAgentInput>(0);
                builder.CloseComponent();
            };
            var output = await renderer.RenderComponentAsync<CascadingValue<AgentContext>>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = context,
                    ["ChildContent"] = input
                }));
            output.ToHtmlString().Should().NotContain("sc-ai-input__textarea");
            await workspace.OpenAsync(CoachPresentation.Overlay);
            return output.ToHtmlString();
        });
        html.Should().Contain("sc-ai-input__textarea");
    }

    [Fact]
    public async Task RepeatedIntentionalMessageIsANewTurnEvenWhenTheMessageObjectIsReused()
    {
        var api = new FakeCoachApiClient();
        var workspace = new CoachWorkspaceState(api);
        await workspace.OpenAsync(CoachPresentation.Overlay);
        using var client = new CoachWorkspaceChatClient(workspace);
        var message = new ChatMessage(ChatRole.User, "Hello");

        await client.GetResponseAsync([message]);
        await client.GetResponseAsync([message]);

        api.SubmittedTurns.Should().HaveCount(2);
        api.SubmittedTurns[0].ClientTurnId.Should().NotBe(api.SubmittedTurns[1].ClientTurnId);
    }

    [Fact]
    public async Task LibraryRetryReusesApplicationTurnIdentityAfterFailure()
    {
        var api = new FakeCoachApiClient
        {
            OnSubmitTurn = _ => throw new CoachApiException(
                HttpStatusCode.ServiceUnavailable, CoachProblemTypes.Timeout, "Timeout", "Retry")
        };
        var workspace = new CoachWorkspaceState(api);
        await workspace.OpenAsync(CoachPresentation.Overlay);
        using var agent = CreateAgent(workspace);
        using var context = new AgentContext(agent);

        await context.SendMessageAsync("Hello");
        context.Status.Should().Be(ConversationStatus.Error);
        var original = api.SubmittedTurns.Single().ClientTurnId;
        api.OnSubmitTurn = null;
        await context.RetryAsync();

        api.SubmittedTurns.Should().HaveCount(2);
        api.SubmittedTurns[1].ClientTurnId.Should().Be(original);
        workspace.Timeline.Count(e => e.Kind == CoachTimelineKind.LearnerMessage).Should().Be(1);
        context.Status.Should().Be(ConversationStatus.Idle);
    }

    [Fact]
    public async Task RestoreUsesOnlyCanonicalHistoryAndClearsDeletedConversations()
    {
        var api = new FakeCoachApiClient();
        var workspace = new CoachWorkspaceState(api);
        await workspace.OpenAsync(CoachPresentation.Overlay);
        using var agent = CreateAgent(workspace);
        using var context = new AgentContext(agent);

        await context.SendMessageAsync("Hello");
        await context.RestoreAsync();
        await context.RestoreAsync();
        var entries = context.Turns.SelectMany(t => t.RequestBlocks.Concat(t.ResponseBlocks))
            .OfType<CoachTimelineBlock>().Select(b => b.Entry).ToList();
        entries.Should().Equal(workspace.Timeline);
        api.SubmitTurnCalls.Should().Be(1);

        await workspace.DeleteSessionAsync();
        await context.RestoreAsync();
        context.Turns.Should().BeEmpty();
    }

    private static UIAgent CreateAgent(CoachWorkspaceState workspace) =>
        new(new CoachWorkspaceChatClient(workspace), options =>
        {
            options.Thread = new CoachConversationThread(workspace);
            options.AddBlockHandler(new CoachTimelineBlockHandler());
        });
}
