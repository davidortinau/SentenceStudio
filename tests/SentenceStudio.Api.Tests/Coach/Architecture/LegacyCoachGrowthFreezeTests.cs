using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.Architecture;

public sealed class LegacyCoachGrowthFreezeTests : IClassFixture<CoachApiFactory>
{
    private readonly CoachApiFactory _factory;

    private static readonly string[] FrozenToolNames =
    [
        "get_learner_profile_summary",
        "get_practice_balance",
        "get_vocabulary_due_summary",
        "get_resource_catalog",
        "preview_practice_plan",
        "get_practice_history_summary",
        "list_user_vocabularies",
        "get_vocabulary_word_detail",
        "get_skill_list",
        "get_skill_detail",
        "get_learning_resource_list",
        "get_learning_resource_detail",
        "get_current_profile_summary",
        "get_learner_settings_summary",
        "get_current_plan_summary",
        "propose_vocabulary_entry",
        "propose_vocabulary_edit",
        "propose_vocabulary_link",
        "propose_vocabulary_removal",
        "propose_skill_entry",
        "propose_skill_edit",
        "propose_skill_archive",
        "propose_resource_entry",
        "propose_resource_edit",
        "propose_resource_removal",
        "propose_preference_change",
        "propose_youtube_import"
    ];

    private static readonly string[] FrozenWriteHandlerToolNames =
    [
        "propose_vocabulary_entry",
        "propose_vocabulary_edit",
        "propose_vocabulary_link",
        "propose_vocabulary_removal",
        "propose_skill_entry",
        "propose_skill_edit",
        "propose_skill_archive",
        "propose_resource_entry",
        "propose_resource_edit",
        "propose_resource_removal",
        "propose_preference_change",
        "propose_youtube_import"
    ];

    private static readonly string[] FrozenCapabilityNames =
    [
        "get_learner_profile_summary",
        "get_practice_balance",
        "get_vocabulary_due_summary",
        "get_resource_catalog",
        "preview_practice_plan",
        "get_practice_history_summary",
        "list_user_vocabularies",
        "get_vocabulary_word_detail",
        "get_skill_list",
        "get_skill_detail",
        "get_learning_resource_list",
        "get_learning_resource_detail",
        "get_current_profile_summary",
        "get_learner_settings_summary",
        "get_current_plan_summary",
        "propose_vocabulary_entry",
        "propose_vocabulary_edit",
        "propose_vocabulary_link",
        "propose_vocabulary_removal",
        "propose_skill_entry",
        "propose_skill_edit",
        "propose_skill_archive",
        "propose_resource_entry",
        "propose_resource_edit",
        "propose_resource_removal",
        "propose_preference_change",
        "propose_youtube_import",
        "get_theme_metadata"
    ];

    private static readonly string[] FrozenIntentMembers =
    [
        "NoChange=0",
        "DirectConstraintChange=1",
        "SuggestConstraintChange=2",
        "AcceptPendingSuggestion=3",
        "RejectPendingSuggestion=4",
        "AskClarification=5",
        "OffTopic=6",
        "PedagogicalAnswer=7"
    ];

    private static readonly string[] FrozenMutationEndpoints =
    [
        "DELETE /api/v1/coach/conversations/{conversationId}",
        "DELETE /api/v1/coach/memories",
        "DELETE /api/v1/coach/memories/{factId}",
        "DELETE /api/v1/coach/sessions/{sessionId}",
        "PATCH /api/v1/coach/conversations/{conversationId}",
        "POST /api/v1/coach/conversations",
        "POST /api/v1/coach/conversations/{conversationId}/operations/{operationId}/cancel",
        "POST /api/v1/coach/conversations/{conversationId}/responses/{messageId}/report",
        "POST /api/v1/coach/conversations/{conversationId}/turns",
        "POST /api/v1/coach/conversations/{conversationId}/writes/{operationId}/accept",
        "POST /api/v1/coach/conversations/{conversationId}/writes/{operationId}/confirm",
        "POST /api/v1/coach/conversations/{conversationId}/writes/{operationId}/confirmation",
        "POST /api/v1/coach/conversations/{conversationId}/writes/{operationId}/reject",
        "POST /api/v1/coach/conversations/{conversationId}/writes/{operationId}/undo",
        "POST /api/v1/coach/memories/{factId}/approve",
        "POST /api/v1/coach/memories/{factId}/reject",
        "POST /api/v1/coach/operator/opportunities/{id}/evidence",
        "POST /api/v1/coach/operator/opportunities/{id}/review",
        "POST /api/v1/coach/sessions",
        "POST /api/v1/coach/sessions/{sessionId}/cancel",
        "POST /api/v1/coach/sessions/{sessionId}/suggestions/{suggestionId}/accept",
        "POST /api/v1/coach/sessions/{sessionId}/suggestions/{suggestionId}/reject",
        "POST /api/v1/coach/sessions/{sessionId}/turns",
        "POST /api/v1/coach/sessions/{sessionId}/undo",
        "PUT /api/v1/coach/memories/{factId}"
    ];

    public LegacyCoachGrowthFreezeTests(CoachApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Registered_tool_surface_matches_the_frozen_legacy_baseline()
    {
        var registry = new CoachToolRegistry(new CoachOptions());
        var actual = registry.All.Select(registration => registration.Name);

        AssertFrozenContract("registered tool surface", FrozenToolNames, actual);
    }

    [Fact]
    public void Registered_write_handlers_match_the_frozen_legacy_baseline()
    {
        var services = new ServiceCollection();
        services.AddCoachReadOnlyTools();

        var registrations = services
            .Where(descriptor => descriptor.ServiceType == typeof(ICoachWriteHandler))
            .ToArray();

        using var scope = _factory.Services.CreateScope();
        var actualToolNames = scope.ServiceProvider
            .GetServices<ICoachWriteHandler>()
            .Select(handler => handler.ToolName);

        AssertFrozenWriteHandlers(
            actualToolNames,
            registrations.Select(descriptor => descriptor.Lifetime));
    }

    [Fact]
    public void Plan_shaped_intent_members_keep_their_stored_names_and_ordinals()
    {
        var actual = Enum.GetValues<CoachIntentKind>()
            .Select(value => $"{value}={(int)value}");

        AssertFrozenContract("plan-shaped intent members", FrozenIntentMembers, actual);
    }

    [Fact]
    public void External_mutation_surface_matches_the_frozen_legacy_baseline()
    {
        var endpointSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var actual = endpointSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => (Endpoint: endpoint, Route: NormalizeRoute(endpoint)))
            .Where(entry => IsCoachRoute(entry.Route))
            .SelectMany(endpoint =>
                endpoint.Endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                    .Where(method => !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
                    .Select(method => $"{method.ToUpperInvariant()} {endpoint.Route}")
                ?? [])
            .ToArray();

        AssertFrozenContract("external mutation endpoints", FrozenMutationEndpoints, actual);
    }

    [Fact]
    public void Production_capability_manifest_matches_the_frozen_legacy_union()
    {
        var manifest = _factory.Services.GetRequiredService<ICoachCapabilityManifest>();

        AssertFrozenContract(
            "production capability manifest",
            FrozenCapabilityNames,
            manifest.All.Select(capability => capability.Name));
    }

    [Fact]
    public void Presentation_capability_remains_absent_and_inert()
    {
        var actual = CoachCapabilityDeclarations.All.Select(capability =>
            $"{capability.Name}|tool={capability.IsToolBacked}|effect={capability.EffectClass}"
            + $"|ceiling={capability.MaxAvailability}|stage={capability.RequiredStage}");

        AssertFrozenContract(
            "declared presentation capabilities",
            [
                "get_theme_metadata|tool=False|effect=PresentationState"
                + "|ceiling=AbsentUnimplemented|stage=Presentation"
            ],
            actual);

        new CoachToolRegistry(new CoachOptions())
            .IsRegistered(CoachCapabilityDeclarations.ThemeMetadataCapabilityName)
            .Should().BeFalse("the absent presentation declaration must not become callable");
    }

    [Fact]
    public void Growth_guard_rejects_a_synthetic_extra_legacy_member()
    {
        var expandedTools = FrozenToolNames.Append("synthetic_extra_legacy_capability");
        var expandedIntents = FrozenIntentMembers.Append("SyntheticIntent=8");

        var toolFailure = Record.Exception(
            () => AssertFrozenContract("negative tool control", FrozenToolNames, expandedTools));
        var intentFailure = Record.Exception(
            () => AssertFrozenContract("negative intent control", FrozenIntentMembers, expandedIntents));

        toolFailure.Should().NotBeNull();
        toolFailure!.Message.Should().Contain("synthetic_extra_legacy_capability");
        intentFailure.Should().NotBeNull();
        intentFailure!.Message.Should().Contain("SyntheticIntent=8");
    }

    [Fact]
    public void Write_handler_guard_rejects_key_growth_and_lifetime_drift()
    {
        var addedKey = Record.Exception(() => AssertFrozenWriteHandlers(
            FrozenWriteHandlerToolNames.Append("synthetic_extra_write"),
            Enumerable.Repeat(ServiceLifetime.Scoped, FrozenWriteHandlerToolNames.Length + 1)));
        var changedLifetime = Record.Exception(() => AssertFrozenWriteHandlers(
            FrozenWriteHandlerToolNames,
            Enumerable.Repeat(ServiceLifetime.Scoped, FrozenWriteHandlerToolNames.Length - 1)
                .Append(ServiceLifetime.Singleton)));

        addedKey.Should().NotBeNull();
        changedLifetime.Should().NotBeNull();
    }

    [Fact]
    public void Endpoint_guard_rejects_a_synthetic_mutation_alias()
    {
        var expanded = FrozenMutationEndpoints.Append(
            "POST /api/v1/coach/sessions/{sessionId}/synthetic-retry");

        Record.Exception(
                () => AssertFrozenContract("negative endpoint control", FrozenMutationEndpoints, expanded))
            .Should().NotBeNull();
    }

    [Fact]
    public void Manifest_guard_rejects_manifest_only_growth_and_registry_loss()
    {
        var addedDeclaration = FrozenCapabilityNames.Append("synthetic_manifest_only_descriptor");
        var droppedRegistryDescriptor = FrozenCapabilityNames
            .Where(name => name != "get_current_plan_summary");

        Record.Exception(
                () => AssertFrozenContract(
                    "negative manifest growth control",
                    FrozenCapabilityNames,
                    addedDeclaration))
            .Should().NotBeNull();
        Record.Exception(
                () => AssertFrozenContract(
                    "negative manifest loss control",
                    FrozenCapabilityNames,
                    droppedRegistryDescriptor))
            .Should().NotBeNull();
    }

    private static void AssertFrozenWriteHandlers(
        IEnumerable<string> toolNames,
        IEnumerable<ServiceLifetime> lifetimes)
    {
        var actualToolNames = toolNames.ToArray();
        var actualLifetimes = lifetimes.ToArray();

        AssertFrozenContract(
            "registered write-handler tool keys",
            FrozenWriteHandlerToolNames,
            actualToolNames);

        Assert.Equal(actualToolNames.Length, actualLifetimes.Length);
        Assert.True(
            actualLifetimes.All(lifetime => lifetime == ServiceLifetime.Scoped),
            $"write-handler lifetime changed. Actual: [{string.Join(", ", actualLifetimes)}].");
    }

    private static string NormalizeRoute(RouteEndpoint endpoint)
    {
        var raw = endpoint.RoutePattern.RawText
            ?? throw new InvalidOperationException("A frozen endpoint has no route pattern.");
        var normalized = $"/{raw.Trim().Trim('/')}";
        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }

    private static bool IsCoachRoute(string route)
    {
        const string coachPrefix = "/api/v1/coach";
        return route.Equals(coachPrefix, StringComparison.Ordinal)
            || route.StartsWith($"{coachPrefix}/", StringComparison.Ordinal);
    }

    private static void AssertFrozenContract(
        string contract,
        IEnumerable<string> expected,
        IEnumerable<string> actual)
    {
        var expectedEntries = expected.Order(StringComparer.Ordinal).ToArray();
        var actualEntries = actual.Order(StringComparer.Ordinal).ToArray();
        var missing = expectedEntries.Except(actualEntries, StringComparer.Ordinal).ToArray();
        var added = actualEntries.Except(expectedEntries, StringComparer.Ordinal).ToArray();

        Assert.True(
            expectedEntries.SequenceEqual(actualEntries, StringComparer.Ordinal),
            $"{contract} changed. Missing: [{string.Join(", ", missing)}]. "
            + $"Added: [{string.Join(", ", added)}].");
    }
}
