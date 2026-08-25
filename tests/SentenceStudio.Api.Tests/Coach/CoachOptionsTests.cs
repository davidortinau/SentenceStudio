using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.AppHost;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Locks the shipped <see cref="CoachOptions"/> defaults and the startup bounds enforced by
/// <see cref="CoachOptionsValidator"/>. The defaults matter as much as the bounds: the coach must
/// arrive off, on the baseline arm, with nobody in the cohort.
/// </summary>
public class CoachOptionsTests
{
    private static CoachOptions BindFrom(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var options = new CoachOptions();
        configuration.GetSection(CoachOptions.SectionName).Bind(options);
        return options;
    }

    /// <summary>
    /// Validates with no host environment. That is the strict configuration: an unknown
    /// environment is treated as non-Development.
    /// </summary>
    private static ValidateOptionsResult Validate(CoachOptions options)
        => new CoachOptionsValidator().Validate(Options.DefaultName, options);

    private static ValidateOptionsResult Validate(CoachOptions options, string environmentName)
        => new CoachOptionsValidator(new OptionsStubEnvironment(environmentName))
            .Validate(Options.DefaultName, options);

    private sealed class OptionsStubEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "SentenceStudio.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static CoachOptions Valid() => new()
    {
        Enabled = true,
        AllowedUserProfileIds = new List<string> { "profile-1" }
    };

    [Fact]
    public void Defaults_AreOffBaselineAndEmptyCohort()
    {
        var options = new CoachOptions();

        options.Enabled.Should().BeFalse("the coach must never be reachable without an explicit opt-in");
        options.Implementation.Should().Be(CoachImplementation.Baseline);
        options.AllowedUserProfileIds.Should().BeEmpty();
    }

    [Fact]
    public void Defaults_MatchTheApprovedBudgetAndRunLimits()
    {
        var options = new CoachOptions();

        options.MaxRunsPerDay.Should().Be(10);
        options.MaxRunsPerWeek.Should().Be(40);
        options.SessionExpiryHours.Should().Be(24);
        options.RevisionRetentionDays.Should().Be(30);
        options.RequestTimeoutSeconds.Should().Be(45);
        options.MaxIterationsPerRequest.Should().Be(6);
        options.MaxClarificationsPerSession.Should().Be(2);
        // A total-generation budget on a reasoning model, not a visible-answer budget: it
        // covers hidden reasoning tokens too. See CoachOptions.MaxOutputTokens for the sizing.
        options.MaxOutputTokens.Should().Be(16_000);
        options.ReasoningEffort.Should().Be("minimal");
        options.MaxTurnTextLength.Should().Be(500);
        options.AgentConfigVersion.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Defaults_ExposeTimeSpanProjections()
    {
        var options = new CoachOptions();

        options.RequestTimeout.Should().Be(TimeSpan.FromSeconds(45));
        options.SessionExpiry.Should().Be(TimeSpan.FromHours(24));
        options.RevisionRetention.Should().Be(TimeSpan.FromDays(30));
    }

    [Fact]
    public void Defaults_DoNotExceedTheSharedClientContractLimits()
    {
        var options = new CoachOptions();

        options.MaxTurnTextLength.Should().BeLessThanOrEqualTo(CoachConstraintLimits.MaxTurnTextLength);
        options.MaxClarificationsPerSession.Should().BeLessThanOrEqualTo(CoachConstraintLimits.MaxClarificationsPerSession);
    }

    [Fact]
    public void Bind_ReadsEveryConfiguredValueFromTheCoachSection()
    {
        var options = BindFrom(
            ("Coach:Enabled", "true"),
            ("Coach:Implementation", "harness"),
            ("Coach:AllowedUserProfileIds:0", "profile-a"),
            ("Coach:AllowedUserProfileIds:1", "profile-b"),
            ("Coach:AgentConfigVersion", "2026-08-14.1"),
            ("Coach:MaxRunsPerDay", "7"),
            ("Coach:MaxRunsPerWeek", "21"),
            ("Coach:SessionExpiryHours", "12"),
            ("Coach:RevisionRetentionDays", "14"),
            ("Coach:RequestTimeoutSeconds", "30"),
            ("Coach:MaxIterationsPerRequest", "4"),
            ("Coach:MaxClarificationsPerSession", "1"),
            ("Coach:MaxOutputTokens", "900"),
            ("Coach:MaxTurnTextLength", "400"));

        options.Enabled.Should().BeTrue();
        options.Implementation.Should().Be(CoachImplementation.Harness, "the enum binds case-insensitively from 'harness'");
        options.AllowedUserProfileIds.Should().BeEquivalentTo(new[] { "profile-a", "profile-b" });
        options.AgentConfigVersion.Should().Be("2026-08-14.1");
        options.MaxRunsPerDay.Should().Be(7);
        options.MaxRunsPerWeek.Should().Be(21);
        options.SessionExpiryHours.Should().Be(12);
        options.RevisionRetentionDays.Should().Be(14);
        options.RequestTimeoutSeconds.Should().Be(30);
        options.MaxIterationsPerRequest.Should().Be(4);
        options.MaxClarificationsPerSession.Should().Be(1);
        options.MaxOutputTokens.Should().Be(900);
        options.MaxTurnTextLength.Should().Be(400);
    }

    [Fact]
    public void Bind_WithNoCoachSection_LeavesTheFeatureOff()
    {
        var options = BindFrom(("Unrelated:Value", "true"));

        options.Enabled.Should().BeFalse();
        options.Implementation.Should().Be(CoachImplementation.Baseline);
    }

    [Fact]
    public void Validate_Defaults_Succeed()
        => Validate(new CoachOptions()).Succeeded.Should().BeTrue();

    [Theory]
    [InlineData(nameof(CoachOptions.MaxRunsPerDay), 0)]
    [InlineData(nameof(CoachOptions.MaxRunsPerDay), 201)]
    [InlineData(nameof(CoachOptions.SessionExpiryHours), 0)]
    [InlineData(nameof(CoachOptions.SessionExpiryHours), 169)]
    [InlineData(nameof(CoachOptions.RevisionRetentionDays), 0)]
    [InlineData(nameof(CoachOptions.RevisionRetentionDays), 366)]
    [InlineData(nameof(CoachOptions.RequestTimeoutSeconds), 4)]
    [InlineData(nameof(CoachOptions.RequestTimeoutSeconds), 121)]
    [InlineData(nameof(CoachOptions.MaxIterationsPerRequest), 0)]
    [InlineData(nameof(CoachOptions.MaxIterationsPerRequest), 21)]
    [InlineData(nameof(CoachOptions.MaxClarificationsPerSession), -1)]
    [InlineData(nameof(CoachOptions.MaxClarificationsPerSession), 3)]
    [InlineData(nameof(CoachOptions.MaxOutputTokens), 1_999)]
    [InlineData(nameof(CoachOptions.MaxOutputTokens), 32_001)]
    [InlineData(nameof(CoachOptions.MaxTurnTextLength), 0)]
    [InlineData(nameof(CoachOptions.MaxTurnTextLength), 501)]
    public void Validate_OutOfRangeValue_FailsAndNamesTheSetting(string property, int value)
    {
        var options = Valid();
        typeof(CoachOptions).GetProperty(property)!.SetValue(options, value);

        var result = Validate(options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain($"Coach:{property}");
    }

    [Fact]
    public void Validate_WeeklyLimitBelowDailyLimit_Fails()
    {
        var options = Valid();
        options.MaxRunsPerDay = 10;
        options.MaxRunsPerWeek = 5;

        var result = Validate(options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("MaxRunsPerWeek");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    public void Validate_BadAgentConfigVersion_Fails(string version)
    {
        var options = Valid();
        options.AgentConfigVersion = version;

        Validate(options).Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_TooLongAgentConfigVersion_Fails()
    {
        var options = Valid();
        options.AgentConfigVersion = new string('v', CoachOptionsValidator.MaxAgentConfigVersionLength + 1);

        Validate(options).Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyCohortEntry_FailsWithIndexOnly()
    {
        var options = Valid();
        options.AllowedUserProfileIds = new List<string> { "profile-1", "  " };

        var result = Validate(options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("AllowedUserProfileIds[1]");
        result.FailureMessage.Should().NotContain("profile-1", "a validation message must never echo a user profile id");
    }

    [Fact]
    public void Validate_DuplicateCohortEntry_Fails()
    {
        var options = Valid();
        options.AllowedUserProfileIds = new List<string> { "profile-1", "profile-1" };

        Validate(options).Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_DuplicateCohortEntry_MessageShapeLockedToIndex()
    {
        var options = Valid();
        options.AllowedUserProfileIds = new List<string> { "aaa", "bbb", "aaa" };

        var result = Validate(options);

        result.Failed.Should().BeTrue();
        // Lock the exact diagnostic shape so future refactors cannot silently degrade it.
        result.FailureMessage.Should().Contain("AllowedUserProfileIds[2] is a duplicate entry");
        // The value must never appear in the message.
        result.FailureMessage.Should().NotContain("aaa");
    }

    [Fact]
    public void Validate_UndefinedImplementation_Fails()
    {
        var options = Valid();
        options.Implementation = (CoachImplementation)42;

        Validate(options).Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ReportsEveryProblemAtOnce()
    {
        var options = Valid();
        options.MaxRunsPerDay = 0;
        options.MaxOutputTokens = 5;
        options.RequestTimeoutSeconds = 1;

        var result = Validate(options);

        result.Failures.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void AddCoachRuntime_WithInvalidConfiguration_ThrowsOnResolve()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coach:Enabled"] = "true",
                ["Coach:RequestTimeoutSeconds"] = "600"
            })
            .Build();

        var provider = new ServiceCollection()
            .AddCoachRuntime(configuration)
            .BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<CoachOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Coach:RequestTimeoutSeconds*");
    }

    [Fact]
    public void AddCoachRuntime_WithValidConfiguration_ResolvesTheRuntimeServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coach:Enabled"] = "true",
                ["Coach:AllowedUserProfileIds:0"] = "profile-1"
            })
            .Build();

        var provider = new ServiceCollection()
            .AddCoachRuntime(configuration)
            .BuildServiceProvider();

        provider.GetRequiredService<IOptions<CoachOptions>>().Value.Enabled.Should().BeTrue();
        provider.GetRequiredService<ICoachAvailabilityPolicy>().Should().BeOfType<CoachAvailabilityPolicy>();
        provider.GetRequiredService<ICoachBudgetService>().Should().BeOfType<InMemoryCoachBudgetService>();
    }

    [Theory]
    [InlineData("profile-1", true)]
    [InlineData(" profile-1 ", true)]
    [InlineData("Profile-1", false)]
    [InlineData("profile-2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsInCohort_MatchesOrdinallyAfterTrimming(string? candidate, bool expected)
    {
        var options = new CoachOptions { AllowedUserProfileIds = new List<string> { "profile-1" } };

        options.IsInCohort(candidate).Should().Be(expected);
    }

    // ------------------------------------------------------------------
    // AppHost forwarding contract: calls the REAL CoachConfigurationReader
    // (source-linked from the AppHost project) so a regression in the
    // AppHost reader is detected directly — no duplicated scan logic.
    // ------------------------------------------------------------------

    /// <summary>
    /// Calls the real AppHost <see cref="CoachConfigurationReader.ReadAllowedUserProfileIds"/>
    /// helper, then re-binds through the same colon-keyed config path the Aspire
    /// <c>WithEnvironment</c> forwarding produces. Verifies blank compaction, trim, ordering,
    /// and the 16-entry bound.
    /// </summary>
    [Fact]
    public void AppHostForwardingContract_MultipleIdsArePreservedAndBlanksAreDropped()
    {
        var sourceConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coach:AllowedUserProfileIds:0"] = "ba20-captain",
                ["Coach:AllowedUserProfileIds:1"] = "  ",
                ["Coach:AllowedUserProfileIds:2"] = "7384-jayne",
                ["Coach:AllowedUserProfileIds:3"] = "",
            })
            .Build();

        // Act: call the real AppHost helper — no duplicated loop
        var forwarded = CoachConfigurationReader.ReadAllowedUserProfileIds(sourceConfig);

        // Rebuild config as the API would see it after WithEnvironment forwarding
        var envVars = new Dictionary<string, string?>();
        for (var i = 0; i < forwarded.Count; i++)
            envVars[$"Coach:AllowedUserProfileIds:{i}"] = forwarded[i];

        var apiConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(envVars)
            .Build();
        var options = new CoachOptions();
        apiConfig.GetSection(CoachOptions.SectionName).Bind(options);

        // Assert: both real IDs survived, blanks did not, ordering is preserved
        options.AllowedUserProfileIds.Should().HaveCount(2);
        options.AllowedUserProfileIds[0].Should().Be("ba20-captain");
        options.AllowedUserProfileIds[1].Should().Be("7384-jayne");
        options.IsInCohort("ba20-captain").Should().BeTrue();
        options.IsInCohort("7384-jayne").Should().BeTrue();
    }

    [Fact]
    public void AppHostForwardingContract_EmptyConfigForwardsNothing_FailClosed()
    {
        var sourceConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        // Act: call the real AppHost helper
        var forwarded = CoachConfigurationReader.ReadAllowedUserProfileIds(sourceConfig);

        forwarded.Should().BeEmpty("absent config must forward nothing — fail-closed");

        var apiConfig = new ConfigurationBuilder().Build();
        var options = new CoachOptions();
        apiConfig.GetSection(CoachOptions.SectionName).Bind(options);

        options.AllowedUserProfileIds.Should().BeEmpty();
        options.IsInCohort("anyone").Should().BeFalse();
    }

    [Fact]
    public void AppHostForwardingContract_ScansBoundAt16Entries()
    {
        CoachConfigurationReader.MaxAllowedEntries.Should().Be(16,
            "the AppHost scans indices 0..15; widening silently drops tail entries");
    }

    [Fact]
    public void AppHostForwardingContract_WhitespacePaddedIdsAreTrimmed()
    {
        var sourceConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coach:AllowedUserProfileIds:0"] = "  padded-id  ",
                ["Coach:AllowedUserProfileIds:1"] = "\ttabbed\t",
            })
            .Build();

        var forwarded = CoachConfigurationReader.ReadAllowedUserProfileIds(sourceConfig);

        forwarded.Should().BeEquivalentTo(new[] { "padded-id", "tabbed" },
            options => options.WithStrictOrdering());
    }

    [Fact]
    public void AppHostForwardingContract_GapsCompactWithoutHoles()
    {
        var entries = new Dictionary<string, string?>
        {
            ["Coach:AllowedUserProfileIds:0"] = "first",
            // index 1 absent (gap)
            ["Coach:AllowedUserProfileIds:2"] = "second",
            // index 3 blank (gap)
            ["Coach:AllowedUserProfileIds:3"] = "  ",
            ["Coach:AllowedUserProfileIds:4"] = "third",
        };
        var sourceConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(entries)
            .Build();

        var forwarded = CoachConfigurationReader.ReadAllowedUserProfileIds(sourceConfig);

        forwarded.Should().HaveCount(3);
        forwarded[0].Should().Be("first");
        forwarded[1].Should().Be("second");
        forwarded[2].Should().Be("third");
    }

    [Fact]
    public void AppHostForwardingContract_Index15IsIncludedIndex16IsDropped()
    {
        var entries = new Dictionary<string, string?>();
        for (var i = 0; i <= 17; i++)
            entries[$"Coach:AllowedUserProfileIds:{i}"] = $"id-{i}";

        var sourceConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(entries)
            .Build();

        var forwarded = CoachConfigurationReader.ReadAllowedUserProfileIds(sourceConfig);

        forwarded.Should().HaveCount(16, "only indices 0..15 are scanned");
        forwarded.Should().Contain("id-15", "index 15 is the last scanned entry");
        forwarded.Should().NotContain("id-16", "index 16 is beyond the scan bound");
        forwarded.Should().NotContain("id-17");
    }

    [Fact]
    public void AppHostForwardingContract_ForwardLoopAgreesWithHelperOutput()
    {
        // Build a source config with a few IDs including gaps and whitespace
        var entries = new Dictionary<string, string?>
        {
            ["Coach:AllowedUserProfileIds:0"] = " alpha ",
            ["Coach:AllowedUserProfileIds:1"] = "",
            ["Coach:AllowedUserProfileIds:2"] = "bravo",
            ["Coach:AllowedUserProfileIds:5"] = "charlie",
        };
        var sourceConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(entries)
            .Build();

        var forwarded = CoachConfigurationReader.ReadAllowedUserProfileIds(sourceConfig);

        // Simulate the AppHost WithEnvironment forwarding loop
        var envVars = new Dictionary<string, string?>();
        for (var i = 0; i < forwarded.Count; i++)
            envVars[$"Coach:AllowedUserProfileIds:{i}"] = forwarded[i];

        var apiConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(envVars)
            .Build();
        var options = new CoachOptions();
        apiConfig.GetSection(CoachOptions.SectionName).Bind(options);

        // The API-side list should exactly match the helper output
        options.AllowedUserProfileIds.Should().BeEquivalentTo(forwarded,
            options => options.WithStrictOrdering(),
            "the forward loop must produce the same compacted, trimmed list as the helper");

        // Verify count and index mapping
        options.AllowedUserProfileIds.Should().HaveCount(3);
        options.AllowedUserProfileIds[0].Should().Be("alpha");
        options.AllowedUserProfileIds[1].Should().Be("bravo");
        options.AllowedUserProfileIds[2].Should().Be("charlie");
    }

    // ------------------------------------------------------------------
    // AppHost dedup defense-in-depth: CoachConfigurationReader drops later
    // ordinal duplicates, reports the source index, preserves first occurrence.
    // ------------------------------------------------------------------

    [Fact]
    public void AppHostDedup_DuplicateIsRemovedAndSourceIndexReported()
    {
        var sourceConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coach:AllowedUserProfileIds:0"] = "7384-jayne",
                ["Coach:AllowedUserProfileIds:1"] = "ba20-captain",
                ["Coach:AllowedUserProfileIds:2"] = "7384-jayne", // duplicate of [0]
            })
            .Build();

        var result = CoachConfigurationReader.ReadAllowedUserProfileIdsWithDiagnostics(sourceConfig);

        result.Ids.Should().HaveCount(2);
        result.Ids[0].Should().Be("7384-jayne");
        result.Ids[1].Should().Be("ba20-captain");
        result.DuplicateSourceIndices.Should().ContainSingle().Which.Should().Be(2);
    }

    [Fact]
    public void AppHostDedup_OrderPreserved_FirstOccurrenceKept()
    {
        var sourceConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coach:AllowedUserProfileIds:0"] = "alpha",
                ["Coach:AllowedUserProfileIds:1"] = "bravo",
                ["Coach:AllowedUserProfileIds:2"] = "charlie",
                ["Coach:AllowedUserProfileIds:3"] = "bravo", // dup of [1]
                ["Coach:AllowedUserProfileIds:4"] = "alpha", // dup of [0]
            })
            .Build();

        var result = CoachConfigurationReader.ReadAllowedUserProfileIdsWithDiagnostics(sourceConfig);

        result.Ids.Should().BeEquivalentTo(new[] { "alpha", "bravo", "charlie" },
            opt => opt.WithStrictOrdering());
        result.DuplicateSourceIndices.Should().BeEquivalentTo(new[] { 3, 4 });
    }

    [Fact]
    public void AppHostDedup_UniqueEntriesUnchanged()
    {
        var sourceConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coach:AllowedUserProfileIds:0"] = "one",
                ["Coach:AllowedUserProfileIds:1"] = "two",
                ["Coach:AllowedUserProfileIds:2"] = "three",
            })
            .Build();

        var result = CoachConfigurationReader.ReadAllowedUserProfileIdsWithDiagnostics(sourceConfig);

        result.Ids.Should().HaveCount(3);
        result.DuplicateSourceIndices.Should().BeEmpty();
    }

    [Fact]
    public void AppHostDedup_ComparisonIsOrdinal_MatchesValidatorSemantics()
    {
        // The API validator uses HashSet<string>(StringComparer.Ordinal) — case-sensitive.
        // The AppHost dedup must agree: "ABC" and "abc" are distinct.
        var sourceConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coach:AllowedUserProfileIds:0"] = "ABC",
                ["Coach:AllowedUserProfileIds:1"] = "abc",
            })
            .Build();

        var result = CoachConfigurationReader.ReadAllowedUserProfileIdsWithDiagnostics(sourceConfig);

        result.Ids.Should().HaveCount(2, "ordinal comparison treats case as distinct");
        result.DuplicateSourceIndices.Should().BeEmpty();
    }

    [Fact]
    public void AppHostDedup_BlankGapsStillCompacted()
    {
        var sourceConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coach:AllowedUserProfileIds:0"] = "first",
                ["Coach:AllowedUserProfileIds:1"] = "  ",
                ["Coach:AllowedUserProfileIds:2"] = "second",
                ["Coach:AllowedUserProfileIds:3"] = "first", // dup
            })
            .Build();

        var result = CoachConfigurationReader.ReadAllowedUserProfileIdsWithDiagnostics(sourceConfig);

        result.Ids.Should().BeEquivalentTo(new[] { "first", "second" },
            opt => opt.WithStrictOrdering());
        result.DuplicateSourceIndices.Should().ContainSingle().Which.Should().Be(3);
    }

    [Fact]
    public void AppHostDedup_LegacyApiReturnsDeduplicatedList()
    {
        // The backward-compatible ReadAllowedUserProfileIds still works and returns deduped results
        var sourceConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coach:AllowedUserProfileIds:0"] = "x",
                ["Coach:AllowedUserProfileIds:1"] = "y",
                ["Coach:AllowedUserProfileIds:2"] = "x",
            })
            .Build();

        var forwarded = CoachConfigurationReader.ReadAllowedUserProfileIds(sourceConfig);

        forwarded.Should().HaveCount(2);
        forwarded[0].Should().Be("x");
        forwarded[1].Should().Be("y");
    }

    // ------------------------------------------------------------------
    // Sam flag dependency chain: CoachOptionsValidator enforces
    // SamOverlay → DurableHistory, SamReadTools → SamOverlay,
    // SamWriteTools → SamReadTools.
    // ------------------------------------------------------------------

    [Fact]
    public void Defaults_SamFlagsAreOff()
    {
        var options = new CoachOptions();

        options.IsSamOverlayEnabled.Should().BeFalse();
        options.IsSamReadToolsEnabled.Should().BeFalse();
        options.IsSamWriteToolsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Bind_SamFlagsBindFromNestedEnabledKeys()
    {
        var options = BindFrom(
            ("Coach:DurableHistory:Enabled", "true"),
            ("Coach:SamOverlay:Enabled", "true"),
            ("Coach:SamReadTools:Enabled", "true"),
            ("Coach:SamWriteTools:Enabled", "true"));

        options.IsSamOverlayEnabled.Should().BeTrue();
        options.IsSamReadToolsEnabled.Should().BeTrue();
        options.IsSamWriteToolsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Validate_SamOverlayWithoutDurableHistory_Fails()
    {
        var options = Valid();
        options.SamOverlay = new CoachFeatureSwitch { Enabled = true };

        var result = Validate(options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("SamOverlay").And.Contain("DurableHistory");
    }

    [Fact]
    public void Validate_SamReadToolsWithoutSamOverlay_Fails()
    {
        var options = Valid();
        options.DurableHistory = new CoachFeatureSwitch { Enabled = true };
        options.SamReadTools = new CoachFeatureSwitch { Enabled = true };

        var result = Validate(options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("SamReadTools").And.Contain("SamOverlay");
    }

    [Fact]
    public void Validate_SamWriteToolsWithoutSamReadTools_Fails()
    {
        var options = Valid();
        options.DurableHistory = new CoachFeatureSwitch { Enabled = true };
        options.SamOverlay = new CoachFeatureSwitch { Enabled = true };
        options.SamWriteTools = new CoachFeatureSwitch { Enabled = true };

        var result = Validate(options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("SamWriteTools").And.Contain("SamReadTools");
    }

    [Fact]
    public void Validate_FullSamChainWithDurableHistory_Succeeds()
    {
        var options = Valid();
        options.DurableHistory = new CoachFeatureSwitch { Enabled = true };
        options.SamOverlay = new CoachFeatureSwitch { Enabled = true };
        options.SamReadTools = new CoachFeatureSwitch { Enabled = true };
        options.SamWriteTools = new CoachFeatureSwitch { Enabled = true };

        Validate(options).Succeeded.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // Development cohort sentinel (__dev_all__)
    // ------------------------------------------------------------------

    [Fact]
    public void IsInCohort_DevAllSentinel_IsNotHonouredByTheStrictOverload()
    {
        // The single-argument overload is the fail-closed one: a caller that has not considered
        // the environment gets the strict answer, so a new call site cannot re-open the wildcard.
        var options = new CoachOptions
        {
            Enabled = true,
            AllowedUserProfileIds = new List<string> { CoachOptions.DevAllSentinel }
        };

        options.IsInCohort("any-random-guid").Should().BeFalse();
        options.IsInCohort("another-profile").Should().BeFalse();
    }

    [Fact]
    public void IsInCohort_DevAllSentinel_AdmitsAnyProfileIdOnlyWhenExplicitlyAllowed()
    {
        var options = new CoachOptions
        {
            Enabled = true,
            AllowedUserProfileIds = new List<string> { CoachOptions.DevAllSentinel }
        };

        options.IsInCohort("any-random-guid", allowDevelopmentSentinel: true).Should().BeTrue();
        options.IsInCohort("another-profile", allowDevelopmentSentinel: true).Should().BeTrue();
        options.IsInCohort("any-random-guid", allowDevelopmentSentinel: false).Should().BeFalse();
    }

    [Fact]
    public void IsInCohort_DevAllSentinel_RejectsNullOrEmpty()
    {
        var options = new CoachOptions
        {
            Enabled = true,
            AllowedUserProfileIds = new List<string> { CoachOptions.DevAllSentinel }
        };

        options.IsInCohort(null, allowDevelopmentSentinel: true).Should().BeFalse();
        options.IsInCohort("", allowDevelopmentSentinel: true).Should().BeFalse();
        options.IsInCohort("   ", allowDevelopmentSentinel: true).Should().BeFalse();
    }

    [Fact]
    public void IsInCohort_SentinelAlongsideRealId_StillMatchesTheRealIdWhenDisallowed()
    {
        // Disallowing the sentinel must not disable the rest of the list.
        var options = new CoachOptions
        {
            Enabled = true,
            AllowedUserProfileIds = new List<string> { CoachOptions.DevAllSentinel, "profile-1" }
        };

        options.IsInCohort("profile-1").Should().BeTrue();
        options.IsInCohort("profile-2").Should().BeFalse();
    }

    [Fact]
    public void IsInCohort_WithoutSentinel_StillFailsClosed()
    {
        var options = new CoachOptions
        {
            Enabled = true,
            AllowedUserProfileIds = new List<string> { "specific-profile" }
        };

        options.IsInCohort("specific-profile").Should().BeTrue();
        options.IsInCohort("other-profile").Should().BeFalse();
    }

    [Theory]
    [InlineData("__dev_all__")]
    [InlineData("  __dev_all__  ")]
    public void ContainsDevelopmentSentinel_DetectsThePaddedSpelling(string entry)
    {
        // A value arriving from an environment variable can carry padding. A padded sentinel must
        // be caught by the validator rather than sliding through as a never-matching cohort id.
        new CoachOptions { AllowedUserProfileIds = new List<string> { entry } }
            .ContainsDevelopmentSentinel.Should().BeTrue();
    }

    [Fact]
    public void ContainsDevelopmentSentinel_IsFalseForOrdinaryCohorts()
    {
        new CoachOptions { AllowedUserProfileIds = new List<string> { "profile-1", "profile-2" } }
            .ContainsDevelopmentSentinel.Should().BeFalse();

        new CoachOptions().ContainsDevelopmentSentinel.Should().BeFalse();
    }

    [Fact]
    public void Validate_DevAllSentinelInCohort_PassesInDevelopment()
    {
        var options = new CoachOptions
        {
            Enabled = true,
            AllowedUserProfileIds = new List<string> { CoachOptions.DevAllSentinel }
        };

        Validate(options, Environments.Development).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Validate_DevAllSentinelInCohort_FailsOutsideDevelopment(string environmentName)
    {
        var options = new CoachOptions
        {
            Enabled = true,
            AllowedUserProfileIds = new List<string> { CoachOptions.DevAllSentinel }
        };

        var result = Validate(options, environmentName);

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainMatch("*__dev_all__*")
            .And.ContainMatch("*Development*");
    }

    [Fact]
    public void Validate_DevAllSentinelInCohort_FailsWhenTheEnvironmentIsUnknown()
    {
        // No environment supplied is not "assume Development". An unknown host gets the strict
        // rules, so a registration that forgets to pass the environment cannot loosen the check.
        var options = new CoachOptions
        {
            Enabled = true,
            AllowedUserProfileIds = new List<string> { CoachOptions.DevAllSentinel }
        };

        Validate(options).Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_PaddedDevAllSentinel_FailsOutsideDevelopment()
    {
        var options = new CoachOptions
        {
            Enabled = true,
            AllowedUserProfileIds = new List<string> { "  __dev_all__ " }
        };

        Validate(options, Environments.Production).Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ExplicitCohortIds_PassEverywhere()
    {
        var options = new CoachOptions
        {
            Enabled = true,
            AllowedUserProfileIds = new List<string> { "profile-1", "profile-2" }
        };

        Validate(options, Environments.Development).Succeeded.Should().BeTrue();
        Validate(options, Environments.Production).Succeeded.Should().BeTrue();
        Validate(options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void AddCoachRuntime_WithDevAllSentinel_OutsideDevelopment_FailsStartup()
    {
        // The whole point of ValidateOnStart: a Production host configured with the wildcard must
        // refuse to boot rather than serve every authenticated user.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coach:Enabled"] = "true",
                ["Coach:AllowedUserProfileIds:0"] = CoachOptions.DevAllSentinel
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddCoachRuntime(configuration, new OptionsStubEnvironment(Environments.Production))
            .BuildServiceProvider();

        var act = () => provider.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*__dev_all__*");
    }

    [Fact]
    public void AddCoachRuntime_WithDevAllSentinel_InDevelopment_Boots()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coach:Enabled"] = "true",
                ["Coach:AllowedUserProfileIds:0"] = CoachOptions.DevAllSentinel
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddCoachRuntime(configuration, new OptionsStubEnvironment(Environments.Development))
            .BuildServiceProvider();

        provider.Invoking(p => p.GetRequiredService<IStartupValidator>().Validate())
            .Should().NotThrow();

        // And the resolved policy honours it, because the same environment reached both.
        provider.GetRequiredService<ICoachAvailabilityPolicy>()
            .Evaluate("any-authenticated-profile").IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Bind_DevAllSentinel_BindsFromConfiguration()
    {
        var options = BindFrom(
            ("Coach:Enabled", "true"),
            ("Coach:AllowedUserProfileIds:0", "__dev_all__"));

        options.Enabled.Should().BeTrue();
        options.AllowedUserProfileIds.Should().ContainSingle().Which.Should().Be("__dev_all__");
        options.IsInCohort("any-profile", allowDevelopmentSentinel: true).Should().BeTrue();
        options.IsInCohort("any-profile").Should().BeFalse("binding does not decide the environment");
    }

    [Fact]
    public void Defaults_ProductionSafe_CoachDisabledAndEmptyCohort()
    {
        // This locks the production-safe invariant: a fresh CoachOptions (no config)
        // denies all users.
        var options = new CoachOptions();

        options.Enabled.Should().BeFalse();
        options.AllowedUserProfileIds.Should().BeEmpty();
        options.IsInCohort("any-profile").Should().BeFalse();
        options.IsSamOverlayEnabled.Should().BeFalse();
        options.IsSamReadToolsEnabled.Should().BeFalse();
        options.IsSamWriteToolsEnabled.Should().BeFalse();
    }

    [Fact]
    public void AppHostDevConfig_EnablesCoachWithDurableHistoryAndOverlay()
    {
        // Simulates the AppHost appsettings.Development.json shape
        var options = BindFrom(
            ("Coach:Enabled", "true"),
            ("Coach:AllowedUserProfileIds:0", "__dev_all__"),
            ("Coach:DurableHistory:Enabled", "true"),
            ("Coach:SamOverlay:Enabled", "true"));

        options.Enabled.Should().BeTrue();
        options.IsInCohort("squad-jayne-profile-guid", allowDevelopmentSentinel: true)
            .Should().BeTrue("dev sentinel admits everyone, in Development");
        options.IsInCohort("squad-jayne-profile-guid")
            .Should().BeFalse("the same configuration admits nobody outside Development");
        options.IsDurableHistoryEnabled.Should().BeTrue();
        options.IsSamOverlayEnabled.Should().BeTrue();
        options.IsSamReadToolsEnabled.Should().BeFalse("Phase 1 leaves read tools off");
        options.IsSamWriteToolsEnabled.Should().BeFalse("Phase 1 leaves write tools off");

        Validate(options, Environments.Development).Succeeded.Should().BeTrue();
        Validate(options, Environments.Production).Failed.Should()
            .BeTrue("this AppHost shape is a local-development shape and must not boot elsewhere");
    }
}
