using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// Regression guard: ensures that consumers requesting <see cref="IYouTubeImportService"/>
/// via DI receive the concrete <see cref="YouTubeImportService"/> implementation.
///
/// Background: Simon introduced the interface and changed all DI registrations to
/// <c>AddSingleton&lt;IYouTubeImportService, YouTubeImportService&gt;()</c>, but two
/// Blazor pages still injected the concrete type. <c>AddSingleton&lt;TService, TImpl&gt;</c>
/// does NOT self-register TImpl, so those pages threw an activation exception at runtime.
///
/// This test mirrors the registration shape used in CoreServiceExtensions and the API/Worker
/// hosts to ensure the interface-only registration resolves correctly.
/// </summary>
public class YouTubeImportDiRegistrationTests
{
    [Fact]
    public void InterfaceRegistration_ResolvesYouTubeImportService()
    {
        // Arrange — same registration shape as CoreServiceExtensions / Api / Workers
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<AudioAnalyzer>();
        services.AddSingleton<IYouTubeImportService, YouTubeImportService>();

        using var provider = services.BuildServiceProvider();

        // Act
        var resolved = provider.GetService<IYouTubeImportService>();

        // Assert — the interface must resolve
        Assert.NotNull(resolved);
        Assert.IsType<YouTubeImportService>(resolved);
    }

    [Fact]
    public void ConcreteType_IsNotDirectlyResolvable_WhenOnlyInterfaceRegistered()
    {
        // Arrange — exactly the pattern that caused the Blazor activation failure
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<AudioAnalyzer>();
        services.AddSingleton<IYouTubeImportService, YouTubeImportService>();

        using var provider = services.BuildServiceProvider();

        // Act — requesting the concrete type should return null (not registered)
        var resolved = provider.GetService<YouTubeImportService>();

        // Assert — concrete is NOT self-registered; pages must inject the interface
        Assert.Null(resolved);
    }
}
