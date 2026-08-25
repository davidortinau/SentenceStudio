using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SentenceStudio.Abstractions;
using SentenceStudio.Api.Tests.Coach.Operations;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Data;

/// <summary>
/// Deleting a resource when the commit's outcome is genuinely unknown.
/// </summary>
/// <remarks>
/// <para>
/// A retrying execution strategy answers a dropped connection by running the work again. That is
/// right for a statement that never landed and wrong for a commit whose acknowledgement was lost:
/// the row is already gone, the retried delete finds nothing to remove, and the repository reports
/// <c>-1</c> — a failure for something that succeeded. The learner is told their resource is still
/// there and it is not, and Sam's ledger records a failed write beside a change that happened.
/// </para>
/// <para>
/// EF's answer is <c>verifySucceeded</c>: after a transient commit error the strategy asks whether
/// the work landed, and a true answer ends the retry with the ambiguous attempt's result. These
/// tests drive that decision rather than asserting it from the source.
/// </para>
/// <para>
/// Real PostgreSQL, because everything under test is the provider's judgement: which errors
/// <c>NpgsqlRetryingExecutionStrategy</c> treats as transient, and what happens between commit and
/// acknowledgement. The SQLite companions in
/// <c>LearningResourceRepositoryDeleteTransactionTests</c> cover the execution-strategy wiring;
/// they cannot cover this, because SQLite installs no retrying strategy at all.
/// </para>
/// </remarks>
public sealed class LearningResourceDeleteCommitAmbiguityPostgresTests : IAsyncLifetime
{
    private const string Owner = "commit-ambiguity-owner";
    private const string Stranger = "commit-ambiguity-stranger";

    private CoachPostgresHarness _harness = null!;
    private ServiceProvider _provider = null!;
    private CommitAmbiguityInterceptor _interceptor = null!;

    /// <summary>
    /// Fails the commit exactly once, after the server has already applied it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape of the incident rather than an approximation of it: the transaction is committed
    /// on the server and the client is then handed a transient failure, so the client's belief and
    /// the database's state have come apart. Injecting before the commit would produce a rollback,
    /// which is the case that was never broken.
    /// </para>
    /// <para>
    /// <see cref="NpgsqlException"/> wrapping a socket error is what the Npgsql retrying strategy
    /// classifies as transient, so the strategy takes the retry path the repository has to survive
    /// rather than surfacing the error directly.
    /// </para>
    /// </remarks>
    private sealed class CommitAmbiguityInterceptor : DbTransactionInterceptor
    {
        private int _armed;

        public int CommitsObserved { get; private set; }

        public void ArmOnce() => Interlocked.Exchange(ref _armed, 1);

        public override void TransactionCommitted(
            DbTransaction transaction, TransactionEndEventData eventData)
        {
            CommitsObserved++;
            ThrowIfArmed();
            base.TransactionCommitted(transaction, eventData);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            CommitsObserved++;
            ThrowIfArmed();
            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }

        private void ThrowIfArmed()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0)
            {
                return;
            }

            throw new NpgsqlException(
                "Simulated connection loss after the commit was applied.",
                new System.Net.Sockets.SocketException(54));
        }
    }

    /// <summary>Fails every commit with an error no strategy treats as transient.</summary>
    private sealed class AlwaysFailingCommitInterceptor : DbTransactionInterceptor
    {
        public override InterceptionResult TransactionCommitting(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result) =>
            throw new InvalidOperationException("Simulated non-transient commit failure.");

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated non-transient commit failure.");
    }

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync(
            "resdelete", migrate: false, withApplicationSchema: true);
        _interceptor = new CommitAmbiguityInterceptor();

        _provider = BuildProvider(_interceptor);

        using var bootstrap = _provider.CreateScope();
        var db = bootstrap.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.UserProfiles.Add(new UserProfile { Id = Owner, Name = "Owner", Email = "owner@test.invalid" });
        db.UserProfiles.Add(new UserProfile { Id = Stranger, Name = "Stranger", Email = "stranger@test.invalid" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _provider?.Dispose();
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    /// <summary>A provider configured the way the deployed API is: Npgsql with retry on failure.</summary>
    private ServiceProvider BuildProvider(IInterceptor interceptor, int maxRetryCount = 3)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseNpgsql(_harness.ConnectionString, npgsql => npgsql.EnableRetryOnFailure(
                    maxRetryCount, TimeSpan.FromMilliseconds(10), errorCodesToAdd: null))
               .AddInterceptors(interceptor)
               .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        services.AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Critical));
        services.AddSingleton<IFileSystemService, StubFileSystem>();
        services.AddSingleton<ISyncService>(new NoOpSyncService());
        services.AddScoped<LearningResourceRepository>();

        return services.BuildServiceProvider();
    }

    // ================================================================== helpers

    private LearningResourceRepository NewRepository(ServiceProvider? provider = null) =>
        (provider ?? _provider).CreateScope().ServiceProvider
            .GetRequiredService<LearningResourceRepository>();

    private async Task<LearningResource> SeedResourceAsync(string owner, string title = "Deletable")
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var resource = new LearningResource
        {
            Id = Guid.NewGuid().ToString("n"),
            Title = title,
            UserProfileId = owner,
            Language = "Korean",
            MediaType = "Vocabulary List",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.LearningResources.Add(resource);
        await db.SaveChangesAsync();
        return resource;
    }

    private async Task<(LearningResource Resource, string WordId)> SeedResourceWithWordAsync(string owner)
    {
        var resource = await SeedResourceAsync(owner, "Deletable with words");

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var word = new VocabularyWord
        {
            Id = Guid.NewGuid().ToString("n"),
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
            Id = Guid.NewGuid().ToString("n"),
            UserId = owner,
            VocabularyWordId = word.Id
        });
        await db.SaveChangesAsync();
        return (resource, word.Id);
    }

    private async Task<bool> ResourceExistsAsync(string resourceId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.LearningResources.AsNoTracking().AnyAsync(r => r.Id == resourceId);
    }

    // ================================================================== commit ambiguity

    /// <summary>
    /// A commit that landed and lost its acknowledgement is reported as the success it was.
    /// </summary>
    /// <remarks>
    /// The exact defect. Without verification the retry re-runs the delete, finds the row gone,
    /// and the repository answers <c>-1</c> — so the caller refuses, the ledger records a failed
    /// write, and the resource is gone anyway.
    /// </remarks>
    [PostgresFact]
    public async Task A_commit_whose_acknowledgement_was_lost_is_reported_as_success()
    {
        var resource = await SeedResourceAsync(Owner);

        _interceptor.ArmOnce();
        var affected = await NewRepository().DeleteResourceAsync(resource, Owner);

        affected.Should().NotBe(-1, "the commit landed; reporting failure would be a lie");
        affected.Should().BeGreaterThan(0);

        (await ResourceExistsAsync(resource.Id)).Should().BeFalse("the delete really happened");
        _interceptor.CommitsObserved.Should().BeGreaterThan(0, "the commit path was exercised");
    }

    /// <summary>
    /// The related deletes land with the resource, not separately from it.
    /// </summary>
    /// <remarks>
    /// The orphan sweep runs inside the same transaction, so a commit that lands takes the
    /// progress rows with it. A verification that only looked at the resource would happily report
    /// success for a half-applied state; this asserts the state is not half-applied.
    /// </remarks>
    [PostgresFact]
    public async Task An_ambiguous_commit_still_leaves_the_related_rows_consistent()
    {
        var (resource, wordId) = await SeedResourceWithWordAsync(Owner);

        _interceptor.ArmOnce();
        var affected = await NewRepository().DeleteResourceAsync(resource, Owner);

        affected.Should().BeGreaterThan(0);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        (await db.LearningResources.AnyAsync(r => r.Id == resource.Id)).Should().BeFalse();
        (await db.ResourceVocabularyMappings.AnyAsync(m => m.ResourceId == resource.Id))
            .Should().BeFalse("the mapping goes with the resource");
        (await db.VocabularyProgresses.AnyAsync(p => p.UserId == Owner && p.VocabularyWordId == wordId))
            .Should().BeFalse("the orphan sweep committed in the same transaction");
    }

    /// <summary>
    /// A repeat after an ambiguous commit answers not-found rather than failure.
    /// </summary>
    /// <remarks>
    /// Idempotency from the caller's side: a client that retries the whole request must not be
    /// told the delete failed. Zero is the honest answer — there is nothing of theirs left to
    /// delete — and it is not <c>-1</c>.
    /// </remarks>
    [PostgresFact]
    public async Task Deleting_again_after_an_ambiguous_commit_is_not_reported_as_a_failure()
    {
        var resource = await SeedResourceAsync(Owner);

        _interceptor.ArmOnce();
        (await NewRepository().DeleteResourceAsync(resource, Owner)).Should().BeGreaterThan(0);

        var again = await NewRepository().DeleteResourceAsync(resource, Owner);

        again.Should().Be(0, "nothing of theirs is left, which is not-found and not a failure");
        again.Should().NotBe(-1);
    }

    // ================================================================== the ordinary paths

    /// <summary>An uninterrupted delete on the real provider is unchanged.</summary>
    [PostgresFact]
    public async Task An_ordinary_delete_still_succeeds()
    {
        var resource = await SeedResourceAsync(Owner);

        var affected = await NewRepository().DeleteResourceAsync(resource, Owner);

        affected.Should().BeGreaterThan(0);
        (await ResourceExistsAsync(resource.Id)).Should().BeFalse();
    }

    /// <summary>
    /// Another learner's resource is refused, and the verification never sees it.
    /// </summary>
    /// <remarks>
    /// The verification treats an absent row as this operation's success, which is only sound
    /// because ownership is proved before the transaction opens. A foreign resource is refused
    /// there and never reaches it — asserted by the row still being present afterwards, which no
    /// amount of verification reasoning could have produced.
    /// </remarks>
    [PostgresFact]
    public async Task Another_learners_resource_is_refused_and_left_alone()
    {
        var resource = await SeedResourceAsync(Stranger);

        var affected = await NewRepository().DeleteResourceAsync(resource, Owner);

        affected.Should().Be(0, "not-found and not-owned answer the same way");
        affected.Should().NotBe(-1);
        (await ResourceExistsAsync(resource.Id)).Should().BeTrue("the stranger's row is untouched");
    }

    /// <summary>A resource that does not exist is not-found, not a failure.</summary>
    [PostgresFact]
    public async Task A_missing_resource_is_not_found()
    {
        var missing = new LearningResource { Id = Guid.NewGuid().ToString("n"), UserProfileId = Owner };

        (await NewRepository().DeleteResourceAsync(missing, Owner)).Should().Be(0);
    }

    /// <summary>
    /// A genuine failure is still reported as one.
    /// </summary>
    /// <remarks>
    /// The counterweight to everything above, and the test that stops the fix from becoming "always
    /// answer success". The commit never lands, the row is still there, and the caller has to hear
    /// so — otherwise Sam would report a removal that did not happen, which is the same class of
    /// lie in the opposite direction.
    /// </remarks>
    [PostgresFact]
    public async Task A_genuine_failure_is_still_reported_as_a_failure()
    {
        var resource = await SeedResourceAsync(Owner);

        await using var failing = BuildProvider(new AlwaysFailingCommitInterceptor(), maxRetryCount: 1);

        var affected = await NewRepository(failing).DeleteResourceAsync(resource, Owner);

        affected.Should().Be(-1, "the work did not land, and the caller has to be told");
        (await ResourceExistsAsync(resource.Id)).Should().BeTrue("nothing was removed");
    }
}
