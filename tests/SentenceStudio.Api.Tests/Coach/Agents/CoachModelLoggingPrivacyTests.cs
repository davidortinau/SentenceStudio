using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Telemetry;

namespace SentenceStudio.Api.Tests.Coach.Agents;

/// <summary>
/// Agent Framework and Microsoft.Extensions.AI log prompts, model responses, and tool arguments
/// once their own categories reach Debug/Trace. A coach run carries learner free text and evidence,
/// so the coach must never hand those libraries the application logger factory.
/// </summary>
/// <remarks>
/// The tests cover the rule from both directions: the internals seam receives the content-free
/// factory by default and is actually the factory both arms use, and a run with everything at Trace
/// puts no learner text into application-visible logs. The seam assertion is the deterministic one
/// — the sentinel assertion depends on what the framework chooses to log, so on its own it could
/// pass for the wrong reason.
/// </remarks>
public class CoachModelLoggingPrivacyTests
{
    private const string Sentinel = "ZZQX-LEARNER-SENTINEL-8891";

    private const string IntentJson = """
        {
          "Kind": "NoChange",
          "AcceptanceState": "NotApplicable",
          "CoachMessage": "ok",
          "EvidenceReferences": []
        }
        """;

    public static TheoryData<CoachImplementation> Arms => new()
    {
        CoachImplementation.Baseline,
        CoachImplementation.Harness
    };

    [Fact]
    public void TheDefaultModelLoggerFactory_IsANullSink()
    {
        var factory = NewFactory(new ScriptedChatClient(IntentJson), new RecordingLoggerFactory());

        factory.ModelLoggerFactory.Should().BeSameAs(NullLoggerFactory.Instance);
        CoachModelLoggerFactory.Safe.IsContentFree.Should().BeTrue();
        CoachModelLoggerFactory.Safe.LoggerFactory.Should().BeSameAs(NullLoggerFactory.Instance);
    }

    [Theory]
    [MemberData(nameof(Arms))]
    public void BothArms_BuildTheirAgentWithTheModelLoggerFactoryNotTheApplicationOne(CoachImplementation arm)
    {
        var applicationLogs = new RecordingLoggerFactory();
        var internalsLogs = new RecordingLoggerFactory();

        var factory = NewFactory(
            new ScriptedChatClient(IntentJson),
            applicationLogs,
            new CoachModelLoggerFactory(internalsLogs));

        var tools = CoachAgentTestDoubles.StubTools();
        var agent = arm == CoachImplementation.Harness
            ? factory.TryCreateHarnessAgent(tools)
            : factory.TryCreateAgent(tools);

        agent.Should().NotBeNull();

        internalsLogs.Categories.Should().NotBeEmpty(
            "the agent internals must resolve their loggers from the seam the coach controls");
        applicationLogs.Categories.Should().NotContain(
            c => IsFrameworkCategory(c),
            "no Agent Framework or Microsoft.Extensions.AI category may be created from the application factory");
    }

    [Fact]
    public void TheApplicationFactory_StillCarriesOurOwnShapeOnlyLogs()
    {
        var applicationLogs = new RecordingLoggerFactory();
        var factory = NewFactory(new ScriptedChatClient(IntentJson), applicationLogs);

        factory.TryCreateAgent(CoachAgentTestDoubles.StubTools()).Should().NotBeNull();

        applicationLogs.Categories.Should().Contain(typeof(CoachAgentFactory).FullName!,
            "hardening the model internals must not silence our own logging");
        applicationLogs.Entries.Should().Contain(e => e.Message.Contains("Coach agent created"));
        applicationLogs.Entries.Should().OnlyContain(e => !ContainsSentinel(e.Message));
    }

    [Theory]
    [MemberData(nameof(Arms))]
    public async Task WithEverythingAtTrace_ARunLeavesNoLearnerTextInApplicationLogs(CoachImplementation arm)
    {
        var applicationLogs = new RecordingLoggerFactory(LogLevel.Trace);
        var client = new ScriptedChatClient(IntentJson);
        var coach = NewCoach(arm, client, applicationLogs);

        var result = await coach.RunTurnAsync(
            CoachAgentTestDoubles.NewRequest($"I have {Sentinel} minutes and no audio"));

        result.Outcome.Should().Be(CoachAgentOutcome.Completed);
        client.CallCount.Should().Be(1, "the sentinel really did travel to the model on this run");

        applicationLogs.Entries.Should().NotBeEmpty("the coach's own logging is still active at Trace");
        applicationLogs.Entries.Should().OnlyContain(e => !ContainsSentinel(e.Message));
        applicationLogs.Entries.Should().OnlyContain(e => !ContainsSentinel(e.State));
        applicationLogs.Categories.Should().NotContain(c => IsFrameworkCategory(c));
    }

    [Theory]
    [MemberData(nameof(Arms))]
    public async Task WithTheSeamRecordingAtTrace_TheApplicationFactoryStillSeesNoFrameworkCategory(
        CoachImplementation arm)
    {
        var applicationLogs = new RecordingLoggerFactory(LogLevel.Trace);
        var internalsLogs = new RecordingLoggerFactory(LogLevel.Trace);
        var client = new ScriptedChatClient(IntentJson);

        var coach = NewCoach(arm, client, applicationLogs, new CoachModelLoggerFactory(internalsLogs));

        var result = await coach.RunTurnAsync(
            CoachAgentTestDoubles.NewRequest($"please note {Sentinel}"));

        result.Outcome.Should().Be(CoachAgentOutcome.Completed);

        // Whatever the framework decides to log — and it may log learner text — it can only reach
        // the seam. In production that seam is a null sink, so the content has nowhere to go.
        applicationLogs.Entries.Should().OnlyContain(e => !ContainsSentinel(e.Message));
        applicationLogs.Categories.Should().NotContain(c => IsFrameworkCategory(c));
    }

    [Theory]
    [MemberData(nameof(Arms))]
    public void TheLeakDetectorItself_CatchesTheUnhardenedWiring(CoachImplementation arm)
    {
        // Wiring the seam to the application factory reproduces the pre-hardening behaviour.
        // If this test ever stops seeing framework categories, the assertions above have gone
        // blind and would keep passing after a real regression.
        var applicationLogs = new RecordingLoggerFactory(LogLevel.Trace);
        var factory = NewFactory(
            new ScriptedChatClient(IntentJson),
            applicationLogs,
            new CoachModelLoggerFactory(applicationLogs));

        var tools = CoachAgentTestDoubles.StubTools();
        var agent = arm == CoachImplementation.Harness
            ? factory.TryCreateHarnessAgent(tools)
            : factory.TryCreateAgent(tools);

        agent.Should().NotBeNull();
        applicationLogs.Categories.Should().Contain(c => IsFrameworkCategory(c));
    }

    private static bool IsFrameworkCategory(string category) =>        category.StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal)
        || category.StartsWith("Microsoft.Agents.AI", StringComparison.Ordinal);

    private static bool ContainsSentinel(string? value) =>
        value?.Contains(Sentinel, StringComparison.OrdinalIgnoreCase) == true;

    private static CoachAgentFactory NewFactory(
        IChatClient chatClient,
        ILoggerFactory applicationLoggerFactory,
        CoachModelLoggerFactory? modelLoggerFactory = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(chatClient);

        return new CoachAgentFactory(
            services.BuildServiceProvider(),
            new TestOptionsMonitor<CoachOptions>(new CoachOptions { Enabled = true }),
            applicationLoggerFactory,
            allowList: null,
            modelLoggerFactory: modelLoggerFactory);
    }

    private static ILearningCoach NewCoach(
        CoachImplementation arm,
        IChatClient chatClient,
        ILoggerFactory applicationLoggerFactory,
        CoachModelLoggerFactory? modelLoggerFactory = null)
    {
        var factory = NewFactory(chatClient, applicationLoggerFactory, modelLoggerFactory);
        var options = new TestOptionsMonitor<CoachOptions>(new CoachOptions { Enabled = true });
        var tools = new CoachAgentTestDoubles.StubToolFactory();
        var telemetry = new CoachTelemetry();

        return arm == CoachImplementation.Harness
            ? new HarnessLearningCoach(
                factory, tools, options, telemetry,
                applicationLoggerFactory.CreateLogger<HarnessLearningCoach>())
            : new BaselineLearningCoach(
                factory, tools, options, telemetry,
                applicationLoggerFactory.CreateLogger<BaselineLearningCoach>());
    }

    /// <summary>
    /// An <see cref="ILoggerFactory"/> that says yes to every level and keeps every category and
    /// entry, so a test can assert on what a component tried to write rather than on configuration.
    /// </summary>
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly LogLevel _minimum;

        public RecordingLoggerFactory(LogLevel minimum = LogLevel.Trace) => _minimum = minimum;

        public ConcurrentBag<string> Categories { get; } = new();

        public ConcurrentBag<RecordedEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName)
        {
            Categories.Add(categoryName);
            return new RecordingLogger(this, categoryName, _minimum);
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        internal sealed record RecordedEntry(string Category, LogLevel Level, string Message, string? State);

        private sealed class RecordingLogger : ILogger
        {
            private readonly RecordingLoggerFactory _owner;
            private readonly string _category;
            private readonly LogLevel _minimum;

            public RecordingLogger(RecordingLoggerFactory owner, string category, LogLevel minimum)
            {
                _owner = owner;
                _category = category;
                _minimum = minimum;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                _owner.Entries.Add(new RecordedEntry(_category, LogLevel.Trace, "scope", state.ToString()));
                return NullScope.Instance;
            }

            public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimum;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _owner.Entries.Add(new RecordedEntry(
                    _category,
                    logLevel,
                    formatter(state, exception),
                    state?.ToString()));
            }

            private sealed class NullScope : IDisposable
            {
                public static NullScope Instance { get; } = new();

                public void Dispose()
                {
                }
            }
        }
    }
}
