using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Contracts.Ai;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// Phase 0 executable baseline for the <c>/api/v1/ai/chat</c> and
/// <c>/api/v1/ai/chat-messages</c> wire contracts, captured before the Agent
/// Framework / MEAI / OpenAI package upgrade so the shapes can be diffed after.
///
/// The full HTTP round-trip lives in <c>tests/SentenceStudio.Api.Tests</c>, which
/// runs in neither CI workflow — <c>.github/workflows/ci.yml</c> and
/// <c>.github/workflows/test.yml</c> both build and test only
/// <c>SentenceStudio.UnitTests</c>. It also cannot be restored on a machine
/// without nuget.org: <c>SentenceStudio.Api</c> references
/// <c>SentenceStudio.ServiceDefaults</c>, whose transitive
/// <c>Microsoft.Extensions.*</c> 11.0.0-preview pins resolve from no other
/// enabled source, so restore fails NU1101 before a single test runs.
/// <c>SentenceStudio.UnitTests</c> is the only test project that avoids that
/// graph entirely (it references just <c>Shared</c> and <c>Sharing</c>), which is
/// why the contract is pinned here at the serialization level — in the one
/// project CI actually runs.
///
/// Three things are locked down:
///
/// 1. <b>Envelope shape.</b> Requests and responses are camelCase on the wire
///    (minimal APIs and <c>PostAsJsonAsync</c> both use
///    <see cref="JsonSerializerDefaults.Web"/>).
/// 2. <b>Typed-response double encoding.</b> A typed result is serialized to JSON
///    by the server and stuffed into <c>ChatResponse.Response</c> as a
///    <i>string</i>; the client then deserializes that string a second time. The
///    inner payload is serialized with STJ <i>defaults</i> (no web naming policy),
///    so DTO wire names are a mixed convention: types carrying
///    <c>[JsonPropertyName]</c> emit snake_case, unattributed types emit
///    PascalCase. The client reconciles both with
///    <c>PropertyNameCaseInsensitive = true</c>. That asymmetry is load-bearing.
/// 3. <b>Allow-list identity.</b> <c>ResolveResponseType</c> matches the client's
///    <c>typeof(T).AssemblyQualifiedName</c> against
///    <c>AiResponseTypeRegistry.AllowedTypes</c> keyed by <c>Type.FullName</c>.
///    A rename or namespace move silently downgrades a typed call to a plain
///    string response — no exception, no build break.
/// </summary>
public class AiChatContractBaselineTests
{
    /// <summary>Options the minimal API and <c>AiApiClient</c> use for the envelope.</summary>
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Options <c>AiGatewayClient</c> uses for the inner typed payload.</summary>
    private static readonly JsonSerializerOptions GatewayOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Snapshot of <c>AiResponseTypeRegistry.AllowedTypes</c>. The registry is
    /// <c>internal</c> to <c>SentenceStudio.Api</c>, so it is mirrored here by
    /// FullName — the exact key the endpoint looks up after stripping assembly
    /// qualification. Adding a DTO to the registry means adding it here too
    /// (see the test-discipline skill: assertions track disk reality).
    /// </summary>
    private static readonly string[] _allowedResponseTypeFullNames =
    {
        "SentenceStudio.Shared.Models.BulkTranslationResponse",
        "SentenceStudio.Shared.Models.ClozureResponse",
        "SentenceStudio.Shared.Models.DiaryFeedbackResponse",
        "SentenceStudio.Shared.Models.DiaryPromptResponse",
        "SentenceStudio.Shared.Models.FreeTextVocabularyExtractionResponse",
        "SentenceStudio.Shared.Models.GradeResponse",
        "SentenceStudio.Shared.Models.Reply",
        "SentenceStudio.Shared.Models.SentencesResponse",
        "SentenceStudio.Shared.Models.ShadowingSentencesResponse",
        "SentenceStudio.Shared.Models.StorytellerResponse",
        "SentenceStudio.Shared.Models.TranslationResponse",
        "SentenceStudio.Shared.Models.VocabularyExtractionResponse",
        "SentenceStudio.Shared.Models.WordAssociationGradeResponse",
        "SentenceStudio.Services.DTOs.GeneratedExampleSentencesDto",
        // Resolved reflectively by the registry because it is internal to Shared.
        "SentenceStudio.Services.ContentClassificationAiResponse",
    };

    // ---------------------------------------------------------------------
    // 1. Envelope shape
    // ---------------------------------------------------------------------

    [Fact]
    public void ChatRequest_SerializesToCamelCaseWireShape()
    {
        var request = new ChatRequest
        {
            Message = "안녕하세요",
            Scenario = "Ordering coffee",
            ResponseType = typeof(Reply).AssemblyQualifiedName,
            Tier = "Reasoning",
            ReasoningEffort = "medium"
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request, WebOptions));
        var root = document.RootElement;

        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            new[] { "message", "scenario", "responseType", "tier", "reasoningEffort" },
            "the /api/v1/ai/chat request contract is camelCase and has exactly these five fields");

        root.GetProperty("message").GetString().Should().Be("안녕하세요");
        root.GetProperty("tier").GetString().Should().Be("Reasoning");
        root.GetProperty("reasoningEffort").GetString().Should().Be("medium");
    }

    [Fact]
    public void ChatRequest_OptionalFieldsSerializeAsNullNotOmitted()
    {
        var request = new ChatRequest { Message = "hello" };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request, WebOptions));
        var root = document.RootElement;

        root.GetProperty("scenario").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("responseType").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("tier").ValueKind.Should().Be(JsonValueKind.Null,
            "a null tier is what makes the endpoint fall back to the fast tier");
        root.GetProperty("reasoningEffort").ValueKind.Should().Be(JsonValueKind.Null,
            "a null effort must not trip the 400 guard in /api/v1/ai/chat");
    }

    [Fact]
    public void ChatResponse_RoundTripsCamelCaseWireShape()
    {
        const string wire = """{"response":"Hello there","language":"ko"}""";

        var response = JsonSerializer.Deserialize<ChatResponse>(wire, WebOptions);

        response.Should().NotBeNull();
        response!.Response.Should().Be("Hello there");
        response.Language.Should().Be("ko");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, WebOptions));
        document.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            new[] { "response", "language" });
    }

    [Fact]
    public void ChatResponse_MissingLanguageDeserializesToNull()
    {
        // The text path returns Language = null; the typed path omits it entirely.
        var response = JsonSerializer.Deserialize<ChatResponse>("""{"response":"hi"}""", WebOptions);

        response.Should().NotBeNull();
        response!.Response.Should().Be("hi");
        response.Language.Should().BeNull();
    }

    [Fact]
    public void ChatMessagesRequest_SerializesRolesAndContentInOrder()
    {
        var request = new ChatMessagesRequest
        {
            Messages =
            {
                new ChatMessageDto { Role = "system", Content = "Be terse." },
                new ChatMessageDto { Role = "user", Content = "Translate 물" },
                new ChatMessageDto { Role = "assistant", Content = "water" }
            },
            Instructions = "Respond in JSON.",
            ResponseType = typeof(TranslationResponse).AssemblyQualifiedName
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request, WebOptions));
        var root = document.RootElement;

        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            new[] { "messages", "instructions", "responseType" });

        var messages = root.GetProperty("messages");
        messages.GetArrayLength().Should().Be(3);
        messages.EnumerateArray().Select(m => m.GetProperty("role").GetString())
            .Should().ContainInOrder(
                new[] { "system", "user", "assistant" },
                "the endpoint maps roles positionally and order is meaningful to the model");
        messages[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            new[] { "role", "content" });
    }

    [Theory]
    [InlineData("assistant")]
    [InlineData("system")]
    [InlineData("user")]
    [InlineData("something-else")]
    public void ChatMessagesRequest_RoleStringsSurviveRoundTripVerbatim(string role)
    {
        // /api/v1/ai/chat-messages switches on the raw role string and defaults
        // unknown values to User, so the transport must not normalize casing.
        var json = JsonSerializer.Serialize(
            new ChatMessagesRequest { Messages = { new ChatMessageDto { Role = role, Content = "x" } } },
            WebOptions);

        var restored = JsonSerializer.Deserialize<ChatMessagesRequest>(json, WebOptions);

        restored!.Messages.Single().Role.Should().Be(role);
    }

    // ---------------------------------------------------------------------
    // 2. Typed-response double encoding
    // ---------------------------------------------------------------------

    [Fact]
    public void TypedResponse_IsCarriedAsAJsonStringInsideTheEnvelope()
    {
        // Server side: JsonSerializer.Serialize(typedResult, responseType) with
        // STJ defaults, then wrapped in ChatResponse.Response.
        var typed = new Reply { Message = "네, 맞아요", Comprehension = 0.8 };
        var envelope = new ChatResponse { Response = JsonSerializer.Serialize(typed, typed.GetType()) };

        var wire = JsonSerializer.Serialize(envelope, WebOptions);

        using var document = JsonDocument.Parse(wire);
        var responseProperty = document.RootElement.GetProperty("response");
        responseProperty.ValueKind.Should().Be(JsonValueKind.String,
            "the typed payload is double-encoded — a nested object here would break AiGatewayClient");

        // Client side: AiGatewayClient deserializes the inner string case-insensitively.
        var inner = JsonSerializer.Deserialize<Reply>(responseProperty.GetString()!, GatewayOptions);

        inner.Should().NotBeNull();
        inner!.Message.Should().Be("네, 맞아요");
        inner.Comprehension.Should().Be(0.8);
    }

    [Fact]
    public void TypedResponse_AttributedDtoKeepsItsExplicitSnakeCaseWireNames()
    {
        // The typed DTOs use a MIXED naming convention, and the inner payload is
        // serialized with STJ defaults (no naming policy). So [JsonPropertyName]
        // is the only thing controlling the wire names on these types — the model
        // is prompted against exactly these names.
        var inner = JsonSerializer.Serialize(new Reply(), typeof(Reply));

        using var document = JsonDocument.Parse(inner);
        document.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            new[]
            {
                "message",
                "comprehension_score",
                "comprehension_notes",
                "grammar_corrections",
                "vocabulary_analysis"
            },
            "Reply declares explicit [JsonPropertyName] snake_case names; renaming a CLR " +
            "property without updating the attribute silently changes the AI wire contract");
    }

    [Fact]
    public void TypedResponse_UnattributedDtoFallsBackToPascalCaseClrNames()
    {
        // Counterpart to the attributed case: DTOs without [JsonPropertyName] emit
        // PascalCase, because the endpoint serializes the inner payload with
        // JsonSerializer.Serialize(value, type) and NOT with web defaults.
        var inner = JsonSerializer.Serialize(new DiaryPromptResponse(), typeof(DiaryPromptResponse));

        using var document = JsonDocument.Parse(inner);
        document.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            new[] { "Prompt", "Hint" },
            "the inner typed payload uses STJ defaults, so unattributed DTOs stay PascalCase — " +
            "a serializer default change here would break every unattributed response DTO at once");
    }

    [Fact]
    public void TypedResponse_ClientReadsBothNamingConventions()
    {
        // AiGatewayClient uses PropertyNameCaseInsensitive = true and nothing else,
        // which is what lets a single client handle both conventions above.
        var attributed = JsonSerializer.Serialize(
            new Reply { Message = "좋아요", Comprehension = 0.9 }, typeof(Reply));
        var unattributed = JsonSerializer.Serialize(
            new DiaryPromptResponse { Prompt = "오늘 뭐 했어요?", Hint = "Describe your day." },
            typeof(DiaryPromptResponse));

        JsonSerializer.Deserialize<Reply>(attributed, GatewayOptions)!
            .Message.Should().Be("좋아요");
        JsonSerializer.Deserialize<Reply>(attributed, GatewayOptions)!
            .Comprehension.Should().Be(0.9);

        var prompt = JsonSerializer.Deserialize<DiaryPromptResponse>(unattributed, GatewayOptions);
        prompt!.Prompt.Should().Be("오늘 뭐 했어요?");
        prompt.Hint.Should().Be("Describe your day.");

        // Case-insensitivity is load-bearing, not incidental: a model that echoes
        // "prompt" instead of "Prompt" must still bind.
        JsonSerializer.Deserialize<DiaryPromptResponse>(
            """{"prompt":"lower","hint":"case"}""", GatewayOptions)!
            .Prompt.Should().Be("lower",
                "dropping PropertyNameCaseInsensitive from AiGatewayClient would silently " +
                "return an all-defaults object instead of failing loudly");
    }

    [Fact]
    public void TypedResponse_EmptyEnvelopeYieldsNoPayload()
    {
        // AiGatewayClient short-circuits on a blank Response rather than throwing.
        var envelope = JsonSerializer.Deserialize<ChatResponse>("""{"response":""}""", WebOptions);

        envelope.Should().NotBeNull();
        string.IsNullOrWhiteSpace(envelope!.Response).Should().BeTrue();
    }

    // ---------------------------------------------------------------------
    // 3. Allow-list identity
    // ---------------------------------------------------------------------

    [Fact]
    public void AllowedResponseTypes_ResolveByFullNameFromTheSharedAssembly()
    {
        var sharedAssembly = typeof(Reply).Assembly;
        var unresolved = _allowedResponseTypeFullNames
            .Where(name => sharedAssembly.GetType(name, throwOnError: false) is null)
            .ToList();

        unresolved.Should().BeEmpty(
            "AiResponseTypeRegistry keys its allow-list by Type.FullName. A renamed or moved " +
            "DTO drops out of the allow-list silently and every typed AI call for it degrades " +
            "to a plain-string response with no exception and no build error.");
    }

    [Fact]
    public void AllowedResponseTypes_AreConstructibleAndSerializable()
    {
        var sharedAssembly = typeof(Reply).Assembly;

        foreach (var name in _allowedResponseTypeFullNames)
        {
            var type = sharedAssembly.GetType(name, throwOnError: false);
            type.Should().NotBeNull($"{name} must exist in SentenceStudio.Shared");

            type!.GetConstructor(Type.EmptyTypes).Should().NotBeNull(
                $"{name} needs a public parameterless constructor — the endpoint serializes it " +
                "with JsonSerializer.Serialize(value, type) and AiGatewayClient deserializes it " +
                "with no JsonConstructor hints");

            var instance = Activator.CreateInstance(type)!;
            var json = JsonSerializer.Serialize(instance, type);
            JsonSerializer.Deserialize(json, type, GatewayOptions).Should().NotBeNull(
                $"{name} must survive the server-serialize / client-deserialize round trip");
        }
    }

    [Fact]
    public void AllowedResponseTypes_SnapshotMatchesTheRegistrySource()
    {
        var repoRoot = FindRepoRoot();
        var registrySource = Path.Combine(
            repoRoot, "src", "SentenceStudio.Api", "AiResponseTypeRegistry.cs");

        Assert.True(
            File.Exists(registrySource),
            $"Expected AiResponseTypeRegistry.cs at {registrySource}. If it moved, update this guard.");

        var source = File.ReadAllText(registrySource);

        var missingFromSource = _allowedResponseTypeFullNames
            .Select(fullName => fullName[(fullName.LastIndexOf('.') + 1)..])
            .Where(simpleName => !source.Contains(simpleName, StringComparison.Ordinal))
            .ToList();

        missingFromSource.Should().BeEmpty(
            "these types are in this test's snapshot but no longer appear in " +
            "AiResponseTypeRegistry.cs — remove them here in the same commit");

        // Catch the other direction: a typeof(...) entry added to the registry
        // without extending this baseline.
        var registryEntries = System.Text.RegularExpressions.Regex
            .Matches(source, @"typeof\((?<name>[A-Za-z0-9_]+)\)")
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var snapshotSimpleNames = _allowedResponseTypeFullNames
            .Select(fullName => fullName[(fullName.LastIndexOf('.') + 1)..])
            .ToHashSet(StringComparer.Ordinal);

        registryEntries.Where(name => !snapshotSimpleNames.Contains(name)).Should().BeEmpty(
            "a response DTO was added to AiResponseTypeRegistry without being added to this " +
            "package-upgrade baseline — add it to _allowedResponseTypeFullNames");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src"))
                && File.Exists(Path.Combine(dir.FullName, "src", "SentenceStudio.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate repo root (expected src/SentenceStudio.sln).");
    }
}
