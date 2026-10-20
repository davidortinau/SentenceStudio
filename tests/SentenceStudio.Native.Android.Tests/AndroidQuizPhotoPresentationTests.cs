using SentenceStudio.Native;
using SentenceStudio.Native.Android;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidQuizPhotoPresentationTests
{
    private static readonly NativeQuizPrompt PhotoPrompt = new(
        "owned-word", "target-language prompt", "Korean", null,
        "https://images.example.invalid/owned-photo",
        IsTargetLanguagePrompt: true, ShowWrittenPrompt: true,
        CanPlayPromptAudio: false, ShowPhoto: true,
        TargetLanguageEngaged: true);

    private static NativeQuizState OpenPhoto() => new(
        NativePhase.Ready, NativeDestination.VocabularyQuiz,
        Prompt: PhotoPrompt, Overlay: NativeQuizOverlay.FullscreenPhoto,
        TurnId: "owned-turn");

    [Fact]
    public void ViewerRequiresTheReadyOwnerTurnPhotoAndFullscreenOverlay()
    {
        var state = OpenPhoto();
        Assert.True(AndroidQuizPhotoPresentationPolicy.CanPresent(state,
            "owner-a", "owner-a", "owned-turn", PhotoPrompt.PhotoUri));
        Assert.False(AndroidQuizPhotoPresentationPolicy.CanPresent(state,
            null, "owner-a", "owned-turn", PhotoPrompt.PhotoUri));
        Assert.False(AndroidQuizPhotoPresentationPolicy.CanPresent(state,
            "owner-b", "owner-a", "owned-turn", PhotoPrompt.PhotoUri));
        Assert.False(AndroidQuizPhotoPresentationPolicy.CanPresent(state,
            "owner-a", "owner-a", "previous-turn", PhotoPrompt.PhotoUri));
        Assert.False(AndroidQuizPhotoPresentationPolicy.CanPresent(state,
            "owner-a", "owner-a", "owned-turn",
            "https://images.example.invalid/other"));
    }

    [Fact]
    public void GradingCancellationAndNavigationCannotKeepAnOldPhotoVisible()
    {
        var state = OpenPhoto();
        NativeQuizState[] noViewer =
        [
            state with { Phase = NativePhase.Feedback },
            state with { Phase = NativePhase.Blocked },
            state with { Destination = NativeDestination.Dashboard },
            state with { Overlay = NativeQuizOverlay.None },
            state with { Overlay = NativeQuizOverlay.LearningDetails },
            state with { Prompt = PhotoPrompt with { ShowPhoto = false } },
            state with { Prompt = PhotoPrompt with { PhotoUri = null } },
            state with { Prompt = PhotoPrompt with { PhotoUri = " " } },
            state with { TurnId = "next-turn" }
        ];
        foreach (var candidate in noViewer)
            Assert.False(AndroidQuizPhotoPresentationPolicy.CanPresent(candidate,
                "owner-a", "owner-a", "owned-turn", PhotoPrompt.PhotoUri));
    }

    [Fact]
    public void FitPanPinchAndDoubleTapKeepThePhotoWithinItsViewport()
    {
        var photo = new AndroidQuizPhotoTransform();
        photo.SetViewport(400, 300, 1600, 800);

        Assert.Equal(400f, photo.ImageWidth);
        Assert.Equal(200f, photo.ImageHeight);
        Assert.True(photo.ContainsImage(200, 150));
        Assert.False(photo.ContainsImage(200, 25));

        photo.DoubleTap(200, 150);
        Assert.Equal(2f, photo.Scale);
        photo.ZoomAndPan(200, 150, 10_000, 10_000, 1f);
        Assert.Equal(200f, photo.OffsetX);
        Assert.Equal(50f, photo.OffsetY);
        photo.ZoomAndPan(200, 150, -20_000, -20_000, 1f);
        Assert.Equal(-200f, photo.OffsetX);
        Assert.Equal(-50f, photo.OffsetY);
        photo.ZoomAndPan(200, 150, 0, 0, 20f);
        Assert.Equal(4f, photo.Scale);
        photo.DoubleTap(200, 150);
        Assert.Equal(1f, photo.Scale);
        Assert.Equal(0f, photo.OffsetX);
        Assert.Equal(0f, photo.OffsetY);
    }

    [Fact]
    public void ZoomKeepsTheGestureCentroidFixedAndRotationResetsGeometry()
    {
        var photo = new AndroidQuizPhotoTransform();
        photo.SetViewport(400, 400, 800, 800);
        photo.DoubleTap(300, 200);
        Assert.Equal(2f, photo.Scale);
        Assert.Equal(-100f, photo.OffsetX);
        Assert.Equal(0f, photo.OffsetY);
        Assert.True(photo.ContainsImage(300, 200));

        photo.SetViewport(300, 400, 800, 800);
        Assert.Equal(1f, photo.Scale);
        Assert.Equal(0f, photo.OffsetX);
        Assert.Equal(300f, photo.ImageWidth);
        Assert.Equal(300f, photo.ImageHeight);
        photo.ZoomAndPan(float.NaN, 200, 0, 0, 2f);
        Assert.Equal(1f, photo.Scale);
        photo.ZoomAndPan(150, 200, 1_000, 1_000, 1f);
        Assert.Equal(0f, photo.OffsetX);
        Assert.Equal(0f, photo.OffsetY);
    }
}
