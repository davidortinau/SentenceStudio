using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Data;

public sealed class ExampleSentenceRepositoryConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _keeper;
    private readonly ServiceProvider _provider;
    private readonly FirstQueryBarrier _barrier = new();

    public ExampleSentenceRepositoryConcurrencyTests()
    {
        var databaseName = $"hint-overlap-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite(connectionString)
                .AddInterceptors(_barrier)
                .ConfigureWarnings(warnings =>
                    warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddLogging();
        services.AddScoped<ExampleSentenceRepository>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
        db.UserProfiles.Add(new UserProfile { Id = "user-a" });
        db.LearningResources.Add(new LearningResource
        {
            Id = "resource-a",
            Title = "Resource",
            UserProfileId = "user-a"
        });
        db.VocabularyWords.AddRange(
            new VocabularyWord { Id = "word-a", TargetLanguageTerm = "하나" },
            new VocabularyWord { Id = "word-b", TargetLanguageTerm = "둘" });
        db.ResourceVocabularyMappings.AddRange(
            new ResourceVocabularyMapping { ResourceId = "resource-a", VocabularyWordId = "word-a" },
            new ResourceVocabularyMapping { ResourceId = "resource-a", VocabularyWordId = "word-b" });
        db.ExampleSentences.AddRange(
            new ExampleSentence
            {
                VocabularyWordId = "word-a",
                LearningResourceId = "resource-a",
                TargetSentence = "하나예요.",
                Status = ExampleSentenceStatus.Curated
            },
            new ExampleSentence
            {
                VocabularyWordId = "word-b",
                LearningResourceId = "resource-a",
                TargetSentence = "둘이에요.",
                Status = ExampleSentenceStatus.Curated
            });
        db.SaveChanges();
        _barrier.Arm();
    }

    [Fact]
    public async Task OverlappingHintLoads_UseIndependentContextsAndCancellationDoesNotPoisonReplacement()
    {
        using var scope = _provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ExampleSentenceRepository>();
        using var firstCts = new CancellationTokenSource();

        var first = repository.GetQuizHintsForWordsAsync(
            "user-a",
            ["word-a"],
            "A1",
            ct: firstCts.Token);
        await _barrier.FirstQueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var replacement = await repository.GetQuizHintsForWordsAsync(
            "user-a",
            ["word-b"],
            "A1");

        replacement.Should().ContainSingle()
            .Which.VocabularyWordId.Should().Be("word-b");

        firstCts.Cancel();
        _barrier.ReleaseFirst.TrySetResult();
        var canceled = async () => await first;
        await canceled.Should().ThrowAsync<OperationCanceledException>();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _keeper.Dispose();
    }

    private sealed class FirstQueryBarrier : DbCommandInterceptor
    {
        private int _armed;
        private int _blocked;

        public TaskCompletionSource FirstQueryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1
                && Interlocked.CompareExchange(ref _blocked, 1, 0) == 0)
            {
                FirstQueryStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
