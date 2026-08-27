using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentenceStudio.Data;
using SentenceStudio.Services.Vocabulary;
using SentenceStudio.Shared.Models;
using SentenceStudio.UnitTests.Logging;

namespace SentenceStudio.UnitTests.Services.Vocabulary;

/// <summary>
/// A real SQLite database plus a scripted chat client, so the backfill is exercised end to end:
/// actual EF translation of the ownership union, actual transactions, actual value conversion of
/// the part-of-speech token.
/// </summary>
internal sealed class PartOfSpeechBackfillHarness : IDisposable
{
    public const string OwnerId = "profile-owner";
    public const string OtherTenantId = "profile-other-tenant";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _dbOptions;

    public PartOfSpeechBackfillHarness()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public CapturingLoggerProvider Logs { get; } = new();

    public ApplicationDbContext NewContext() => new(_dbOptions);

    /// <summary>Closes the database so any query at all throws. Proves a "no query" claim.</summary>
    public void BreakDatabase() => _connection.Close();

    /// <summary>Adds a word owned through <c>VocabularyProgress</c>.</summary>
    public VocabularyWord AddWordOwnedByProgress(
        string id,
        string ownerId,
        string term = "책",
        VocabularyPartOfSpeech? partOfSpeech = null)
    {
        using var db = NewContext();
        var word = NewWord(id, term, partOfSpeech);
        db.VocabularyWords.Add(word);
        db.VocabularyProgresses.Add(new VocabularyProgress
        {
            Id = $"progress-{id}",
            VocabularyWordId = id,
            UserId = ownerId
        });
        db.SaveChanges();
        return word;
    }

    /// <summary>Adds a word owned only through a mapping to one of the learner's resources.</summary>
    public VocabularyWord AddWordOwnedByResource(
        string id,
        string ownerId,
        string term = "학교",
        VocabularyPartOfSpeech? partOfSpeech = null)
    {
        using var db = NewContext();
        var resourceId = $"resource-{id}";
        db.VocabularyWords.Add(NewWord(id, term, partOfSpeech));
        db.LearningResources.Add(new LearningResource
        {
            Id = resourceId,
            UserProfileId = ownerId,
            Title = "Owned resource"
        });
        db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
        {
            Id = $"mapping-{id}",
            ResourceId = resourceId,
            VocabularyWordId = id
        });
        db.SaveChanges();
        return db.VocabularyWords.Single(w => w.Id == id);
    }

    /// <summary>Adds a word nobody owns: no progress row, no resource mapping.</summary>
    public void AddUnownedWord(string id, string term = "고아")
    {
        using var db = NewContext();
        db.VocabularyWords.Add(NewWord(id, term, null));
        db.SaveChanges();
    }

    public VocabularyPartOfSpeech? PartOfSpeechOf(string id)
    {
        using var db = NewContext();
        return db.VocabularyWords.AsNoTracking().Single(w => w.Id == id).PartOfSpeech;
    }

    public int CountClassified()
    {
        using var db = NewContext();
        return db.VocabularyWords.AsNoTracking().Count(w => w.PartOfSpeech != null);
    }

    public VocabularyPartOfSpeechBackfillService CreateService(
        FakePartOfSpeechChatClient chatClient,
        VocabularyPartOfSpeechBackfillOptions options,
        ApplicationDbContext? db = null)
    {
        var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(Logs);
        });

        return new VocabularyPartOfSpeechBackfillService(
            db ?? NewContext(),
            chatClient,
            Options.Create(options),
            loggerFactory.CreateLogger<VocabularyPartOfSpeechBackfillService>());
    }

    public static VocabularyPartOfSpeechBackfillOptions EnabledFor(
        string? userProfileId = OwnerId,
        int batchSize = VocabularyPartOfSpeechBackfillOptions.DefaultBatchSize,
        int maxWords = VocabularyPartOfSpeechBackfillOptions.DefaultMaxWords) =>
        new()
        {
            Enabled = true,
            UserProfileIds = userProfileId is null ? new List<string>() : new List<string> { userProfileId },
            BatchSize = batchSize,
            MaxWords = maxWords
        };

    private static VocabularyWord NewWord(string id, string term, VocabularyPartOfSpeech? partOfSpeech) => new()
    {
        Id = id,
        TargetLanguageTerm = term,
        // Private material that must never reach the model.
        NativeLanguageTerm = "SECRET-GLOSS",
        MnemonicText = "SECRET-MNEMONIC",
        Tags = "SECRET-TAG",
        Lemma = "lemma-form",
        Language = "Korean",
        LexicalUnitType = LexicalUnitType.Word,
        PartOfSpeech = partOfSpeech,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    public void Dispose()
    {
        if (_connection.State != System.Data.ConnectionState.Closed)
        {
            _connection.Close();
        }

        _connection.Dispose();
    }
}
