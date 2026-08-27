using Microsoft.Extensions.Logging.Abstractions;

namespace SentenceStudio.Api.Coach.Agents;

/// <summary>
/// The logger factory the coach hands to Agent Framework and Microsoft.Extensions.AI internals.
/// </summary>
/// <remarks>
/// <para>
/// Those libraries log prompts, model responses, and tool arguments when their own categories are
/// raised to <c>Debug</c> or <c>Trace</c>. A coach run carries learner free text, due-vocabulary
/// evidence, and plan previews, so an operator raising a log level — locally, or on a production
/// host chasing an unrelated bug — must not be able to turn model content into log output. The
/// coach therefore never hands the application's <see cref="ILoggerFactory"/> to those internals.
/// </para>
/// <para>
/// This is a distinct type rather than a plain <see cref="ILoggerFactory"/> parameter on purpose.
/// If the seam were typed as <see cref="ILoggerFactory"/>, the DI container would happily satisfy
/// it with the application factory — the exact leak this exists to prevent — because that is the
/// registered service for that type. Nothing registers this wrapper, so the production path always
/// falls back to <see cref="Safe"/>.
/// </para>
/// <para>
/// This does not restrict the coach's own logging. <c>BaselineLearningCoach</c>,
/// <c>HarnessLearningCoach</c>, <c>CoachAgentTurnRunner</c>, and <see cref="CoachAgentFactory"/>
/// keep logging through the application factory; those logs are written by us and carry shape only
/// — outcome, stop reason, counts — never learner or model text.
/// </para>
/// </remarks>
public sealed class CoachModelLoggerFactory
{
    /// <summary>
    /// The production value: a sink that cannot emit anything, whatever the configured level.
    /// </summary>
    public static CoachModelLoggerFactory Safe { get; } = new(NullLoggerFactory.Instance);

    /// <summary>
    /// Wraps a factory for the agent internals.
    /// </summary>
    /// <remarks>
    /// Only a content-free sink belongs here. The constructor is public so a test can pass a
    /// recording factory and prove the coach really routes the internals through this seam rather
    /// than through the application factory; production code should use <see cref="Safe"/>.
    /// </remarks>
    public CoachModelLoggerFactory(ILoggerFactory loggerFactory)
    {
        LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <summary>The wrapped factory.</summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>
    /// True when the wrapped factory is a null sink, which cannot emit model content at any level.
    /// </summary>
    public bool IsContentFree =>
        ReferenceEquals(LoggerFactory, NullLoggerFactory.Instance) || LoggerFactory is NullLoggerFactory;
}
