using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Services.Api;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// What the memory surface is currently able to do.
/// </summary>
public enum CoachMemoryAvailability
{
    /// <summary>Not asked yet.</summary>
    Unknown = 0,

    /// <summary>The route group answered, and the learner can manage what Sam remembers.</summary>
    Available,

    /// <summary>
    /// The route group answered 404. The feature is off, the learner is outside the cohort, or
    /// there is nothing of theirs to find — deliberately indistinguishable.
    /// </summary>
    Unavailable
}

/// <summary>
/// What the learner should be told after a memory write.
/// </summary>
public enum CoachMemoryOutcome
{
    /// <summary>The write landed.</summary>
    Saved = 0,

    /// <summary>The fact changed underneath the learner. The list was re-read.</summary>
    Conflict,

    /// <summary>The value cannot be saved as a preference. The reason is deliberately generic.</summary>
    ValueRejected,

    /// <summary>Saved preferences could not be reached.</summary>
    Unavailable,

    /// <summary>The device is offline.</summary>
    Offline,

    /// <summary>The fact is gone.</summary>
    Gone
}

/// <summary>
/// The learner's view of what Sam remembers: the active facts, the candidates waiting for a
/// decision, and the four writes that change either list.
/// </summary>
/// <remarks>
/// <para>
/// This holds no opinion about what a fact means. It reads what the server says, sends back the
/// version it was shown, and re-reads after every write — because the server rotates its context
/// checkpoint on approve, edit, and forget, and a client that patched its own list would drift
/// from what Sam is actually using.
/// </para>
/// <para>
/// A 404 anywhere collapses to <see cref="CoachMemoryAvailability.Unavailable"/> and hides the
/// whole surface. The server refuses to distinguish "feature off" from "not yours" on purpose,
/// and reconstructing that difference in the client would hand back the probe the server declined
/// to offer.
/// </para>
/// </remarks>
public sealed class CoachMemoryDirectory(ICoachApiClient client, CoachFeatureFlags? flags = null)
{
    private readonly ICoachApiClient _client = client;
    private readonly CoachFeatureFlags _flags = flags ?? new CoachFeatureFlags(client);
    private readonly List<CoachMemoryFactDto> _active = [];
    private readonly List<CoachMemoryFactDto> _candidates = [];

    private bool _loaded;

    /// <summary>Facts Sam is allowed to use, newest first.</summary>
    public IReadOnlyList<CoachMemoryFactDto> Active => _active;

    /// <summary>Facts proposed from something the learner said, waiting for a decision.</summary>
    public IReadOnlyList<CoachMemoryFactDto> Candidates => _candidates;

    /// <summary>Whether the memory surface should be shown at all.</summary>
    public CoachMemoryAvailability Availability { get; private set; } = CoachMemoryAvailability.Unknown;

    /// <summary>True while a read or a write is in flight.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>True when the last read failed because the device is offline.</summary>
    public bool IsOffline { get; private set; }

    /// <summary>The resource key describing the last outcome, or null when there is nothing to say.</summary>
    public string? NoticeKey { get; private set; }

    /// <summary>Raised whenever anything above changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// True when Sam has nothing to draw on. Used to explain the paused state rather than showing
    /// an empty box that reads like a failure.
    /// </summary>
    public bool IsPaused => Availability == CoachMemoryAvailability.Available && _active.Count == 0;

    /// <summary>Reads both lists once. Subsequent calls are no-ops until a refresh is asked for.</summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
        {
            return;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-reads both lists from the server.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        NoticeKey = null;
        Notify();

        try
        {
            await _flags.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

            if (!_flags.IsMemoryAvailable)
            {
                // Saved preferences are off for this learner. The memory routes would answer 404,
                // and asking anyway would tell us nothing the availability response has not
                // already said.
                Availability = CoachMemoryAvailability.Unavailable;
                _active.Clear();
                _candidates.Clear();
                _loaded = true;
                return;
            }

            var active = await _client.ListActiveMemoriesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (active is null)
            {
                // The flag said yes and the route says no. The route wins, and there is nothing
                // else worth asking for.
                Availability = CoachMemoryAvailability.Unavailable;
                _active.Clear();
                _candidates.Clear();
                _loaded = true;
                return;
            }

            var candidates = await _client.ListMemoryCandidatesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            Availability = CoachMemoryAvailability.Available;
            IsOffline = false;

            Replace(_active, active.Items);
            Replace(_candidates, candidates?.Items ?? []);
            _loaded = true;
        }
        catch (HttpRequestException)
        {
            IsOffline = true;
            NoticeKey = "Coach_MemoryOffline";
        }
        catch (CoachApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // The reads answer 404 by returning null, but a write that raced a feature being
            // turned off throws one instead. Treating both as the same answer is what keeps the
            // surface from sitting in an undecided state that neither shows nor hides.
            Availability = CoachMemoryAvailability.Unavailable;
            _active.Clear();
            _candidates.Clear();
            _loaded = true;
        }
        catch (CoachApiException ex)
        {
            NoticeKey = NoticeFor(Classify(ex));
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    /// <summary>
    /// Approves a candidate, optionally replacing the value the learner was shown first.
    /// </summary>
    /// <remarks>
    /// Editing on the way in is one action, not two: approving and then correcting would mean the
    /// unedited value was briefly eligible for a prompt, which is the opposite of what a learner
    /// who edited it asked for.
    /// </remarks>
    public Task<CoachMemoryOutcome> ApproveAsync(
        CoachMemoryFactDto fact,
        CoachMemoryValueDto? editedValue = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact);

        return WriteAsync(
            async ct =>
            {
                var saved = await _client
                    .ApproveMemoryAsync(fact.Id, new CoachMemoryApproveRequest(fact.Version, editedValue), ct)
                    .ConfigureAwait(false);

                return saved is null ? CoachMemoryOutcome.Gone : CoachMemoryOutcome.Saved;
            },
            cancellationToken);
    }

    /// <summary>Declines a candidate. Nothing is remembered and nothing is written to a prompt.</summary>
    public Task<CoachMemoryOutcome> RejectAsync(
        CoachMemoryFactDto fact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact);

        return WriteAsync(
            async ct =>
            {
                await _client
                    .RejectMemoryAsync(fact.Id, new CoachMemoryRejectRequest(fact.Version), ct)
                    .ConfigureAwait(false);

                return CoachMemoryOutcome.Saved;
            },
            cancellationToken);
    }

    /// <summary>Replaces the value of a fact the learner already approved.</summary>
    public Task<CoachMemoryOutcome> EditAsync(
        CoachMemoryFactDto fact,
        CoachMemoryValueDto value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(value);

        return WriteAsync(
            async ct =>
            {
                var saved = await _client
                    .EditMemoryAsync(fact.Id, new CoachMemoryEditRequest(fact.Version, value), ct)
                    .ConfigureAwait(false);

                return saved is null ? CoachMemoryOutcome.Gone : CoachMemoryOutcome.Saved;
            },
            cancellationToken);
    }

    /// <summary>Forgets one fact.</summary>
    public Task<CoachMemoryOutcome> ForgetAsync(
        CoachMemoryFactDto fact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact);

        return WriteAsync(
            async ct =>
            {
                await _client.ForgetMemoryAsync(fact.Id, fact.Version, ct).ConfigureAwait(false);
                return CoachMemoryOutcome.Saved;
            },
            cancellationToken);
    }

    /// <summary>Forgets everything Sam remembers about this learner.</summary>
    /// <returns>The outcome, and how many facts were removed.</returns>
    public async Task<(CoachMemoryOutcome Outcome, int Forgotten)> ForgetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var forgotten = 0;

        var outcome = await WriteAsync(
            async ct =>
            {
                var result = await _client.ForgetAllMemoriesAsync(ct).ConfigureAwait(false);

                if (result is null)
                {
                    return CoachMemoryOutcome.Gone;
                }

                forgotten = result.Forgotten;
                return CoachMemoryOutcome.Saved;
            },
            cancellationToken).ConfigureAwait(false);

        return (outcome, forgotten);
    }

    /// <summary>Clears the last notice once the learner has had a chance to read it.</summary>
    public void ClearNotice()
    {
        if (NoticeKey is null)
        {
            return;
        }

        NoticeKey = null;
        Notify();
    }

    /// <summary>
    /// Forgets everything cached for the signed-in learner. Used when the account changes.
    /// </summary>
    /// <remarks>
    /// Saved preferences are the most personal thing the coach holds — they are sentences a
    /// learner wrote about themselves — so they are cleared on an account boundary for the same
    /// reason the transcript is, and by the same single path. Availability goes with them because
    /// the answer was about a learner who has gone.
    /// </remarks>
    public void Reset()
    {
        _active.Clear();
        _candidates.Clear();
        _loaded = false;
        Availability = CoachMemoryAvailability.Unknown;
        IsBusy = false;
        IsOffline = false;
        NoticeKey = null;
        Notify();
    }

    /// <summary>
    /// Runs one write, then re-reads both lists.
    /// </summary>
    /// <remarks>
    /// The re-read is unconditional, including after a conflict. Approving, editing, and forgetting
    /// all rotate the server's context checkpoint, so the only honest way to show what Sam is using
    /// is to ask again rather than to patch the list locally.
    /// </remarks>
    private async Task<CoachMemoryOutcome> WriteAsync(
        Func<CancellationToken, Task<CoachMemoryOutcome>> write,
        CancellationToken cancellationToken)
    {
        if (Availability == CoachMemoryAvailability.Unavailable)
        {
            return CoachMemoryOutcome.Unavailable;
        }

        IsBusy = true;
        NoticeKey = null;
        Notify();

        CoachMemoryOutcome outcome;

        try
        {
            outcome = await write(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            IsOffline = true;
            outcome = CoachMemoryOutcome.Offline;
        }
        catch (CoachApiException ex)
        {
            outcome = Classify(ex);
        }
        finally
        {
            IsBusy = false;
        }

        if (outcome is not (CoachMemoryOutcome.Offline or CoachMemoryOutcome.Unavailable))
        {
            _loaded = false;
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        NoticeKey = NoticeFor(outcome);
        Notify();
        return outcome;
    }

    /// <summary>
    /// Maps a problem response onto an outcome.
    /// </summary>
    /// <remarks>
    /// A rejected value is classified from the problem type alone. The server never echoes the
    /// offending text and the client never asks for it: the learner is told the value cannot be
    /// saved, not which part of it tripped a screen, because that reply would be a working oracle
    /// for the content policy.
    /// </remarks>
    private static CoachMemoryOutcome Classify(CoachApiException ex) => ex.ProblemType switch
    {
        CoachMemoryProblemTypes.Conflict => CoachMemoryOutcome.Conflict,
        CoachMemoryProblemTypes.ValueRejected => CoachMemoryOutcome.ValueRejected,
        CoachMemoryProblemTypes.Unavailable => CoachMemoryOutcome.Unavailable,
        _ => ex.StatusCode switch
        {
            System.Net.HttpStatusCode.Conflict => CoachMemoryOutcome.Conflict,
            System.Net.HttpStatusCode.UnprocessableEntity => CoachMemoryOutcome.ValueRejected,
            System.Net.HttpStatusCode.ServiceUnavailable => CoachMemoryOutcome.Unavailable,
            System.Net.HttpStatusCode.NotFound => CoachMemoryOutcome.Gone,
            _ => CoachMemoryOutcome.Unavailable
        }
    };

    private static string? NoticeFor(CoachMemoryOutcome outcome) => outcome switch
    {
        CoachMemoryOutcome.Saved => "Coach_MemorySaved",
        CoachMemoryOutcome.Conflict => "Coach_MemoryConflict",
        CoachMemoryOutcome.ValueRejected => "Coach_MemoryValueRejected",
        CoachMemoryOutcome.Unavailable => "Coach_MemoryUnavailable",
        CoachMemoryOutcome.Offline => "Coach_MemoryOffline",
        CoachMemoryOutcome.Gone => "Coach_MemoryGone",
        _ => null
    };

    private static void Replace(List<CoachMemoryFactDto> target, IReadOnlyList<CoachMemoryFactDto> items)
    {
        target.Clear();
        target.AddRange(items);
    }

    private void Notify() => Changed?.Invoke();
}
