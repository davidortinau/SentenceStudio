using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach;

public sealed class CoachAGUIEndpointsTests
{
    private const string Owner = "agui-owner";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = AGUIJsonUtilities.DefaultTypeInfoResolver
    };

    [Fact]
    public async Task AgentRequiresAuthentication()
    {
        await using var factory = new CoachApiFactory();
        using var http = factory.CreateClient();
        using var response = await http.PostAsJsonAsync("/api/v1/coach/agent", Input("missing"), Json);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        factory.ChatClient.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingThreadDoesNotInvokeTheModel(bool durable)
    {
        await using var factory = new CoachApiFactory
        {
            CoachEnabled = true,
            CohortUserProfileId = Owner,
            DurableHistory = durable
        };
        using var http = factory.CreateClient();
        Authenticate(http, Owner);
        var input = Input("not-owned", durable);
        using var response = await http.PostAsJsonAsync("/api/v1/coach/agent", input, Json);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.ChatClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ClientCannotSupplyBothTurnShapes()
    {
        await using var factory = new CoachApiFactory();
        using var http = factory.CreateClient();
        Authenticate(http, Owner);
        var input = Input("missing");
        input.State = JsonSerializer.SerializeToElement(new CoachAgentState
        {
            Turn = Text(),
            DurableTurn = new CoachConversationTurnRequest { Turn = Text(), IdempotencyKey = "key" }
        });
        using var response = await http.PostAsJsonAsync("/api/v1/coach/agent", input, Json);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        factory.ChatClient.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SDKClientReceivesOnlyValidatedStateAndRetryDoesNotRunTwice(bool durable)
    {
        var model = new ScriptedLearningCoach
        {
            NextResult = new CoachAgentTurnResult
            {
                Outcome = CoachAgentOutcome.Completed,
                Intent = new CoachTurnIntent { Kind = CoachIntentKind.NoChange, CoachMessage = "Try a greeting." }
            }
        };
        await using var factory = new CoachApiFactory
        {
            CoachEnabled = true,
            CohortUserProfileId = Owner,
            DurableHistory = durable
        };
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILearningCoach>();
            services.AddSingleton<ILearningCoach>(model);
        }));
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.UserProfiles.Add(new UserProfile
            {
                Id = Owner,
                Name = "AG-UI learner",
                NativeLanguage = "English",
                TargetLanguage = "Korean",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        using var http = host.CreateClient();
        Authenticate(http, Owner);
        using var session = await http.PostAsJsonAsync("/api/v1/coach/sessions", new StartCoachSessionRequest { Resume = false });
        session.EnsureSuccessStatusCode();
        var id = (await session.Content.ReadFromJsonAsync<CoachSessionResponse>())!.SessionId;

        using var client = new AGUIChatClient(new AGUIChatClientOptions(http, "/api/v1/coach/agent"));
        var input = Input(id, durable);
        var options = new ChatOptions { RawRepresentationFactory = _ => input };
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "How do I say hello?")], options))
        {
            updates.Add(update);
        }

        var snapshot = updates.Select(u => u.RawRepresentation).OfType<StateSnapshotEvent>().Single();
        var state = snapshot.Snapshot.Deserialize<CoachAgentState>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        state.Result.Should().NotBeNull();
        state.Result!.Messages.Should().Contain(m => m.Text == "Try a greeting.");
        state.Result.ChangeReceipt.Should().BeNull();
        model.LastRequest!.LearnerText.Should().Be(Text().Text);
        if (durable)
        {
            state.Operation.Should().NotBeNull();
        }
        await foreach (var _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "How do I say hello?")], options)) { }
        model.RunCount.Should().Be(1);
    }

    private static RunAgentInput Input(string threadId, bool durable = false) => new()
    {
        ThreadId = threadId,
        RunId = "run-1",
        State = JsonSerializer.SerializeToElement(durable
            ? new CoachAgentState
            {
                DurableTurn = new CoachConversationTurnRequest
                {
                    Turn = Text(),
                    OperationId = "operation-1",
                    IdempotencyKey = "idempotency-1"
                }
            }
            : new CoachAgentState { Turn = Text() })
    };

    private static CoachTurnRequest Text() => new()
    {
        InputKind = CoachTurnInputKind.Text,
        Text = "How do I say hello?",
        ClientTurnId = "turn-1"
    };

    private static void Authenticate(HttpClient http, string owner) =>
        http.DefaultRequestHeaders.Authorization = new("Bearer", TestJwtGenerator.GenerateToken(userProfileId: owner));
}
