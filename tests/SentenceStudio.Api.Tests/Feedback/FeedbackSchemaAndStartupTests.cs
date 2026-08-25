using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Feedback;
using SentenceStudio.Api.Feedback.Persistence;

namespace SentenceStudio.Api.Tests.Feedback;

/// <summary>
/// The feedback model and the feedback migrations describe the same schema.
/// </summary>
/// <remarks>
/// <para>
/// Needs no server. <c>HasPendingModelChanges</c> compares the model built from the context against
/// the snapshot compiled from <c>Migrations/</c>, and never opens the connection — so this fails in
/// CI, on the model, while the person who changed it is still looking at it.
/// </para>
/// <para>
/// The alternative is what this schema deliberately does not do: suppress
/// <c>PendingModelChangesWarning</c>. Under suppression a model change with no migration produces
/// no error anywhere — <c>MigrateAsync</c> runs, the host starts, and the first request that
/// touches the missing column is the bug report.
/// </para>
/// </remarks>
public sealed class FeedbackModelMigrationParityTests
{
    private static FeedbackDbContext NewContext() =>
        new(new DbContextOptionsBuilder<FeedbackDbContext>()
            .UseNpgsql(
                "Host=model-parity.invalid;Database=unused",
                npgsql => npgsql.MigrationsHistoryTable(FeedbackSchema.MigrationsHistoryTable))
            .Options);

    [Fact]
    public void The_feedback_model_has_no_changes_the_migrations_do_not_describe()
    {
        using var db = NewContext();

        db.Database.HasPendingModelChanges().Should().BeFalse(
            "a model change without a migration reaches production as a missing column — add the "
            + "migration and update FeedbackDbContextModelSnapshot");
    }

    [Fact]
    public void The_initial_feedback_migration_is_discoverable()
    {
        using var db = NewContext();

        db.Database.GetMigrations().Should().Contain(
            "20260822002726_InitialFeedbackSchema",
            "a migration EF cannot discover is silently skipped, and the exactly-once ledger would "
            + "then have no table to arbitrate in");
    }

    /// <summary>
    /// The feedback migrations keep their own history table.
    /// </summary>
    /// <remarks>
    /// Three migration sets share one physical database. Without a dedicated history table this one
    /// would read the application's rows, conclude its own migrations had already run, and create
    /// nothing.
    /// </remarks>
    [Fact]
    public void The_feedback_migrations_use_their_own_history_table()
    {
        FeedbackSchema.MigrationsHistoryTable.Should().Be("__FeedbackMigrationsHistory");
        FeedbackSchema.MigrationsHistoryTable.Should().NotBe("__EFMigrationsHistory");
        FeedbackSchema.MigrationsHistoryTable.Should().NotBe("__CoachMigrationsHistory");
    }

    /// <summary>
    /// Nothing re-suppresses the pending-model warning for this context.
    /// </summary>
    /// <remarks>
    /// Source-level, because suppression is a call on an options builder and there is no runtime
    /// surface that reports "somebody ignored this". One re-added <c>ConfigureWarnings</c> makes
    /// the parity test above unreachable in the host it protects.
    /// </remarks>
    [Fact]
    public void No_feedback_context_registration_suppresses_the_pending_model_warning()
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
            if (!source.Contains("FeedbackDbContext", StringComparison.Ordinal)
                || !source.Contains("PendingModelChangesWarning", StringComparison.Ordinal))
            {
                continue;
            }

            if (SuppressesForFeedbackContext(source))
            {
                offenders.Add(Path.GetRelativePath(root.FullName, path));
            }
        }

        offenders.Should().BeEmpty(
            "the feedback context must not suppress model drift; add the missing migration instead");
    }

    /// <summary>
    /// The feedback schema is not in <c>ApplicationDbContext</c>, so it needs no SQLite twin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dual-provider rule in this repository is that every <c>ApplicationDbContext</c> migration
    /// needs a hand-written SQLite counterpart carrying <c>[DbContext]</c> and <c>[Migration]</c>,
    /// or it is silently skipped on mobile. That has shipped broken to devices twice.
    /// </para>
    /// <para>
    /// The way to have zero such twins is to have zero such migrations — which is a structural
    /// property, asserted here, not a habit.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_feedback_entities_are_not_registered_on_the_application_context()
    {
        var applicationEntities = typeof(SentenceStudio.Data.ApplicationDbContext)
            .GetProperties()
            .Select(p => p.PropertyType)
            .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(t => t.GenericTypeArguments[0])
            .ToArray();

        applicationEntities.Should().NotContain(typeof(FeedbackSubmission));
        applicationEntities.Should().NotContain(typeof(FeedbackRateWindow));
    }

    private static bool SuppressesForFeedbackContext(string source)
    {
        var lines = source.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("FeedbackDbContext>", StringComparison.Ordinal))
            {
                continue;
            }

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

/// <summary>
/// The signing key a deployment must configure, and the ones it must not.
/// </summary>
public sealed class FeedbackHmacKeyProviderTests
{
    private const string ValidKey = "feedback-hmac-key-that-is-long-enough!!";

    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e =>
                new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    /// <summary>
    /// A non-development host with no feedback key refuses to start.
    /// </summary>
    /// <remarks>
    /// A missing key is a deployment defect, and the moment to report a deployment defect is at
    /// rollout, where it fails the rollout — not on the first learner request, where it is a 500 in
    /// a feature nobody is watching.
    /// </remarks>
    [Fact]
    public void A_production_host_without_a_feedback_key_refuses_to_start()
    {
        var act = () => FeedbackHmacKeyProvider.Create(Config(), allowGeneratedKey: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Feedback:HmacKey*");
    }

    /// <summary>
    /// There is no fallback to the JWT signing key. At all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The old behaviour was to fall back with a warning, and it is worse than it reads. It puts
    /// the key that authenticates every session into a second HMAC context whose message is
    /// attacker-influenced — the preview payload contains a title and body derived from submitted
    /// text — and two constructions over one key are only safe while their encodings cannot
    /// collide, which nothing was enforcing.
    /// </para>
    /// <para>
    /// It also made rotation impossible in the direction that matters: rotating in response to a
    /// feedback-side leak signs every learner out.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_production_host_does_not_fall_back_to_the_jwt_signing_key()
    {
        var configuration = Config(
            ("Jwt:SigningKey", "a-perfectly-good-jwt-signing-key-32ch!!"));

        var act = () => FeedbackHmacKeyProvider.Create(configuration, allowGeneratedKey: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Feedback:HmacKey*");
    }

    /// <summary>Configuring the two keys to the same value is refused.</summary>
    /// <remarks>
    /// Removing the code fallback accomplishes nothing if an operator can re-create it by hand in
    /// a settings file — and it is an easy thing to do while trying to make the feature work.
    /// </remarks>
    [Fact]
    public void A_production_host_refuses_a_feedback_key_equal_to_the_jwt_key()
    {
        var configuration = Config(
            ("Feedback:HmacKey", ValidKey),
            ("Jwt:SigningKey", ValidKey));

        var act = () => FeedbackHmacKeyProvider.Create(configuration, allowGeneratedKey: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must not be the same value*");
    }

    [Fact]
    public void A_production_host_refuses_a_short_key()
    {
        var configuration = Config(("Feedback:HmacKey", "too-short"));

        var act = () => FeedbackHmacKeyProvider.Create(configuration, allowGeneratedKey: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*at least*");
    }

    [Fact]
    public void A_production_host_with_a_dedicated_key_starts()
    {
        var configuration = Config(
            ("Feedback:HmacKey", ValidKey),
            ("Jwt:SigningKey", "a-different-jwt-signing-key-32-chars!!"));

        var provider = FeedbackHmacKeyProvider.Create(configuration, allowGeneratedKey: false);

        provider.Key.Length.Should().Be(System.Text.Encoding.UTF8.GetByteCount(ValidKey));
    }

    /// <summary>Development generates a key rather than demanding one.</summary>
    [Fact]
    public void Development_generates_a_key_when_none_is_configured()
    {
        var provider = FeedbackHmacKeyProvider.Create(Config(), allowGeneratedKey: true);

        provider.Key.Length.Should().BeGreaterThanOrEqualTo(32);
    }

    /// <summary>A generated key is random per process, not a constant.</summary>
    /// <remarks>
    /// A hard-coded development key is the kind of thing that reaches production through a
    /// mis-set environment variable, at which point every preview token in the world is forgeable.
    /// </remarks>
    [Fact]
    public void A_generated_key_is_not_a_shared_constant()
    {
        var first = FeedbackHmacKeyProvider.Create(Config(), allowGeneratedKey: true);
        var second = FeedbackHmacKeyProvider.Create(Config(), allowGeneratedKey: true);

        first.Key.ToArray().Should().NotBeEquivalentTo(second.Key.ToArray());
    }

    /// <summary>The provider exposes no way to read the key back as text.</summary>
    /// <remarks>
    /// Structural, because the reliable way a secret ends up in a log is a convenient
    /// <c>ToString()</c> or a <c>string Key</c> property that somebody interpolated once.
    /// </remarks>
    [Fact]
    public void The_provider_exposes_no_string_accessor_for_the_key()
    {
        typeof(IFeedbackHmacKeyProvider).GetProperties()
            .Should().OnlyContain(p => p.PropertyType != typeof(string));

        typeof(FeedbackHmacKeyProvider).GetProperties()
            .Where(p => p.CanRead)
            .Should().OnlyContain(p => p.PropertyType != typeof(string));
    }
}

/// <summary>
/// The registration-time gate, exercised the way the host runs it.
/// </summary>
public sealed class FeedbackRegistrationTests
{
    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "SentenceStudio.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e =>
                new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void Adding_feedback_in_production_without_a_key_throws_at_registration()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => services.AddFeedback(
            Config(("ConnectionStrings:sentencestudio", "Host=x;Database=y;Username=z")),
            new FakeEnvironment());

        act.Should().Throw<InvalidOperationException>().WithMessage("*Feedback:HmacKey*");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Adding_feedback_in_development_or_testing_without_a_key_succeeds(string environment)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddFeedback(
            Config(("ConnectionStrings:sentencestudio", "Host=x;Database=y;Username=z")),
            new FakeEnvironment { EnvironmentName = environment });

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IFeedbackHmacKeyProvider>().Key.Length
            .Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Only Development and Testing may run on a generated key.
    /// </summary>
    /// <remarks>
    /// A theory over the environment names a deployment actually uses. "Staging" is the one worth
    /// naming: it is the environment most likely to be treated as development-ish by someone
    /// wiring configuration, and it is the one where a forgeable token has real users behind it.
    /// </remarks>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("production")]
    public void No_other_environment_may_run_on_a_generated_key(string environment)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => services.AddFeedback(
            Config(("ConnectionStrings:sentencestudio", "Host=x;Database=y;Username=z")),
            new FakeEnvironment { EnvironmentName = environment });

        act.Should().Throw<InvalidOperationException>();
    }
}

/// <summary>
/// The limits a deployment is allowed to choose.
/// </summary>
public sealed class FeedbackOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(Action<FeedbackOptions> configure)
    {
        var options = new FeedbackOptions();
        configure(options);
        return new FeedbackOptionsValidator().Validate(null, options);
    }

    [Fact]
    public void The_shipped_defaults_are_valid()
    {
        Validate(_ => { }).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void The_shipped_defaults_are_the_policy_that_was_asked_for()
    {
        var options = new FeedbackOptions();

        options.MaxPreviewsPerWindow.Should().Be(10);
        options.PreviewWindow.Should().Be(TimeSpan.FromHours(1));
        options.MaxSubmitsPerWindow.Should().Be(3);
        options.SubmitWindow.Should().Be(TimeSpan.FromHours(24));
        options.SubmitCooldown.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void A_limit_that_is_not_a_limit_is_refused()
    {
        Validate(o => o.MaxPreviewsPerWindow = 10_000).Failed.Should().BeTrue();
        Validate(o => o.MaxSubmitsPerWindow = 10_000).Failed.Should().BeTrue();
        Validate(o => o.MaxPreviewsPerWindow = 0).Failed.Should().BeTrue();
    }

    /// <summary>
    /// A token may not outlive the window that limits the previews producing it.
    /// </summary>
    /// <remarks>
    /// Otherwise an owner banks tokens: sign ten inside the hour, wait for the window to roll,
    /// then redeem all ten — and the preview limit has bounded nothing.
    /// </remarks>
    [Fact]
    public void A_token_may_not_outlive_its_preview_window()
    {
        Validate(o =>
        {
            o.PreviewWindow = TimeSpan.FromMinutes(5);
            o.TokenLifetime = TimeSpan.FromMinutes(10);
        }).Failed.Should().BeTrue();
    }

    [Fact]
    public void An_unbounded_token_lifetime_is_refused()
    {
        Validate(o =>
        {
            o.PreviewWindow = TimeSpan.FromDays(7);
            o.TokenLifetime = TimeSpan.FromDays(7);
        }).Failed.Should().BeTrue();
    }

    [Fact]
    public void A_retention_window_outside_the_accepted_range_is_refused()
    {
        Validate(o => o.RetentionDays = 1).Failed.Should().BeTrue();
        Validate(o => o.RetentionDays = 5_000).Failed.Should().BeTrue();
    }
}
