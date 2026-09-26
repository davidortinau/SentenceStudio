using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Contracts.AppOperation;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.UnitTests.AppOperation;

public sealed class ApplicationCapabilityContractTests
{
    [Fact]
    public void Well_formed_identity_reference_and_requests_are_valid()
    {
        var capability = ValidReference();

        capability.Capability.IsValid().Should().BeTrue();
        capability.Version.IsValid().Should().BeTrue();
        capability.IsValid().Should().BeTrue();
        new ApplicationCapabilityQueryRequest { Capability = capability }.IsValid().Should().BeTrue();
        new ApplicationCapabilityCommandRequest { Capability = capability }.IsValid().Should().BeTrue();
    }

    [Theory]
    [InlineData(null, "vocabulary")]
    [InlineData("", "vocabulary")]
    [InlineData("   ", "vocabulary")]
    [InlineData("vocabulary.catalog.read", null)]
    [InlineData("vocabulary.catalog.read", "")]
    [InlineData("vocabulary.catalog.read", "\t")]
    public void Identity_requires_nonblank_code_and_family(string? code, string? family)
    {
        var identity = new ApplicationCapabilityIdentity
        {
            Code = code!,
            Family = family!
        };

        identity.IsValid().Should().BeFalse();
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    public void Capability_version_requires_a_positive_value(int value)
    {
        new ApplicationCapabilityVersion { Value = value }.IsValid().Should().BeFalse();
    }

    [Fact]
    public void Reference_rejects_null_or_malformed_nested_shapes()
    {
        ApplicationCapabilityReference[] invalid =
        [
            new() { Capability = null!, Version = new() { Value = 1 } },
            new()
            {
                Capability = new() { Code = "vocabulary.catalog.read", Family = "vocabulary" },
                Version = null!
            },
            new()
            {
                Capability = new() { Code = " ", Family = "vocabulary" },
                Version = new() { Value = 1 }
            },
            new()
            {
                Capability = new() { Code = "vocabulary.catalog.read", Family = " " },
                Version = new() { Value = 1 }
            },
            new()
            {
                Capability = new() { Code = "vocabulary.catalog.read", Family = "vocabulary" },
                Version = new() { Value = 0 }
            }
        ];

        invalid.Should().OnlyContain(reference => !reference.IsValid());
    }

    [Theory]
    [InlineData("""{"capability":null,"version":{"value":1}}""")]
    [InlineData("""{"capability":{"code":null,"family":"vocabulary"},"version":{"value":1}}""")]
    [InlineData("""{"capability":{"code":" ","family":"vocabulary"},"version":{"value":1}}""")]
    [InlineData("""{"capability":{"code":"vocabulary.catalog.read","family":null},"version":{"value":1}}""")]
    [InlineData("""{"capability":{"code":"vocabulary.catalog.read","family":"\t"},"version":{"value":1}}""")]
    [InlineData("""{"capability":{"code":"vocabulary.catalog.read","family":"vocabulary"},"version":null}""")]
    [InlineData("""{"capability":{"code":"vocabulary.catalog.read","family":"vocabulary"},"version":{"value":0}}""")]
    public void Deserialized_reference_rejects_explicit_null_whitespace_and_nonpositive_version(string json)
    {
        var reference = JsonSerializer.Deserialize<ApplicationCapabilityReference>(json, WireJson.Client);

        reference.Should().NotBeNull();
        reference!.IsValid().Should().BeFalse();
    }

    [Theory]
    [InlineData(true, """{"capability":null}""")]
    [InlineData(false, """{"capability":null}""")]
    [InlineData(true, """{"capability":{"capability":null,"version":{"value":1}}}""")]
    [InlineData(false, """{"capability":{"capability":null,"version":{"value":1}}}""")]
    [InlineData(true, """{"capability":{"capability":{"code":" ","family":"vocabulary"},"version":{"value":1}}}""")]
    [InlineData(false, """{"capability":{"capability":{"code":"vocabulary.catalog.read","family":" "},"version":{"value":1}}}""")]
    [InlineData(true, """{"capability":{"capability":{"code":"vocabulary.catalog.read","family":"vocabulary"},"version":{"value":0}}}""")]
    [InlineData(false, """{"capability":{"capability":{"code":"vocabulary.catalog.read","family":"vocabulary"},"version":null}}""")]
    public void Requests_delegate_to_nested_reference_validation(bool query, string json)
    {
        var isValid = query
            ? JsonSerializer.Deserialize<ApplicationCapabilityQueryRequest>(json, WireJson.Client)!.IsValid()
            : JsonSerializer.Deserialize<ApplicationCapabilityCommandRequest>(json, WireJson.Client)!.IsValid();

        isValid.Should().BeFalse();
    }

    private static ApplicationCapabilityReference ValidReference() =>
        new()
        {
            Capability = new()
            {
                Code = "vocabulary.catalog.read",
                Family = "vocabulary"
            },
            Version = new() { Value = 1 }
        };
}
