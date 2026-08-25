using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebApp.Platform;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Privacy contract for the plan-date lane.
/// </summary>
/// <remarks>
/// <para>
/// Found in Aspire structured logs on 2026-08-15: <c>WebAppPlanDateContext</c> emitted
/// "resolved timezone 'America/Chicago' for user 'c0a366ad-...'" — with the raw profile id in a
/// structured <c>UserId</c> field — on every <c>/api/v1/coach/*</c> request. The coach telemetry
/// contract forbids user or tenant identifiers in telemetry, and that contract is about what
/// reaches the log sink, not about which namespace emitted it.
/// </para>
/// <para>
/// These tests assert on BOTH channels: the rendered message a human reads, and the structured
/// state a log aggregator indexes and retains. Scrubbing only the message would leave the id
/// searchable in the sink, which is the more dangerous half.
/// </para>
/// </remarks>
public class PlanDateLoggingPrivacyTests
{
    /// <summary>A profile id shaped like the real one from the reported log line.</summary>
    private const string SentinelProfileId = "c0a366ad-4f7e-4a1e-9b53-2f6d1e8c7a90";

    private const string TimeZoneId = "America/Chicago";

    // ---------------------------------------------------------------- the defect

    [Fact]
    public void ResolvingATimezoneNeverLogsTheProfileId()
    {
        var (log, context) = Resolve(withProfile: true, ianaTimeZoneId: TimeZoneId);

        context.TimeZone.Id.Should().Be(TimeZoneId, "behavior is unchanged");
        log.AssertNoSentinelAnywhere();
    }

    [Fact]
    public void ResolvingATimezoneStillLogsTheUsefulShape()
    {
        var (log, _) = Resolve(withProfile: true, ianaTimeZoneId: TimeZoneId);

        // The diagnostic value of the line has to survive the scrub, or the next person just
        // adds the id back.
        log.StructuredValues.Should().Contain(kvp => kvp.Key == "IanaId" && (string?)kvp.Value == TimeZoneId);
        log.StructuredValues.Should().Contain(kvp => kvp.Key == "ProfileResolved" && Equals(kvp.Value, true));
    }

    [Fact]
    public void AProfileWithNoTimezoneNeverLogsTheProfileId()
    {
        var (log, context) = Resolve(withProfile: true, ianaTimeZoneId: null);

        context.TimeZone.Should().Be(TimeZoneInfo.Utc, "behavior is unchanged");
        log.AssertNoSentinelAnywhere();
        log.StructuredValues.Should().Contain(kvp => kvp.Key == "ProfileResolved" && Equals(kvp.Value, true));
    }

    [Fact]
    public void AFailedLookupNeverLogsTheProfileId()
    {
        // The warning path is the most dangerous one: warnings ship to production sinks even
        // when Debug does not.
        var (log, context) = ResolveWithBrokenDatabase();

        context.TimeZone.Should().Be(TimeZoneInfo.Utc, "behavior is unchanged");
        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
        log.AssertNoSentinelAnywhere();
    }

    [Fact]
    public void NoAuthenticatedLearnerLogsTheShapeAndNoIdentifier()
    {
        var (log, context) = Resolve(withProfile: false, ianaTimeZoneId: null);

        context.TimeZone.Should().Be(TimeZoneInfo.Utc);
        log.AssertNoSentinelAnywhere();
        log.StructuredValues.Should().Contain(kvp => kvp.Key == "ProfileResolved" && Equals(kvp.Value, false));
    }

    [Fact]
    public void NoLogLineInTheLaneCarriesAUserIdField()
    {
        // A structured field literally named UserId is the shape the aggregator indexes.
        foreach (var log in new[]
                 {
                     Resolve(withProfile: true, ianaTimeZoneId: TimeZoneId).Log,
                     Resolve(withProfile: true, ianaTimeZoneId: null).Log,
                     Resolve(withProfile: false, ianaTimeZoneId: null).Log,
                     ResolveWithBrokenDatabase().Log
                 })
        {
            var identifierFields = log.StructuredValues
                .Where(kvp => kvp.Key is "UserId" or "ProfileId" or "NameId")
                .ToList();

            identifierFields.Should().BeEmpty(
                "identifier fields must not exist at all, not merely be empty");
        }
    }

    // ---------------------------------------------------------------- harness

    private static (CapturingLoggerProvider Log, IPlanDateContext Context) Resolve(
        bool withProfile,
        string? ianaTimeZoneId)
    {
        var services = new ServiceCollection();
        var log = new CapturingLoggerProvider(SentinelProfileId);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(log));

        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(connection));
        services.AddSingleton<CircuitUserStateAccessor>();

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Database.EnsureCreated();
            db.UserProfiles.Add(new UserProfile
            {
                Id = SentinelProfileId,
                IanaTimeZoneId = ianaTimeZoneId
            });
            db.SaveChanges();
        }

        var accessor = provider.GetRequiredService<CircuitUserStateAccessor>();
        accessor.Current = withProfile ? new CircuitUserState(null, SentinelProfileId) : CircuitUserState.Empty;

        try
        {
            var logger = provider.GetRequiredService<ILogger<WebAppPlanDateContext>>();
            return (log, new WebAppPlanDateContext(provider, logger));
        }
        finally
        {
            accessor.Current = null;
        }
    }

    /// <summary>Drives the catch block: a profile is known but no DbContext is registered.</summary>
    private static (CapturingLoggerProvider Log, IPlanDateContext Context) ResolveWithBrokenDatabase()
    {
        var services = new ServiceCollection();
        var log = new CapturingLoggerProvider(SentinelProfileId);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(log));
        services.AddSingleton<CircuitUserStateAccessor>();

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<CircuitUserStateAccessor>();
        accessor.Current = new CircuitUserState(null, SentinelProfileId);

        try
        {
            var logger = provider.GetRequiredService<ILogger<WebAppPlanDateContext>>();
            return (log, new WebAppPlanDateContext(provider, logger));
        }
        finally
        {
            accessor.Current = null;
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State);

    private sealed class CapturingLoggerProvider(string sentinel) : ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = new();

        public IReadOnlyList<KeyValuePair<string, object?>> StructuredValues =>
            Entries.SelectMany(e => e.State).ToList();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose() { }

        /// <summary>Fails if the sentinel appears in a rendered message OR any structured value.</summary>
        public void AssertNoSentinelAnywhere()
        {
            Entries.Should().NotBeEmpty("the lane must still log something useful");

            Entries.Should().NotContain(
                e => e.Message.Contains(sentinel, StringComparison.OrdinalIgnoreCase),
                "the rendered message must not carry the profile id");

            var leaked = StructuredValues
                .Where(kvp => kvp.Value?.ToString()?.Contains(sentinel, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();

            leaked.Should().BeEmpty(
                "the structured state a log aggregator indexes must not carry the profile id either");
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = state as IReadOnlyList<KeyValuePair<string, object?>>
                             ?? Array.Empty<KeyValuePair<string, object?>>();

                owner.Entries.Add(new LogEntry(logLevel, formatter(state, exception), values));
            }
        }
    }
}
