using Microsoft.Extensions.Options;

namespace SentenceStudio.Api.Feedback;

/// <summary>
/// Everything about the feedback lane a deployment is allowed to choose.
/// </summary>
/// <remarks>
/// The limits are here rather than as constants so a test can compress a rolling day into a few
/// seconds without a second code path. The <em>defaults</em> are the shipped policy, and the
/// validator refuses a deployment that widens them past the point where a single account could
/// use this endpoint to spend the project's AI budget or fill its public issue tracker.
/// </remarks>
public sealed class FeedbackOptions
{
    /// <summary>The configuration section these bind to.</summary>
    public const string SectionName = "Feedback";

    /// <summary>How long a signed preview stays redeemable.</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How many previews one owner may request per rolling window.</summary>
    public int MaxPreviewsPerWindow { get; set; } = 10;

    /// <summary>The preview window.</summary>
    public TimeSpan PreviewWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How many submissions one owner may claim per rolling window.</summary>
    public int MaxSubmitsPerWindow { get; set; } = 3;

    /// <summary>The submission window.</summary>
    public TimeSpan SubmitWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>The minimum gap between two claimed submissions by one owner.</summary>
    public TimeSpan SubmitCooldown { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a settled submission row is kept before the retention sweep removes it.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Whether the retention sweep runs on this deployment.</summary>
    public bool RetentionSweepEnabled { get; set; } = true;

    /// <summary>How often the retention sweep runs.</summary>
    public TimeSpan RetentionSweepInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// How long a caller that lost the claim race waits for the winner to settle before answering
    /// "in doubt". Bounded on purpose: a loser that waited indefinitely would hold a request open
    /// for as long as GitHub is slow.
    /// </summary>
    public TimeSpan ReplayWait { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>How often the loser re-reads the ledger row while it waits.</summary>
    public TimeSpan ReplayPollInterval { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>How many times a rate-limit compare-and-swap retries before giving up.</summary>
    public int RateLimitCasAttempts { get; set; } = 8;
}

/// <summary>
/// Refuses to start a host whose feedback limits are not limits.
/// </summary>
public sealed class FeedbackOptionsValidator : IValidateOptions<FeedbackOptions>
{
    /// <summary>The longest token lifetime that will be accepted.</summary>
    public static readonly TimeSpan MaxTokenLifetime = TimeSpan.FromHours(1);

    /// <summary>The most previews per window any deployment may allow.</summary>
    public const int MaxPreviewsCeiling = 60;

    /// <summary>The most submissions per window any deployment may allow.</summary>
    public const int MaxSubmitsCeiling = 25;

    /// <summary>The shortest retention window that will be accepted.</summary>
    public const int MinRetentionDays = 7;

    /// <summary>The longest retention window that will be accepted.</summary>
    public const int MaxRetentionDays = 730;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, FeedbackOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.TokenLifetime <= TimeSpan.Zero || options.TokenLifetime > MaxTokenLifetime)
        {
            failures.Add(
                $"{FeedbackOptions.SectionName}:TokenLifetime must be positive and no more than "
                + $"{MaxTokenLifetime}. It was {options.TokenLifetime}.");
        }

        if (options.MaxPreviewsPerWindow is < 1 || options.MaxPreviewsPerWindow > MaxPreviewsCeiling)
        {
            failures.Add(
                $"{FeedbackOptions.SectionName}:MaxPreviewsPerWindow must be between 1 and "
                + $"{MaxPreviewsCeiling}. It was {options.MaxPreviewsPerWindow}.");
        }

        if (options.MaxSubmitsPerWindow is < 1 || options.MaxSubmitsPerWindow > MaxSubmitsCeiling)
        {
            failures.Add(
                $"{FeedbackOptions.SectionName}:MaxSubmitsPerWindow must be between 1 and "
                + $"{MaxSubmitsCeiling}. It was {options.MaxSubmitsPerWindow}.");
        }

        if (options.PreviewWindow <= TimeSpan.Zero)
        {
            failures.Add($"{FeedbackOptions.SectionName}:PreviewWindow must be positive.");
        }

        if (options.SubmitWindow <= TimeSpan.Zero)
        {
            failures.Add($"{FeedbackOptions.SectionName}:SubmitWindow must be positive.");
        }

        if (options.SubmitCooldown < TimeSpan.Zero)
        {
            failures.Add($"{FeedbackOptions.SectionName}:SubmitCooldown must not be negative.");
        }

        if (options.RetentionDays is < MinRetentionDays or > MaxRetentionDays)
        {
            failures.Add(
                $"{FeedbackOptions.SectionName}:RetentionDays must be between {MinRetentionDays} "
                + $"and {MaxRetentionDays}. It was {options.RetentionDays}.");
        }

        if (options.RetentionSweepInterval <= TimeSpan.Zero)
        {
            failures.Add($"{FeedbackOptions.SectionName}:RetentionSweepInterval must be positive.");
        }

        if (options.ReplayWait < TimeSpan.Zero)
        {
            failures.Add($"{FeedbackOptions.SectionName}:ReplayWait must not be negative.");
        }

        if (options.ReplayPollInterval <= TimeSpan.Zero)
        {
            failures.Add($"{FeedbackOptions.SectionName}:ReplayPollInterval must be positive.");
        }

        if (options.RateLimitCasAttempts < 1)
        {
            failures.Add($"{FeedbackOptions.SectionName}:RateLimitCasAttempts must be at least 1.");
        }

        // A token that outlives its own rate-limit window lets an owner bank previews: sign ten,
        // wait for the window to roll, and redeem all ten. The lifetime has to be the shorter one.
        if (options.TokenLifetime > options.PreviewWindow)
        {
            failures.Add(
                $"{FeedbackOptions.SectionName}:TokenLifetime ({options.TokenLifetime}) must not "
                + $"exceed PreviewWindow ({options.PreviewWindow}), or previews can be banked past "
                + "the window that limits them.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
