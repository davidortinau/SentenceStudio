using System.Text.Json;

namespace SentenceStudio.Evaluation.Tests.Import;

public sealed record ExpectedVocabulary(string Lemma, string Gloss, IReadOnlyList<string>? Alternates = null)
{
    public IEnumerable<string> AcceptedForms => new[] { Lemma }.Concat(Alternates ?? Array.Empty<string>());
}

public sealed record ForbiddenTerm(string Term, string Why);

public sealed record ImportVocabularyFixture(
    string Id,
    string Title,
    string Trap,
    IReadOnlyList<string> Tags,
    string Text,
    IReadOnlyList<ExpectedVocabulary> Expected,
    IReadOnlyList<string> Allowed,
    IReadOnlyList<ForbiddenTerm> Forbidden);

public sealed record ImportVocabularyFixtureSet(
    string TargetLanguage,
    string NativeLanguage,
    IReadOnlyList<ImportVocabularyFixture> Fixtures);

public static class ImportVocabularyFixtures
{
    public const string FileName = "import-vocabulary-fixtures.json";

    private static readonly Lazy<ImportVocabularyFixtureSet> Set = new(Load);

    public static ImportVocabularyFixtureSet All => Set.Value;

    public static ImportVocabularyFixture Get(string id) =>
        All.Fixtures.Single(f => f.Id == id);

    private static ImportVocabularyFixtureSet Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", FileName);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ImportVocabularyFixtureSet>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        }) ?? throw new InvalidOperationException($"Could not read {path}");
    }
}
