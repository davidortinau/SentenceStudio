using System.Runtime.CompilerServices;
using System.Xml.Linq;
using SentenceStudio.Native.iOS.Platform;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeHeadContractTests
{
    [Theory]
    [InlineData("https://example.invalid/", false)]
    [InlineData("http://localhost:56301/", true)]
    [InlineData("http://127.0.0.1:56301/", true)]
    public void NativeApiEndpoint_AcceptsHttpsOrExplicitDebugLoopback(
        string input, bool allowLocalHttp) =>
        Assert.Equal(input, NativeApiEndpoint.Validate(input, allowLocalHttp).AbsoluteUri);

    [Theory]
    [InlineData("http://localhost:56301/", false)]
    [InlineData("http://api.example.invalid/", true)]
    [InlineData("https://person:secret@example.invalid/", true)]
    [InlineData("https://example.invalid/profile", true)]
    [InlineData("https://example.invalid/?token=x", true)]
    [InlineData("file:///tmp/api", true)]
    public void NativeApiEndpoint_RejectsInsecureOrNonRootConfiguration(
        string input, bool allowLocalHttp) =>
        Assert.Throws<ArgumentException>(() =>
            NativeApiEndpoint.Validate(input, allowLocalHttp));

    [Fact]
    public void NativeProject_IsIosSwiftUiWithIsolatedDebugAndReleaseIds()
    {
        var project = XDocument.Load(Path.Combine(HeadPath, "SentenceStudio.Native.iOS.csproj"));
        Assert.Equal("net11.0-ios", project.Descendants("TargetFramework").Single().Value);
        Assert.Contains(project.Descendants("ProjectReference"),
            reference => reference.Attribute("Include")?.Value
                == "../SentenceStudio.Native/SentenceStudio.Native.csproj");
        Assert.Contains(project.Descendants("ProjectReference"),
            reference => reference.Attribute("Include")?.Value
                == "../SentenceStudio.Shared/SentenceStudio.Shared.csproj");
        Assert.Contains(project.Descendants("PackageReference"),
            reference => reference.Attribute("Include")?.Value == "Microsoft.SwiftUI");
        Assert.Equal("0.1.0-preview.3", project.Descendants("PackageVersion")
            .Single(item => item.Attribute("Include")?.Value == "Microsoft.SwiftUI")
            .Attribute("Version")?.Value);
        Assert.DoesNotContain(project.Descendants("PackageReference"), reference =>
            (reference.Attribute("Include")?.Value ?? string.Empty).Contains(
                "Maui", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(project.Descendants("PackageReference"), reference =>
            (reference.Attribute("Include")?.Value ?? string.Empty).Contains(
                "Comet", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(project.Descendants("ProjectReference"), reference =>
            (reference.Attribute("Include")?.Value ?? string.Empty).Contains(
                "MauiCompatibility", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, project.Descendants("ApplicationId").Count());
        Assert.All(project.Descendants("ApplicationId"), id =>
            Assert.Contains(".native.", id.Value, StringComparison.Ordinal));
        Assert.Contains(project.Descendants("ApplicationId"), id =>
            id.Attribute("Condition")?.Value.Contains("NativePreviewSurface",
                StringComparison.Ordinal) == true
            && id.Value.EndsWith(".fixture", StringComparison.Ordinal));
    }

    [Fact]
    public void AgentAndLocalNetworking_AreDebugOnlyAndReleaseHasNoReferences()
    {
        var project = XDocument.Load(Path.Combine(HeadPath, "SentenceStudio.Native.iOS.csproj"));
        var agentReferences = project.Descendants("NativeReference").ToList();
        Assert.Equal(2, agentReferences.Count);
        Assert.All(agentReferences, reference =>
            Assert.Contains("'$(Configuration)' == 'Debug'",
                reference.Parent!.Attribute("Condition")!.Value, StringComparison.Ordinal));
        var debugNetworking = Assert.Single(project.Descendants("PartialAppManifest"));
        Assert.Contains("'$(Configuration)' == 'Debug'",
            debugNetworking.Attribute("Condition")!.Value, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(HeadPath, "Native", "AilohaDebugShim.swift")));
    }

    [Fact]
    public void AuthenticatedSample_IsOptInAndDebugOnly()
    {
        var project = XDocument.Load(Path.Combine(HeadPath, "SentenceStudio.Native.iOS.csproj"));
        var owner = Assert.Single(project.Descendants("AssemblyAttribute")
            .Where(item => item.Element("_Parameter1")?.Value
                == "SentenceStudio.Native.AuthenticatedSampleOwnerId"));
        Assert.Contains("'$(Configuration)' == 'Debug'",
            owner.Parent!.Attribute("Condition")!.Value, StringComparison.Ordinal);
        Assert.Contains("NativeAuthenticatedSampleOwnerId",
            owner.Parent.Attribute("Condition")!.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void SwiftUiPreview_UsesOnlyTheStandaloneLocalArtifactFeed()
    {
        var config = XDocument.Load(Path.Combine(HeadPath, "NuGet.Config"));
        var source = Assert.Single(config.Descendants("packageSources")
            .Descendants("add"));
        Assert.Equal("swiftui-preview", source.Attribute("key")?.Value);
        Assert.EndsWith("/Microsoft.SwiftUI/artifacts",
            source.Attribute("value")?.Value, StringComparison.Ordinal);

        var mapping = Assert.Single(config.Descendants("packageSource"));
        Assert.Equal("swiftui-preview", mapping.Attribute("key")?.Value);
        Assert.Equal("Microsoft.SwiftUI",
            Assert.Single(mapping.Descendants("package")).Attribute("pattern")?.Value);
    }

    [Fact]
    public void InfoPlist_EnablesFullScreenIphoneAndIpadOnIos26()
    {
        var manifest = XDocument.Load(Path.Combine(HeadPath, "Info.plist"));
        var fields = manifest.Descendants("key").Select(key => key.Value).ToArray();
        Assert.Contains("UILaunchScreen", fields);
        Assert.Contains("UIApplicationSceneManifest", fields);
        Assert.Contains("UIDeviceFamily", fields);
        Assert.Equal(["1", "2"], manifest.Descendants("integer").Select(element => element.Value));
        Assert.Equal("26.0", XDocument.Load(Path.Combine(HeadPath, "SentenceStudio.Native.iOS.csproj"))
            .Descendants("SupportedOSPlatformVersion").Single().Value);
    }

    private static string HeadPath => Path.GetFullPath(
        Path.Combine(Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
