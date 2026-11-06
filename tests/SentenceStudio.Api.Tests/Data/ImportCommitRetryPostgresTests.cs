using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SentenceStudio.Abstractions;
using SentenceStudio.Api.Tests.Coach.Operations;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Data;

/// <summary>
/// The Import Content commit and the video import's resource creation under the retrying
/// execution strategy the web, API and Workers install on PostgreSQL (B75, B76).
/// </summary>
/// <remarks>
/// <para>
/// Both opened their own transaction, which <c>NpgsqlRetryingExecutionStrategy</c> refuses, so
/// every web import commit and every server-side video import failed on PostgreSQL while
/// passing on SQLite (no retrying strategy). Both now run through
/// <see cref="RetryableTransaction"/>.
/// </para>
/// <para>
/// The faults are the two a retry has to survive: a transient error before the commit (the
/// whole unit runs again from a rolled-back state) and a commit that landed but lost its
/// acknowledgement (the unit must not run again, or the import is saved twice). Real
/// PostgreSQL, because which errors are transient is the provider's judgement.
/// </para>
/// </remarks>
public sealed class ImportCommitRetryPostgresTests : IAsyncLifetime
{
    private const string Owner = "import-retry-owner";
    private const string Pasted = "오늘 밥을 먹었어요. 물을 마셨어요.";

    private CoachPostgresHarness _harness = null!;
    private ServiceProvider _provider = null!;
    private readonly LostAcknowledgement _lostAck = new();
    private readonly TransientBeforeCommit _beforeCommit = new();

    /// <summary>Fails the commit once, after the server applied it (the ambiguous case).</summary>
    private sealed class LostAcknowledgement : DbTransactionInterceptor
    {
        private int _armed;
        public int Commits { get; private set; }

        public void ArmOnce() => Interlocked.Exchange(ref _armed, 1);

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        {
            Commits++;
            ThrowIfArmed();
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Commits++;
            ThrowIfArmed();
            return Task.CompletedTask;
        }

        private void ThrowIfArmed()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
                throw new NpgsqlException("Simulated connection loss after the commit was applied.",
                    new System.Net.Sockets.SocketException(54));
        }
    }

    /// <summary>Fails the first insert into a table once, inside the transaction (it rolls back).</summary>
    private sealed class TransientBeforeCommit : DbCommandInterceptor
    {
        private string? _table;
        public int Fired { get; private set; }

        public void ArmOnce(string table) => Volatile.Write(ref _table, table);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfArmed(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfArmed(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void ThrowIfArmed(DbCommand command)
        {
            var table = Volatile.Read(ref _table);
            if (table is null
                || !command.CommandText.Contains($"INSERT INTO \"{table}\"", StringComparison.Ordinal)
                || Interlocked.CompareExchange(ref _table, null, table) != table)
                return;
            Fired++;
            throw new NpgsqlException("Simulated connection loss before the commit.",
                new System.Net.Sockets.SocketException(54));
        }
    }

    private sealed class OwnerPreferences : IPreferencesService
    {
        public T Get<T>(string key, T defaultValue) =>
            key == "active_profile_id" && Owner is T owner ? owner : defaultValue;
        public void Set<T>(string key, T value) { }
        public void Remove(string key) { }
        public void Clear() { }
    }

    private sealed class NoAi : IAiService
    {
        public Task<T> SendPrompt<T>(string prompt, AiTier tier = AiTier.Fast, string? reasoningEffort = null) =>
            throw new NotSupportedException("The commit does not call the AI.");
        public Task<string> SendImage(string imagePath, string prompt) =>
            throw new NotSupportedException();
        public Task<Stream> TextToSpeechAsync(string text, string voice, float speed = 1.0f) =>
            throw new NotSupportedException();
    }

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
            return;

        _harness = await CoachPostgresHarness.CreateAsync(
            "importretry", migrate: false, withApplicationSchema: true);

        var services = new ServiceCollection();
        // Configured the way the deployed web, API and Workers are: Npgsql with retry on failure.
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseNpgsql(_harness.ConnectionString, npgsql => npgsql.EnableRetryOnFailure(
                    3, TimeSpan.FromMilliseconds(10), errorCodesToAdd: null))
               .AddInterceptors(_lostAck, _beforeCommit)
               .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Critical));
        services.AddSingleton<IFileSystemService, StubFileSystem>();
        services.AddSingleton<ISyncService>(new NoOpSyncService());
        services.AddSingleton<IPreferencesService, OwnerPreferences>();
        services.AddSingleton<IAiService, NoAi>();
        services.AddScoped<LearningResourceRepository>();
        services.AddScoped<ExampleSentenceRepository>();
        services.AddScoped<ContentImportService>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.UserProfiles.Add(new UserProfile { Id = Owner, Name = "Owner", Email = "import-retry@test.invalid" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _provider?.Dispose();
        if (_harness is not null)
            await _harness.DisposeAsync();
    }

    private static ContentImportCommit Commit(string title, ImportTarget? target = null,
        DedupMode dedup = DedupMode.Skip) => new()
    {
        Preview = new ContentImportPreview
        {
            Rows = new List<ImportRow>
            {
                new() { RowNumber = 1, TargetLanguageTerm = "밥", ParsedTargetLanguageTerm = "밥",
                    NativeLanguageTerm = "rice", SourceSentence = "오늘 밥을 먹었어요.",
                    SourceSentenceTranslation = "I ate rice today.", Status = RowStatus.Ok, IsSelected = true },
                new() { RowNumber = 2, TargetLanguageTerm = "물", ParsedTargetLanguageTerm = "물",
                    NativeLanguageTerm = "water", SourceSentence = "물을 마셨어요.",
                    SourceSentenceTranslation = "I drank water.", Status = RowStatus.Ok, IsSelected = true }
            },
            SourceText = Pasted
        },
        Target = target ?? new ImportTarget
        {
            Mode = ImportTargetMode.New,
            NewResourceTitle = title,
            TargetLanguage = "Korean",
            NativeLanguage = "English"
        },
        DedupMode = dedup,
        ReviewedByLearner = true
    };

    private async Task<ContentImportResult> ImportAsync(ContentImportCommit commit)
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ContentImportService>().CommitImportAsync(commit);
    }

    private async Task<(int Resources, int Words, int Mappings, int Examples)> CountsAsync(string? title = null)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var resourceIds = await db.LearningResources.AsNoTracking()
            .Where(r => r.UserProfileId == Owner && (title == null || r.Title == title))
            .Select(r => r.Id).ToListAsync();
        return (
            resourceIds.Count,
            await db.VocabularyWords.AsNoTracking().CountAsync(w => w.UserProfileId == Owner),
            await db.ResourceVocabularyMappings.AsNoTracking().CountAsync(m => resourceIds.Contains(m.ResourceId)),
            await db.ExampleSentences.AsNoTracking().CountAsync(e => e.UserProfileId == Owner));
    }

    [PostgresFact]
    public async Task An_import_commits_on_PostgreSQL_with_its_examples()
    {
        var result = await ImportAsync(Commit("Plain import"));

        result.CreatedCount.Should().Be(2);
        (await CountsAsync("Plain import")).Should().Be((1, 2, 2, 2));
    }

    [PostgresFact]
    public async Task A_transient_failure_before_the_commit_runs_the_import_again_once()
    {
        _beforeCommit.ArmOnce("ExampleSentence");

        var result = await ImportAsync(Commit("Retried import"));

        _beforeCommit.Fired.Should().Be(1, "the fault reached the import's last write");
        result.CreatedCount.Should().Be(2, "the retried attempt starts from empty results");
        result.Items.Should().HaveCount(2);
        (await CountsAsync("Retried import")).Should().Be((1, 2, 2, 2),
            "the rolled-back attempt left nothing behind and the retry wrote everything once");
    }

    [PostgresFact]
    public async Task A_commit_whose_acknowledgement_was_lost_is_not_saved_twice()
    {
        _lostAck.ArmOnce();

        var result = await ImportAsync(Commit("Ambiguous import"));

        _lostAck.Commits.Should().BeGreaterThan(0);
        result.CreatedCount.Should().Be(2);
        (await CountsAsync("Ambiguous import")).Should().Be((1, 2, 2, 2),
            "verifySucceeded found the resource chosen before the first attempt");
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.LearningResources.AsNoTracking().SingleAsync(r => r.Title == "Ambiguous import"))
            .Id.Should().Be(result.ResourceId);
    }

    [PostgresFact]
    public async Task An_append_whose_acknowledgement_was_lost_does_not_duplicate_words()
    {
        // Import all creates new words on every run, so a redone append would double them; the
        // append is recognized by the links chosen before the first attempt.
        var first = await ImportAsync(Commit("Append target"));
        var existing = new ImportTarget { Mode = ImportTargetMode.Existing, ExistingResourceId = first.ResourceId };
        _lostAck.ArmOnce();

        var appended = await ImportAsync(Commit("unused", existing, DedupMode.ImportAll));

        appended.CreatedCount.Should().Be(2);
        appended.ResourceId.Should().Be(first.ResourceId);
        (await CountsAsync("Append target")).Should().Be((1, 4, 4, 4),
            "each run wrote two words, their links and their examples once; a redone append would make it six");
    }

    [PostgresFact]
    public async Task The_video_import_creates_its_resource_on_PostgreSQL_through_a_lost_acknowledgement()
    {
        var pipeline = new VideoImportPipelineService(
            _provider, NullLogger<VideoImportPipelineService>.Instance,
            youtubeImport: null!, formatting: null!, aiService: null!, fileSystem: new StubFileSystem());
        var import = new VideoImport
        {
            UserProfileId = Owner,
            VideoUrl = "https://youtube.com/watch?v=RETRY",
            VideoTitle = "Retried video",
            Language = "Korean",
            CleanedTranscript = Pasted
        };
        var items = new[]
        {
            new ExtractedVocabularyItem { TargetLanguageTerm = "밥", NativeLanguageTerm = "rice",
                ExampleSentence = "오늘 밥을 먹었어요.", ExampleSentenceTranslation = "I ate rice today." },
            new ExtractedVocabularyItem { TargetLanguageTerm = "물", NativeLanguageTerm = "water",
                ExampleSentence = "물을 마셨어요.", ExampleSentenceTranslation = "I drank water." }
        };
        List<VideoImportPipelineService.ExtractedVocabularyForImport> Vocabulary() => items
            .Select(item => new VideoImportPipelineService.ExtractedVocabularyForImport(item.ToVocabularyWord("Korean"), item))
            .ToList();

        _beforeCommit.ArmOnce("ResourceVocabularyMapping");
        var retried = await pipeline.CreateLearningResourceAsync(import, Vocabulary());
        _beforeCommit.Fired.Should().Be(1);

        import.VideoTitle = "Ambiguous video";
        _lostAck.ArmOnce();
        var ambiguous = await pipeline.CreateLearningResourceAsync(import, Vocabulary());

        (await CountsAsync("Retried video")).Should().Be((1, 2, 2, 2));
        (await CountsAsync("Ambiguous video")).Resources.Should().Be(1, "not saved a second time");
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.LearningResources.AsNoTracking().SingleAsync(r => r.Title == "Retried video")).Id.Should().Be(retried);
        (await db.LearningResources.AsNoTracking().SingleAsync(r => r.Title == "Ambiguous video")).Id.Should().Be(ambiguous);
    }
}
