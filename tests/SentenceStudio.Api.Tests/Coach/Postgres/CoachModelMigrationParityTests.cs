using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// The coach model and the coach migrations describe the same schema.
/// </summary>
/// <remarks>
/// <para>
/// This is the assertion that used to be a suppressed warning. Every host that built a
/// <see cref="CoachDbContext"/> ignored <c>PendingModelChangesWarning</c>, so a model change made
/// without a migration produced no error anywhere: <c>MigrateAsync</c> ran, the host started, and
/// the first request that touched the missing column was the report.
/// </para>
/// <para>
/// The suppression is gone from the API host and from every test harness that builds this context.
/// This test is the same check moved forward in time — it fails in CI, on the model, before a
/// database is involved at all, so the answer arrives while the person who changed the model is
/// still looking at it.
/// </para>
/// <para>
/// It needs no server. <c>HasPendingModelChanges</c> compares the model built from the context
/// against the snapshot compiled from <c>Migrations/</c>; the connection string is never opened,
/// which is why this runs as an ordinary fact rather than a PostgreSQL one.
/// </para>
/// </remarks>
public sealed class CoachModelMigrationParityTests
{
    /// <summary>A configured context whose connection is never opened.</summary>
    private static CoachDbContext NewContext() =>
        new(new DbContextOptionsBuilder<CoachDbContext>()
            .UseNpgsql(
                "Host=model-parity.invalid;Database=unused",
                npgsql => npgsql.MigrationsHistoryTable("__CoachMigrationsHistory"))
            .Options);

    [Fact]
    public void The_coach_model_has_no_changes_the_migrations_do_not_describe()
    {
        using var db = NewContext();

        db.Database.HasPendingModelChanges().Should().BeFalse(
            "a model change without a migration reaches production as a missing column, and the "
            + "warning that used to say so is no longer suppressed — add the migration and update "
            + "CoachDbContextModelSnapshot");
    }

    /// <summary>
    /// Nothing re-suppresses the warning for this context.
    /// </summary>
    /// <remarks>
    /// Source-level, because the suppression is a call on an options builder and there is no
    /// runtime surface that reports "somebody ignored this". A single re-added
    /// <c>ConfigureWarnings</c> would make the test above unreachable in the host it protects.
    /// </remarks>
    [Fact]
    public void No_coach_context_registration_suppresses_the_pending_model_warning()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src")))
        {
            root = root.Parent;
        }

        root.Should().NotBeNull();

        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(root!.FullName, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(path);
            if (!source.Contains("CoachDbContext", StringComparison.Ordinal)
                || !source.Contains("PendingModelChangesWarning", StringComparison.Ordinal))
            {
                continue;
            }

            if (SuppressesForCoachContext(source))
            {
                offenders.Add(Path.GetRelativePath(root.FullName, path));
            }
        }

        offenders.Should().BeEmpty(
            "the coach context must not suppress model drift; add the missing migration instead");
    }

    /// <summary>
    /// True when a registration or options builder for the coach context ignores the warning.
    /// </summary>
    /// <remarks>
    /// Scoped to the block that names the context rather than to the whole file, because
    /// <c>Program.cs</c> configures two contexts and only one of them is this test's business.
    /// <c>ApplicationDbContext</c> is dual-provider with a hand-written SQLite migration set and
    /// suppresses the warning deliberately; flagging it here would be this test reaching past what
    /// it was asked to protect.
    /// </remarks>
    private static bool SuppressesForCoachContext(string source)
    {
        var lines = source.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("CoachDbContext>", StringComparison.Ordinal))
            {
                continue;
            }

            // Walk to the end of this registration: a `});` closes an AddDbContext lambda and a
            // `.Options` closes an options-builder chain.
            for (var j = i; j < lines.Length; j++)
            {
                var line = lines[j];
                var trimmed = line.TrimStart();
                var isComment = trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal);

                if (!isComment
                    && line.Contains("PendingModelChangesWarning", StringComparison.Ordinal)
                    && line.Contains("Ignore", StringComparison.Ordinal))
                {
                    return true;
                }

                if (trimmed.StartsWith("});", StringComparison.Ordinal)
                    || trimmed.StartsWith(".Options", StringComparison.Ordinal))
                {
                    break;
                }
            }
        }

        return false;
    }
}
