using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using SentenceStudio.Contracts.AppOperation;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.UnitTests.AppOperation;

public sealed class ApplicationWireEnumTests
{
    private static IReadOnlyList<Type> ApplicationEnums { get; } = typeof(AppOperationWireSurface).Assembly
        .GetTypes()
        .Where(type => type.IsPublic
            && type.IsEnum
            && type.Namespace == AppOperationWireSurface.Namespace)
        .OrderBy(type => type.Name, StringComparer.Ordinal)
        .ToArray();

    [Fact]
    public void Every_application_enum_declares_a_safe_tolerant_fallback()
    {
        ApplicationEnums.Should().NotBeEmpty();

        foreach (var enumType in ApplicationEnums)
        {
            WireEnumFallback.IsAnnotated(enumType).Should().BeTrue(enumType.Name);
            var fallback = WireEnumFallback.Describe(enumType);

            fallback.Kind.Should().Be(WireEnumFallbackKind.SafeZero, enumType.Name);
            Convert.ToInt64(fallback.Value).Should().Be(0, enumType.Name);
            fallback.MemberName.Should().Be("Unknown", enumType.Name);
            fallback.Rationale.Length.Should().BeGreaterThan(40, enumType.Name);

            enumType.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType
                .Should().Be(typeof(JsonStringEnumConverter), enumType.Name);
        }
    }

    [Fact]
    public void Every_application_enum_has_unique_values_and_a_safe_zero()
    {
        foreach (var enumType in ApplicationEnums)
        {
            var values = Enum.GetValues(enumType)
                .Cast<object>()
                .Select(Convert.ToInt64)
                .ToArray();

            values.Should().OnlyHaveUniqueItems(enumType.Name);
            values.Should().Contain(0, enumType.Name);
            Enum.GetName(enumType, 0).Should().Be("Unknown", enumType.Name);
            values.Should().OnlyContain(value => value >= 0, enumType.Name);
        }
    }

    [Fact]
    public void Every_application_enum_round_trips_by_canonical_name()
    {
        foreach (var enumType in ApplicationEnums)
        {
            foreach (var value in Enum.GetValues(enumType))
            {
                var json = JsonSerializer.Serialize(value, enumType, WireJson.Client);
                json.Should().Be($"\"{Enum.GetName(enumType, value)}\"", $"{enumType.Name}.{value}");

                JsonSerializer.Deserialize(json, enumType, WireJson.Client)
                    .Should().Be(value, $"{enumType.Name}.{value}");
            }
        }
    }

    [Fact]
    public void Every_unknown_application_enum_value_deserializes_to_safe_zero()
    {
        foreach (var enumType in ApplicationEnums)
        {
            JsonSerializer.Deserialize("\"FutureValue\"", enumType, WireJson.Client)
                .Should().Be(Enum.ToObject(enumType, 0), enumType.Name);

            JsonSerializer.Deserialize("987654", enumType, WireJson.Client)
                .Should().Be(Enum.ToObject(enumType, 0), enumType.Name);
        }
    }
}
