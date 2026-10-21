#pragma warning disable MEAI001 // IRealtimeClient is experimental in Microsoft.Extensions.AI.

using System.Threading.Channels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SentenceStudio.Services.Realtime;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.WebUI.Pages;

/// <summary>
/// Voice mode for the Conversation activity: a live, spoken exchange with the scenario's partner.
/// </summary>
/// <remarks>
/// The realtime session runs where this component runs (on the server for the web app), so the
/// browser or WebView only captures and plays audio. Spoken turns land in the same thread as typed
/// turns, so the conversation can continue in text afterwards.
/// </remarks>
public partial class Conversation
{
    [Inject] private IServiceProvider Services { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IConfiguration Configuration { get; set; } = default!;

    private enum VoiceState { Off, Connecting, Listening, PartnerSpeaking }

    private VoiceState voiceState = VoiceState.Off;
    private bool voiceAvailable;
    private double micLevel;
    private IRealtimeClient? realtimeClient;
    private ConversationVoiceSession? voiceSession;
    private IJSObjectReference? voiceModule;
    private DotNetObjectReference<Conversation>? voiceRef;
    private Channel<string>? playbackQueue;

    private readonly HashSet<ConversationChunk> voiceChunks = new();
    private readonly Dictionary<ConversationChunk, List<string>> toolNotes = new();
    private readonly List<string> pendingToolNotes = new();
    private readonly Dictionary<string, ConversationChunk> voiceUserChunks = new();
    private readonly Dictionary<string, ConversationChunk> voiceAssistantChunks = new();

    private bool IsVoiceOn => voiceState != VoiceState.Off;

    // Matches the persona the voice instructions use when no scenario is selected.
    private string PartnerName => string.IsNullOrWhiteSpace(activeScenario?.PersonaName)
        ? SentenceStudio.Services.ConversationService.DefaultPersonaName
        : activeScenario!.PersonaName;

    private void InitializeVoice()
    {
        realtimeClient = Services.GetService<IRealtimeClient>();
        voiceAvailable = realtimeClient is not null;
    }

    private Task ToggleVoice() => IsVoiceOn ? StopVoiceAsync() : StartVoiceAsync();

    private async Task StartVoiceAsync()
    {
        if (realtimeClient is null || IsVoiceOn)
        {
            return;
        }

        StopAudio();
        voiceState = VoiceState.Connecting;
        StateHasChanged();

        try
        {
            var instructions = await ConversationSvc.GetVoiceInstructionsAsync(activeScenario, targetLanguage, chunks);
            voiceSession = await ConversationVoiceSession.StartAsync(
                realtimeClient,
                instructions,
                targetLanguage,
                RealtimeVoiceRegistration.TranscriptionModel(Configuration),
                userVocabulary,
                Logger);
            WireVoiceEvents(voiceSession);

            playbackQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            voiceModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./_content/SentenceStudio.UI/js/realtime-voice.js");
            voiceRef ??= DotNetObjectReference.Create(this);
            await voiceModule.InvokeAsync<bool>("start", voiceRef, new { halfDuplex = true });
            await voiceModule.InvokeVoidAsync("followThread", chatContainer);
            _ = PumpPlaybackAsync(playbackQueue.Reader);

            voiceState = VoiceState.Listening;
            await voiceSession.BeginAsync();
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Voice mode could not open the microphone");
            Toast.ShowError(Localize["Conversation_VoiceMicDenied"]);
            await StopVoiceAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Voice mode could not start");
            Toast.ShowError(Localize["Conversation_VoiceUnavailable"]);
            await StopVoiceAsync();
        }
    }

    private void WireVoiceEvents(ConversationVoiceSession session)
    {
        session.AudioDelta += audio =>
        {
            playbackQueue?.Writer.TryWrite(audio);
            if (voiceState == VoiceState.Listening)
            {
                _ = InvokeAsync(() => { voiceState = VoiceState.PartnerSpeaking; StateHasChanged(); });
            }
        };

        session.SpeechStarted += () => _ = InvokeAsync(async () =>
        {
            while (playbackQueue?.Reader.TryRead(out _) == true) { }
            await SafeVoiceJsAsync("clearPlayback");
            voiceState = VoiceState.Listening;
            StateHasChanged();
        });

        session.UserTurnStarted += itemId => _ = InvokeAsync(() =>
        {
            if (voiceUserChunks.ContainsKey(itemId))
            {
                return;
            }

            var chunk = NewVoiceChunk(ConversationRole.User, "Me");
            voiceUserChunks[itemId] = chunk;
            StateHasChanged();
        });

        session.UserTranscriptCompleted += (itemId, text) => _ = InvokeAsync(() =>
        {
            if (!voiceUserChunks.TryGetValue(itemId, out var chunk))
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                chunk = NewVoiceChunk(ConversationRole.User, "Me");
                voiceUserChunks[itemId] = chunk;
            }

            chunk.Text = string.IsNullOrWhiteSpace(text) ? "…" : text;
            StateHasChanged();
        });

        session.AssistantTranscriptDelta += (itemId, delta) => _ = InvokeAsync(() =>
        {
            AssistantChunkFor(itemId).Text += delta;
            StateHasChanged();
        });

        session.AssistantTranscriptCompleted += (itemId, text) => _ = InvokeAsync(() =>
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                AssistantChunkFor(itemId).Text = text;
            }
            StateHasChanged();
        });

        session.ToolInvoked += (term, summary) => _ = InvokeAsync(() =>
        {
            pendingToolNotes.Add($"{term} → {summary}");
            StateHasChanged();
        });

        session.Failed += message => _ = InvokeAsync(() =>
        {
            Logger.LogWarning("Voice session reported: {Message}", message);
        });
    }

    private ConversationChunk AssistantChunkFor(string itemId)
    {
        if (voiceAssistantChunks.TryGetValue(itemId, out var existing))
        {
            return existing;
        }

        var chunk = NewVoiceChunk(ConversationRole.Assistant, PartnerName);
        voiceAssistantChunks[itemId] = chunk;
        if (pendingToolNotes.Count > 0)
        {
            toolNotes[chunk] = new List<string>(pendingToolNotes);
            pendingToolNotes.Clear();
        }

        return chunk;
    }

    private ConversationChunk NewVoiceChunk(ConversationRole role, string author)
    {
        var chunk = new ConversationChunk { Text = "", Author = author, Role = role, SentTime = DateTime.Now };
        voiceChunks.Add(chunk);
        chunks.Add(chunk);
        return chunk;
    }

    private async Task PumpPlaybackAsync(ChannelReader<string> reader)
    {
        try
        {
            await foreach (var audio in reader.ReadAllAsync())
            {
                await InvokeAsync(() => SafeVoiceJsAsync("enqueue", audio));
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Voice playback pump stopped");
        }
    }

    private async Task SafeVoiceJsAsync(string identifier, params object?[] args)
    {
        if (voiceModule is null)
        {
            return;
        }

        try
        {
            await voiceModule.InvokeVoidAsync(identifier, args);
        }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
        catch (TaskCanceledException) { }
    }

    /// <summary>Called by realtime-voice.js with 100 ms of PCM16 microphone audio.</summary>
    [JSInvokable]
    public Task OnMicAudio(byte[] pcm16) => voiceSession?.AppendAudioAsync(pcm16) ?? Task.CompletedTask;

    /// <summary>Called by realtime-voice.js about four times a second with the input level (0-1).</summary>
    [JSInvokable]
    public Task OnMicLevel(double level)
    {
        micLevel = level;
        return InvokeAsync(StateHasChanged);
    }

    /// <summary>Called by realtime-voice.js when queued partner audio has finished playing.</summary>
    [JSInvokable]
    public Task OnPlaybackIdle()
    {
        if (voiceState != VoiceState.PartnerSpeaking)
        {
            return Task.CompletedTask;
        }

        voiceState = VoiceState.Listening;
        return InvokeAsync(StateHasChanged);
    }

    private async Task StopVoiceAsync()
    {
        var wasOn = IsVoiceOn;
        voiceState = VoiceState.Off;
        playbackQueue?.Writer.TryComplete();
        playbackQueue = null;

        await SafeVoiceJsAsync("stop");

        if (voiceSession is not null)
        {
            var session = voiceSession;
            voiceSession = null;
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Voice session dispose failed");
            }
        }

        // Keep spoken turns in the thread; drop placeholders that never received words.
        chunks.RemoveAll(c => voiceChunks.Contains(c) && (string.IsNullOrWhiteSpace(c.Text) || c.Text == "…"));
        voiceUserChunks.Clear();
        voiceAssistantChunks.Clear();
        pendingToolNotes.Clear();
        micLevel = 0;

        if (wasOn)
        {
            StateHasChanged();
        }
    }

    private async Task DisposeVoiceAsync()
    {
        await StopVoiceAsync();
        if (voiceModule is not null)
        {
            try
            {
                await voiceModule.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
            catch (ObjectDisposedException) { }
        }

        voiceModule = null;
        voiceRef?.Dispose();
        voiceRef = null;
    }

    private string VoiceStatusText => voiceState switch
    {
        VoiceState.Connecting => Localize["Conversation_VoiceConnecting"],
        VoiceState.PartnerSpeaking => string.Format(Localize["Conversation_VoiceSpeaking"], PartnerName),
        _ => Localize["Conversation_VoiceListening"],
    };
}
