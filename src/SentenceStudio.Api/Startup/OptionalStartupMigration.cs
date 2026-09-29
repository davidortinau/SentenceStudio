namespace SentenceStudio.Api.Startup;

/// <summary>Optional schema failures may degrade a feature, never a lost lock.</summary>
internal static class OptionalStartupMigration
{
    public static async Task RunAsync(
        Func<CancellationToken, Task> migrate,
        Func<CancellationToken, Task> verifyLock,
        Action<Exception> onFeatureFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(migrate);
        ArgumentNullException.ThrowIfNull(verifyLock);
        ArgumentNullException.ThrowIfNull(onFeatureFailure);

        await verifyLock(cancellationToken);
        try
        {
            await migrate(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Probe the same backend before allowing a nonfatal feature-degrade path.
            // A broken session, missing advisory lock, or cancellation throws here.
            await verifyLock(cancellationToken);
            onFeatureFailure(ex);
            return;
        }

        await verifyLock(cancellationToken);
    }
}
