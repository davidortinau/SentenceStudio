using Microsoft.Extensions.Options;

namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>
/// Refuses to start a host whose opportunity-ledger configuration is unsafe.
/// </summary>
/// <remarks>
/// <para>
/// The load-bearing rule is the third one: the operator surface must be impossible outside
/// Development. Route mapping already skips it there, but a route registration is a line of code
/// somebody can move, and a configuration reload does not re-run this validator — so the check
/// exists to make the <em>deployment</em> illegal, not merely the request. This mirrors
/// <c>CoachOptionsValidator</c>'s treatment of the <c>__dev_all__</c> cohort sentinel, which
/// fails startup for the same reason rather than warning.
/// </para>
/// </remarks>
public sealed class CoachOpportunityOptionsValidator : IValidateOptions<CoachOpportunityOptions>
{
    /// <summary>The lowest retention window the ledger will accept.</summary>
    public const int MinRetentionDays = 7;

    /// <summary>The highest retention window the ledger will accept.</summary>
    public const int MaxRetentionDays = 730;

    private readonly IHostEnvironment? _environment;

    public CoachOpportunityOptionsValidator(IHostEnvironment? environment = null)
    {
        _environment = environment;
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, CoachOpportunityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var isDevelopment = _environment?.IsDevelopment() == true;
        var environmentName = _environment?.EnvironmentName ?? "(unknown)";

        if (options.RetentionDays is < MinRetentionDays or > MaxRetentionDays)
        {
            failures.Add(
                $"{CoachOpportunityOptions.SectionName}:RetentionDays must be between " +
                $"{MinRetentionDays} and {MaxRetentionDays}. It was {options.RetentionDays}.");
        }

        if (options.OperatorSurface is null)
        {
            failures.Add(
                $"{CoachOpportunityOperatorSurfaceOptions.SectionName} must be an object with an " +
                "'Enabled' child, not a bare value.");

            return ValidateOptionsResult.Fail(failures);
        }

        if (options.OperatorSurface.Enabled && !isDevelopment)
        {
            failures.Add(
                $"'{CoachOpportunityOperatorSurfaceOptions.SectionName}:Enabled' is true in the " +
                $"'{environmentName}' environment. The Sam opportunity operator surface can read " +
                "encrypted learner messages and this host has no admin authorization primitive, " +
                "so it is Development-only. Set it to false, or run in Development.");
        }

        if (options.OperatorSurface.AllowCrossOwnerEvidence && !isDevelopment)
        {
            failures.Add(
                $"'{CoachOpportunityOperatorSurfaceOptions.SectionName}:AllowCrossOwnerEvidence' " +
                $"is true in the '{environmentName}' environment. Reading one learner's messages " +
                "from another learner's row is a deliberate cross-tenant read and is " +
                "Development-only. Set it to false.");
        }

        if (options.OperatorSurface.AllowCrossOwnerEvidence && !options.OperatorSurface.Enabled)
        {
            // Not merely redundant: a deployment that believes cross-owner reads are configured
            // while the surface is off has a mistaken model of what it turned on, and the next
            // person to enable the surface inherits a permission nobody re-reviewed.
            failures.Add(
                $"'{CoachOpportunityOperatorSurfaceOptions.SectionName}:AllowCrossOwnerEvidence' " +
                "is true while the operator surface itself is disabled. Enable the surface " +
                "deliberately, or set AllowCrossOwnerEvidence to false.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
