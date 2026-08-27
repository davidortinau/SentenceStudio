using System.Collections.Immutable;

namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// The one place that says what a destination is, what it accepts, and what it can change.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a catalogue and not a route string.</b> Before this, Sam had no way to name a screen, and
/// the cheap fix was a string field. A string cannot be checked: nothing stops a path the app does
/// not serve, a query the model composed, or an identifier belonging to a different learner. This
/// table is the checkable version — a closed member, a fixed parameter contract, and a stated
/// consequence, all three asserted by tests rather than by review.
/// </para>
/// <para>
/// <b>Why it lives in Contracts.</b> Both Sam hosts render destinations, and the server decides
/// them. If the parameter contract lived only on the server, the client would carry its own copy
/// and the two would drift; if it lived only on the client, the server could emit a parameter
/// nobody reads. One table, both sides, one census test.
/// </para>
/// <para>
/// <b>Side effects are the destination's ceiling, not its floor.</b> A route is labelled by the
/// most consequential thing it permits. <c>/vocabulary</c> is where a learner reads their words and
/// also where they delete them, so it is <see cref="CoachRouteSideEffect.EditsLearnerData"/>. A
/// disclosure that described the gentlest available action would be technically true and useless.
/// </para>
/// <para>
/// <b>Adding a member is a decision.</b> The census test pins the exact set, so a new screen fails
/// the build until somebody states its parameters and its consequence deliberately.
/// </para>
/// </remarks>
public static class CoachRouteCatalog
{
    /// <summary>Every destination Sam may name, keyed by route.</summary>
    public static readonly ImmutableDictionary<CoachRouteName, CoachRouteDescriptor> All =
        new[]
        {
            new CoachRouteDescriptor(
                CoachRouteName.ActivityLog,
                CoachRouteSideEffect.None,
                [CoachRouteParameterKey.PlanDate]),

            // The learner's own words, and the screen where they can be removed. Labelled by the
            // delete, not by the read — S15 sends people here and the whole point of S15 is that
            // the consequence is stated first.
            new CoachRouteDescriptor(
                CoachRouteName.Vocabulary,
                CoachRouteSideEffect.EditsLearnerData,
                [CoachRouteParameterKey.VocabularyWordId, CoachRouteParameterKey.ResourceId]),

            // EditsLearnerData, not ChangesSettings. Settings is where the learner exports their
            // data and where they delete their whole coach conversation history — Settings.razor
            // carries both. The ceiling is the most consequential permitted effect, and an
            // irreversible deletion of stored conversations outranks a preference change. Calling
            // it ChangesSettings would be technically true about some of the screen and would
            // understate the one action a learner cannot undo.
            new CoachRouteDescriptor(
                CoachRouteName.Settings,
                CoachRouteSideEffect.EditsLearnerData,
                []),

            new CoachRouteDescriptor(
                CoachRouteName.Skills,
                CoachRouteSideEffect.EditsLearnerData,
                [CoachRouteParameterKey.SkillId]),

            new CoachRouteDescriptor(
                CoachRouteName.Writing,
                CoachRouteSideEffect.StartsActivity,
                [CoachRouteParameterKey.ResourceId, CoachRouteParameterKey.SkillId]),

            // Submissions here become a public issue outside the app and the app cannot withdraw
            // one. That is a stronger disclosure than any other route needs, and it is the reason
            // PublishesPublicly exists as its own member rather than folding into EditsLearnerData.
            new CoachRouteDescriptor(
                CoachRouteName.Feedback,
                CoachRouteSideEffect.PublishesPublicly,
                [])
        }.ToImmutableDictionary(descriptor => descriptor.Route);

    /// <summary>
    /// Builds a destination, dropping any parameter the route does not accept.
    /// </summary>
    /// <remarks>
    /// Dropping rather than throwing is deliberate. A caller that attaches a stray parameter has a
    /// bug, but the learner's honest answer should not become a 500 because of it — they still get
    /// the screen, just without the deep link. Callers that need the strict reading can compare the
    /// returned parameter list against what they passed.
    /// </remarks>
    /// <param name="route">The destination. <see cref="CoachRouteName.Unknown"/> yields null.</param>
    /// <param name="parameters">Typed parameters; unaccepted keys are discarded.</param>
    public static CoachDestinationDto? Build(
        CoachRouteName route,
        IEnumerable<CoachRouteParameterDto>? parameters = null)
    {
        if (!All.TryGetValue(route, out var descriptor))
        {
            return null;
        }

        var accepted = parameters is null
            ? []
            : parameters
                .Where(parameter => descriptor.AcceptedParameters.Contains(parameter.Key))
                .Where(IsWellFormed)
                .ToArray();

        return new CoachDestinationDto(descriptor.Route, accepted, descriptor.SideEffect);
    }

    /// <summary>
    /// Whether a parameter's <em>value</em> is one this key is allowed to carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Checking the key was never enough.</b> An accepted key with an unchecked value is a
    /// string concatenated into a URL: <c>사과?answer=apple</c> is a legal
    /// <see cref="CoachRouteParameterKey.VocabularyWordId"/> as far as the key contract is
    /// concerned, and it smuggles both a query string and a target term into a destination the
    /// client will navigate to. The embargo scanner cannot see it either, because the member's
    /// type is <c>string</c> and the offending content arrives at runtime.
    /// </para>
    /// <para>
    /// <b>Allow-list, not deny-list.</b> Each key names the exact shape it accepts, and everything
    /// else is dropped. A deny-list of dangerous characters is a list somebody has to keep
    /// complete; "digits only" and "exactly an ISO calendar date" are complete by construction and
    /// reject a query, a fragment, a path separator, whitespace, a control character, a time
    /// component, and Korean text without naming any of them.
    /// </para>
    /// <para>
    /// Dropped rather than thrown, matching <see cref="Build"/>: a caller that composed a bad
    /// identifier has a bug, and the learner should still get the screen without the deep link
    /// rather than a failed turn.
    /// </para>
    /// </remarks>
    public static bool IsWellFormed(CoachRouteParameterDto parameter)
    {
        if (parameter is null || string.IsNullOrEmpty(parameter.Value))
        {
            return false;
        }

        return parameter.Key switch
        {
            CoachRouteParameterKey.VocabularyWordId => IsServerIdentifier(parameter.Value),
            CoachRouteParameterKey.SkillId => IsServerIdentifier(parameter.Value),
            CoachRouteParameterKey.ResourceId => IsServerIdentifier(parameter.Value),
            CoachRouteParameterKey.PlanDate => IsCalendarDate(parameter.Value),

            // Unknown, and any member added later without a rule here. Refusing by default means a
            // new key cannot ship unvalidated; it ships carrying nothing until somebody states its
            // shape.
            _ => false
        };
    }

    /// <summary>A positive integral server identifier, and nothing else.</summary>
    /// <remarks>
    /// No sign, no separators, no leading or trailing space, no exponent. Length-bounded before
    /// parsing so a very long digit string cannot be used to make the check expensive. Zero is
    /// refused because no row carries it and a zero deep link would land the learner nowhere.
    /// </remarks>
    private static bool IsServerIdentifier(string value)
    {
        if (value.Length is 0 or > 19)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return long.TryParse(
                   value,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var parsed)
               && parsed > 0;
    }

    /// <summary>An ISO calendar date, with no time and no zone.</summary>
    /// <remarks>
    /// Exact-format parsing, so <c>2026-08-22T00:00:00Z</c> is refused rather than silently
    /// truncated. A plan date is a learner-local calendar day; attaching an instant to it would
    /// invite the client to re-interpret it in another zone and land on the wrong day.
    /// </remarks>
    private static bool IsCalendarDate(string value) =>
        value.Length == 10
        && DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out _);
}

/// <summary>One catalogue row.</summary>
/// <param name="Route">The screen.</param>
/// <param name="SideEffect">The most consequential thing the learner can do there.</param>
/// <param name="AcceptedParameters">
/// The typed parameters this destination reads. May be empty; a destination with no parameters is
/// a screen link, which is a complete answer for most limitations.
/// </param>
public sealed record CoachRouteDescriptor(
    CoachRouteName Route,
    CoachRouteSideEffect SideEffect,
    ImmutableArray<CoachRouteParameterKey> AcceptedParameters);
