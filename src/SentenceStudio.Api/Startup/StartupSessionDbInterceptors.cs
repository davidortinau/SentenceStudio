using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SentenceStudio.Api.Startup;

/// <summary>EF may never reopen or close the caller-owned lock connection.</summary>
internal sealed class StartupSessionConnectionInterceptor(StartupPostgresSessionFence fence)
    : DbConnectionInterceptor
{
    public override InterceptionResult ConnectionOpening(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        fence.AssertConnection(connection);
        fence.LatchLoss();
        throw new InvalidOperationException("EF attempted to open a new startup database connection.");
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        fence.AssertConnection(connection, cancellationToken);
        fence.LatchLoss();
        throw new InvalidOperationException("EF attempted to reopen the startup database connection.");
    }

    public override InterceptionResult ConnectionClosing(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        fence.AssertConnection(connection);
        fence.LatchLoss();
        throw new InvalidOperationException("EF attempted to close the startup database connection.");
    }

    public override ValueTask<InterceptionResult> ConnectionClosingAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        fence.AssertConnection(connection);
        fence.LatchLoss();
        throw new InvalidOperationException("EF attempted to close the startup database connection.");
    }
}

/// <summary>Every EF migration, backfill, seed, and Identity command checks the lock owner.</summary>
internal sealed class StartupSessionCommandInterceptor(StartupPostgresSessionFence fence)
    : DbCommandInterceptor
{
    private void AssertCommand(DbCommand command, CancellationToken cancellationToken = default)
        => fence.AssertConnection(command.Connection
            ?? throw new InvalidOperationException("A startup command has no database connection."),
            cancellationToken);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        AssertCommand(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        AssertCommand(command, cancellationToken);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        AssertCommand(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        AssertCommand(command, cancellationToken);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        AssertCommand(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        AssertCommand(command, cancellationToken);
        return ValueTask.FromResult(result);
    }
}
