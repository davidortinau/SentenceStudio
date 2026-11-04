using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// When the Compose quiz draws, reports, opens and toggles the turn's photo.
/// The shared presenter owns the photo rules: it loads the owner's bytes
/// (<see cref="NativeQuizPrompt.Photo"/>), asks the head to draw them
/// (<see cref="NativeQuizPrompt.PhotoRequested"/>), and counts the photo as a
/// prompt (<see cref="NativeQuizPrompt.ShowPhoto"/>) only after the head reports
/// that it was drawn, as the iOS SwiftUI image does.
/// </summary>
internal static class AndroidQuizPhotoPolicy
{
    /// <summary>The thumbnail is on screen for this question or its feedback.</summary>
    public static bool ShowsThumbnail(NativeQuizState state) =>
        state.Destination == NativeDestination.VocabularyQuiz
        && state.Phase is NativePhase.Ready or NativePhase.Feedback
        && state.Sentence is null
        && !string.IsNullOrWhiteSpace(state.TurnId)
        && state.Prompt is { PhotoRequested: true, Photo: { Bytes.Length: > 0 } };

    /// <summary>The drawn thumbnail still has to be reported to the presenter.</summary>
    public static bool NeedsRenderReport(NativeQuizState state, string? owner) =>
        !string.IsNullOrWhiteSpace(owner)
        && ShowsThumbnail(state)
        && state.Overlay == NativeQuizOverlay.None
        && state.Prompt?.ShowPhoto == false;

    /// <summary>The thumbnail opens the full-screen viewer (question or feedback).</summary>
    public static bool CanOpenFullScreen(NativeQuizState state) =>
        ShowsThumbnail(state)
        && state.Overlay == NativeQuizOverlay.None
        && state.Prompt is { ShowPhoto: true, PhotoUri: { Length: > 0 } };

    /// <summary>
    /// The Show image / Hide image control: whenever the turn has a loaded photo
    /// that is requested, shown, or switchable by the photo prompt setting.
    /// </summary>
    public static bool ShowsToggle(NativeQuizState state) =>
        state.Destination == NativeDestination.VocabularyQuiz
        && state.Phase is NativePhase.Ready or NativePhase.Feedback
        && state.Sentence is null
        && !string.IsNullOrWhiteSpace(state.TurnId)
        && state.Prompt is { Photo: not null } prompt
        && (prompt.PhotoRequested || prompt.ShowPhoto || prompt.CanTogglePhoto);
}
