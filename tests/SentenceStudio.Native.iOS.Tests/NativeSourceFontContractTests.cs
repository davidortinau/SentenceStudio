using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeSourceFontContractTests
{
    [Fact]
    public void PublicSourceFonts_AreRegisteredByExactBundlePath_WithTheirOflNotices()
    {
        var fonts = Path.Combine(HeadPath, "Resources", "Fonts");
        using var manifest = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(fonts, "font-provenance.json")));
        var faces = manifest.RootElement.GetProperty("faces")
            .EnumerateArray().ToArray();
        var licenses = manifest.RootElement.GetProperty("licenses")
            .EnumerateArray().ToArray();

        using var infoReader = XmlReader.Create(
            new StringReader(File.ReadAllText(Path.Combine(HeadPath, "Info.plist"))),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        var info = XDocument.Load(infoReader);
        var listed = info.Descendants("key")
            .Single(element => element.Value == "UIAppFonts")
            .ElementsAfterSelf().First().Elements("string")
            .Select(element => element.Value).Order().ToArray();
        Assert.Equal(faces.Select(face => face.GetProperty("file").GetString()!)
            .Order().ToArray(), listed);
        Assert.Equal(6, listed.Length);

        foreach (var face in faces)
        {
            var relative = face.GetProperty("file").GetString()!;
            var file = Path.Combine(HeadPath, "Resources",
                relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), relative);
            Assert.Equal(face.GetProperty("sha256").GetString(),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))
                    .ToLowerInvariant());
            Assert.False(string.IsNullOrWhiteSpace(
                face.GetProperty("postScriptName").GetString()));
        }

        Assert.Equal(3, licenses.Length);
        foreach (var license in licenses)
        {
            var relative = license.GetProperty("file").GetString()!;
            var file = Path.Combine(fonts, "Licenses", Path.GetFileName(relative));
            Assert.Contains("SIL OPEN FONT LICENSE Version 1.1",
                File.ReadAllText(file));
            Assert.Equal(license.GetProperty("sha256").GetString(),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))
                    .ToLowerInvariant());
        }

        var project = XDocument.Load(
            Path.Combine(HeadPath, "SentenceStudio.Native.iOS.csproj"));
        Assert.Contains(project.Descendants("BundleResource"), item =>
            item.Attribute("Include")?.Value == "Resources/Fonts/*.ttf"
            && item.Attribute("Link")?.Value == "Fonts/%(Filename)%(Extension)");
        Assert.DoesNotContain(project.Descendants("BundleResource"), item =>
            item.Attribute("Include")?.Value.Contains("Licenses/",
                StringComparison.Ordinal) == true);
    }

    private static string HeadPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string CurrentFile([CallerFilePath] string path = "") => path;
}
