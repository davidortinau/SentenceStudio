using Azure.Core;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentenceStudio.Api.Coach.Opportunities.Digest;
using SentenceStudio.Api.Coach.Persistence;

namespace SentenceStudio.Tools.SamOpportunityDigest;

/// <summary>
/// Prints the content-free Sam opportunity digest for an operator to review.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this can read:</b> counts, closed-vocabulary codes, review statuses, timestamps,
/// distinct-learner counts, and content-free fingerprints. <b>What it cannot read:</b> anything
/// else. The projection lives in <see cref="CoachOpportunityDigestReader"/> and names no owner,
/// conversation, message, turn, or write identifier — the guard tests in
/// <c>CoachOpportunityDigestTests</c> assert that against the SQL the provider actually emits,
/// not against this comment.
/// </para>
/// <para>
/// <b>Credentials never live in this repository.</b> The connection string arrives in an
/// environment variable, or is assembled from a host/database/user plus an Entra access token
/// fetched at run time. Nothing is written to disk, nothing is echoed, and the tool refuses to
/// start rather than falling back to a default connection.
/// </para>
/// <para>
/// <b>The session is read-only at the server.</b> <c>default_transaction_read_only=on</c> is sent
/// as a startup option on every pooled connection, so a write is refused by PostgreSQL rather than
/// by this program's good intentions.
/// </para>
/// </remarks>
internal static class Program
{
    private const string ConnectionStringVariable = "COACH_DIGEST_CONNECTION_STRING";
    private const string AspireConnectionStringVariable = "ConnectionStrings__sentencestudio";
    private const string HostVariable = "COACH_DIGEST_HOST";
    private const string DatabaseVariable = "COACH_DIGEST_DATABASE";
    private const string UserVariable = "COACH_DIGEST_USER";
    private const string IdentityVariable = "COACH_DIGEST_AZURE_IDENTITY";

    /// <summary>The Entra scope PostgreSQL Flexible Server accepts as a password.</summary>
    private const string PostgresEntraScope = "https://ossrdbms-aad.database.windows.net/.default";

    /// <summary>
    /// Cancelled by Ctrl+C, so an operator can abandon a slow read against a remote database
    /// without leaving a half-written output file behind.
    /// </summary>
    private static readonly CancellationTokenSource Cancellation = new();

    private const int ExitSuccess = 0;
    private const int ExitUsage = 1;
    private const int ExitNotConfigured = 2;
    private const int ExitFailed = 3;

    private static async Task<int> Main(string[] args)
    {
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Cancellation.Cancel();
        };

        Options options;

        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(Options.Usage).ConfigureAwait(false);
            return ExitUsage;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(Options.Usage);
            return ExitSuccess;
        }

        string connectionString;

        try
        {
            connectionString = await ResolveConnectionStringAsync(Cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitNotConfigured;
        }

        try
        {
            var digest = await ReadAsync(connectionString, options).ConfigureAwait(false);

            var rendered = options.Json
                ? CoachOpportunityDigestJson.Serialize(digest)
                : CoachOpportunityDigestMarkdown.Render(digest);

            if (options.OutputPath is { Length: > 0 } path)
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(path, rendered, Cancellation.Token)
                    .ConfigureAwait(false);

                await Console.Error.WriteLineAsync(
                    $"Wrote {digest.Lines.Count} problem line(s) and " +
                    $"{digest.ReportReasons.Count} reason line(s) to {path}.")
                    .ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine(rendered);
            }

            return ExitSuccess;
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C. A stack trace here would read like a failure of the digest rather than an
            // operator changing their mind.
            await Console.Error.WriteLineAsync("Cancelled.").ConfigureAwait(false);
            return ExitFailed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The message, never the connection string: an exception from Npgsql can carry the
            // host and user, and this output is designed to be pasted into an issue or a CI log.
            await Console.Error.WriteLineAsync(
                $"The digest could not be read: {ex.GetType().Name}. " +
                "Check network reachability to the database and that the credential is valid. " +
                "See docs/sam-opportunity-digest.md.").ConfigureAwait(false);

            return ExitFailed;
        }
    }

    private static async Task<CoachOpportunityDigest> ReadAsync(
        string connectionString,
        Options options)
    {
        // Read-only at the server, not merely by convention here. See
        // CoachOpportunityDigestConnection, which is proven against a real PostgreSQL server.
        var readOnly = CoachOpportunityDigestConnection.ForReadOnly(
            connectionString,
            "sam-opportunity-digest");

        var contextOptions = new DbContextOptionsBuilder<CoachDbContext>()
            .UseNpgsql(readOnly)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        await using var db = new CoachDbContext(contextOptions);

        var reader = new CoachOpportunityDigestReader(db, TimeProvider.System);

        return await reader.ReadAsync(options.SinceUtc, Cancellation.Token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the connection string from the environment, or from an Entra token.
    /// </summary>
    /// <remarks>
    /// There is deliberately no fallback to a compiled-in host, a local default, or a file on
    /// disk. A tool that reads a production ledger must fail with instructions rather than
    /// silently connect to whatever it can find.
    /// </remarks>
    private static async Task<string> ResolveConnectionStringAsync(CancellationToken cancellationToken)
    {
        var direct = Environment.GetEnvironmentVariable(ConnectionStringVariable)
                     ?? Environment.GetEnvironmentVariable(AspireConnectionStringVariable);

        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        var useIdentity = string.Equals(
            Environment.GetEnvironmentVariable(IdentityVariable), "1", StringComparison.Ordinal);

        var host = Environment.GetEnvironmentVariable(HostVariable);
        var database = Environment.GetEnvironmentVariable(DatabaseVariable);
        var user = Environment.GetEnvironmentVariable(UserVariable);

        if (!useIdentity
            || string.IsNullOrWhiteSpace(host)
            || string.IsNullOrWhiteSpace(database)
            || string.IsNullOrWhiteSpace(user))
        {
            throw new InvalidOperationException(
                $"No database credential was supplied. Set {ConnectionStringVariable}, or set " +
                $"{IdentityVariable}=1 together with {HostVariable}, {DatabaseVariable}, and " +
                $"{UserVariable} to authenticate with an Entra token. " +
                "See docs/sam-opportunity-digest.md.");
        }

        var credential = new DefaultAzureCredential();

        var token = await credential.GetTokenAsync(
            new TokenRequestContext([PostgresEntraScope]),
            cancellationToken).ConfigureAwait(false);

        return new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Database = database,
            Username = user,
            Password = token.Token,
            SslMode = SslMode.Require
        }.ConnectionString;
    }

    /// <summary>What the caller asked for.</summary>
    private sealed record Options(
        DateTime? SinceUtc,
        bool Json,
        string? OutputPath,
        bool ShowHelp)
    {
        public const string Usage = """
            sam-opportunity-digest — the content-free Sam opportunity digest.

              --days <n>       Window, in whole days back from now. Default 7. Use 0 for everything retained.
              --since <utc>    Explicit ISO-8601 UTC lower bound. Overrides --days.
              --json           Emit JSON instead of markdown.
              --output <path>  Write to a file instead of stdout.
              --help           Print this.

            Credentials (one of):
              COACH_DIGEST_CONNECTION_STRING   A PostgreSQL connection string.
              COACH_DIGEST_AZURE_IDENTITY=1    Plus COACH_DIGEST_HOST, COACH_DIGEST_DATABASE,
                                               COACH_DIGEST_USER — authenticates with an Entra token.

            The output carries counts, closed codes, review statuses, timestamps, and content-free
            fingerprints. It never carries learner content, owner ids, conversation ids, message
            ids, tool arguments, emails, or decrypted evidence.
            """;

        public static Options Parse(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);

            var days = 7;
            DateTime? since = null;
            var json = false;
            string? output = null;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--help" or "-h":
                        return new Options(null, false, null, ShowHelp: true);

                    case "--json":
                        json = true;
                        break;

                    case "--days":
                        days = ParseInt(Next(args, ref i, "--days"), "--days");
                        break;

                    case "--since":
                        since = ParseSince(Next(args, ref i, "--since"));
                        break;

                    case "--output" or "-o":
                        output = Next(args, ref i, "--output");
                        break;

                    default:
                        throw new ArgumentException($"Unrecognised argument '{args[i]}'.");
                }
            }

            if (since is null && days > 0)
            {
                since = DateTime.UtcNow.AddDays(-days);
            }

            return new Options(since, json, output, ShowHelp: false);
        }

        private static string Next(string[] args, ref int index, string name)
        {
            index++;

            if (index >= args.Length)
            {
                throw new ArgumentException($"{name} requires a value.");
            }

            return args[index];
        }

        private static int ParseInt(string value, string name) =>
            int.TryParse(value, out var parsed) && parsed >= 0
                ? parsed
                : throw new ArgumentException($"{name} must be a non-negative whole number.");

        private static DateTime ParseSince(string value) =>
            DateTime.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed
                : throw new ArgumentException("--since must be an ISO-8601 UTC timestamp.");
    }
}
