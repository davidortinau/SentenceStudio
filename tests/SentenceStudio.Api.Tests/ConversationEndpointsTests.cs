using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Conversation;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Contracts.Conversation;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests;

/// <summary>
/// Integration tests for the Conversation HTTP endpoints (spec 004).
/// The Flutter client treats <c>conversation_dtos.dart</c> as the source of
/// truth for the wire shape; these tests pin the contract.
/// </summary>
public sealed class ConversationEndpointsTests : IClassFixture<ConversationApiFactory>
{
    private readonly ConversationApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ConversationEndpointsTests(ConversationApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient ClientWithJwt(string userProfileId)
    {
        var token = TestJwtGenerator.GenerateToken(userProfileId: userProfileId);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<ConversationScenario> SeedScenarioAsync(
        string name = "First Meeting",
        ConversationType type = ConversationType.OpenEnded,
        bool isPredefined = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var scenario = new ConversationScenario
        {
            Name = name,
            NameKorean = "첫 만남",
            PersonaName = "Mr. Kim",
            PersonaDescription = "a friendly stranger",
            SituationDescription = "Meeting for the first time at a coffee shop.",
            ConversationType = type,
            QuestionBank = null,
            IsPredefined = isPredefined,
        };
        db.ConversationScenarios.Add(scenario);
        await db.SaveChangesAsync();
        return scenario;
    }

    [Fact]
    public async Task GetScenarios_NoJwt_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/conversation/scenarios");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetScenarios_Authed_ReturnsSeededScenariosWithPascalCaseKeys()
    {
        await SeedScenarioAsync(name: $"PascalCase Smoke {Guid.NewGuid():N}");
        var client = ClientWithJwt($"conv-scenarios-{Guid.NewGuid():N}");

        var response = await client.GetAsync("/api/v1/conversation/scenarios");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await response.Content.ReadAsStringAsync();
        // Wire contract: scenario keys are PascalCase. A camelCase key like
        // "personaName" indicates JSON serialization defaults leaked through.
        raw.Should().Contain("\"PersonaName\"");
        raw.Should().Contain("\"ConversationType\"");
        raw.Should().Contain("\"IsPredefined\"");
        raw.Should().NotContain("\"personaName\"");
        raw.Should().NotContain("\"conversationType\"");

        var scenarios = await response.Content.ReadFromJsonAsync<List<ConversationScenarioDto>>(JsonOptions);
        scenarios.Should().NotBeNullOrEmpty();
        scenarios!.Should().Contain(s => s.PersonaName == "Mr. Kim");
        scenarios.Should().OnlyContain(s => s.ConversationType == "OpenEnded" || s.ConversationType == "Finite");
    }

    [Fact]
    public async Task PostStart_NoJwt_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/conversation/start",
            new ConversationStartRequest { TargetLanguage = "ko" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostStart_KnownScenario_ReturnsOpeningMessage()
    {
        var scenario = await SeedScenarioAsync(name: $"Start OK {Guid.NewGuid():N}");
        _factory.ConversationService.OpeningMessage = "안녕하세요! 반갑습니다.";

        var client = ClientWithJwt($"conv-start-{Guid.NewGuid():N}");
        var response = await client.PostAsJsonAsync("/api/v1/conversation/start",
            new ConversationStartRequest { ScenarioId = scenario.Id, TargetLanguage = "ko" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<ConversationStartResponse>(JsonOptions);
        payload.Should().NotBeNull();
        payload!.FirstAssistantMessage.Should().Be("안녕하세요! 반갑습니다.");
        payload.PersonaName.Should().Be("Mr. Kim");
        payload.ConversationType.Should().Be("OpenEnded");

        // Language normalization happens at the endpoint boundary.
        _factory.ConversationService.LastTargetLanguageLabel.Should().Be("Korean");
        _factory.ConversationService.LastScenario.Should().NotBeNull();
        _factory.ConversationService.LastScenario!.Id.Should().Be(scenario.Id);
    }

    [Fact]
    public async Task PostStart_UnknownScenario_Returns404()
    {
        var client = ClientWithJwt($"conv-start-404-{Guid.NewGuid():N}");
        var response = await client.PostAsJsonAsync("/api/v1/conversation/start",
            new ConversationStartRequest { ScenarioId = 999999, TargetLanguage = "ko" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostContinue_OneTurnHistory_ReturnsExpectedWireShape()
    {
        var scenario = await SeedScenarioAsync(name: $"Continue OK {Guid.NewGuid():N}");
        _factory.ConversationService.ContinueResponse = new ConversationContinueResponse
        {
            AssistantMessage = "네, 좋아요.",
            ComprehensionScore = 92,
            ComprehensionNotes = "Great natural phrasing.",
            GrammarCorrections = new()
            {
                new ConversationGrammarCorrectionDto
                {
                    Original = "나는 학생이에요",
                    Corrected = "저는 학생이에요",
                    Explanation = "Use 저 in polite speech.",
                },
            },
            VocabularyAnalysis = new(),
            IsComplete = false,
        };

        var client = ClientWithJwt($"conv-continue-{Guid.NewGuid():N}");
        var response = await client.PostAsJsonAsync("/api/v1/conversation/continue",
            new ConversationContinueRequest
            {
                ScenarioId = scenario.Id,
                TargetLanguage = "ko",
                History = new()
                {
                    new ConversationHistoryItemDto
                    {
                        Role = "assistant", Author = "Mr. Kim", Text = "안녕하세요.",
                    },
                    new ConversationHistoryItemDto
                    {
                        Role = "user", Text = "안녕하세요, 저는 학생이에요.",
                    },
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await response.Content.ReadAsStringAsync();
        // Wire contract: response keys are camelCase. Assert key presence to
        // catch JSON serialization regressions early.
        raw.Should().Contain("\"assistantMessage\"");
        raw.Should().Contain("\"comprehensionScore\"");
        raw.Should().Contain("\"comprehensionNotes\"");
        raw.Should().Contain("\"grammarCorrections\"");
        raw.Should().Contain("\"vocabularyAnalysis\"");
        raw.Should().Contain("\"isComplete\"");
        raw.Should().NotContain("\"AssistantMessage\"");
        raw.Should().NotContain("\"ComprehensionScore\"");

        var payload = await response.Content.ReadFromJsonAsync<ConversationContinueResponse>(JsonOptions);
        payload.Should().NotBeNull();
        payload!.AssistantMessage.Should().Be("네, 좋아요.");
        payload.ComprehensionScore.Should().BeInRange(0, 100);
        payload.ComprehensionScore.Should().Be(92);
        payload.GrammarCorrections.Should().ContainSingle()
            .Which.Corrected.Should().Be("저는 학생이에요");
        payload.IsComplete.Should().BeFalse();

        // Endpoint extracts the last entry as the user's new message; prior
        // turns become conversation history for the service call.
        _factory.ConversationService.LastUserMessage.Should().Be("안녕하세요, 저는 학생이에요.");
        _factory.ConversationService.LastHistory.Should().ContainSingle()
            .Which.Role.Should().Be("assistant");
    }

    [Fact]
    public async Task PostContinue_EmptyHistory_Returns400()
    {
        var client = ClientWithJwt($"conv-continue-empty-{Guid.NewGuid():N}");
        var response = await client.PostAsJsonAsync("/api/v1/conversation/continue",
            new ConversationContinueRequest
            {
                ScenarioId = null,
                TargetLanguage = "ko",
                History = new(),
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostContinue_LastTurnNotUser_Returns400()
    {
        var client = ClientWithJwt($"conv-continue-badlast-{Guid.NewGuid():N}");
        var response = await client.PostAsJsonAsync("/api/v1/conversation/continue",
            new ConversationContinueRequest
            {
                ScenarioId = null,
                TargetLanguage = "ko",
                History = new()
                {
                    new ConversationHistoryItemDto
                    {
                        Role = "assistant", Text = "안녕하세요.",
                    },
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostContinue_UnknownScenario_Returns404()
    {
        var client = ClientWithJwt($"conv-continue-404-{Guid.NewGuid():N}");
        var response = await client.PostAsJsonAsync("/api/v1/conversation/continue",
            new ConversationContinueRequest
            {
                ScenarioId = 999999,
                TargetLanguage = "ko",
                History = new()
                {
                    new ConversationHistoryItemDto { Role = "user", Text = "안녕." },
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 50)]
    [InlineData(0.85, 85)]
    [InlineData(1.0, 100)]
    [InlineData(85.0, 85)]   // grader returned a 0-100 magnitude — normalize, don't multiply.
    [InlineData(100.0, 100)]
    [InlineData(-0.5, 0)]    // clamp below
    [InlineData(150.0, 100)] // clamp above
    public void NormalizeComprehensionScore_HandlesBothScales(double raw, int expected)
    {
        ServerConversationService.NormalizeComprehensionScore(raw).Should().Be(expected);
    }
}
