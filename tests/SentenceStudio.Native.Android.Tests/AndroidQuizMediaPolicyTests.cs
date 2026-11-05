using System.Reflection;
using SentenceStudio.Native.Android;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidQuizMediaPolicyTests
{
    private static readonly Uri ApiRoot = new("http://10.0.2.2:5081/");

    [Fact]
    public void QuizAudioIsCachedOnlyThroughTheSharedSpeechCache()
    {
        // The shared NativeShadowingSpeech keeps a clip only when the API confirmed the requested
        // voice; the Android head must not synthesize or record history around it.
        var root = Path.GetFullPath(typeof(AndroidQuizMediaPolicyTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!);
        var media = File.ReadAllText(Path.Combine(root, "AndroidQuizMedia.cs"));

        Assert.Contains("new NativeShadowingSpeech(", media);
        Assert.Contains("clips.GetAsync(owner, text, language, cancellationToken)", media);
        Assert.DoesNotContain("SaveStreamHistoryAsync", media);
        Assert.DoesNotContain(".SynthesizeAsync(", media);
    }

    [Fact]
    public void PhotoTypeComesFromTheImageBytesNotTheServerLabel()
    {
        Assert.Equal("image/png", AndroidQuizMediaPolicy.DetectImageContentType(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0 }));
        Assert.Equal("image/jpeg", AndroidQuizMediaPolicy.DetectImageContentType(
            new byte[] { 0xff, 0xd8, 0xff, 0xe0 }));
        Assert.Equal("image/gif", AndroidQuizMediaPolicy.DetectImageContentType(
            "GIF89a"u8.ToArray()));
        Assert.Equal("image/webp", AndroidQuizMediaPolicy.DetectImageContentType(
            "RIFF\0\0\0\0WEBPVP8 "u8.ToArray()));
    }

    [Fact]
    public void TextHtmlOrTruncatedBytesAreNeverAPhoto()
    {
        Assert.Null(AndroidQuizMediaPolicy.DetectImageContentType(
            "<!doctype html><html>"u8.ToArray()));
        Assert.Null(AndroidQuizMediaPolicy.DetectImageContentType(
            new byte[] { 137, 80, 78 }));
        Assert.Null(AndroidQuizMediaPolicy.DetectImageContentType(
            "RIFF\0\0\0\0WAVEfmt "u8.ToArray()));
        Assert.Null(AndroidQuizMediaPolicy.DetectImageContentType([]));
    }

    [Fact]
    public void PublicHttpsCanLoadWhileApiPhotosWaitForSharedOwnerTokenVerification()
    {
        Assert.True(AndroidQuizMediaPolicy.TryResolveRemotePhoto(
            "https://images.example.test/photo.jpg?signature=opaque",
            ApiRoot, allowEmulatorHttp: true, out var publicUri));
        Assert.Equal("https", publicUri!.Scheme);
        Assert.True(AndroidQuizMediaPolicy.CanRequestWithoutOwnerToken(
            publicUri, ApiRoot));
        Assert.True(AndroidQuizMediaPolicy.TryResolveRemotePhoto(
            "/api/media/photo.jpg", ApiRoot, allowEmulatorHttp: true,
            out var apiUri));
        Assert.Equal("http://10.0.2.2:5081/api/media/photo.jpg",
            apiUri!.AbsoluteUri);
        Assert.False(AndroidQuizMediaPolicy.CanRequestWithoutOwnerToken(
            apiUri, ApiRoot));
        Assert.False(AndroidQuizMediaPolicy.CanRequestWithoutOwnerToken(
            new Uri("HTTP://10.0.2.2:5081/redirected.jpg"), ApiRoot));
        Assert.False(AndroidQuizMediaPolicy.CanRequestWithoutOwnerToken(
            new Uri("https://10.0.2.2:443/alternate.jpg"), ApiRoot));
        Assert.False(AndroidQuizMediaPolicy.CanRequestWithoutOwnerToken(
            null, ApiRoot));
    }

    [Theory]
    [InlineData("http://example.test/photo.jpg")]
    [InlineData("http://10.0.2.2:9999/photo.jpg")]
    [InlineData("//example.test/photo.jpg")]
    [InlineData("https://name:password@example.test/photo.jpg")]
    [InlineData("file:///storage/emulated/0/private.jpg")]
    [InlineData("content://other.app/photo")]
    [InlineData("/\\example.test/photo.jpg")]
    public void UnsafePhotoSourcesDoNotReachTheNetwork(string source)
    {
        Assert.False(AndroidQuizMediaPolicy.TryResolveRemotePhoto(
            source, ApiRoot, allowEmulatorHttp: true, out _));
    }

    [Fact]
    public void EmulatorCleartextIsDebugOnly()
    {
        Assert.False(AndroidQuizMediaPolicy.TryResolveRemotePhoto(
            "/api/media/photo.jpg", ApiRoot, allowEmulatorHttp: false, out _));
    }

    [Theory]
    [InlineData("/data/user/0/app/files/Media/owner-a/photo.jpg", true)]
    [InlineData("/data/user/0/app/files/Media/owner-b/photo.jpg", false)]
    [InlineData("/data/user/0/app/files/images/photo.jpg", false)]
    [InlineData("/data/user/0/app/files/Media/owner-a/../../tokens.json", false)]
    [InlineData("/data/user/0/app/files-private/Media/owner-a/photo.jpg", false)]
    [InlineData("/sdcard/photo.jpg", false)]
    public void LocalPhotosCannotEscapeThePrivateAppFolder(string path, bool expected)
    {
        Assert.Equal(expected, AndroidQuizMediaPolicy.IsPrivatePhoto(
            path, "/data/user/0/app/files", "owner-a"));
    }
}
