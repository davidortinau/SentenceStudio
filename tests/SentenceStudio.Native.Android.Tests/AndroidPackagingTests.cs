using System.Reflection;
using System.Xml.Linq;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidPackagingTests
{
    private static readonly string HeadRoot = Path.GetFullPath(typeof(AndroidPackagingTests)
        .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!);

    [Fact]
    public void DebugHasAnIsolatedIdentityAndReleaseKeepsProductionIdentity()
    {
        var project = XDocument.Load(Path.Combine(
            HeadRoot, "SentenceStudio.Native.Android.csproj"));
        var ids = project.Descendants("ApplicationId").ToDictionary(
            node => (string?)node.Attribute("Condition") ?? string.Empty,
            node => node.Value);
        Assert.Equal("com.simplyprofound.sentencestudio.compose.debug",
            ids["'$(Configuration)' == 'Debug'"]);
        Assert.Equal("com.simplyprofound.sentencestudio",
            ids["'$(Configuration)' == 'Release'"]);
        Assert.Contains(project.Descendants("ProjectReference"), reference =>
            ((string?)reference.Attribute("Include"))?.EndsWith(
                "/SentenceStudio.Native.csproj", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void AilohaBindingIsReferencedOnlyForDebug()
    {
        var project = XDocument.Load(Path.Combine(
            HeadRoot, "SentenceStudio.Native.Android.csproj"));
        var bindings = project.Descendants("ProjectReference").Where(reference =>
            ((string?)reference.Attribute("Include"))?.Contains(
                "AilohaBinding/", StringComparison.Ordinal) == true);
        var binding = Assert.Single(bindings);
        Assert.Equal("'$(Configuration)' == 'Debug'",
            (string?)binding.Attribute("Condition"));
        Assert.StartsWith("#if DEBUG", File.ReadAllText(
            Path.Combine(HeadRoot, "DebugAgentApplication.cs")));
    }

    [Theory]
    [InlineData("Debug")]
    [InlineData("Release")]
    public void ManifestEnforcesApi36AndConfigurationSpecificHttp(string configuration)
    {
        var manifest = XDocument.Load(Path.Combine(
            HeadRoot, "Properties", $"AndroidManifest.{configuration}.xml"));
        XNamespace android = "http://schemas.android.com/apk/res/android";
        Assert.Equal("36",
            (string?)manifest.Descendants("uses-sdk").Single().Attribute(android + "minSdkVersion"));
        var application = manifest.Descendants("application").Single();
        if (configuration == "Release")
        {
            Assert.Equal("false",
                (string?)application.Attribute(android + "usesCleartextTraffic"));
            Assert.Null(application.Attribute(android + "networkSecurityConfig"));
        }
        else
        {
            Assert.Equal("@xml/native_network_security_config",
                (string?)application.Attribute(android + "networkSecurityConfig"));
            var rules = XDocument.Load(Path.Combine(
                HeadRoot, "Resources", "xml", "native_network_security_config.xml"));
            Assert.Equal("false",
                (string?)rules.Descendants("base-config").Single()
                    .Attribute("cleartextTrafficPermitted"));
            var exception = Assert.Single(rules.Descendants("domain-config"));
            Assert.Equal("true",
                (string?)exception.Attribute("cleartextTrafficPermitted"));
            Assert.Equal("10.0.2.2",
                Assert.Single(exception.Elements("domain")).Value);
            var project = XDocument.Load(Path.Combine(
                HeadRoot, "SentenceStudio.Native.Android.csproj"));
            Assert.Contains(project.Descendants("AndroidResource"), resource =>
                (string?)resource.Attribute("Remove")
                    == "Resources/xml/native_network_security_config.xml"
                && (string?)resource.Attribute("Condition")
                    == "'$(Configuration)' == 'Release'");
        }
    }
}
