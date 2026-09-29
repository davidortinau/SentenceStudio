using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentenceStudio.Contracts.Startup;
using SentenceStudio.Data;

namespace SentenceStudio.Workers.Startup;

public interface IWorkerStartupReadiness
{
    Task WaitAsync(CancellationToken cancellationToken);
}

internal interface IWorkerMigrationMetadata
{
    string GetExpectedApplicationMigrationId();
    Task<PostgresDatabaseReadiness> GetDatabaseReadinessAsync(
        string expectedMigrationId, CancellationToken cancellationToken);
}

internal sealed class EfWorkerMigrationMetadata(ApplicationDbContext db) : IWorkerMigrationMetadata
{
    public string GetExpectedApplicationMigrationId() =>
        db.Database.GetMigrations().LastOrDefault()
        ?? throw new InvalidOperationException(
            "Workers cannot start: no application migrations were discovered.");

    public async Task<PostgresDatabaseReadiness> GetDatabaseReadinessAsync(
        string expectedMigrationId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
        {
            throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.WrongProvider);
        }

        try
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                var connection = db.Database.GetDbConnection();
                return await PostgresDatabaseIdentity.ReadReadinessAsync(
                    connection,
                    () => db.Database.GetDbConnection(),
                    () => connection is NpgsqlConnection npgsql ? npgsql.ProcessID : 0,
                    async ct => (await db.Database.GetPendingMigrationsAsync(ct)).Count(),
                    expectedMigrationId, cancellationToken);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
        catch (PostgresDatabaseIdentityException)
        {
            throw;
        }
        catch (DbException)
        {
            throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.QueryUnavailable);
        }
        catch (InvalidOperationException)
        {
            throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.QueryUnavailable);
        }
        catch (TimeoutException)
        {
            throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.QueryUnavailable);
        }
    }
}

internal sealed class WorkerStartupReadiness(
    IHttpClientFactory clientFactory,
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    ILogger<WorkerStartupReadiness> logger) : IHostedService, IWorkerStartupReadiness
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitAsync(CancellationToken cancellationToken) =>
        _ready.Task.WaitAsync(cancellationToken);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, lifetime.ApplicationStopping);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var metadata = scope.ServiceProvider.GetRequiredService<IWorkerMigrationMetadata>();
            var expectedMigrationId = metadata.GetExpectedApplicationMigrationId();
            using var api = clientFactory.CreateClient(WorkerApiReadinessGate.ClientName);

            logger.LogInformation("Waiting for the API migration owner and application schema before starting Workers.");
            await WorkerApiReadinessGate.WaitAsync(
                api, ct => metadata.GetDatabaseReadinessAsync(expectedMigrationId, ct), expectedMigrationId,
                cancellationToken: stopping.Token);

            _ready.TrySetResult();
            logger.LogInformation("Compatible API and application schema are ready; Workers may start.");
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            _ready.TrySetCanceled(stopping.Token);
            throw;
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            logger.LogCritical(ex, "Workers startup refused: API/schema migration gate failed.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ready.TrySetCanceled();
        return Task.CompletedTask;
    }
}
