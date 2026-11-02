using System.Collections.Immutable;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

public static class NativeSceneAccess
{
    public const string Screen = "native.scene.screen";
    public const string Loading = "native.scene.loading";
    public const string Empty = "native.scene.empty";
    public const string EmptyGallery = "native.scene.empty.gallery";
    public const string EmptyBack = "native.scene.empty.back";
    public const string Error = "native.scene.error";
    public const string OpenGallery = "native.scene.gallery.open";
    public const string Image = "native.scene.image";
    public const string ImageLoading = "native.scene.image.loading";
    public const string ImageFailed = "native.scene.image.failed";
    public const string Results = "native.scene.results";
    public const string ResultsEmpty = "native.scene.results.empty";
    /// <summary>Result cards are "native.scene.result.{index}" (index = grading order, so ids are stable; the list shows the newest first) with ".answer", ".accuracy", ".explanation", ".suggested", ".details", ".toggle" suffixes.</summary>
    public const string Result = "native.scene.result";
    public const string Input = "native.scene.input";
    public const string NewScene = "native.scene.new";
    public const string Grade = "native.scene.grade";
    public const string Notices = "native.scene.notices";
    /// <summary>Notices are "native.scene.notice.{index}".</summary>
    public const string Notice = "native.scene.notice";
    public const string DismissNotices = "native.scene.notices.dismiss";
    public const string Gallery = "native.scene.gallery";
    public const string GalleryClose = "native.scene.gallery.close";
    public const string GallerySelect = "native.scene.gallery.select";
    public const string GalleryDelete = "native.scene.gallery.delete";
    public const string GalleryUrl = "native.scene.gallery.url";
    public const string GalleryAdd = "native.scene.gallery.add";
    public const string GalleryPickPhoto = "native.scene.gallery.photo";
    public const string GalleryLoading = "native.scene.gallery.loading";
    public const string GalleryEmpty = "native.scene.gallery.empty";
    /// <summary>Gallery tiles are "native.scene.gallery.item.{imageId}" with ".current" and ".checked" suffixes.</summary>
    public const string GalleryItem = "native.scene.gallery.item";
    public const string Back = "native.scene.back";
}

public static class NativeSceneText
{
    public const string Title = "Activity_DescribeScene";
    public const string Loading = "Scene_LoadingScene";
    public const string Results = "Scene_Results";
    public const string SubmitDescription = "Scene_SubmitDescription";
    public const string DescribeWhatYouSee = "Scene_DescribeWhatYouSee";
    public const string NoSceneImages = "Scene_NoSceneImages";
    public const string BrowseGallery = "Scene_BrowseGallery";
    public const string ImageGallery = "Scene_ImageGallery";
    /// <summary>Format: {0} = selected count.</summary>
    public const string Delete = "Scene_Delete";
    public const string AddImage = "Scene_AddImage";
    public const string PasteImageUrl = "Scene_PasteImageURL";
    public const string Current = "Scene_Current";
    public const string Fluency = "Writing_Fluency";
    public const string GoBack = "Common_GoBack";
    public const string Cancel = "Common_Cancel";
    public const string Unavailable = "NativeActivity_LaunchUnavailable";
    // Source hard-coded English (Scene.razor), localized for native.
    public const string More = "NativeScene_More";
    public const string Less = "NativeScene_Less";
    public const string Grade = "Writing_Grade";
    public const string SelectMultiple = "NativeScene_SelectMultiple";
    public const string GalleryEmpty = "NativeScene_GalleryEmpty";
    public const string ChoosePhoto = "NativeScene_ChoosePhoto";
    public const string SceneImage = "NativeScene_SceneImage";
    public const string ImageFailed = "NativeScene_ImageFailed";
    public const string Dismiss = "NativeScene_DismissNotices";
    public const string NoImagesFound = "NativeScene_NoImagesFound";
    public const string LoadFailed = "NativeScene_LoadFailed";
    public const string GradeFailed = "NativeScene_GradeFailed";
    /// <summary>Format: {0} = whole accuracy percent.</summary>
    public const string GradeExcellent = "NativeScene_GradeExcellent";
    /// <summary>Format: {0} = whole accuracy percent.</summary>
    public const string GradeGood = "NativeScene_GradeGood";
    /// <summary>Format: {0} = whole accuracy percent.</summary>
    public const string GradeKeepPracticing = "NativeScene_GradeKeepPracticing";
    public const string GalleryLoadFailed = "NativeScene_GalleryLoadFailed";
    public const string ImageAdded = "NativeScene_ImageAdded";
    public const string ImageSaveFailed = "NativeScene_ImageSaveFailed";
    public const string InvalidUrl = "NativeScene_InvalidUrl";
    /// <summary>Format: {0} = deleted count.</summary>
    public const string Deleted = "NativeScene_Deleted";
    public const string DeleteFailed = "NativeScene_DeleteFailed";
    public const string PhotoFailed = "NativeScene_PhotoFailed";
    public const string PhotoTooLarge = "NativeScene_PhotoTooLarge";
}

/// <summary>Where the current or a gallery image's bytes stand.</summary>
public enum NativeSceneImageLoad { Loading, Loaded, Failed }

/// <summary>The scene image on the page (source imageUrl / currentSceneId).</summary>
public sealed record NativeSceneImage(int Id, NativeSceneImageLoad Load, byte[]? Bytes);

/// <summary>A graded description card (source Sentence), newest first.</summary>
public sealed record NativeSceneResult(
    int Index,
    string Answer,
    double Accuracy,
    double Fluency,
    string? AccuracyExplanation,
    string? FluencyExplanation,
    string? RecommendedSentence,
    string? GrammarNotes)
{
    public string AccessibilityId => $"{NativeSceneAccess.Result}.{Index}";
}

/// <summary>A gallery tile (source scene-gallery-item).</summary>
public sealed record NativeSceneGalleryItem(
    int Id, NativeSceneImageLoad Load, byte[]? Bytes, bool IsCurrent, bool IsChecked)
{
    public string AccessibilityId => $"{NativeSceneAccess.GalleryItem}.{Id}";
}

/// <summary>The gallery overlay (source showGallery block).</summary>
public sealed record NativeSceneGallery(
    bool IsLoading,
    ImmutableArray<NativeSceneGalleryItem> Items,
    bool IsSelecting,
    int SelectedCount,
    string NewImageUrl,
    bool IsSaving)
{
    public bool CanAddUrl => !IsSaving && !string.IsNullOrWhiteSpace(NewImageUrl);

    public bool CanDelete => !IsSaving && IsSelecting && SelectedCount > 0;
}

/// <summary>
/// Phases: Loading (first scene), Ready (an image is current), Empty (no
/// image: Browse Gallery / Go Back), Blocked (owner absent/changed or launch
/// refused). Destination becomes TodaysPlan after Back.
/// </summary>
public sealed record NativeSceneState(
    NativePhase Phase,
    NativeDestination Destination = NativeDestination.Activity,
    NativeSceneImage? Image = null,
    ImmutableArray<NativeSceneResult> Results = default,
    int ExpandedIndex = -1,
    string Draft = "",
    bool IsGrading = false,
    NativeSceneGallery? Gallery = null,
    ImmutableArray<NativeMessage> Notices = default,
    string? ErrorKey = null,
    bool HasPendingSync = false)
{
    public bool CanGrade => Phase == NativePhase.Ready && Image is not null && !IsGrading
        && !string.IsNullOrWhiteSpace(Draft);

    /// <summary>A grade in flight keeps the scene: new scene and gallery switching wait for it.</summary>
    public bool CanChangeScene => Phase is NativePhase.Ready or NativePhase.Empty
        && !IsGrading;
}

/// <summary>
/// AI port for Describe a Scene: the source TeacherService.GradeDescription
/// (GradeMyDescription template, Reasoning tier, minimal effort) through the
/// owner-bound native gateway.
/// </summary>
public interface INativeSceneAi
{
    Task<GradeResponse?> GradeAsync(string owner, string description, string sceneDescription,
        string targetLanguage, string nativeLanguage, CancellationToken cancellationToken);
}

/// <summary>Bytes chosen in the system photo picker (iOS PhotosPicker, Android PickVisualMedia).</summary>
public sealed record NativeScenePickedPhoto(byte[] Data, string MimeType);

/// <summary>
/// The owner's scene image gallery (source SceneImageService) plus image
/// bytes. SceneImage has no owner column and is not in the CoreSync set; on a
/// native device it lives in the owner's own SQLite file, so every call names
/// the owner and refuses an absent or inactive one.
/// </summary>
public interface INativeSceneImageStore
{
    Task<IReadOnlyList<SceneImage>> ListAsync(string owner, CancellationToken cancellationToken);

    Task<SceneImage?> GetRandomAsync(string owner, CancellationToken cancellationToken);

    Task<SceneImage?> AddUrlAsync(string owner, string url, CancellationToken cancellationToken);

    Task<SceneImage?> AddPhotoAsync(string owner, NativeScenePickedPhoto photo,
        CancellationToken cancellationToken);

    /// <summary>Deletes the owner's rows (and their local photo files); returns the rows deleted.</summary>
    Task<int> DeleteAsync(string owner, IReadOnlyCollection<int> ids, CancellationToken cancellationToken);

    Task<byte[]?> LoadBytesAsync(string owner, SceneImage image, CancellationToken cancellationToken);
}
