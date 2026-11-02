using System.Collections.Immutable;
using System.Globalization;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

public static class NativeDiaryAccess
{
    public const string Screen = "native.diary.screen";
    public const string Loading = "native.diary.loading";
    public const string Error = "native.diary.error";
    public const string Back = "native.diary.back";
    public const string GoBack = "native.diary.go-back";
    public const string Notices = "native.diary.notices";
    /// <summary>Notices are "native.diary.notice.{index}".</summary>
    public const string Notice = "native.diary.notice";
    public const string DismissNotices = "native.diary.notices.dismiss";
    // List (source Diary.razor)
    public const string List = "native.diary.list";
    public const string Today = "native.diary.today";
    public const string TodayWordCount = "native.diary.today.word-count";
    public const string PastEntries = "native.diary.past";
    /// <summary>Past entry cards are "native.diary.entry.{id}" with ".date", ".snippet", ".word-count", ".feedback" suffixes.</summary>
    public const string Entry = "native.diary.entry";
    public const string Empty = "native.diary.empty";
    // Editor (source DiaryEditor.razor)
    public const string Editor = "native.diary.editor";
    public const string PromptCard = "native.diary.prompt";
    public const string PromptToggle = "native.diary.prompt.toggle";
    public const string PromptText = "native.diary.prompt.text";
    public const string PromptHint = "native.diary.prompt.hint";
    public const string PromptEmpty = "native.diary.prompt.empty";
    public const string NewPrompt = "native.diary.prompt.new";
    public const string Input = "native.diary.input";
    public const string Stars = "native.diary.stars";
    public const string WordCount = "native.diary.word-count";
    public const string FontDecrease = "native.diary.font.decrease";
    public const string FontSize = "native.diary.font.size";
    public const string FontIncrease = "native.diary.font.increase";
    public const string SaveStatus = "native.diary.save-status";
    public const string Save = "native.diary.save";
    // Viewer (source DiaryViewer.razor)
    public const string Viewer = "native.diary.viewer";
    public const string NotFound = "native.diary.not-found";
    public const string ViewerPrompt = "native.diary.viewer.prompt";
    public const string ViewerDate = "native.diary.viewer.date";
    public const string ViewerWordCount = "native.diary.viewer.word-count";
    public const string ViewerContent = "native.diary.viewer.content";
    public const string Feedback = "native.diary.feedback";
    public const string FeedbackButton = "native.diary.feedback.request";
    public const string FeedbackHint = "native.diary.feedback.hint";
    public const string FeedbackRecommended = "native.diary.feedback.recommended";
    public const string FeedbackNotes = "native.diary.feedback.notes";
    public const string FeedbackStrengths = "native.diary.feedback.strengths";
}

public static class NativeDiaryText
{
    public const string Title = "Activity_Diary";
    public const string TodayTitle = "Diary_TodayTitle";
    public const string StartToday = "Diary_StartToday";
    public const string StartTodayHint = "Diary_StartTodayHint";
    public const string ContinueToday = "Diary_ContinueToday";
    public const string PastEntries = "Diary_PastEntries";
    public const string EmptyState = "Diary_EmptyState";
    /// <summary>Format: {0} = word count.</summary>
    public const string WordCount = "Diary_WordCount";
    /// <summary>Format: {0} = word count.</summary>
    public const string Stars = "Diary_Stars";
    /// <summary>Format: {0} = earned stars (0-3).</summary>
    public const string StarsLabel = "Diary_StarsLabel";
    public const string StarsTooltip = "Diary_StarsTooltip";
    public const string TogglePrompt = "Diary_TogglePrompt";
    public const string Prompt = "Diary_Prompt";
    public const string NewPrompt = "Diary_NewPrompt";
    public const string NoPromptYet = "Diary_NoPromptYet";
    public const string EditorPlaceholder = "Diary_EditorPlaceholder";
    public const string Saving = "Diary_Saving";
    public const string Saved = "Diary_Saved";
    public const string Save = "Common_Save";
    public const string SaveFailed = "Diary_SaveFailed";
    public const string PromptFailed = "Diary_PromptFailed";
    public const string NotFound = "Diary_NotFound";
    public const string AiFeedback = "Diary_AiFeedback";
    public const string GetFeedback = "Diary_GetFeedback";
    public const string Regenerate = "Diary_Regenerate";
    public const string Analyzing = "Diary_Analyzing";
    public const string FeedbackHint = "Diary_FeedbackHint";
    public const string FeedbackFailed = "Diary_FeedbackFailed";
    public const string Recommended = "Diary_Recommended";
    public const string Notes = "Diary_Notes";
    public const string Strengths = "Diary_Strengths";
    public const string Today = "Diary_Today";
    public const string Yesterday = "Diary_Yesterday";
    public const string HasFeedback = "Diary_HasFeedback";
    public const string DecreaseFontSize = "Diary_DecreaseFontSize";
    public const string IncreaseFontSize = "Diary_IncreaseFontSize";
    public const string GoBack = "Common_GoBack";
    public const string Dismiss = "Writing_DismissNotices";
    public const string Unavailable = "NativeActivity_LaunchUnavailable";

    /// <summary>Source FormatDate: "ddd, MMM d, yyyy" in the current culture.</summary>
    public static string FormatDate(DateTime date) =>
        date.ToString("ddd, MMM d, yyyy", CultureInfo.CurrentCulture);
}

/// <summary>Which of the three source pages is showing: /diary, /diary/today or /diary/{Id}.</summary>
public enum NativeDiaryPage { List, Editor, Viewer }

public enum NativeDiarySaveStatus { Idle, Saving, Saved }

/// <summary>
/// A list row. <see cref="DateKey"/> is Diary_Today / Diary_Yesterday when
/// the source shows that label instead of the formatted date.
/// </summary>
public sealed record NativeDiaryEntrySummary(
    string Id, DateTime EntryDate, string? DateKey, string Snippet, int WordCount, bool HasFeedback)
{
    public string AccessibilityId => $"{NativeDiaryAccess.Entry}.{Id}";
}

public sealed record NativeDiaryListState(
    bool IsLoading,
    NativeDiaryEntrySummary? Today,
    ImmutableArray<NativeDiaryEntrySummary> PastEntries)
{
    /// <summary>Source shows the empty state only when there is neither a today entry nor a past entry.</summary>
    public bool IsEmpty => !IsLoading && Today is null && PastEntries.IsDefaultOrEmpty;
}

public sealed record NativeDiaryEditorState(
    bool IsLoading,
    string? PromptText,
    string? PromptHint,
    bool IsPromptCollapsed,
    bool IsLoadingPrompt,
    string Content,
    int WordCount,
    int FontSize,
    NativeDiarySaveStatus SaveStatus)
{
    public int Stars => NativeDiaryStars.For(WordCount);

    public bool CanDecreaseFont => FontSize > NativeDiaryPresenter.MinFontSize;

    public bool CanIncreaseFont => FontSize < NativeDiaryPresenter.MaxFontSize;

    public bool CanSave => !IsLoading && SaveStatus != NativeDiarySaveStatus.Saving;
}

public sealed record NativeDiaryViewerState(
    bool IsLoading,
    bool NotFound,
    string? Id = null,
    DateTime EntryDate = default,
    string? PromptText = null,
    string? PromptHint = null,
    string Content = "",
    int WordCount = 0,
    bool HasFeedback = false,
    string? FeedbackRecommended = null,
    string? FeedbackNotes = null,
    string? FeedbackStrengths = null,
    bool IsLoadingFeedback = false)
{
    /// <summary>The entry's target language (source passes it to GetFeedbackAsync).</summary>
    public string? Language { get; init; }

    /// <summary>Source LoadFeedback returns without a request when the entry has no content.</summary>
    public bool CanRequestFeedback => !IsLoading && !NotFound && !IsLoadingFeedback
        && !string.IsNullOrWhiteSpace(Content);
}

/// <summary>
/// Phases: Loading (first list load), Ready (one of the three pages),
/// Blocked (owner absent/changed or launch refused). Destination becomes
/// TodaysPlan after the list's Back (source NavigateTo("/")).
/// </summary>
public sealed record NativeDiaryState(
    NativePhase Phase,
    NativeDestination Destination = NativeDestination.Activity,
    NativeDiaryPage Page = NativeDiaryPage.List,
    NativeDiaryListState? List = null,
    NativeDiaryEditorState? Editor = null,
    NativeDiaryViewerState? Viewer = null,
    ImmutableArray<NativeMessage> Notices = default,
    string? ErrorKey = null,
    bool HasPendingSync = false);

/// <summary>Source "Angry Birds" stars: 0-49 none, 50-100 one, 101-150 two, 151+ three (goal 200).</summary>
public static class NativeDiaryStars
{
    public const int Star1Threshold = 50;
    public const int Star2Threshold = 101;
    public const int Star3Threshold = 151;
    public const int WordGoal = 200;

    public static int For(int wordCount) => wordCount switch
    {
        >= Star3Threshold => 3,
        >= Star2Threshold => 2,
        >= Star1Threshold => 1,
        _ => 0,
    };
}

/// <summary>
/// AI port for Diary: the source GenerateDiaryPrompt (default Fast tier) and
/// GradeDiaryEntry (Reasoning tier) templates through the owner-bound gateway.
/// </summary>
public interface INativeDiaryAi
{
    Task<DiaryPromptResponse?> GeneratePromptAsync(string owner, string targetLanguage,
        string nativeLanguage, CancellationToken cancellationToken);

    Task<DiaryFeedbackResponse?> GetFeedbackAsync(string owner, string content, string? promptText,
        string targetLanguage, string nativeLanguage, CancellationToken cancellationToken);
}
