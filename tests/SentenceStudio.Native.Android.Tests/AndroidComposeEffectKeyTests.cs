using System.Reflection;
using System.Text.RegularExpressions;
using SentenceStudio.Native.Android;

namespace SentenceStudio.Native.Android.Tests;

/// <summary>
/// The pinned Compose facade throws NotSupportedException for an effect key that is not null,
/// a string, a primitive or a Java object (a tuple key closed the quiz on photo turns and on
/// autoplay). Every effect in the head goes through AndroidComposeEffects, whose overloads take
/// only supported key types, and several values become one string with AndroidEffectKey.
/// </summary>
public sealed class AndroidComposeEffectKeyTests
{
    private static readonly string HeadRoot = Path.GetFullPath(typeof(AndroidComposeEffectKeyTests)
        .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!);

    [Fact]
    public void EveryLaunchedEffect_GoesThroughTheTypedKeyHelper()
    {
        var sources = Directory.EnumerateFiles(HeadRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(sources);
        var direct = sources
            .Where(path => Path.GetFileName(path) != "AndroidComposeEffects.cs"
                && File.ReadAllText(path).Contains("LaunchedEffect(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(direct);
        Assert.Contains(sources, path =>
            File.ReadAllText(path).Contains("composer.Effect(", StringComparison.Ordinal));

        var helper = File.ReadAllText(Path.Combine(HeadRoot, "AndroidComposeEffects.cs"));
        var keyTypes = Regex.Matches(helper, @"this IComposer composer, (?<type>[\w\.\?]+) key")
            .Select(match => match.Groups["type"].Value)
            .ToList();
        Assert.Equal(["string?", "long", "Java.Lang.Object?"], keyTypes);
    }

    [Fact]
    public void EffectKey_IsOneStableStringOrNullWhenAPartIsMissing()
    {
        var asset = new object();

        Assert.Equal("owner|turn|true|42", AndroidEffectKey.Of("owner", "turn", true, 42));
        Assert.Equal(AndroidEffectKey.Of("owner", "turn", asset),
            AndroidEffectKey.Of("owner", "turn", asset));
        Assert.NotEqual(AndroidEffectKey.Of("owner", "turn", asset),
            AndroidEffectKey.Of("owner", "turn", new object()));
        Assert.Null(AndroidEffectKey.Of("owner", null, asset));
        Assert.Null(AndroidEffectKey.Of((object?)null));
        // Separators inside a value cannot make two different keys equal.
        Assert.NotEqual(AndroidEffectKey.Of("a|b", "c"), AndroidEffectKey.Of("a", "b|c"));
    }
}
