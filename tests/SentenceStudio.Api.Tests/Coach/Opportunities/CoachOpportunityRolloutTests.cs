using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Endpoints;
using SentenceStudio.Api.Coach.Runtime;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// The rollout defaults, the configuration key contract, and the route-mapping gate.
/// </summary>
public class CoachOpportunityRolloutTests
{
    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "SentenceStudio.Api";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    // ---------------------------------------------------------------- defaults

    [Fact]
    public void CaptureIsOffByDefault()
    {
        var options = new CoachOpportunityOptions();

        options.Enabled.Should().BeFalse(
            "Production stays off until the end-to-end suite has been reviewed and the flip is " +
            "approved; a default-on telemetry table is a decision nobody made");
    }

    [Fact]
    public void TheOperatorSurfaceIsOffByDefault()
    {
        var options = new CoachOpportunityOptions();

        options.OperatorSurface.Enabled.Should().BeFalse();
        options.OperatorSurface.AllowCrossOwnerEvidence.Should().BeFalse(
            "this is the one control that crosses the boundary the cross-tenant tests were " +
            "built to defend");
    }

    [Fact]
    public void TheRetentionDefaultIsTheApprovedWindow()
    {
        new CoachOpportunityOptions().RetentionDays.Should().Be(180);
        new CoachOpportunityOptions().RetentionSweepEnabled.Should().BeTrue(
            "a deployment that turns capture off must still age out the rows it already wrote");
    }

    [Fact]
    public void TheShippedDevelopmentConfigurationTurnsCaptureAndTheSurfaceOn()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(
                Path.Combine(RepositoryRoot(), "src", "SentenceStudio.Api", "appsettings.Development.json"),
                optional: false)
            .Build();

        var options = new CoachOpportunityOptions();
        configuration.GetSection(CoachOpportunityOptions.SectionName).Bind(options);

        options.Enabled.Should().BeTrue();
        options.OperatorSurface.Enabled.Should().BeTrue();
        options.OperatorSurface.AllowCrossOwnerEvidence.Should().BeFalse();
        options.RetentionDays.Should().Be(180);
    }

    // ---------------------------------------------------------------- validation

    [Fact]
    public void TheOperatorSurfaceCannotBeEnabledOutsideDevelopment()
    {
        var validator = new CoachOpportunityOptionsValidator(
            new StubEnvironment { EnvironmentName = Environments.Production });

        var result = validator.Validate(null, new CoachOpportunityOptions
        {
            Enabled = true,
            OperatorSurface = new CoachOpportunityOperatorSurfaceOptions { Enabled = true }
        });

        result.Failed.Should().BeTrue(
            "route mapping already skips it there, but a route registration is a line of code " +
            "somebody can move — this makes the deployment illegal, not merely the request");
        result.FailureMessage.Should().Contain("Development-only");
    }

    [Fact]
    public void CrossOwnerEvidenceCannotBeEnabledOutsideDevelopment()
    {
        var validator = new CoachOpportunityOptionsValidator(
            new StubEnvironment { EnvironmentName = "Staging" });

        validator.Validate(null, new CoachOpportunityOptions
        {
            OperatorSurface = new CoachOpportunityOperatorSurfaceOptions
            {
                Enabled = false,
                AllowCrossOwnerEvidence = true
            }
        }).Failed.Should().BeTrue();
    }

    [Fact]
    public void CrossOwnerEvidenceWithoutTheSurfaceIsRefused()
    {
        var validator = new CoachOpportunityOptionsValidator(
            new StubEnvironment { EnvironmentName = Environments.Development });

        validator.Validate(null, new CoachOpportunityOptions
        {
            OperatorSurface = new CoachOpportunityOperatorSurfaceOptions
            {
                Enabled = false,
                AllowCrossOwnerEvidence = true
            }
        }).Failed.Should().BeTrue(
            "a deployment that believes cross-owner reads are configured while the surface is " +
            "off has a mistaken model of what it turned on");
    }

    [Fact]
    public void CaptureAloneIsValidInProduction()
    {
        var validator = new CoachOpportunityOptionsValidator(
            new StubEnvironment { EnvironmentName = Environments.Production });

        validator.Validate(null, new CoachOpportunityOptions
        {
            Enabled = true,
            OperatorSurface = new CoachOpportunityOperatorSurfaceOptions { Enabled = false }
        }).Succeeded.Should().BeTrue(
            "capture writes content-free rows no learner-facing path reads, after the response " +
            "is computed, inside try/catch — the review surface is the part that needs a gate");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(1000)]
    public void AnOutOfRangeRetentionWindowIsRefused(int days) =>
        new CoachOpportunityOptionsValidator(new StubEnvironment())
            .Validate(null, new CoachOpportunityOptions { RetentionDays = days })
            .Failed.Should().BeTrue();

    [Fact]
    public void ADevelopmentHostMayEnableEverything() =>
        new CoachOpportunityOptionsValidator(
                new StubEnvironment { EnvironmentName = Environments.Development })
            .Validate(null, new CoachOpportunityOptions
            {
                Enabled = true,
                OperatorSurface = new CoachOpportunityOperatorSurfaceOptions
                {
                    Enabled = true,
                    AllowCrossOwnerEvidence = true
                }
            })
            .Succeeded.Should().BeTrue();

    // ---------------------------------------------------------------- key spelling

    [Theory]
    [InlineData("Coach:Opportunities")]
    [InlineData("Coach:Opportunities:OperatorSurface")]
    public void TheFlatSpellingIsAStartupFailure(string flatKey)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [flatKey] = "true" })
            .Build();

        var result = new CoachConfigurationKeyValidator(configuration)
            .Validate(null, new CoachOptions());

        result.Failed.Should().BeTrue(
            "the flat spelling binds to nothing, so the feature would stay off while the " +
            "deployment believed it was on — the durable-history incident, not repeated");
        result.FailureMessage.Should().Contain(flatKey);
    }

    [Fact]
    public void TheNestedSpellingIsAccepted()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coach:Opportunities:Enabled"] = "true",
                ["Coach:Opportunities:OperatorSurface:Enabled"] = "true"
            })
            .Build();

        new CoachConfigurationKeyValidator(configuration)
            .Validate(null, new CoachOptions())
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public void BothOpportunityKeysAreRetiredFlatKeys()
    {
        var flat = CoachConfigurationKeyValidator.RetiredFlatKeys
            .Select(entry => entry.FlatKey)
            .ToList();

        flat.Should().Contain("Coach:Opportunities");
        flat.Should().Contain("Coach:Opportunities:OperatorSurface");
    }

    [Fact]
    public void TheNestedKeysBindAsExpected()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coach:Opportunities:Enabled"] = "true",
                ["Coach:Opportunities:RetentionDays"] = "90",
                ["Coach:Opportunities:OperatorSurface:Enabled"] = "true",
                ["Coach:Opportunities:OperatorSurface:AllowCrossOwnerEvidence"] = "true"
            })
            .Build();

        var options = new CoachOpportunityOptions();
        configuration.GetSection(CoachOpportunityOptions.SectionName).Bind(options);

        options.Enabled.Should().BeTrue();
        options.RetentionDays.Should().Be(90);
        options.Retention.Should().Be(TimeSpan.FromDays(90));
        options.OperatorSurface.Enabled.Should().BeTrue();
        options.OperatorSurface.AllowCrossOwnerEvidence.Should().BeTrue();
    }

    // ---------------------------------------------------------------- gate 1: routes

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void TheOperatorRoutesAreNotMappedOutsideDevelopment(string environmentName)
    {
        var app = WebApplication.CreateBuilder().Build();

        app.MapCoachOpportunityOperator(new StubEnvironment { EnvironmentName = environmentName });

        RouteCount(app).Should().Be(0,
            "the routes must not exist at all, so a request 404s rather than 403s — a 403 on an " +
            "operator route is an advertisement");
    }

    [Fact]
    public void TheOperatorRoutesAreMappedInDevelopment()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        var app = builder.Build();

        app.MapCoachOpportunityOperator(
            new StubEnvironment { EnvironmentName = Environments.Development });

        RouteCount(app).Should().Be(6);
    }

    [Fact]
    public void TheEvidenceRouteIsAPost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        var app = builder.Build();

        app.MapCoachOpportunityOperator(
            new StubEnvironment { EnvironmentName = Environments.Development });

        var evidence = Endpoints(app)
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText!.EndsWith("/evidence", StringComparison.Ordinal));

        evidence.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
            .Should().BeEquivalentTo(["POST"],
                "a reveal must not be linkable, prefetchable, cacheable, or reachable from a " +
                "browser history entry");
    }

    [Fact]
    public void TheAcknowledgementLiteralIsPinned() =>
        CoachOpportunityLimits.EvidenceRevealAcknowledgement.Should().Be("reveal-learner-content");

    private static int RouteCount(WebApplication app) => Endpoints(app).Count;

    private static IReadOnlyList<Endpoint> Endpoints(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToList();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
