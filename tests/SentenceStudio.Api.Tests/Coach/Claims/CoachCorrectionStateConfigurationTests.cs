using FluentAssertions;
using Microsoft.Extensions.Configuration;
using SentenceStudio.Api.Coach.Runtime;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// <c>Coach:CorrectionState:Enabled</c>: safe default, strict validation, total bypass.
/// </summary>
/// <remarks>
/// <para>
/// The failure a feature flag hides is not "the feature is off". It is "the deployment believes the
/// feature is on and every metric reads zero, which looks like nothing going wrong". A dispute
/// count of zero is indistinguishable from a build where no learner ever corrected the coach, and
/// that is a number somebody will present at a review.
/// </para>
/// <para>
/// So the two spellings that produce it — the flat key and an unparseable boolean — stop the host
/// rather than defaulting.
/// </para>
/// </remarks>
public sealed class CoachCorrectionStateConfigurationTests
{
    private static CoachConfigurationKeyValidator Validator(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting =>
                new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();

        return new CoachConfigurationKeyValidator(configuration);
    }

    [Fact]
    public void The_default_is_off()
    {
        new CoachOptions().IsCorrectionStateEnabled.Should().BeFalse(
            "promotion is the operator's decision and belongs to the rollout step; a workstream "
            + "that shipped itself already on would be answering a question nobody asked it");
    }

    [Fact]
    public void An_absent_key_is_legal_and_means_off()
    {
        Validator().Validate(null, new CoachOptions()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("True")]
    [InlineData("FALSE")]
    [InlineData(" true ")]
    public void A_parseable_boolean_is_accepted(string value)
    {
        Validator((CoachConfigurationKeyValidator.CorrectionStateEnabledKey, value))
            .Validate(null, new CoachOptions())
            .Succeeded.Should().BeTrue();
    }

    /// <summary>
    /// The values that read as true to a human and bind to false.
    /// </summary>
    [Theory]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("1")]
    [InlineData("enabled")]
    [InlineData("Y")]
    public void A_truthy_non_boolean_stops_the_host(string value)
    {
        var result = Validator((CoachConfigurationKeyValidator.CorrectionStateEnabledKey, value))
            .Validate(null, new CoachOptions());

        result.Failed.Should().BeTrue(
            "'{0}' binds to false, and a deployment that believes correction state is on while it "
            + "is off gets a dispute metric of zero that reads as 'no learner ever corrected the "
            + "coach'",
            value);

        result.FailureMessage.Should().Contain(CoachConfigurationKeyValidator.CorrectionStateEnabledKey);
    }

    /// <summary>The flat spelling binds to nothing and is refused by name.</summary>
    [Fact]
    public void The_flat_spelling_stops_the_host()
    {
        var result = Validator(("Coach:CorrectionState", "true")).Validate(null, new CoachOptions());

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(
            CoachConfigurationKeyValidator.CorrectionStateEnabledKey,
            "the message must name the spelling that works, not only the one that does not");
    }

    /// <summary>The canonical key is the one the option actually binds from.</summary>
    [Fact]
    public void The_canonical_key_binds_the_option()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>(
                    CoachConfigurationKeyValidator.CorrectionStateEnabledKey, "true")
            ])
            .Build();

        var options = new CoachOptions();
        configuration.GetSection(CoachOptions.SectionName).Bind(options);

        options.IsCorrectionStateEnabled.Should().BeTrue(
            "the validator and the binder must agree on the spelling, or the validator is guarding "
            + "a key nothing reads");
    }
}
