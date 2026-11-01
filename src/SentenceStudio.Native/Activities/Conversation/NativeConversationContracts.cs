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
}

public enum NativeConversationNoticeKind { Info, Error }

/// <summary>A source toast shown inline; <see cref="Key"/> is a localization key.</summary>
public sealed record NativeConversationNotice(NativeConversationNoticeKind Kind, string Key);

public sealed record NativeConversationScenarioOption(int Id, string DisplayName, string PersonaName);

public sealed record NativeConversationCorrection(string? Original, string? Corrected, string? Explanation);

/// <summary>One chat bubble, oldest first. User bubbles carry the AI's corrections and comprehension of them.</summary>
public sealed record NativeConversationMessage(
    int Index,
    bool IsUser,
    string Author,
    string Text,
    ImmutableArray<NativeConversationCorrection> Corrections,
    double Comprehension)
{
    public string AccessibilityId => $"{NativeConversationAccess.Message}.{Index}";

    /// <summary>Source shows "Comprehension: n%" only when the score is positive.</summary>
    public bool ShowsComprehension => Comprehension > 0;
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
    int? ActiveScenarioId = null,
    string TargetLanguage = "Korean",
    ImmutableArray<NativeConversationMessage> Messages = default,
    bool IsStarting = false,
    bool IsWaitingForReply = false,
    bool IsPlayingAudio = false,
    bool ShowQuickPhrases = false,
    string Draft = "",
    int DraftVersion = 0,
    ImmutableArray<NativeConversationNotice> Notices = default,
    string? ErrorKey = null,
    bool HasPendingSync = false)
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

    public bool CanSend => Phase == NativePhase.Ready && !IsWaitingForReply
        && !string.IsNullOrWhiteSpace(Draft);

    public bool CanEdit => Phase == NativePhase.Ready && !IsWaitingForReply;
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
