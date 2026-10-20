using SentenceStudio.Native.Android;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidQuizMediaPolicyTests
{
    private static readonly Uri ApiRoot = new("http://10.0.2.2:5081/");

    [Theory]
    [InlineData("Korean", "ko-KR")]
    [InlineData("English", "en-US")]
    [InlineData("French", "fr-FR")]
    [InlineData("German", "de-DE")]
    [InlineData("Spanish", "es-ES")]
    [InlineData("Japanese", "ja-JP")]
    [InlineData("Chinese", "zh-CN")]
    public void PromptSideLanguageSelectsItsOwnSpeechLocale(string language, string tag)
    {
        Assert.Equal(tag, AndroidQuizMediaPolicy.LanguageTag(language));
    }

    [Fact]
    public void UnknownLanguageCannotSilentlyUseTheWrongVoice()
    {
        Assert.Null(AndroidQuizMediaPolicy.LanguageTag("not-a-language"));
        Assert.Null(AndroidQuizMediaPolicy.LanguageTag(null));
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
