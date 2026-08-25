using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.UnitTests.Coach;

/// <summary>
/// The action space: the closed set of screens Sam may name, and the terms it may name them on.
/// </summary>
/// <remarks>
/// <para>
/// Before this catalogue there was no destination code at all, and the cheap fix would have been a
/// route string. These tests are the reason the cheap fix was not taken: every property below is
/// one a string could not have — a fixed census, a parameter contract, a consequence that must be
/// stated, and an inability to carry a path or a query.
/// </para>
/// <para>
/// The census test is deliberately exact rather than a lower bound. A new screen appearing in the
/// catalogue is a decision about what Sam is allowed to point a learner at, and a decision that
/// slides in under <c>Should().HaveCountGreaterThan</c> is a decision nobody made.
/// </para>
/// </remarks>
public sealed class CoachRouteCatalogTests
{
    /// <summary>
    /// The six screens, pinned. Adding a member here without adding it below fails the build.
    /// </summary>
    private static readonly CoachRouteName[] ExpectedRoutes =
    [
        CoachRouteName.ActivityLog,
        CoachRouteName.Vocabulary,
        CoachRouteName.Settings,
        CoachRouteName.Skills,
        CoachRouteName.Writing,
        CoachRouteName.Feedback
    ];

    [Fact]
    public void Catalog_holds_exactly_the_approved_screens()
    {
        CoachRouteCatalog.All.Keys.Should().BeEquivalentTo(
            ExpectedRoutes,
            "the catalogue is the whole action space, and a screen Sam may name is a decision with "
            + "a side-effect disclosure attached rather than an entry somebody added in passing");
    }

    [Fact]
    public void Every_route_member_except_unknown_is_in_the_catalog()
    {
        var members = Enum.GetValues<CoachRouteName>()
            .Where(route => route != CoachRouteName.Unknown)
            .ToArray();

        members.Should().NotBeEmpty("the census below is vacuous against an empty enum");

        CoachRouteCatalog.All.Keys.Should().BeEquivalentTo(
            members,
            "a route the wire can express but the catalogue cannot describe would reach a client "
            + "with no parameter contract and no consequence stated");
    }

    [Fact]
    public void Unknown_is_not_a_destination()
    {
        CoachRouteCatalog.All.Should().NotContainKey(
            CoachRouteName.Unknown,
            "Unknown is the tolerant-reader fallback, not a screen; a catalogue row for it would "
            + "turn an unrecognised value into a navigable link");

        CoachRouteCatalog.Build(CoachRouteName.Unknown).Should().BeNull();
    }

    [Fact]
    public void Every_descriptor_states_a_real_side_effect()
    {
        CoachRouteCatalog.All.Values.Should().OnlyContain(
            descriptor => descriptor.SideEffect != CoachRouteSideEffect.Unknown,
            "a destination whose consequence is unstated is the exact case the disclosure exists "
            + "for, and the client renders Unknown as 'consequences not stated' — a server-side "
            + "Unknown would ship that non-answer to every learner");
    }

    [Fact]
    public void Every_descriptor_declares_its_parameter_contract()
    {
        CoachRouteCatalog.All.Should().NotBeEmpty();

        foreach (var (route, descriptor) in CoachRouteCatalog.All)
        {
            descriptor.AcceptedParameters.Should().NotBeNull(
                "{0} must declare a contract even when it is empty; a null contract is an "
                + "unbounded one",
                route);

            descriptor.AcceptedParameters.Should().NotContain(
                CoachRouteParameterKey.Unknown,
                "{0} may not accept the fallback key, which no producer can meaningfully fill",
                route);

            descriptor.AcceptedParameters.Should().OnlyHaveUniqueItems(
                "a duplicated key in {0}'s contract makes the accept/drop decision order-dependent",
                route);
        }
    }

    [Fact]
    public void Descriptor_route_matches_its_key()
    {
        foreach (var (route, descriptor) in CoachRouteCatalog.All)
        {
            descriptor.Route.Should().Be(
                route,
                "a mismatched key silently makes a lookup return another screen's parameters and "
                + "another screen's disclosure");
        }
    }

    /// <summary>
    /// The consequence a screen is labelled with must be its ceiling, not its floor.
    /// </summary>
    /// <remarks>
    /// Pinned per route rather than checked generically, because "is this the most consequential
    /// thing the screen permits" is a judgement no assertion can make — but a change to the
    /// judgement should be visible in a diff. Vocabulary is the one that matters: it is where S15
    /// sends people, and it is where they can delete their words.
    /// </remarks>
    [Theory]
    [InlineData(CoachRouteName.ActivityLog, CoachRouteSideEffect.None)]
    [InlineData(CoachRouteName.Vocabulary, CoachRouteSideEffect.EditsLearnerData)]
    // EditsLearnerData, not ChangesSettings: Settings.razor deletes the learner's whole coach
    // conversation history as well as changing preferences, and the label is the ceiling.
    [InlineData(CoachRouteName.Settings, CoachRouteSideEffect.EditsLearnerData)]
    [InlineData(CoachRouteName.Skills, CoachRouteSideEffect.EditsLearnerData)]
    [InlineData(CoachRouteName.Writing, CoachRouteSideEffect.StartsActivity)]
    [InlineData(CoachRouteName.Feedback, CoachRouteSideEffect.PublishesPublicly)]
    public void Side_effect_is_pinned_per_route(CoachRouteName route, CoachRouteSideEffect expected)
    {
        CoachRouteCatalog.All[route].SideEffect.Should().Be(expected);
    }

    [Fact]
    public void Feedback_discloses_public_publication()
    {
        CoachRouteCatalog.All[CoachRouteName.Feedback].SideEffect.Should().Be(
            CoachRouteSideEffect.PublishesPublicly,
            "a feedback submission becomes a public issue the app cannot withdraw, and a learner "
            + "sent there by Sam without that disclosure has been set up to publish something they "
            + "believed was private");
    }

    // ── The parameter contract is enforced, not advisory ─────────────────────

    [Fact]
    public void Build_keeps_accepted_parameters()
    {
        var destination = CoachRouteCatalog.Build(
            CoachRouteName.Vocabulary,
            [new CoachRouteParameterDto(CoachRouteParameterKey.VocabularyWordId, "8412")]);

        destination.Should().NotBeNull();
        destination!.Parameters.Should().ContainSingle()
            .Which.Key.Should().Be(CoachRouteParameterKey.VocabularyWordId);
    }

    [Fact]
    public void Build_drops_parameters_the_route_does_not_accept()
    {
        var destination = CoachRouteCatalog.Build(
            CoachRouteName.Settings,
            [new CoachRouteParameterDto(CoachRouteParameterKey.VocabularyWordId, "8412")]);

        destination.Should().NotBeNull();
        destination!.Parameters.Should().BeEmpty(
            "settings reads no vocabulary identifier, and passing one through would put a "
            + "learner-owned id into a link that ignores it");
    }

    [Fact]
    public void Build_drops_the_unknown_key()
    {
        var destination = CoachRouteCatalog.Build(
            CoachRouteName.Vocabulary,
            [new CoachRouteParameterDto(CoachRouteParameterKey.Unknown, "anything")]);

        destination!.Parameters.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_drops_blank_values(string value)
    {
        var destination = CoachRouteCatalog.Build(
            CoachRouteName.Vocabulary,
            [new CoachRouteParameterDto(CoachRouteParameterKey.VocabularyWordId, value)]);

        destination!.Parameters.Should().BeEmpty(
            "a blank identifier is not a deep link, it is a malformed one");
    }

    [Fact]
    public void Build_without_parameters_yields_a_screen_link()
    {
        var destination = CoachRouteCatalog.Build(CoachRouteName.Feedback);

        destination.Should().NotBeNull();
        destination!.Parameters.Should().BeEmpty();
        destination.Route.Should().Be(CoachRouteName.Feedback);
    }

    // ── No free-form route can exist ─────────────────────────────────────────

    /// <summary>
    /// The structural guard. A string property on any of these shapes is how a path, a query, or a
    /// model-authored URL would get to a client, so the only string permitted anywhere in the
    /// destination graph is a typed parameter's value.
    /// </summary>
    [Fact]
    public void No_destination_shape_carries_free_form_text()
    {
        Type[] shapes = [typeof(CoachDestinationDto), typeof(CoachRouteDescriptor)];

        foreach (var shape in shapes)
        {
            var stringProperties = shape
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType == typeof(string))
                .Select(property => property.Name)
                .ToArray();

            stringProperties.Should().BeEmpty(
                "{0} must not expose a string: a route, a path, a query or a label written by the "
                + "model would all arrive through one, and the whole catalogue exists so that a "
                + "destination is a closed member instead. Offending: {1}",
                shape.Name,
                string.Join(", ", stringProperties));
        }
    }

    /// <summary>
    /// The one permitted string, bounded by the key beside it.
    /// </summary>
    [Fact]
    public void The_only_string_is_a_typed_parameter_value()
    {
        var properties = typeof(CoachRouteParameterDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .ToArray();

        properties.Should().BeEquivalentTo(
            [nameof(CoachRouteParameterDto.Value)],
            "a parameter value is an identifier or a date whose meaning is fixed by its key; any "
            + "other string on this shape would be uncontracted text");
    }

    [Fact]
    public void Serialized_destination_carries_no_path_or_query()
    {
        var destination = CoachRouteCatalog.Build(
            CoachRouteName.Vocabulary,
            [new CoachRouteParameterDto(CoachRouteParameterKey.ResourceId, "77")]);

        var json = JsonSerializer.Serialize(destination, WireJson.Client);

        json.Should().NotContain("/", "a wire destination that contains a path is a route string");
        json.Should().NotContain("?", "a wire destination that contains a query is a route string");
        json.Should().Contain("Vocabulary");
    }

    [Fact]
    public void Catalog_is_immutable()
    {
        CoachRouteCatalog.All.Should().BeAssignableTo<ImmutableDictionary<CoachRouteName, CoachRouteDescriptor>>(
            "a mutable catalogue is a catalogue one request can widen for every other request");
    }
}
