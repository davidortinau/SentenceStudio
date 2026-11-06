using System.Collections.Immutable;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

public static class NativeConversationAccess
{
    public const string Screen = "native.conversation.screen";
    public const string Loading = "native.conversation.loading";
    public const string Blocked = "native.conversation.blocked";
    public const string BlockedBack = "native.conversation.blocked.back";
    public const string Notices = "native.conversation.notices";
    /// <summary>Notices are "native.conversation.notice.{index}".</summary>
    public const string Notice = "native.conversation.notice";
    public const string DismissNotices = "native.conversation.notices.dismiss";
    public const string Scenario = "native.conversation.scenario";
    public const string Language = "native.conversation.language";
    /// <summary>Language rows are "native.conversation.language.{name}".</summary>
    public const string LanguageOption = "native.conversation.language";
    public const string Messages = "native.conversation.messages";
    /// <summary>
    /// Bubbles are "native.conversation.message.{index}" with ".text",
    /// ".correction.{n}", ".comprehension" and ".listen" suffixes.
    /// </summary>
    public const string Message = "native.conversation.message";
    public const string Typing = "native.conversation.typing";
    public const string Audio = "native.conversation.audio";
    public const string AudioStop = "native.conversation.audio.stop";
    public const string Input = "native.conversation.input";
    public const string QuickPhrasesToggle = "native.conversation.quick-phrases";
    /// <summary>Quick phrase buttons are "native.conversation.quick-phrase.{index}".</summary>
    public const string QuickPhrase = "native.conversation.quick-phrase";
    public const string Send = "native.conversation.send";
    public const string Back = "native.conversation.back";
    /// <summary>The waveform button in the message field that starts voice mode.</summary>
    public const string VoiceStart = "native.conversation.voice.start";
    /// <summary>The panel that replaces the composer while voice mode runs.</summary>
    public const string VoicePanel = "native.conversation.voice";
    public const string VoiceStatus = "native.conversation.voice.status";
    public const string VoiceLevel = "native.conversation.voice.level";
    public const string VoiceStop = "native.conversation.voice.stop";
}

public static class NativeConversationText
{
    public const string Title = "Activity_Conversation";
    public const string Starting = "Conversation_StartingConversation";
    public const string Scenario = "Conversation_Scenario";
    public const string TypeMessage = "Conversation_TypeMessage";
    public const string Send = "Conversation_Send";
    public const string Listen = "Conversation_Listen";
    /// <summary>Format: {0} = whole comprehension score.</summary>
    public const string Comprehension = "Conversation_Comprehension";
    public const string Stop = "Conversation_Stop";
    public const string PlayingAudio = "Conversation_PlayingAudio";
    public const string GoBack = "Common_GoBack";
    // Source toasts (Conversation.razor), localized instead of hard-coded English.
    public const string LoadFailed = "NativeConversation_LoadFailed";
    public const string StartFailed = "NativeConversation_StartFailed";
    public const string ReplyFailed = "NativeConversation_ReplyFailed";
    public const string AudioFailed = "NativeConversation_AudioFailed";
    public const string AudioOffline = "OfflineAudioUnavailable";
    public const string ChangeLanguage = "NativeConversation_ChangeLanguage";
    public const string QuickPhrases = "NativeConversation_QuickPhrases";
    public const string Dismiss = "NativeConversation_DismissNotices";
    public const string Unavailable = "NativeActivity_LaunchUnavailable";
    // Voice mode (Techorama demo strings).
    public const string Voice = "Conversation_Voice";
    public const string VoiceStart = "Conversation_VoiceStart";
    public const string VoiceEnd = "Conversation_VoiceEnd";
    public const string VoiceConnecting = "Conversation_VoiceConnecting";
    public const string VoiceListening = "Conversation_VoiceListening";
    /// <summary>Format: {0} = the partner's name.</summary>
    public const string VoiceSpeaking = "Conversation_VoiceSpeaking";
    public const string VoiceTranscribing = "Conversation_VoiceTranscribing";
    /// <summary>Format: {0} = "term → result".</summary>
    public const string VoiceToolNote = "Conversation_VoiceToolNote";
    public const string VoiceUnavailable = "Conversation_VoiceUnavailable";
    public const string VoiceMicDenied = "Conversation_VoiceMicDenied";
}

/// <summary>Voice mode: Off, opening the session, the learner's turn, or the partner speaking.</summary>
public enum NativeConversationVoice
{
    Off,
    Connecting,
    Listening,
    PartnerSpeaking,
}

public sealed record NativeConversationScenarioOption(string Id, string DisplayName, string PersonaName);

public sealed record NativeConversationCorrection(string? Original, string? Corrected, string? Explanation);

/// <summary>
/// One chat bubble, oldest first. User bubbles carry the AI's corrections and comprehension of them.
/// Spoken turns (<see cref="IsVoice"/>) show their transcript; the partner's spoken turns carry
/// the vocabulary lookups made before them.
/// </summary>
public sealed record NativeConversationMessage(
    int Index,
    bool IsUser,
    string Author,
    string Text,
    ImmutableArray<NativeConversationCorrection> Corrections,
    double Comprehension,
    bool IsVoice = false,
    ImmutableArray<string> ToolNotes = default)
{
    public string AccessibilityId => $"{NativeConversationAccess.Message}.{Index}";

    /// <summary>Source shows "Comprehension: n%" only when the score is positive.</summary>
    public bool ShowsComprehension => Comprehension > 0;

    /// <summary>A spoken turn whose words have not been transcribed yet.</summary>
    public bool IsTranscribing => IsVoice && string.IsNullOrEmpty(Text);

    /// <summary>Listen replays a typed partner turn; a spoken turn was already heard.</summary>
    public bool CanListen => !IsUser && !IsVoice;
}

/// <summary>
/// Phases: Loading ("Starting conversation" while the opening line is pending
/// and there are no messages), Ready (selector, chat and input), Blocked
/// (owner absent/changed or launch refused). Destination becomes TodaysPlan
/// after Back.
/// </summary>
public sealed record NativeConversationState(
    NativePhase Phase,
    NativeDestination Destination = NativeDestination.Activity,
    ImmutableArray<NativeConversationScenarioOption> Scenarios = default,
    string? ActiveScenarioId = null,
    string TargetLanguage = "Korean",
    ImmutableArray<NativeConversationMessage> Messages = default,
    bool IsStarting = false,
    bool IsWaitingForReply = false,
    bool IsPlayingAudio = false,
    bool ShowQuickPhrases = false,
    string Draft = "",
    int DraftVersion = 0,
    ImmutableArray<NativeMessage> Notices = default,
    string? ErrorKey = null,
    bool HasPendingSync = false,
    NativeConversationVoice Voice = NativeConversationVoice.Off,
    bool IsVoiceAvailable = false,
    double MicLevel = 0,
    string PartnerName = "")
{
    /// <summary>Source toolbar dropdown: SentenceStudio.Common.Constants.Languages.</summary>
    public static ImmutableArray<string> Languages { get; } =
        [.. SentenceStudio.Common.Constants.Languages];

    /// <summary>Source helper phrases: eight Korean literals regardless of target language.</summary>
    public static ImmutableArray<string> QuickPhrases { get; } =
    [
        "이게 뭐예요?",
        "더 자세히 설명해 주세요.",
        "이해해요.",
        "다시 말해 주세요.",
        "조금만 할 수 있어요.",
        "도와줘서 감사합니다.",
        "천천히 말해 주세요.",
        "그게 무슨 뜻이에요?",
    ];

    public bool CanSend => CanEdit && !string.IsNullOrWhiteSpace(Draft);

    public bool CanEdit => Phase == NativePhase.Ready && !IsWaitingForReply
        && Voice == NativeConversationVoice.Off;

    public bool IsVoiceOn => Voice != NativeConversationVoice.Off;

    /// <summary>Voice starts from a ready chat with no reply pending and the opening line in.</summary>
    public bool CanStartVoice => IsVoiceAvailable && CanEdit && !IsStarting;
}

/// <summary>
/// AI port for Conversation: the source ConversationService.StartConversation
/// and ContinueConversation prompts (Fast tier) through the owner-bound
/// native gateway.
/// </summary>
public interface INativeConversationAi
{
    Task<string?> StartAsync(string owner, ConversationScenario? scenario, string targetLanguage,
        CancellationToken cancellationToken);

    Task<Reply?> ContinueAsync(string owner, IReadOnlyList<ConversationChunk> chunks,
        ConversationScenario? scenario, string targetLanguage, CancellationToken cancellationToken);
}

/// <summary>Source IScenarioService.GetAllScenariosAsync over the owner's device store.</summary>
public interface INativeConversationScenarios
{
    Task<IReadOnlyList<ConversationScenario>> GetAllAsync(string owner, CancellationToken cancellationToken);
}

/// <summary>What voice mode needs to open a session: the same scenario, language, recent turns and owner vocabulary as the text chat.</summary>
public sealed record NativeConversationVoiceRequest(
    ConversationScenario? Scenario,
    string TargetLanguage,
    IReadOnlyList<ConversationChunk> RecentTurns,
    IReadOnlyList<VocabularyWord> Vocabulary);

/// <summary>
/// Opens a realtime voice session for the owner: the API mints a short-lived client secret for
/// the signed-in owner, and the device connects to the realtime model with it.
/// </summary>
public interface INativeConversationVoice
{
    Task<SentenceStudio.Services.Realtime.IConversationVoiceSession> StartAsync(
        string owner, NativeConversationVoiceRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The platform's microphone and speaker for voice mode. Audio is PCM16 mono at 24 kHz in both
/// directions. The platform keeps the microphone quiet while partner audio plays (and a short
/// tail after it), so a speaker does not feed the partner's voice back as the learner's turn.
/// Callbacks may arrive on any thread.
/// </summary>
public interface INativeVoiceAudio
{
    /// <summary>True when the microphone may be used; asks the learner on first use.</summary>
    Task<bool> RequestMicrophoneAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts capture and playback. <paramref name="microphone"/> receives about 100 ms of audio
    /// at a time, <paramref name="level"/> the input level (0 to 1), and
    /// <paramref name="playbackIdle"/> fires when queued partner audio has finished.
    /// Throws when the audio hardware cannot start.
    /// </summary>
    void Start(Action<byte[]> microphone, Action<double> level, Action playbackIdle);

    /// <summary>Queues partner audio after any already queued.</summary>
    void Play(byte[] pcm16);

    /// <summary>Drops queued partner audio (the learner started speaking).</summary>
    void ClearPlayback();

    /// <summary>Stops capture and playback and releases the audio session. Safe to call more than once.</summary>
    void Stop();
}
