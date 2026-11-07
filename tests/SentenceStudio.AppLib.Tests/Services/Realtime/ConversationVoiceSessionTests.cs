#pragma warning disable MEAI001 // IRealtimeClient is experimental in Microsoft.Extensions.AI.

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

        public async IAsyncEnumerable<RealtimeServerMessage> GetStreamingResponseAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Streams++;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
