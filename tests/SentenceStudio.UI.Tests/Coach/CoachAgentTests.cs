using System.Net;
using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebApp.Coach;
using SentenceStudio.WebApp.Components.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

public sealed class CoachAgentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MountedTranscriptAndInputTrackCanonicalWorkspaceHistory(bool durable)
    {
        var api = new FakeCoachApiClient { DurableHistoryAvailable = durable };
        var workspace = new CoachWorkspaceState(api, new CoachConversationDirectory(api));
        await using var mounted = await MountedAgent.CreateAsync(workspace);
        await mounted.Renderer.Dispatcher.InvokeAsync(async () =>
        {
            mounted.Html.Should().NotContain("sc-ai-input__textarea");
            await workspace.OpenAsync(CoachPresentation.Overlay);
            workspace.Draft = "Hello";
            await workspace.SendDraftAsync();
        });
        await mounted.Renderer.Dispatcher.InvokeAsync(() =>
        {
            mounted.Context.Turns.SelectMany(t => t.RequestBlocks.Concat(t.ResponseBlocks))
                .OfType<CoachTimelineBlock>().Select(b => b.Entry).Should().Equal(workspace.Timeline);
            mounted.Html.Should().Contain("Hello").And.Contain("sc-ai-input__textarea");
        });
        await mounted.Renderer.Dispatcher.InvokeAsync(() => mounted.Context.SendMessageAsync("Hello again"));
        await mounted.Renderer.Dispatcher.InvokeAsync(() =>
        {
            mounted.Context.Turns.SelectMany(t => t.RequestBlocks.Concat(t.ResponseBlocks))
                .OfType<CoachTimelineBlock>().Select(b => b.Entry).Should().Equal(workspace.Timeline);
            mounted.Html.Should().Contain("Hello again");
        });
        await mounted.Renderer.Dispatcher.InvokeAsync(() => workspace.DeleteSessionAsync());
        await mounted.Renderer.Dispatcher.InvokeAsync(() =>
        {
            mounted.Context.Turns.Should().BeEmpty();
            mounted.Html.Should().NotContain("Hello").And.NotContain("sc-ai-input__textarea");
        });
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
        await using var mounted = await MountedAgent.CreateAsync(workspace);
        var context = mounted.Context;

        await mounted.Renderer.Dispatcher.InvokeAsync(() => context.SendMessageAsync("Hello"));
        context.Status.Should().Be(ConversationStatus.Error);
        var original = api.SubmittedTurns.Single().ClientTurnId;
        api.OnSubmitTurn = null;
        await mounted.Renderer.Dispatcher.InvokeAsync(() => context.RetryAsync());

        api.SubmittedTurns.Should().HaveCount(2);
        api.SubmittedTurns[1].ClientTurnId.Should().Be(original);
        workspace.Timeline.Count(e => e.Kind == CoachTimelineKind.LearnerMessage).Should().Be(1);
        context.Status.Should().Be(ConversationStatus.Idle);
        mounted.Context.Should().BeSameAs(context);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("history")]
    [InlineData("delete")]
    [InlineData("recover")]
    [InlineData("dismiss")]
    public async Task ThreadChangesAndExternalRecoveryReplaceOnlyTheAgentContext(string transition)
    {
        var api = new FakeCoachApiClient { DurableHistoryAvailable = true };
        var workspace = new CoachWorkspaceState(api, new CoachConversationDirectory(api));
        await workspace.OpenConversationAsync(CoachPresentation.Overlay);
        workspace.Draft = "Existing conversation";
        await workspace.SendDraftAsync();
        var otherConversation = workspace.ConversationId;
        await workspace.OpenConversationAsync(CoachPresentation.Overlay);
        api.OnSubmitConversationTurn = (_, _) => throw new CoachApiException(
            HttpStatusCode.ServiceUnavailable, null, "Unavailable", "Retry");
        await using var mounted = await MountedAgent.CreateAsync(workspace);
        var shell = mounted.Probe;
        var failedContext = mounted.Context;
        await mounted.Renderer.Dispatcher.InvokeAsync(() => failedContext.SendMessageAsync("Failed in A"));
        failedContext.Status.Should().Be(ConversationStatus.Error);
        var failedTurn = api.SubmittedConversationTurns.Last();
        api.OnSubmitConversationTurn = null;

        await mounted.Renderer.Dispatcher.InvokeAsync(async () =>
        {
            switch (transition)
            {
                case "empty":
                    await workspace.OpenConversationAsync(CoachPresentation.Overlay);
                    break;
                case "history":
                    await workspace.OpenConversationAsync(CoachPresentation.Overlay, otherConversation);
                    break;
                case "delete":
                    await workspace.DeleteSessionAsync();
                    break;
                case "recover":
                    await workspace.RetryDurableTurnAsync();
                    break;
                case "dismiss":
                    workspace.KeepCurrentPlan();
                    break;
            }
        });
        await mounted.Renderer.Dispatcher.InvokeAsync(() =>
        {
            mounted.Probe.Should().BeSameAs(shell);
            mounted.Context.Should().NotBeSameAs(failedContext);
            mounted.Context.Status.Should().Be(ConversationStatus.Idle);
            mounted.Context.Error.Should().BeNull();
            mounted.Html.Should().NotContain("sc-ai-error");
            mounted.Context.Turns.SelectMany(t => t.RequestBlocks.Concat(t.ResponseBlocks))
                .OfType<CoachTimelineBlock>().Select(b => b.Entry).Should().Equal(workspace.Timeline);
            if (transition is "empty" or "delete")
            {
                mounted.Context.Turns.Should().BeEmpty();
            }
        });
        var requests = api.SubmitConversationTurnCalls;
        await mounted.Renderer.Dispatcher.InvokeAsync(() => failedContext.RetryAsync());
        api.SubmitConversationTurnCalls.Should().Be(requests);
        if (transition == "recover")
        {
            api.SubmittedConversationTurns.Last().OperationId.Should().Be(failedTurn.OperationId);
            api.SubmittedConversationTurns.Last().IdempotencyKey.Should().Be(failedTurn.IdempotencyKey);
        }
        await mounted.Renderer.Dispatcher.InvokeAsync(async () =>
        {
            if (transition == "delete")
            {
                await workspace.OpenConversationAsync(CoachPresentation.Overlay);
            }
            await mounted.Context.SendMessageAsync("Current conversation follow-up");
            mounted.Context.Status.Should().Be(ConversationStatus.Idle);
            mounted.Html.Should().NotContain("sc-ai-error");
        });
        api.SubmitConversationTurnCalls.Should().Be(requests + 1);
        api.SubmittedConversationTurns.Last().Turn.Text.Should().Be("Current conversation follow-up");
    }

    [Fact]
    public async Task AdapterRejectsAnOldMessageRetriedInAnotherConversation()
    {
        var api = new FakeCoachApiClient { DurableHistoryAvailable = true };
        var workspace = new CoachWorkspaceState(api, new CoachConversationDirectory(api));
        await workspace.OpenConversationAsync(CoachPresentation.Overlay);
        using var client = new CoachWorkspaceChatClient(workspace);
        var message = new ChatMessage(ChatRole.User, "Failed in A");
        api.OnSubmitConversationTurn = (_, _) => throw new HttpRequestException("Disconnected");
        var send = () => client.GetResponseAsync([message]);
        await send.Should().ThrowAsync<InvalidOperationException>();
        await workspace.OpenConversationAsync(CoachPresentation.Overlay);
        api.OnSubmitConversationTurn = null;

        await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no longer current*");
        api.SubmitConversationTurnCalls.Should().Be(1);
        workspace.Timeline.Should().BeEmpty();
    }

    [Theory]
    [InlineData("missing-snapshot")]
    [InlineData("missing-finish")]
    [InlineData("missing-operation")]
    public async Task IncompleteAGUIResponseReleasesWorkspaceAndRetryPreservesDurableHandles(string failure)
    {
        var api = new FakeCoachApiClient { DurableHistoryAvailable = true };
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = AGUIJsonUtilities.DefaultTypeInfoResolver
        };
        var domainJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var calls = 0;
        using var handler = new AgentResponseHandler(request =>
        {
            var input = JsonSerializer.Deserialize<RunAgentInput>(request, json)!;
            var state = input.State!.Value.Deserialize<CoachAgentState>(domainJson)!;
            var operation = new CoachTurnOperationDto
            {
                OperationId = state.DurableTurn!.OperationId,
                ConversationId = input.ThreadId,
                State = CoachTurnOperationState.Completed,
                Result = CoachStateMachineTests.Turn(),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            var retry = ++calls > 1;
            var events = new List<BaseEvent>
            {
                new RunStartedEvent { ThreadId = input.ThreadId, RunId = input.RunId }
            };
            if (retry || failure != "missing-snapshot")
            {
                events.Add(new StateSnapshotEvent
                {
                    Snapshot = JsonSerializer.SerializeToElement(
                        new CoachAgentState { Operation = retry || failure != "missing-operation" ? operation : null }, domainJson)
                });
            }
            if (retry || failure != "missing-finish")
            {
                events.Add(new RunFinishedEvent { ThreadId = input.ThreadId, RunId = input.RunId });
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Concat(events.Select(e =>
                    $"data: {JsonSerializer.Serialize(e, json)}\n\n")), Encoding.UTF8, "text/event-stream")
            };
        });
        var transport = new CoachAGUITransport(new AgentClientFactory(handler));
        api.OnSubmitConversationTurnAsync = (id, request, token) =>
            transport.SubmitConversationTurnAsync(id, request, token);
        var workspace = new CoachWorkspaceState(api, new CoachConversationDirectory(api));
        await workspace.OpenConversationAsync(CoachPresentation.Overlay);
        await using var mounted = await MountedAgent.CreateAsync(workspace);
        var context = mounted.Context;

        await mounted.Renderer.Dispatcher.InvokeAsync(() => context.SendMessageAsync("Hello"));

        workspace.State.Should().Be(CoachUiState.Failed);
        workspace.IsBusy.Should().BeFalse();
        workspace.CanSubmit.Should().BeTrue();
        context.Status.Should().Be(ConversationStatus.Error);
        var original = api.SubmittedConversationTurns.Single();
        await mounted.Renderer.Dispatcher.InvokeAsync(() => context.RetryAsync());

        context.Status.Should().Be(ConversationStatus.Idle);
        workspace.State.Should().Be(CoachUiState.Ready);
        workspace.IsBusy.Should().BeFalse();
        api.SubmittedConversationTurns.Should().HaveCount(2);
        api.SubmittedConversationTurns[1].OperationId.Should().Be(original.OperationId);
        api.SubmittedConversationTurns[1].IdempotencyKey.Should().Be(original.IdempotencyKey);
        api.SubmittedConversationTurns[1].Turn.ClientTurnId.Should().Be(original.Turn.ClientTurnId);
        workspace.Timeline.Count(e => e.Kind == CoachTimelineKind.LearnerMessage).Should().Be(1);
    }

    public sealed class ContextProbe : ComponentBase
    {
        [CascadingParameter] public AgentContext Context { get; set; } = default!;
    }

    private sealed class MountedAgent(
        Microsoft.Extensions.DependencyInjection.ServiceProvider provider, HtmlRenderer renderer) : IAsyncDisposable
    {
        public HtmlRenderer Renderer => renderer;
        public ContextProbe Probe { get; private set; } = default!;
        public AgentContext Context => Probe.Context;
        public string Html => _output.ToHtmlString();
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _output;

        public static async Task<MountedAgent> CreateAsync(CoachWorkspaceState workspace)
        {
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton(workspace);
            services.AddSingleton(new CoachWorkspaceChatClient(workspace));
            services.AddScoped<BlazorLocalizationService>();
            services.AddScoped<CoachPersona>();
            services.AddSingleton<IJSRuntime, StubJSRuntime>();
            services.AddSingleton<NavigationManager, TestNavigationManager>();
            var provider = services.BuildServiceProvider();
            var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var mounted = new MountedAgent(provider, renderer);
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                RenderFragment content = builder =>
                {
                    builder.OpenComponent<ContextProbe>(0);
                    builder.AddComponentReferenceCapture(1, instance => mounted.Probe = (ContextProbe)instance);
                    builder.CloseComponent();
                    builder.OpenComponent<CoachChatPane>(2);
                    builder.CloseComponent();
                    builder.OpenComponent<CoachComposer>(3);
                    builder.CloseComponent();
                };
                mounted._output = await renderer.RenderComponentAsync<CoachAgentBoundary>(
                    ParameterView.FromDictionary(new Dictionary<string, object?> { ["ChildContent"] = content }));
            });
            return mounted;
        }

        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed class AgentResponseHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            respond(await request.Content!.ReadAsStringAsync(token));
    }

    private sealed class AgentClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://coach.invalid")
        };
    }
}
