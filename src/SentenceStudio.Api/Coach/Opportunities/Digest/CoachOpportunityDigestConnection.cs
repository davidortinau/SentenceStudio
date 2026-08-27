using Npgsql;

namespace SentenceStudio.Api.Coach.Opportunities.Digest;

/// <summary>
/// Builds the connection the digest reads through: read-only, at the server.
/// </summary>
/// <remarks>
/// <para>
/// The digest is an operator tool that runs against Production with whatever rights the operator's
/// own credential carries — which is usually enough to write. The projection never writes, but
/// "never writes" is a property of code somebody can change; <c>default_transaction_read_only=on</c>
/// is a property of the session PostgreSQL itself enforces, and a write is refused by the database
/// rather than by this program's good intentions.
/// </para>
/// <para>
/// Sent as a startup option rather than as a <c>SET</c> statement on first use, because the pool
/// opens connections lazily: a statement issued once on one connection would leave every other
/// pooled connection writable.
/// </para>
/// <para>
/// This lives in the API assembly rather than in the tool so it can be proven against a real
/// server — see <c>CoachOpportunityDigestPostgresTests</c>, which asserts the session reports
/// itself read-only and that an insert through it is refused.
/// </para>
/// </remarks>
public static class CoachOpportunityDigestConnection
{
    /// <summary>The PostgreSQL startup option that makes the whole session read-only.</summary>
    public const string ReadOnlyStartupOption = "-c default_transaction_read_only=on";

    /// <summary>
    /// Returns <paramref name="connectionString"/> with the read-only startup option applied.
    /// </summary>
    /// <param name="connectionString">The operator's connection string.</param>
    /// <param name="applicationName">
    /// What the session calls itself in <c>pg_stat_activity</c>, so a DBA looking at a live server
    /// can tell the digest apart from the application.
    /// </param>
    public static string ForReadOnly(string connectionString, string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        builder.Options = AppendReadOnly(builder.Options);
        builder.ApplicationName = applicationName;
        builder.CommandTimeout = 60;

        return builder.ConnectionString;
    }

    /// <summary>
    /// Adds the read-only option without dropping any startup option already present.
    /// </summary>
    /// <remarks>
    /// Appended rather than assigned: a deployment that already passes startup options — a search
    /// path, a statement timeout — must keep them, and silently replacing them would change
    /// behaviour the operator configured deliberately.
    /// </remarks>
    public static string AppendReadOnly(string? existing) =>
        string.IsNullOrWhiteSpace(existing)
            ? ReadOnlyStartupOption
            : $"{existing} {ReadOnlyStartupOption}";
}
