using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Contracts.AppOperation;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.UnitTests.AppOperation;

public sealed class ClientActionContractTests
{
    [Fact]
    public void Every_valid_action_has_exactly_its_selected_payload()
    {
        ValidActions().Should().OnlyContain(action => action.IsValid());
    }

    [Fact]
    public void Unknown_missing_and_mismatched_action_payloads_are_inert()
    {
        ClientAction[] invalid =
        [
            new() { Kind = ClientActionKind.Unknown },
            new() { Kind = ClientActionKind.LaunchActivity },
            new()
            {
                Kind = ClientActionKind.LaunchActivity,
                LaunchActivity = new()
                {
                    Activity = ApplicationActivityKind.Unknown,
                    LaunchReferenceId = "launch-1"
                }
            },
            new()
            {
                Kind = ClientActionKind.LaunchActivity,
                LaunchActivity = new()
                {
                    Activity = ApplicationActivityKind.VocabularyReview,
                    LaunchReferenceId = "launch-1"
                },
                RefreshDomainView = new() { View = ApplicationDomainViewKind.Progress }
            },
            new()
            {
                Kind = ClientActionKind.RefreshDomainView,
                ReloadLearnerContext = new() { Context = ApplicationLearnerContextKind.ActiveLanguage }
            },
            new()
            {
                Kind = ClientActionKind.ReloadLearnerContext,
                ReloadLearnerContext = new() { Context = ApplicationLearnerContextKind.Unknown }
            }
        ];

        invalid.Should().OnlyContain(action => !action.IsValid());
    }

    [Fact]
    public void An_unknown_wire_action_kind_cannot_become_actionable()
    {
        const string json = """
            {
              "kind": "OpenArbitraryDestination",
              "launchActivity": {
                "activity": "VocabularyReview",
                "launchReferenceId": "launch-1"
              }
            }
            """;

        var action = JsonSerializer.Deserialize<ClientAction>(json, WireJson.Client);

        action.Should().NotBeNull();
        action!.Kind.Should().Be(ClientActionKind.Unknown);
        action.IsValid().Should().BeFalse();
    }

    [Fact]
    public void Undefined_numeric_action_kinds_are_inert_for_every_payload_combination()
    {
        ClientAction[] invalid =
        [
            new() { Kind = (ClientActionKind)987654 },
            new()
            {
                Kind = (ClientActionKind)987654,
                LaunchActivity = new()
                {
                    Activity = ApplicationActivityKind.VocabularyReview,
                    LaunchReferenceId = "launch-1"
                }
            },
            new()
            {
                Kind = (ClientActionKind)987654,
                RefreshDomainView = new() { View = ApplicationDomainViewKind.Progress }
            },
            new()
            {
                Kind = (ClientActionKind)987654,
                ReloadLearnerContext = new() { Context = ApplicationLearnerContextKind.ActiveLanguage }
            }
        ];

        invalid.Should().OnlyContain(action => !action.IsValid());
    }

    [Fact]
    public void Undefined_numeric_payload_enums_are_inert_for_every_payload_variant()
    {
        ClientAction[] invalid =
        [
            new()
            {
                Kind = ClientActionKind.LaunchActivity,
                LaunchActivity = new()
                {
                    Activity = (ApplicationActivityKind)987654,
                    LaunchReferenceId = "launch-1"
                }
            },
            new()
            {
                Kind = ClientActionKind.RefreshDomainView,
                RefreshDomainView = new() { View = (ApplicationDomainViewKind)987654 }
            },
            new()
            {
                Kind = ClientActionKind.ReloadLearnerContext,
                ReloadLearnerContext = new() { Context = (ApplicationLearnerContextKind)987654 }
            }
        ];

        invalid.Should().OnlyContain(action => !action.IsValid());
    }

    [Theory]
    [MemberData(nameof(UndefinedActionKindJson))]
    public void Undefined_numeric_json_action_kinds_are_inert_for_every_payload_combination(string json)
    {
        var action = JsonSerializer.Deserialize<ClientAction>(json, WireJson.Client);

        action.Should().NotBeNull();
        action!.IsValid().Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(UndefinedPayloadJson))]
    public void Undefined_numeric_json_payload_values_are_inert_for_every_payload_variant(string json)
    {
        var action = JsonSerializer.Deserialize<ClientAction>(json, WireJson.Client);

        action.Should().NotBeNull();
        action!.IsValid().Should().BeFalse();
    }

    [Fact]
    public void A_launch_action_round_trips_without_a_route_or_arbitrary_payload()
    {
        var original = ValidActions().Single(action => action.Kind == ClientActionKind.LaunchActivity);

        var json = JsonSerializer.Serialize(original, WireJson.Client);
        var restored = JsonSerializer.Deserialize<ClientAction>(json, WireJson.Client);

        json.Contains("route", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        json.Contains("url", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        restored.Should().BeEquivalentTo(original);
        restored!.IsValid().Should().BeTrue();
    }

    private static IReadOnlyList<ClientAction> ValidActions() =>
    [
        new() { Kind = ClientActionKind.None },
        new()
        {
            Kind = ClientActionKind.LaunchActivity,
            LaunchActivity = new()
            {
                Activity = ApplicationActivityKind.VocabularyReview,
                LaunchReferenceId = "launch-1"
            }
        },
        new()
        {
            Kind = ClientActionKind.RefreshDomainView,
            RefreshDomainView = new() { View = ApplicationDomainViewKind.Progress }
        },
        new()
        {
            Kind = ClientActionKind.ReloadLearnerContext,
            ReloadLearnerContext = new() { Context = ApplicationLearnerContextKind.ActiveLanguage }
        }
    ];

    public static TheoryData<string> UndefinedActionKindJson => new()
    {
        """{"kind":987654}""",
        """
        {
          "kind": 987654,
          "launchActivity": {
            "activity": "VocabularyReview",
            "launchReferenceId": "launch-1"
          }
        }
        """,
        """
        {
          "kind": 987654,
          "refreshDomainView": {
            "view": "Progress"
          }
        }
        """,
        """
        {
          "kind": 987654,
          "reloadLearnerContext": {
            "context": "ActiveLanguage"
          }
        }
        """
    };

    public static TheoryData<string> UndefinedPayloadJson => new()
    {
        """
        {
          "kind": "LaunchActivity",
          "launchActivity": {
            "activity": 987654,
            "launchReferenceId": "launch-1"
          }
        }
        """,
        """
        {
          "kind": "RefreshDomainView",
          "refreshDomainView": {
            "view": 987654
          }
        }
        """,
        """
        {
          "kind": "ReloadLearnerContext",
          "reloadLearnerContext": {
            "context": 987654
          }
        }
        """
    };
}
