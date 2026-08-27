using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using Xunit;

namespace SentenceStudio.UnitTests.Services.Plans;

/// <summary>
/// Proves the extraction pipeline now persists the part of speech it already
/// produced, and that the stored value round-trips through real SQLite.
/// </summary>
/// <remarks>
/// Before this change <c>ToVocabularyWord()</c> computed
/// <c>PartOfSpeech</c> and dropped it, which is why "focus on active verbs"
/// had no grounding in 3545 rows of the sample database.
/// </remarks>
public sealed class VocabularyPartOfSpeechPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public VocabularyPartOfSpeechPersistenceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseSqlite(_connection)
               .ConfigureWarnings(w =>
                   w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        _provider = services.BuildServiceProvider();

        using var bootstrap = _provider.CreateScope();
        bootstrap.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    private ApplicationDbContext NewDb() =>
        _provider.CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>();

    [Theory]
    [InlineData("verb", VocabularyPartOfSpeech.Verb)]
    [InlineData("Verb", VocabularyPartOfSpeech.Verb)]
    [InlineData(" VERB ", VocabularyPartOfSpeech.Verb)]
    [InlineData("verbs", VocabularyPartOfSpeech.Verb)]
    [InlineData("noun", VocabularyPartOfSpeech.Noun)]
    [InlineData("adjective", VocabularyPartOfSpeech.Adjective)]
    [InlineData("adverb", VocabularyPartOfSpeech.Adverb)]
    [InlineData("expression", VocabularyPartOfSpeech.Expression)]
    [InlineData("counter", VocabularyPartOfSpeech.Counter)]
    [InlineData("particle", VocabularyPartOfSpeech.Particle)]
    public void ExtractedItem_PersistsCanonicalPartOfSpeech(string extracted, VocabularyPartOfSpeech expected)
    {
        var dto = new ExtractedVocabularyItem
        {
            TargetLanguageTerm = "읽다",
            NativeLanguageTerm = "to read",
            PartOfSpeech = extracted
        };

        dto.ToVocabularyWord().PartOfSpeech.Should().Be(expected);
    }

    [Fact]
    public void ExtractedItem_WithUnmodelledValue_MapsToOtherNotAnUndefinedEnum()
    {
        var dto = new ExtractedVocabularyItem
        {
            TargetLanguageTerm = "그",
            NativeLanguageTerm = "that",
            PartOfSpeech = "determiner"
        };

        var word = dto.ToVocabularyWord();
        word.PartOfSpeech.Should().Be(VocabularyPartOfSpeech.Other);
        Enum.IsDefined(word.PartOfSpeech!.Value).Should().BeTrue("no unsafe cast may produce an undefined member");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractedItem_WithNoPartOfSpeech_StaysNull(string? extracted)
    {
        var dto = new ExtractedVocabularyItem
        {
            TargetLanguageTerm = "책",
            NativeLanguageTerm = "book",
            PartOfSpeech = extracted
        };

        dto.ToVocabularyWord().PartOfSpeech.Should().BeNull("null means never classified");
    }

    [Fact]
    public void FreeTextExtractedItem_PersistsPartOfSpeech()
    {
        var dto = new ExtractedVocabularyItemWithConfidence
        {
            TargetLanguageTerm = "가다",
            NativeLanguageTerm = "to go",
            PartOfSpeech = "verb"
        };

        dto.ToVocabularyWord().PartOfSpeech.Should().Be(VocabularyPartOfSpeech.Verb);
    }

    [Fact]
    public void PartOfSpeech_RoundTripsThroughSqliteAsACanonicalToken()
    {
        var id = Guid.NewGuid().ToString();
        using (var db = NewDb())
        {
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = id,
                TargetLanguageTerm = "읽다",
                NativeLanguageTerm = "to read",
                PartOfSpeech = VocabularyPartOfSpeech.Verb,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            db.SaveChanges();
        }

        using (var db = NewDb())
        {
            db.VocabularyWords.Single(w => w.Id == id).PartOfSpeech
                .Should().Be(VocabularyPartOfSpeech.Verb);
        }

        // The column stores the readable token, not an int, so a future value
        // stays diagnosable and re-mappable without a data migration.
        using var raw = _connection.CreateCommand();
        raw.CommandText = "SELECT PartOfSpeech FROM VocabularyWord WHERE Id = $id";
        raw.Parameters.AddWithValue("$id", id);
        raw.ExecuteScalar().Should().Be("verb");
    }

    [Fact]
    public void LegacyRowWithNullPartOfSpeech_LoadsWithoutError()
    {
        var id = Guid.NewGuid().ToString();
        using (var seed = _connection.CreateCommand())
        {
            seed.CommandText =
                "INSERT INTO VocabularyWord (Id, TargetLanguageTerm, NativeLanguageTerm, LexicalUnitType, CreatedAt, UpdatedAt) " +
                "VALUES ($id, '책', 'book', 1, '2026-01-01', '2026-01-01')";
            seed.Parameters.AddWithValue("$id", id);
            seed.ExecuteNonQuery();
        }

        using var db = NewDb();
        db.VocabularyWords.Single(w => w.Id == id).PartOfSpeech.Should().BeNull();
    }

    [Fact]
    public void UnrecognizedStoredToken_ReadsBackAsOther()
    {
        var id = Guid.NewGuid().ToString();
        using (var seed = _connection.CreateCommand())
        {
            seed.CommandText =
                "INSERT INTO VocabularyWord (Id, TargetLanguageTerm, PartOfSpeech, LexicalUnitType, CreatedAt, UpdatedAt) " +
                "VALUES ($id, '그', 'determiner', 1, '2026-01-01', '2026-01-01')";
            seed.Parameters.AddWithValue("$id", id);
            seed.ExecuteNonQuery();
        }

        using var db = NewDb();
        db.VocabularyWords.Single(w => w.Id == id).PartOfSpeech
            .Should().Be(VocabularyPartOfSpeech.Other, "forward compatibility must never throw on read");
    }
}

/// <summary>
/// Static guard for the dual-provider migration pair. A SQLite copy missing
/// <c>[Migration]</c> is invisible to EF and silently no-ops on mobile — the bug
/// that shipped twice (RefreshToken 2026-05-03, ActivitySession 2026-07-02).
/// </summary>
public sealed class VocabularyPartOfSpeechMigrationTests
{
    private const string MigrationId = "20260815221600_AddVocabularyPartOfSpeech";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "SentenceStudio.Shared")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull("the test must be able to locate the repository root");
        return dir!.FullName;
    }

    [Theory]
    [InlineData("Migrations", "text")]
    [InlineData(@"Migrations/Sqlite", "TEXT")]
    public void BothProviderMigrationsExistWithDiscoveryAttributes(string relativeDir, string columnType)
    {
        var path = Path.Combine(RepoRoot(), "src", "SentenceStudio.Shared",
            relativeDir.Replace('/', Path.DirectorySeparatorChar), $"{MigrationId}.cs");

        File.Exists(path).Should().BeTrue($"the {relativeDir} migration must exist at {path}");

        var source = File.ReadAllText(path);
        source.Should().Contain("[DbContext(typeof(ApplicationDbContext))]");
        source.Should().Contain($"[Migration(\"{MigrationId}\")]",
            "without this attribute EF never discovers the migration and silently skips it");
        source.Should().Contain($"type: \"{columnType}\"", "provider column types must not be swapped");
        source.Should().Contain("nullable: true", "the column must be a nullable add only");
        source.Should().NotContain("Sql(", "no data backfill or raw DDL belongs in this migration");
    }

    [Fact]
    public void MigrationIsAddColumnOnlyAndReversible()
    {
        var path = Path.Combine(RepoRoot(), "src", "SentenceStudio.Shared", "Migrations", $"{MigrationId}.cs");
        var source = File.ReadAllText(path);

        source.Should().Contain("AddColumn<string>");
        source.Should().Contain("DropColumn");
        source.Should().NotContain("DropTable");
        source.Should().NotContain("AlterColumn");
    }
}
