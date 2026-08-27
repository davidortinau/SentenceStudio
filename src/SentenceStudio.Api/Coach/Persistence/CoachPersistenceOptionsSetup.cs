using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Runtime;

namespace SentenceStudio.Api.Coach.Persistence;

/// <summary>
/// Projects the operator-owned <see cref="CoachOptions"/> values onto
/// <see cref="CoachPersistenceOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the single seam between the public <c>Coach:*</c> configuration contract and the
/// persistence layer. It exists so an operator has exactly one key per knob:
/// </para>
/// <list type="bullet">
/// <item><c>Coach:SessionExpiryHours</c> → <see cref="CoachPersistenceOptions.SessionLifetime"/></item>
/// <item><c>Coach:RevisionRetentionDays</c> → <see cref="CoachPersistenceOptions.RevisionRetention"/></item>
/// <item><c>Coach:AgentConfigVersion</c> → <see cref="CoachPersistenceOptions.AgentConfigVersion"/></item>
/// </list>
/// <para>
/// It runs as a post-configure step so it wins over any in-code <c>Configure</c> call a test host
/// or future caller makes for the non-projected members, and so the bounds enforced by
/// <see cref="CoachOptionsValidator"/> at startup are the only validation these values need.
/// </para>
/// </remarks>
public sealed class CoachPersistenceOptionsSetup : IPostConfigureOptions<CoachPersistenceOptions>
{
    private readonly IOptions<CoachOptions> _coachOptions;

    public CoachPersistenceOptionsSetup(IOptions<CoachOptions> coachOptions)
    {
        _coachOptions = coachOptions;
    }

    public void PostConfigure(string? name, CoachPersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var coach = _coachOptions.Value;
        options.SessionLifetime = coach.SessionExpiry;
        options.RevisionRetention = coach.RevisionRetention;
        options.AgentConfigVersion = coach.AgentConfigVersion;
    }
}
