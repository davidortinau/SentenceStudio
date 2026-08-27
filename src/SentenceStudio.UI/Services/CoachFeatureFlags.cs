using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// The one place the coach's per-feature availability flags are read and cached for a circuit.
/// </summary>
/// <remarks>
/// <para>
/// Durable history and saved preferences are announced by
/// <see cref="CoachAvailabilityResponse.IsDurableHistoryAvailable"/> and
/// <see cref="CoachAvailabilityResponse.IsMemoryAvailable"/>. Asking the flag is strictly better
/// than the probe it replaced: the feature routes answer 404 for both "this is switched off" and
/// "this is not your data", so a 404 could never tell a learner with no history from a learner
/// whose history feature is disabled.
/// </para>
/// <para>
/// Both flags default to <see langword="false"/>, and that default is load-bearing rather than
/// merely cautious. A server old enough not to send the fields is a server that does not have the
/// features, so deserializing the absent field to false is the correct answer, not a compatibility
/// gap to work around. The same default covers an availability call that failed outright: a
/// feature we cannot confirm stays hidden, which is the same stance the dashboard entry point
/// already takes.
/// </para>
/// <para>
/// A flag saying yes is still not a promise. The route can answer 404 anyway — a feature switched
/// off between the availability read and the list call, or a request that resolves no owner — so
/// callers keep treating a 404 as authoritative and fall back to the session-only experience.
/// The flag decides whether to ask at all; the route decides what is actually there.
/// </para>
/// </remarks>
public sealed class CoachFeatureFlags(ICoachApiClient client)
{
    private readonly ICoachApiClient _client = client;
    private CoachAvailabilityResponse? _availability;

    /// <summary>Raised after availability is first loaded or applied, so layout hosts can re-render.</summary>
    public event Action? Loaded;

    /// <summary>True once availability has been read or published at least once.</summary>
    public bool HasLoaded => _availability is not null;

    /// <summary>
    /// True when the server says durable conversation history is usable. False until availability
    /// is known.
    /// </summary>
    public bool IsDurableHistoryAvailable => _availability?.IsDurableHistoryAvailable ?? false;

    /// <summary>
    /// True when the server says saved learner preferences are usable. False until availability is
    /// known.
    /// </summary>
    public bool IsMemoryAvailable => _availability?.IsMemoryAvailable ?? false;

    /// <summary>
    /// True when the server says the Sam persistent overlay UX is enabled. False until
    /// availability is known.
    /// </summary>
    public bool IsSamOverlayAvailable => _availability?.IsSamOverlayAvailable ?? false;

    /// <summary>
    /// True when the server says Sam may propose changes and the client may render approval
    /// controls. False until availability is known, and false on any server that does not send
    /// the field.
    /// </summary>
    public bool IsSamWriteAvailable => _availability?.IsSamWriteAvailable ?? false;

    /// <summary>
    /// Records an availability response that was already read elsewhere, so the workspace and the
    /// two directories agree without any of them asking twice.
    /// </summary>
    public void Apply(CoachAvailabilityResponse availability)
    {
        _availability = availability;
        Loaded?.Invoke();
    }

    /// <summary>Reads availability once per circuit if nobody has yet.</summary>
    /// <remarks>
    /// A failed read leaves both flags false rather than throwing. Availability is a gate on
    /// optional surfaces, and a gate that can crash the page it guards is worse than a gate that
    /// closes.
    /// </remarks>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_availability is not null)
        {
            return;
        }

        try
        {
            _availability = await _client.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _availability = new CoachAvailabilityResponse
            {
                IsAvailable = false,
                State = CoachAvailabilityState.Disabled
            };
        }

        Loaded?.Invoke();
    }

    /// <summary>Forgets the cached answer so the next read asks again.</summary>
    public void Reset() => _availability = null;
}
