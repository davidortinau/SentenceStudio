using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Data;

/// <summary>
/// Runs one unit of work in a transaction owned by the context's execution strategy (B75, B76).
/// </summary>
/// <remarks>
/// <para>
/// The Aspire Npgsql registration (web, API, Workers) installs
/// <c>NpgsqlRetryingExecutionStrategy</c>, which refuses a transaction the caller begins itself
/// (<c>InvalidOperationException</c> at <c>BeginTransactionAsync</c>). SQLite (MAUI, native)
/// installs no retrying strategy, so code that opens its own transaction passes there and fails
/// on PostgreSQL.
/// </para>
/// <para>
/// A transient failure runs the whole unit again from a cleared change tracker, so an attempt
/// never replays what a rolled-back one staged. A commit whose acknowledgement was lost is
/// ambiguous: <paramref name="verifySucceeded"/> is then asked whether the work landed (also on a
/// cleared tracker), and a true answer ends the retry with that attempt's result instead of
/// writing it twice. It must check something the unit writes under an id or value chosen before
/// this call, because every attempt runs the unit again.
/// </para>
/// <para>
/// Not for a caller that already holds a transaction (the strategy refuses that too); other
/// hand-rolled copies of this pattern are listed under B76 in docs/native-port-backports.md.
/// </para>
/// </remarks>
public static class RetryableTransaction
{
    public static Task<TResult> ExecuteAsync<TResult>(
        DbContext db,
        Func<CancellationToken, Task<TResult>> operation,
        Func<CancellationToken, Task<bool>> verifySucceeded,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(verifySucceeded);

        return db.Database.CreateExecutionStrategy().ExecuteInTransactionAsync(
            async ct =>
            {
                db.ChangeTracker.Clear();
                return await operation(ct);
            },
            async ct =>
            {
                db.ChangeTracker.Clear();
                return await verifySucceeded(ct);
            },
            cancellationToken);
    }
}
