using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.AI;
using SentenceStudio.Evaluation.Tests.Import;
using SentenceStudio.Evaluation.Tests.Infrastructure;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Evaluation.Tests.Offline;

/// <summary>
/// Offline checks that make no network call and cost nothing. They prove the eval wiring before a
/// paid run: the request the Azure SDK would send, the app import path with a canned model reply,
/// and the deterministic coverage scoring.
/// </summary>
[Trait("Category", "Offline")]
public sealed class OfflinePipelineTests
{
    private static readonly Uri FakeResource = new("https://example.invalid/");

    [Fact]
    public async Task TargetRequest_UsesTypedJsonSchema_AndCarriesOutputCap()
    {
        var handler = new RecordingHandler("""{"vocabulary":[]}""");
        var azure = EvalChatClients.CreateAzure(FakeResource, "gpt-5-mini", new FakeCredential(), new HttpClient(handler));
        IChatClient target = new OutputTokenCapChatClient(azure, 16000);

        // Same call shape as AiService.SendPrompt<T> on the direct-client branch.
        var response = await target.GetResponseAsync<FreeTextVocabularyExtractionResponse>(
            [new ChatMessage(ChatRole.User, "prompt")],
            AiChatOptionsFactory.Create(reasoningEffort: null));

        Assert.NotNull(response.Result);
        var body = handler.LastBody!;
        Assert.Equal(16000, body["max_completion_tokens"]?.GetValue<int>());
        Assert.Null(body["max_tokens"]);
        Assert.Null(body["temperature"]);
        Assert.Null(body["reasoning_effort"]);
        Assert.Equal("json_schema", body["response_format"]?["type"]?.GetValue<string>());
        Assert.Contains("/openai/deployments/gpt-5-mini/chat/completions", handler.LastUri!.AbsolutePath);
    }

    [Fact]
    public async Task JudgeRequest_DropsSamplingParameters_AndSetsEffortAndCap()
    {
        var handler = new RecordingHandler("<S0>steps</S0><S1>why</S1><S2>5</S2>");
        var azure = EvalChatClients.CreateAzure(FakeResource, "gpt-5", new FakeCredential(), new HttpClient(handler));
        IChatClient judge = new ReasoningJudgeChatClient(azure, 3000, "low");

        // The built-in quality evaluators' fixed options (GroundednessEvaluator / CompletenessEvaluator 10.9.0).
        var evaluatorOptions = new ChatOptions
        {
            Temperature = 0.0f,
            MaxOutputTokens = 800,
            TopP = 1.0f,
            Seed = 123,
            PresencePenalty = 0.0f,
            FrequencyPenalty = 0.0f,
            ResponseFormat = ChatResponseFormat.Text,
        };

        await judge.GetResponseAsync([new ChatMessage(ChatRole.User, "judge this")], evaluatorOptions);

        var body = handler.LastBody!;
        Assert.Equal(3000, body["max_completion_tokens"]?.GetValue<int>());
        Assert.Null(body["max_tokens"]);
        Assert.Null(body["temperature"]);
        Assert.Null(body["top_p"]);
        Assert.Null(body["seed"]);
        Assert.Null(body["presence_penalty"]);
        Assert.Null(body["frequency_penalty"]);
        Assert.Equal("low", body["reasoning_effort"]?.GetValue<string>());
        // The evaluator's own options object is not mutated (it is a shared static in the library).
        Assert.Equal(0.0f, evaluatorOptions.Temperature);
    }

    [Fact]
    public async Task AppImportPath_RendersAppTemplate_AndMapsTypedRows()
    {
        var fixture = ImportVocabularyFixtures.Get("proper-noun-trap");
        var canned = new CannedChatClient("""
            {"vocabulary":[
              {"targetLanguageTerm":"회사원","nativeLanguageTerm":"office worker","confidence":"high","lexicalUnitType":"Word","relatedTerms":[]},
              {"targetLanguageTerm":"출장을 가다","nativeLanguageTerm":"to go on a business trip","confidence":"high","lexicalUnitType":"Phrase","relatedTerms":["출장","가다"]},
              {"targetLanguageTerm":"민준","nativeLanguageTerm":"Minjun (name)","confidence":"low","lexicalUnitType":"Word","relatedTerms":[]},
              {"targetLanguageTerm":"스타벅스","nativeLanguageTerm":"Starbucks","confidence":"medium","lexicalUnitType":"Word","relatedTerms":[]}
            ]}
            """);
        var capture = new CapturingChatClient(canned);

        await using var harness = new AppImportHarness(capture, "gpt-5-mini");
        var preview = await harness.ParseAsync(fixture.Text, "Korean", "English");

        Assert.Equal("Free-form text (AI-extracted)", preview.DetectedFormat);
        var prompt = Assert.Single(capture.Calls).Messages.Single().Text;
        Assert.Contains("You are a Korean language learning specialist", prompt);
        Assert.Contains(fixture.Text, prompt);
        Assert.Equal(4, preview.Rows.Count);
        Assert.False(preview.Rows.Single(r => r.TargetLanguageTerm == "민준").IsSelected); // low confidence -> unchecked

        var score = VocabularyCoverage.Score(fixture, preview.Rows);
        Assert.Equal(3, score.SelectedCount);
        Assert.Equal(2, score.AcceptableSelectedCount);            // 회사원, 출장을 가다
        Assert.Equal(["스타벅스 (brand name)"], score.ForbiddenSelected);
        Assert.Equal(["민준"], score.ForbiddenUnchecked);
        Assert.Equal(2, score.Found.Count);                          // 회사원, 출장
    }

    [Theory]
    [InlineData("먹다 (to eat)", "먹다")]
    [InlineData("다음 달", "다음달")]
    [InlineData("-ㄹ 예정이다", "ㄹ예정이다")]
    [InlineData("'시간이 없다'", "시간이없다")]
    public void Normalize_IsPredictable(string input, string expected) =>
        Assert.Equal(expected, KoreanLemma.Normalize(input));

    [Fact]
    public void Fixtures_AreWellFormed()
    {
        var set = ImportVocabularyFixtures.All;
        Assert.Equal(6, set.Fixtures.Count);
        foreach (var f in set.Fixtures)
        {
            var sentences = f.Text.Split(['.', '?', '!'], StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.InRange(sentences, 2, 5);
            Assert.DoesNotContain(',', f.Text); // commas would route prose to the CSV parser (see README)
            Assert.NotEmpty(f.Expected);
            var forbidden = f.Forbidden.Select(x => KoreanLemma.Normalize(x.Term)).ToHashSet();
            Assert.DoesNotContain(f.Expected.SelectMany(e => e.AcceptedForms).Select(KoreanLemma.Normalize), forbidden.Contains);
            Assert.DoesNotContain(f.Allowed.Select(KoreanLemma.Normalize), forbidden.Contains);
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("offline-test", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class RecordingHandler(string assistantContent) : HttpMessageHandler
    {
        public JsonObject? LastBody { get; private set; }

        public Uri? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastBody = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            var payload = new JsonObject
            {
                ["id"] = "chatcmpl-offline",
                ["object"] = "chat.completion",
                ["created"] = 0,
                ["model"] = "offline",
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = assistantContent },
                    ["finish_reason"] = "stop",
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["completion_tokens"] = 1, ["total_tokens"] = 2 },
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class CannedChatClient(string json) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
