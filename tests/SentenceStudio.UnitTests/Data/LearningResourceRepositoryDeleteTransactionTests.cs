using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Data;

/// <summary>
/// Regression cover for deleting a resource on a provider that installs a retrying execution
/// strategy — which is what the API actually runs on.
/// </summary>
/// <remarks>
/// <para>
/// Aspire's Npgsql registration installs <c>NpgsqlRetryingExecutionStrategy</c>, and EF Core
/// refuses a user-initiated <c>BeginTransaction</c> under any strategy that retries. The repository
/// opened its own transaction directly, so on the server every delete threw
/// <c>InvalidOperationException</c> before touching a row, the catch turned it into <c>-1</c>, and
/// the caller reported a failure while the resource quietly survived. Every existing test passed,
/// because they all run on SQLite, which installs no retrying strategy at all.
/// </para>
/// <para>
/// Found by browser E2E on 2026-08-19: Sam's protected resource removal (`SAM-RES-06`) was
/// confirmed by the learner, the ledger recorded <c>execution_failed</c>, and the row was still in
/// Postgres afterwards. The webapp's own resource delete was broken the same way.
/// </para>
/// <para>
/// The strategy below is the minimum that reproduces it: EF's refusal is keyed on
/// <see cref="IExecutionStrategy.RetriesOnFailure"/>, not on the provider, so a retrying strategy
/// over SQLite fails in exactly the same place a real PostgreSQL connection does — without needing
/// a server in the test run.
/// </para>
/// </remarks>
public sealed class LearningResourceRepositoryDeleteTransactionTests : IDisposable
{
    private const string Owner = "delete-tx-owner";
    private const string Stranger = "delete-tx-stranger";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private string _activeUserId = Owner;

    /// <summary>
    /// A strategy that reports it retries, and otherwise runs the operation once.
    /// </summary>
    /// <remarks>
    /// Deriving from <see cref="ExecutionStrategy"/> gets the real
    /// <c>OnFirstExecution</c> guard — the one that throws
    /// "does not support user-initiated transactions" — rather than a hand-rolled imitation of it.
    /// </remarks>
    private sealed class AlwaysRetryingExecutionStrategy : ExecutionStrategy
    {
        public AlwaysRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
            : base(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(1))
        {
        }

        protected override bool ShouldRetryOn(Exception exception) => false;
    }

    public LearningResourceRepositoryDeleteTransactionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseSqlite(_connection, sqlite => sqlite
                   .ExecutionStrategy(d => new AlwaysRetryingExecutionStrategy(d)))
               .ConfigureWarnings(w =>
                   w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

        var preferences = new Mock<IPreferencesService>();
        preferences.Setup(p => p.Get("active_profile_id", It.IsAny<string>())).Returns(() => _activeUserId);

        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.AppDataDirectory).Returns(Directory.GetCurrentDirectory());

        services.AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(preferences.Object);
        services.AddSingleton(fileSystem.Object);
        services.AddSingleton<ISyncService>(new NoOpSyncService());
        services.AddScoped<LearningResourceRepository>();

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
        db.UserProfiles.Add(new UserProfile { Id = Owner, Name = "Owner", Email = "owner@test.invalid" });
        db.UserProfiles.Add(new UserProfile { Id = Stranger, Name = "Stranger", Email = "stranger@test.invalid" });
        db.SaveChanges();
    }

    private LearningResource SeedResource(string userProfileId, string title = "Deletable")
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var resource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = title,
            UserProfileId = userProfileId,
            Language = "Korean",
            MediaType = "Vocabulary List",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.LearningResources.Add(resource);
        db.SaveChanges();
        return resource;
    }

    private (LearningResource Resource, VocabularyWord Word) SeedResourceWithWord(string userProfileId)
    {
        var resource = SeedResource(userProfileId, "Deletable with words");
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var word = new VocabularyWord
        {
            Id = Guid.NewGuid().ToString(),
            TargetLanguageTerm = "수박",
            NativeLanguageTerm = "watermelon",
            Language = "Korean",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.VocabularyWords.Add(word);
        db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
        {
            ResourceId = resource.Id,
            VocabularyWordId = word.Id
        });
        db.VocabularyProgresses.Add(new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userProfileId,
            VocabularyWordId = word.Id
        });
        db.SaveChanges();
        return (resource, word);
    }

    private LearningResourceRepository Repository(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<LearningResourceRepository>();

    [Fact]
    public async Task TheTestStrategyReallyRefusesAUserInitiatedTransaction()
    {
        // Guards this file's own premise. If a future EF version stopped refusing, every test
        // above would keep passing while proving nothing, and the bug could come back unseen.
        // The refusal lands on the first operation inside the transaction, not on opening it —
        // which is exactly where the production stack trace pointed.
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync();

        var act = async () => await db.LearningResources.CountAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*does not support user-initiated transactions*");
    }

    [Fact]
    public async Task DeleteResourceAsync_SucceedsUnderARetryingExecutionStrategy()
    {
        var resource = SeedResource(Owner);

        using var scope = _provider.CreateScope();
        var affected = await Repository(scope).DeleteResourceAsync(resource, Owner);

        affected.Should().BeGreaterThan(
            0,
            "a user-initiated transaction has to run through CreateExecutionStrategy(), or the "
            + "provider the API actually uses refuses it and the delete is silently swallowed");

        using var verify = _provider.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.LearningResources.Any(r => r.Id == resource.Id).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteResourceAsync_StillSweepsOrphanedProgressUnderARetryingStrategy()
    {
        var (resource, word) = SeedResourceWithWord(Owner);

        using var scope = _provider.CreateScope();
        var affected = await Repository(scope).DeleteResourceAsync(resource, Owner);

        affected.Should().BeGreaterThan(0);

        using var verify = _provider.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.ResourceVocabularyMappings.Any(m => m.ResourceId == resource.Id).Should().BeFalse();
        db.VocabularyProgresses.Any(p => p.VocabularyWordId == word.Id).Should().BeFalse(
            "progress whose last reachable mapping went with the resource would otherwise be "
            + "eternally due");
    }

    [Fact]
    public async Task DeleteResourceAsync_StillRefusesAResourceTheCallerDoesNotOwn()
    {
        var strangersResource = SeedResource(Stranger);

        using var scope = _provider.CreateScope();
        var affected = await Repository(scope).DeleteResourceAsync(strangersResource, Owner);

        affected.Should().Be(0, "a retryable transaction must not widen who may delete what");

        using var verify = _provider.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.LearningResources.Any(r => r.Id == strangersResource.Id).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteResourceAsync_StillRefusesWhenNoUserIsInScope()
    {
        var resource = SeedResource(Owner);
        _activeUserId = string.Empty;

        using var scope = _provider.CreateScope();
        var affected = await Repository(scope).DeleteResourceAsync(resource);

        affected.Should().Be(0);

        using var verify = _provider.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.LearningResources.Any(r => r.Id == resource.Id).Should().BeTrue();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }
}
