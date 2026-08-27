using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Runtime;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// <c>Coach:*</c> is the only configuration surface for coach persistence. These tests prove
/// an operator edit actually reaches the store and the cleanup service, and that no second
/// key claims control of the same knob.
/// </summary>
public class CoachPersistenceOptionsSetupTests
{
    private static CoachPersistenceOptions Resolve(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCoachRuntime(configuration);
        services.AddCoachPersistence(configuration);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<CoachPersistenceOptions>>().Value;
    }

    [Fact]
    public void PersistenceOptions_ProjectTheCoachSectionDefaults()
    {
        var options = Resolve();

        options.SessionLifetime.Should().Be(TimeSpan.FromHours(24));
        options.RevisionRetention.Should().Be(TimeSpan.FromDays(30));
        options.AgentConfigVersion.Should().Be("2");
        options.SessionSchemaVersion.Should().Be(CoachPersistenceOptions.CurrentSessionSchemaVersion);
    }

    [Fact]
    public void PersistenceOptions_FollowOperatorEdits()
    {
        var options = Resolve(
            ("Coach:SessionExpiryHours", "6"),
            ("Coach:RevisionRetentionDays", "7"),
            ("Coach:AgentConfigVersion", "2026-08-14.2"));

        options.SessionLifetime.Should().Be(TimeSpan.FromHours(6));
        options.RevisionRetention.Should().Be(TimeSpan.FromDays(7));
        options.AgentConfigVersion.Should().Be("2026-08-14.2");
    }

    [Fact]
    public void LegacyPersistenceSection_IsNotHonoured()
    {
        // The old Coach:Persistence:* keys are gone. If they ever bind again, an operator has
        // two keys for one knob and the losing one silently does nothing — the bug this
        // consolidation fixes.
        var options = Resolve(
            ("Coach:SessionExpiryHours", "6"),
            ("Coach:Persistence:SessionLifetime", "99:00:00"),
            ("Coach:Persistence:AgentConfigVersion", "ghost"),
            ("Coach:Persistence:RevisionRetention", "365.00:00:00"));

        options.SessionLifetime.Should().Be(TimeSpan.FromHours(6));
        options.AgentConfigVersion.Should().Be("2");
        options.RevisionRetention.Should().Be(TimeSpan.FromDays(30));
    }

    [Fact]
    public void PersistenceOptionsType_ExposesNoConfigurationSection()
    {
        typeof(CoachPersistenceOptions).GetField("SectionName").Should().BeNull(
            "persistence must not advertise a configuration section of its own");
    }

    [Fact]
    public async Task AgentConfigVersionChange_InvalidatesAStoredSession()
    {
        var before = Resolve(("Coach:AgentConfigVersion", "2026-08-14.1"));
        using var harness = new CoachPersistenceHarness(options: before);
        using var db = harness.NewContext();

        var created = await harness.NewSessionStore(db)
            .CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        created.AgentConfigVersion.Should().Be("2026-08-14.1");

        // The operator ships new instructions and bumps the version.
        var after = Resolve(("Coach:AgentConfigVersion", "2026-08-14.2"));
        harness.Options.AgentConfigVersion = after.AgentConfigVersion;

        var result = await harness.NewSessionStore(db).LoadAsync(CoachPersistenceSamples.OwnerUserId, created.Id);

        result.Status.Should().Be(CoachSessionLoadStatus.ConfigVersionMismatch,
            "a session created under the previous agent contract must not resume against the new one");
    }

    [Fact]
    public async Task SessionExpiryHours_DrivesTheStoreExpiry()
    {
        var options = Resolve(("Coach:SessionExpiryHours", "2"));
        using var harness = new CoachPersistenceHarness(options: options);
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var created = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        created.ExpiresAt.Should().Be(harness.Time.GetUtcNow().UtcDateTime + TimeSpan.FromHours(2));

        harness.Time.Advance(TimeSpan.FromMinutes(90));
        (await store.LoadAsync(CoachPersistenceSamples.OwnerUserId, created.Id)).Status
            .Should().Be(CoachSessionLoadStatus.Found);

        harness.Time.Advance(TimeSpan.FromHours(3));
        (await store.LoadAsync(CoachPersistenceSamples.OwnerUserId, created.Id)).Status
            .Should().Be(CoachSessionLoadStatus.Expired, "the configured 2h window, not the 24h default, applies");
    }

    [Fact]
    public async Task RevisionRetentionDays_DrivesTheCleanupWindow()
    {
        var options = Resolve(("Coach:RevisionRetentionDays", "2"), ("Coach:SessionExpiryHours", "168"));
        using var harness = new CoachPersistenceHarness(options: options);
        using var db = harness.NewContext();
        var store = harness.NewSessionStore(db);

        var session = await store.CreateAsync(CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());
        await store.AppendRevisionAsync(CoachPersistenceSamples.OwnerUserId, session.Id, CoachPersistenceSamples.RevisionInput());

        harness.Time.Advance(TimeSpan.FromDays(1));
        (await harness.NewCleanupService(db).RunAsync()).RevisionsDeleted.Should().Be(0,
            "a one-day-old revision is inside the configured two-day window");

        harness.Time.Advance(TimeSpan.FromDays(2));
        (await harness.NewCleanupService(db).RunAsync()).RevisionsDeleted.Should().Be(1,
            "the configured two-day retention, not the 30-day default, applies");

        (await db.CoachPlanRevisions.CountAsync()).Should().Be(0);
    }
}
