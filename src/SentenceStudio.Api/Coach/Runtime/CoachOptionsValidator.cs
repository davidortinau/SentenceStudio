using Microsoft.Extensions.Hosting;
using SentenceStudio.Api.Coach.Validation.Claims;
using Microsoft.Extensions.Options;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Runtime;

/// <summary>
/// Startup validation for <see cref="CoachOptions"/>.
/// </summary>
/// <remarks>
/// Registered with <c>ValidateOnStart()</c> so a bad <c>Coach:*</c> value stops the host at
/// boot with every problem listed at once, instead of surfacing as a runaway budget, an
/// unbounded run, or a never-expiring session later.
/// </remarks>
public sealed class CoachOptionsValidator : IValidateOptions<CoachOptions>
{
    private readonly IHostEnvironment? _environment;

    /// <summary>
    /// Creates the validator for a host environment.
    /// </summary>
    /// <param name="environment">
    /// The host environment, used only to decide whether the development cohort sentinel is
    /// permitted. Null is treated as non-Development: an unknown environment gets the strict
    /// rules, so forgetting to supply one cannot loosen a check.
    /// </param>
    public CoachOptionsValidator(IHostEnvironment? environment = null)
    {
        _environment = environment;
    }

    /// <summary>Smallest accepted run timeout, in seconds.</summary>
    public const int MinRequestTimeoutSeconds = 5;

    /// <summary>Largest accepted run timeout, in seconds.</summary>
    public const int MaxRequestTimeoutSeconds = 120;

    /// <summary>Largest accepted daily run budget.</summary>
    public const int MaxRunsPerDayCeiling = 200;

    /// <summary>Largest accepted weekly run budget.</summary>
    public const int MaxRunsPerWeekCeiling = 1000;

    /// <summary>Largest accepted session expiry, in hours (one week).</summary>
    public const int MaxSessionExpiryHours = 168;

    /// <summary>Largest accepted revision retention, in days.</summary>
    public const int MaxRevisionRetentionDays = 365;

    /// <summary>Largest accepted model/tool iteration budget for one run.</summary>
    public const int MaxIterationsCeiling = 20;

    /// <summary>
    /// Smallest accepted per-response output token cap.
    /// </summary>
    /// <remarks>
    /// On a reasoning model the cap covers hidden reasoning tokens as well as the visible
    /// answer, so a small cap does not produce a short answer — it produces no answer at all,
    /// after the run has already been paid for. The floor is set above anything that can
    /// reproduce that.
    /// </remarks>
    public const int MinOutputTokens = 2_000;

    /// <summary>Largest accepted per-response output token cap. The cap is never removed.</summary>
    public const int MaxOutputTokensCeiling = 32_000;

    /// <summary>Largest accepted agent config version string length.</summary>
    public const int MaxAgentConfigVersionLength = 64;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, CoachOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (!Enum.IsDefined(options.Implementation))
        {
            failures.Add($"{CoachOptions.SectionName}:Implementation must be 'baseline' or 'harness'.");
        }

        ValidateAgentConfigVersion(options, failures);
        ValidateReasoningEffort(options, failures);
        ValidateCohort(options, failures);
        ValidateSamFlagDependencies(options, failures);
        ValidateGroundingStage(options, failures);

        RequireRange(failures, nameof(CoachOptions.MaxRunsPerDay), options.MaxRunsPerDay, 1, MaxRunsPerDayCeiling);
        RequireRange(failures, nameof(CoachOptions.MaxRunsPerWeek), options.MaxRunsPerWeek, 1, MaxRunsPerWeekCeiling);

        if (options.MaxRunsPerWeek < options.MaxRunsPerDay)
        {
            failures.Add(
                $"{CoachOptions.SectionName}:MaxRunsPerWeek ({options.MaxRunsPerWeek}) must be greater than or equal to " +
                $"{CoachOptions.SectionName}:MaxRunsPerDay ({options.MaxRunsPerDay}).");
        }

        RequireRange(failures, nameof(CoachOptions.SessionExpiryHours), options.SessionExpiryHours, 1, MaxSessionExpiryHours);
        RequireRange(failures, nameof(CoachOptions.RevisionRetentionDays), options.RevisionRetentionDays, 1, MaxRevisionRetentionDays);
        RequireRange(failures, nameof(CoachOptions.RequestTimeoutSeconds), options.RequestTimeoutSeconds, MinRequestTimeoutSeconds, MaxRequestTimeoutSeconds);
        RequireRange(failures, nameof(CoachOptions.MaxIterationsPerRequest), options.MaxIterationsPerRequest, 1, MaxIterationsCeiling);
        RequireRange(failures, nameof(CoachOptions.MaxClarificationsPerSession), options.MaxClarificationsPerSession, 0, CoachConstraintLimits.MaxClarificationsPerSession);
        RequireRange(failures, nameof(CoachOptions.MaxOutputTokens), options.MaxOutputTokens, MinOutputTokens, MaxOutputTokensCeiling);
        RequireRange(failures, nameof(CoachOptions.MaxTurnTextLength), options.MaxTurnTextLength, 1, CoachConstraintLimits.MaxTurnTextLength);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Refuses a grounding stage that is not one of the four rungs.
    /// </summary>
    /// <remarks>
    /// <c>CoachConfigurationKeyValidator</c> catches the string a human typed wrong.
    /// This catches the value a caller set in code — a test host, a future options
    /// post-configure, a cast — where there is no raw string to read. An undefined stage
    /// compares as greater-or-less than the real rungs under the <c>&gt;=</c> the engine uses, so
    /// it does not fail closed on its own; it silently behaves like whichever rung it sorts beside.
    /// </remarks>
    private static void ValidateGroundingStage(CoachOptions options, List<string> failures)
    {
        if (options.Grounding is null)
        {
            failures.Add(
                $"{CoachOptions.SectionName}:Grounding must not be null. The grounding ladder has a "
                + "fail-safe default and no way to express 'absent'.");
            return;
        }

        if (!Enum.IsDefined(options.Grounding.Stage))
        {
            failures.Add(
                $"{CoachOptions.SectionName}:Grounding:Stage is '{(int)options.Grounding.Stage}', "
                + $"which is not a grounding stage. Use one of "
                + $"{string.Join(", ", Enum.GetNames<CoachGroundingStage>())}.");
        }
    }

    private static void ValidateAgentConfigVersion(CoachOptions options, List<string> failures)
    {
        var version = options.AgentConfigVersion;

        if (string.IsNullOrWhiteSpace(version))
        {
            failures.Add($"{CoachOptions.SectionName}:AgentConfigVersion must not be empty.");
            return;
        }

        if (version.Length > MaxAgentConfigVersionLength)
        {
            failures.Add(
                $"{CoachOptions.SectionName}:AgentConfigVersion must be {MaxAgentConfigVersionLength} characters or fewer.");
            return;
        }

        foreach (var c in version)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-' && c != '_')
            {
                failures.Add(
                    $"{CoachOptions.SectionName}:AgentConfigVersion may contain only letters, digits, '.', '-', and '_'.");
                return;
            }
        }
    }

    /// <summary>
    /// Rejects an effort name the application's chat-options factory would silently drop.
    /// Reuses the same vocabulary as every other AI call so the two cannot drift.
    /// </summary>
    private static void ValidateReasoningEffort(CoachOptions options, List<string> failures)
    {
        if (!SentenceStudio.Services.AiChatOptionsFactory.IsSupportedReasoningEffort(options.ReasoningEffort))
        {
            failures.Add(
                $"{CoachOptions.SectionName}:ReasoningEffort must be one of 'minimal', 'low', 'medium', or 'high'.");
        }
    }

    private void ValidateCohort(CoachOptions options, List<string> failures)
    {
        if (options.AllowedUserProfileIds is null)
        {
            failures.Add($"{CoachOptions.SectionName}:AllowedUserProfileIds must not be null.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < options.AllowedUserProfileIds.Count; i++)
        {
            var id = options.AllowedUserProfileIds[i];

            if (string.IsNullOrWhiteSpace(id))
            {
                // Index only. The value itself is a user identifier and never appears in a message.
                failures.Add($"{CoachOptions.SectionName}:AllowedUserProfileIds[{i}] must not be empty.");
                continue;
            }

            if (!seen.Add(id.Trim()))
            {
                failures.Add($"{CoachOptions.SectionName}:AllowedUserProfileIds[{i}] is a duplicate entry.");
            }
        }

        ValidateDevelopmentSentinel(options, failures);
    }

    /// <summary>
    /// Refuses to boot a non-Development host whose cohort names the development sentinel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CoachOptions.DevAllSentinel"/> admits every authenticated user. That is
    /// acceptable on a laptop, where the alternative is pasting a profile GUID that changes with
    /// every fresh Postgres volume. It is not acceptable anywhere else, and the failure mode is
    /// silent: the deployment looks correctly cohort-gated because a list of allowed ids is
    /// present, while the one entry in it matches everyone.
    /// </para>
    /// <para>
    /// A startup failure rather than a warning, because a warning is exactly what a
    /// misconfiguration like this survives. A null environment is treated as non-Development so
    /// an unknown host gets the strict answer.
    /// </para>
    /// </remarks>
    private void ValidateDevelopmentSentinel(CoachOptions options, List<string> failures)
    {
        if (!options.ContainsDevelopmentSentinel || _environment?.IsDevelopment() == true)
        {
            return;
        }

        failures.Add(
            $"{CoachOptions.SectionName}:AllowedUserProfileIds contains the development-only " +
            $"sentinel '{CoachOptions.DevAllSentinel}', which admits every authenticated user. " +
            $"It is permitted only in the Development environment (this host is " +
            $"'{_environment?.EnvironmentName ?? "unknown"}'). Name each participating " +
            $"user_profile_id explicitly instead.");
    }

    /// <summary>
    /// Validates the Sam feature flag dependency chain:
    /// SamOverlay requires DurableHistory; SamReadTools requires SamOverlay; SamWriteTools requires SamReadTools.
    /// </summary>
    private static void ValidateSamFlagDependencies(CoachOptions options, List<string> failures)
    {
        if (options.IsSamOverlayEnabled && !options.IsDurableHistoryEnabled)
        {
            failures.Add(
                $"{CoachOptions.SectionName}:SamOverlay:Enabled requires {CoachOptions.SectionName}:DurableHistory:Enabled.");
        }

        if (options.IsSamReadToolsEnabled && !options.IsSamOverlayEnabled)
        {
            failures.Add(
                $"{CoachOptions.SectionName}:SamReadTools:Enabled requires {CoachOptions.SectionName}:SamOverlay:Enabled.");
        }

        if (options.IsSamWriteToolsEnabled && !options.IsSamReadToolsEnabled)
        {
            failures.Add(
                $"{CoachOptions.SectionName}:SamWriteTools:Enabled requires {CoachOptions.SectionName}:SamReadTools:Enabled.");
        }
    }

    private static void RequireRange(List<string> failures, string property, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            failures.Add($"{CoachOptions.SectionName}:{property} must be between {min} and {max}. Actual: {value}.");
        }
    }
}
