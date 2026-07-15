using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SentenceStudio.Abstractions;

namespace SentenceStudio.UnitTests.Services;

public class PhotoViewerRequestTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCreate_NullOrWhitespace_ReturnsFalse(string? uri)
    {
        PhotoViewerRequest.TryCreate(uri, "label", out var request).Should().BeFalse();
        request.Should().BeNull();
    }

    [Fact]
    public void TryCreate_RelativeUri_ReturnsFalse()
    {
        PhotoViewerRequest.TryCreate("/images/test.jpg", "label", out var request).Should().BeFalse();
        request.Should().BeNull();
    }

    [Theory]
    [InlineData("ftp://example.com/image.jpg")]
    [InlineData("file:///local/path.jpg")]
    [InlineData("data:image/png;base64,abc")]
    public void TryCreate_UnsupportedScheme_ReturnsFalse(string uri)
    {
        PhotoViewerRequest.TryCreate(uri, "label", out var request).Should().BeFalse();
        request.Should().BeNull();
    }

    [Theory]
    [InlineData("https://images.example.com/photo.jpg")]
    [InlineData("http://cdn.example.com/mnemonic.png")]
    public void TryCreate_ValidHttpsOrHttp_Succeeds(string uri)
    {
        PhotoViewerRequest.TryCreate(uri, "test label", out var request).Should().BeTrue();
        request.Should().NotBeNull();
        request!.ImageUri.ToString().Should().Be(uri);
        request.AccessibilityLabel.Should().Be("test label");
    }

    [Fact]
    public void TryCreate_NullAccessibilityLabel_Allowed()
    {
        PhotoViewerRequest.TryCreate("https://example.com/img.jpg", null, out var request).Should().BeTrue();
        request!.AccessibilityLabel.Should().BeNull();
    }
}

public class DefaultPhotoViewerServiceTests
{
    private readonly DefaultPhotoViewerService _service = new();

    [Fact]
    public async Task ShowAsync_AlwaysReturnsNotHandled()
    {
        PhotoViewerRequest.TryCreate("https://example.com/img.jpg", "label", out var request);
        var result = await _service.ShowAsync(request!);
        result.HandledByNative.Should().BeFalse();
    }

    [Fact]
    public async Task ShowAsync_NullRequest_ThrowsArgumentNull()
    {
        var act = () => _service.ShowAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ShowAsync_CancelledToken_ThrowsOperationCanceled()
    {
        PhotoViewerRequest.TryCreate("https://example.com/img.jpg", "label", out var request);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => _service.ShowAsync(request!, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

public class PhotoViewerCoordinatorTests
{
    private readonly Mock<IPhotoViewerService> _mockService = new();
    private readonly ILogger<PhotoViewerCoordinator> _logger =
        NullLoggerFactory.Instance.CreateLogger<PhotoViewerCoordinator>();

    private PhotoViewerCoordinator CreateCoordinator() => new(_mockService.Object, _logger);

    private static PhotoViewerRequest CreateValidRequest()
    {
        PhotoViewerRequest.TryCreate("https://example.com/img.jpg", "test", out var r);
        return r!;
    }

    [Fact]
    public async Task TryShowNativeAsync_ServiceReturnsHandled_ReturnsTrue()
    {
        _mockService
            .Setup(s => s.ShowAsync(It.IsAny<PhotoViewerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PhotoViewerResult(HandledByNative: true));

        var result = await CreateCoordinator().TryShowNativeAsync(CreateValidRequest());
        result.Should().BeTrue();
    }

    [Fact]
    public async Task TryShowNativeAsync_ServiceReturnsNotHandled_ReturnsFalse()
    {
        _mockService
            .Setup(s => s.ShowAsync(It.IsAny<PhotoViewerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PhotoViewerResult(HandledByNative: false));

        var result = await CreateCoordinator().TryShowNativeAsync(CreateValidRequest());
        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryShowNativeAsync_ServiceThrows_ReturnsFalseDoesNotSwallow()
    {
        _mockService
            .Setup(s => s.ShowAsync(It.IsAny<PhotoViewerRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("native viewer crashed"));

        // Should return false (fallback to web), not rethrow
        var result = await CreateCoordinator().TryShowNativeAsync(CreateValidRequest());
        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryShowNativeAsync_CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockService
            .Setup(s => s.ShowAsync(It.IsAny<PhotoViewerRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => CreateCoordinator().TryShowNativeAsync(CreateValidRequest(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TryShowNativeAsync_NullRequest_ThrowsArgumentNull()
    {
        var act = () => CreateCoordinator().TryShowNativeAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}

#if DEBUG
public class DebugPhotoViewerSelectorTests
{
    private readonly Mock<IPreferencesService> _mockPrefs = new();
    private readonly ILogger<DebugPhotoViewerSelector> _logger =
        NullLoggerFactory.Instance.CreateLogger<DebugPhotoViewerSelector>();

    private static PhotoViewerRequest CreateValidRequest()
    {
        PhotoViewerRequest.TryCreate("https://example.com/img.jpg", "test", out var r);
        return r!;
    }

    [Theory]
    [InlineData("webview")]
    [InlineData("WEBVIEW")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid_value")]
    [InlineData("WebView")]
    public async Task ShowAsync_NonNativeValues_ReturnsNotHandled(string prefValue)
    {
        _mockPrefs
            .Setup(p => p.Get(DebugPhotoViewerSelector.PreferenceKey, DebugPhotoViewerSelector.WebViewValue))
            .Returns(prefValue);

        var selector = new DebugPhotoViewerSelector(_mockPrefs.Object, _logger);
        var result = await selector.ShowAsync(CreateValidRequest());
        result.HandledByNative.Should().BeFalse();
    }

    [Theory]
    [InlineData("native")]
    [InlineData("Native")]
    [InlineData("NATIVE")]
    public async Task ShowAsync_NativeValue_DelegatesToNativeImpl(string prefValue)
    {
        _mockPrefs
            .Setup(p => p.Get(DebugPhotoViewerSelector.PreferenceKey, DebugPhotoViewerSelector.WebViewValue))
            .Returns(prefValue);

        var mockNative = new Mock<IPhotoViewerService>();
        mockNative
            .Setup(s => s.ShowAsync(It.IsAny<PhotoViewerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PhotoViewerResult(HandledByNative: true));

        var selector = new DebugPhotoViewerSelector(_mockPrefs.Object, _logger, mockNative.Object);
        var result = await selector.ShowAsync(CreateValidRequest());
        result.HandledByNative.Should().BeTrue();
        mockNative.Verify(s => s.ShowAsync(It.IsAny<PhotoViewerRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ShowAsync_NativeWithNoNativeImpl_FallsBackToDefault()
    {
        _mockPrefs
            .Setup(p => p.Get(DebugPhotoViewerSelector.PreferenceKey, DebugPhotoViewerSelector.WebViewValue))
            .Returns("native");

        // No native impl passed → uses DefaultPhotoViewerService internally
        var selector = new DebugPhotoViewerSelector(_mockPrefs.Object, _logger);
        var result = await selector.ShowAsync(CreateValidRequest());
        result.HandledByNative.Should().BeFalse();
    }

    [Fact]
    public async Task ShowAsync_CancelledToken_ThrowsOperationCanceled()
    {
        _mockPrefs
            .Setup(p => p.Get(DebugPhotoViewerSelector.PreferenceKey, DebugPhotoViewerSelector.WebViewValue))
            .Returns("webview");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var selector = new DebugPhotoViewerSelector(_mockPrefs.Object, _logger);
        var act = () => selector.ShowAsync(CreateValidRequest(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
#endif
