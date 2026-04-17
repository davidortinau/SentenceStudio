using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plugin.Maui.HelpKit.Eval;

public record GoldenQaItem(
    string Id,
    string Category,
    string Question,
    string[] ExpectedAnswerKeywords,
    string[] RequiredCitationPaths,
    string Language,
    bool MustRefuse,
    string? Notes);

public static class GoldenSet
{
    // NOTE (to Zoe): add this entry to Plugin.Maui.HelpKit.Eval.csproj so the JSON is
    // embedded alongside the assembly:
    //
    //   <ItemGroup>
    //     <EmbeddedResource Include="golden-qa.json" />
    //   </ItemGroup>
    //
    // The loader below first tries the embedded resource, then falls back to reading
    // the file from disk relative to the test assembly — this lets the set work in
    // both the final packaged form and during local iteration.

    private const string EmbeddedResourceName = "Plugin.Maui.HelpKit.Eval.golden-qa.json";
    private const string FileName = "golden-qa.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static IReadOnlyList<GoldenQaItem>? _cached;

    public static IReadOnlyList<GoldenQaItem> Load()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        using var stream = OpenStream();
        var document = JsonSerializer.Deserialize<GoldenDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException("golden-qa.json deserialized to null.");

        if (document.Items is null || document.Items.Count == 0)
        {
            throw new InvalidOperationException("golden-qa.json has no items.");
        }

        _cached = document.Items;
        return _cached;
    }

    private static Stream OpenStream()
    {
        var assembly = typeof(GoldenSet).Assembly;
        var embedded = assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (embedded is not null)
        {
            return embedded;
        }

        // Fallback: find the JSON file on disk relative to the assembly.
        var directory = Path.GetDirectoryName(assembly.Location);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, FileName);
            if (File.Exists(candidate))
            {
                return File.OpenRead(candidate);
            }

            var parent = Directory.GetParent(directory);
            if (parent is null)
            {
                break;
            }

            directory = parent.FullName;
        }

        throw new FileNotFoundException(
            $"Could not locate {FileName} as an embedded resource ({EmbeddedResourceName}) " +
            "or on disk near the test assembly.");
    }

    private sealed record GoldenDocument(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("items")] List<GoldenQaItem> Items);
}
