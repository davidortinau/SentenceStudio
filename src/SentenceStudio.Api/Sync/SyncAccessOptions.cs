namespace SentenceStudio.Api.Sync;

/// <summary>
/// <c>Sync:AllowHeaderlessClients</c>, false unless configured, in every environment
/// (Captain 2026-11-04, decision 1; GitHub #204). A CoreSync request without the owner header
/// gets no owner checks: a download returns every learner's rows and an upload can write any
/// row. With the switch closed, every CoreSync route refuses such a request with 403.
/// Opening it only exists for a deliberate, temporary rollout of legacy builds.
/// </summary>
internal sealed record SyncAccessOptions(bool AllowHeaderlessClients)
{
    internal const string AllowHeaderlessClientsKey = "Sync:AllowHeaderlessClients";

    internal static SyncAccessOptions From(IConfiguration configuration) =>
        new(configuration.GetValue(AllowHeaderlessClientsKey, false));
}
