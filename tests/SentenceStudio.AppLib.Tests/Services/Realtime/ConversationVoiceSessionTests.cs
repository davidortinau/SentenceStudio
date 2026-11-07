#pragma warning disable MEAI001 // IRealtimeClient is experimental in Microsoft.Extensions.AI.
#pragma warning disable OPENAI002 // OpenAI.Realtime types are experimental in the OpenAI SDK.

using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SentenceStudio.Services.Realtime;
using Xunit;

namespace SentenceStudio.AppLib.Tests.Services.Realtime;

/// <summary>
/// Lifecycle of the Conversation voice session (review of d8df0599..c55822d8): a session ended
/// before it began (End or Back while voice was starting) starts nothing, and a session made for
/// one conversation disposes its own client.
/// </summary>
public sealed class ConversationVoiceSessionTests
{
    [Fact]
    public async Task EndedBeforeBegin_SendsNothingAndStartsNoReceiveLoop()
    {
        var inner = new FakeSession();
        var client = new Mock<IRealtimeClient>();
        client.Setup(c => c.CreateSessionAsync(It.IsAny<RealtimeSessionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(inner);
        var session = await ConversationVoiceSession.StartAsync(client.Object, "instructions", "Korean",
            "gpt-4o-transcribe", [], NullLogger.Instance, disposeClient: true);
        var ended = false;
        session.Ended += () => ended = true;

        await session.DisposeAsync();
        await session.BeginAsync();

        inner.Sent.Should().BeEmpty();
        inner.Streams.Should().Be(0);
        inner.Disposed.Should().BeTrue();
        client.Verify(c => c.Dispose(), Times.Once);
        ended.Should().BeFalse();
    }

    [Fact]
    public async Task SessionCannotOpen_DisposesItsClient()
    {
        var client = new Mock<IRealtimeClient>();
        client.Setup(c => c.CreateSessionAsync(It.IsAny<RealtimeSessionOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));

        var start = () => ConversationVoiceSession.StartAsync(client.Object, "instructions", "Korean",
            "gpt-4o-transcribe", [], NullLogger.Instance, disposeClient: true);

        await start.Should().ThrowAsync<HttpRequestException>();
        client.Verify(c => c.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Pace_OpensWithItsSpeedAndPause_AndAnUpdateKeepsTheVoice()
    {
        var inner = new FakeSession();
        RealtimeSessionOptions? opened = null;
        var client = new Mock<IRealtimeClient>();
        client.Setup(c => c.CreateSessionAsync(It.IsAny<RealtimeSessionOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<RealtimeSessionOptions?, CancellationToken>((options, _) => opened = options)
            .ReturnsAsync(inner);

        await using var session = await ConversationVoiceSession.StartAsync(client.Object, "instructions", "Korean",
            "gpt-4o-transcribe", [], NullLogger.Instance,
            pace: new ConversationVoicePace(0.8f, TimeSpan.FromMilliseconds(1500)));

        opened!.Voice.Should().Be("marin");
        AssertPace(opened, 0.8f, TimeSpan.FromMilliseconds(1500));

        // Out of the provider's range: clamped to 0.25..1.5 and 0.2 s..5 s.
        await session.SetPaceAsync(new ConversationVoicePace(3f, TimeSpan.FromSeconds(30)));

        var update = inner.Sent.Should().ContainSingle().Which.Should().BeOfType<SessionUpdateRealtimeClientMessage>().Which;
        update.Options.Voice.Should().BeNull("the provider refuses a voice change once the partner has spoken");
        update.Options.Instructions.Should().Be("instructions");
        update.Options.Tools.Should().ContainSingle();
        AssertPace(update.Options, 1.5f, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Begin_ReadsTheGivenLineAloud_OrLetsThePartnerSpeakFirst()
    {
        var inner = new FakeSession();
        var client = new Mock<IRealtimeClient>();
        client.Setup(c => c.CreateSessionAsync(It.IsAny<RealtimeSessionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(inner);
        await using var reading = await ConversationVoiceSession.StartAsync(client.Object, "instructions", "Korean",
            "gpt-4o-transcribe", [], NullLogger.Instance);

        await reading.BeginAsync("안녕하세요! 커피 드릴까요?");

        var first = inner.Sent.Should().ContainSingle().Which.Should().BeOfType<CreateResponseRealtimeClientMessage>().Which;
        first.Instructions.Should().Be(ConversationVoiceSession.ReadAloudInstructions("Korean", "안녕하세요! 커피 드릴까요?"));
        first.ToolMode.Should().Be(ChatToolMode.None);
        first.MaxOutputTokens.Should().BeGreaterThan(400);
        // Raw JSON for the provider: a quoted string (an unquoted value fails to serialize).
        first.AdditionalProperties.Should().ContainKey(ConversationVoiceSession.PurposeKey)
            .WhoseValue.Should().Be("\"read_aloud\"");

        var plain = new FakeSession();
        client.Setup(c => c.CreateSessionAsync(It.IsAny<RealtimeSessionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plain);
        await using var speaking = await ConversationVoiceSession.StartAsync(client.Object, "instructions", "Korean",
            "gpt-4o-transcribe", [], NullLogger.Instance);

        await speaking.BeginAsync();

        var response = plain.Sent.Should().ContainSingle().Which.Should().BeOfType<CreateResponseRealtimeClientMessage>().Which;
        response.Instructions.Should().BeNull();
        response.AdditionalProperties.Should().BeNull();
    }

    [Fact]
    public async Task ReadAloudTurn_PlaysItsAudioButRaisesNoTranscript()
    {
        var inner = new FakeSession();
        var client = new Mock<IRealtimeClient>();
        client.Setup(c => c.CreateSessionAsync(It.IsAny<RealtimeSessionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(inner);
        await using var session = await ConversationVoiceSession.StartAsync(client.Object, "instructions", "Korean",
            "gpt-4o-transcribe", [], NullLogger.Instance);
        var audio = new List<string>();
        var transcripts = new List<(string Item, string Text)>();
        session.AudioDelta += audio.Add;
        session.AssistantTranscriptDelta += (item, text) => transcripts.Add((item, text));
        session.AssistantTranscriptCompleted += (item, text) => transcripts.Add((item, text));
        await session.BeginAsync("안녕하세요!");

        // The provider echoes the read-aloud metadata on response.created (as BinaryData).
        inner.Receive(new ResponseCreatedRealtimeServerMessage(RealtimeServerMessageType.ResponseCreated)
        {
            ResponseId = "r1",
            AdditionalProperties = new()
            {
                [ConversationVoiceSession.PurposeKey] = BinaryData.FromString("\"read_aloud\""),
            },
        });
        inner.Receive(Output(RealtimeServerMessageType.OutputAudioDelta, "r1", "i1", audio: "AAA="));
        inner.Receive(Output(RealtimeServerMessageType.OutputAudioTranscriptionDelta, "r1", "i1", text: "안녕"));
        inner.Receive(Output(RealtimeServerMessageType.OutputAudioTranscriptionDone, "r1", "i1", text: "안녕하세요!"));
        // The partner's next turn is an ordinary response.
        inner.Receive(new ResponseCreatedRealtimeServerMessage(RealtimeServerMessageType.ResponseCreated) { ResponseId = "r2" });
        inner.Receive(Output(RealtimeServerMessageType.OutputAudioTranscriptionDelta, "r2", "i2", text: "네"));
        inner.Receive(Output(RealtimeServerMessageType.OutputAudioTranscriptionDone, "r2", "i2", text: "네, 좋아요."));

        await WaitForAsync(() => transcripts.Count == 2);
        audio.Should().Equal("AAA=");
        transcripts.Should().Equal(("i2", "네"), ("i2", "네, 좋아요."));
    }

    private static OutputTextAudioRealtimeServerMessage Output(RealtimeServerMessageType type, string response,
        string item, string? text = null, string? audio = null) =>
        new(type) { ResponseId = response, ItemId = item, Text = text, Audio = audio };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 150 && !condition(); i++)
            await Task.Delay(20);
        condition().Should().BeTrue();
    }

    [Fact]
    public void ReadAloudInstructions_AskForTheLineWordForWord()
    {
        var instructions = ConversationVoiceSession.ReadAloudInstructions("Korean", "  안녕하세요! 커피 드릴까요?  ");

        instructions.Should().Contain("in Korean").And.Contain("word for word");
        instructions.Should().EndWith("\n\n안녕하세요! 커피 드릴까요?");
    }

    private static void AssertPace(RealtimeSessionOptions options, float speed, TimeSpan pause)
    {
        var raw = options.RawRepresentationFactory!.Invoke().Should()
            .BeOfType<OpenAI.Realtime.RealtimeConversationSessionOptions>().Which;
        raw.AudioOptions.OutputAudioOptions.Speed.Should().Be(speed);
        var turn = raw.AudioOptions.InputAudioOptions.TurnDetection.Should()
            .BeOfType<OpenAI.Realtime.RealtimeServerVadTurnDetection>().Which;
        turn.SilenceDuration.Should().Be(pause);
        turn.CreateResponseEnabled.Should().BeTrue();
        turn.InterruptResponseEnabled.Should().BeTrue();
    }

    private sealed class FakeSession : IRealtimeClientSession
    {
        public List<RealtimeClientMessage> Sent { get; } = [];
        public int Streams { get; private set; }
        public bool Disposed { get; private set; }

        public RealtimeSessionOptions? Options => null;

        public Task SendAsync(RealtimeClientMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        private readonly System.Threading.Channels.Channel<RealtimeServerMessage> _incoming =
            System.Threading.Channels.Channel.CreateUnbounded<RealtimeServerMessage>();

        public void Receive(RealtimeServerMessage message) => _incoming.Writer.TryWrite(message);

        public async IAsyncEnumerable<RealtimeServerMessage> GetStreamingResponseAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Streams++;
            await foreach (var message in _incoming.Reader.ReadAllAsync(cancellationToken))
                yield return message;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
