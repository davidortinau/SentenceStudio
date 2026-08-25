using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Api.Coach.Runtime;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// What the shipped Production manifest actually says, and what the reviewer path actually is.
/// </summary>
/// <remarks>
/// <para>
/// <c>Coach:Reports:Enabled</c> being true in Production is a product promise: the learner is told
/// a person looks at their report. These tests hold the two halves of that promise together — the
/// switch is on in the file that ships, and the artifact a person reads exists, is documented, has
/// a named owner, and carries no credential.
/// </para>
/// <para>
/// They assert against the repository's own files rather than against test fixtures on purpose. A
/// configuration test that binds an in-memory dictionary proves the binder works; it does not
/// prove the deployment says what everyone believes it says.
/// </para>
/// </remarks>
public class CoachOpportunityProductionRolloutTests
{
    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "SentenceStudio.Api";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfigurationRoot ProductionConfiguration() =>
        new ConfigurationBuilder()
            .AddJsonFile(
                Path.Combine(RepositoryRoot(), "src", "SentenceStudio.Api", "appsettings.Production.json"),
                optional: false)
            .Build();

    // ---------------------------------------------------------------- the shipped manifest

    [Fact]
    public void TheShippedProductionConfigurationTurnsReportingOn()
    {
        var options = new CoachResponseReportOptions();
        ProductionConfiguration().GetSection(CoachResponseReportOptions.SectionName).Bind(options);

        options.Enabled.Should().BeTrue(
            "the learner-facing flag control ships, and the production reviewer path — the " +
            "content-free digest — is what makes 'Reported for review' true");
        options.RetentionDays.Should().Be(180);
        options.RetentionSweepEnabled.Should().BeTrue();
    }

    [Fact]
    public void TheShippedProductionConfigurationLeavesAutomaticCaptureOff()
    {
        var options = new CoachOpportunityOptions();
        ProductionConfiguration().GetSection(CoachOpportunityOptions.SectionName).Bind(options);

        options.Enabled.Should().BeFalse(
            "automatic capture is unchanged by the reports flip — it is still awaiting Captain's " +
            "approval after SAM-OPP-01…10");
    }

    [Fact]
    public void TheShippedProductionConfigurationKeepsLedgerRetentionOn()
    {
        var options = new CoachOpportunityOptions();
        ProductionConfiguration().GetSection(CoachOpportunityOptions.SectionName).Bind(options);

        options.RetentionSweepEnabled.Should().BeTrue(
            "reports raise UserReportedResponse ledger rows even with capture off, so Production " +
            "writes to this table and the rows have to age out");
        options.RetentionDays.Should().Be(180);
    }

    [Fact]
    public void TheShippedProductionConfigurationKeepsTheOperatorSurfaceOff()
    {
        var options = new CoachOpportunityOptions();
        ProductionConfiguration().GetSection(CoachOpportunityOptions.SectionName).Bind(options);

        options.OperatorSurface.Enabled.Should().BeFalse(
            "it can decrypt learner messages and this host has no admin authorization primitive");
        options.OperatorSurface.AllowCrossOwnerEvidence.Should().BeFalse();
    }

    [Fact]
    public void TheShippedProductionConfigurationStartsAProductionHost()
    {
        var configuration = ProductionConfiguration();
        var environment = new StubEnvironment();

        var opportunities = new CoachOpportunityOptions();
        configuration.GetSection(CoachOpportunityOptions.SectionName).Bind(opportunities);

        var reports = new CoachResponseReportOptions();
        configuration.GetSection(CoachResponseReportOptions.SectionName).Bind(reports);

        new CoachOpportunityOptionsValidator(environment)
            .Validate(null, opportunities).Succeeded.Should().BeTrue();

        new CoachResponseReportOptionsValidator()
            .Validate(null, reports).Succeeded.Should().BeTrue();

        new CoachConfigurationKeyValidator(configuration)
            .Validate(null, new CoachOptions()).Succeeded.Should().BeTrue(
                "every coach switch in the manifest is spelled as a nested :Enabled key");
    }

    [Fact]
    public void ReportingWithoutRetentionRefusesToStart() =>
        new CoachResponseReportOptionsValidator()
            .Validate(null, new CoachResponseReportOptions
            {
                Enabled = true,
                RetentionSweepEnabled = false
            })
            .Failed.Should().BeTrue(
                "a deployment that accepts learner reports and never ages them out has not " +
                "chosen a retention policy, it has postponed one");

    [Fact]
    public void EnablingTheOperatorSurfaceInProductionStillRefusesToStart() =>
        new CoachOpportunityOptionsValidator(new StubEnvironment())
            .Validate(null, new CoachOpportunityOptions
            {
                Enabled = false,
                OperatorSurface = new CoachOpportunityOperatorSurfaceOptions { Enabled = true }
            })
            .Failed.Should().BeTrue(
                "shipping reports to Production must not have loosened the gate on the surface " +
                "that can decrypt learner messages");

    // ---------------------------------------------------------------- what the AppHost forwards

    [Fact]
    public void TheAppHostForwardsBothLedgerSwitchesButNotTheOperatorSurface()
    {
        var appHost = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "SentenceStudio.AppHost", "AppHost.cs"));

        appHost.Should().Contain("Coach__Reports__Enabled",
            "the report switch has to be flippable without a redeploy");
        appHost.Should().Contain("Coach__Opportunities__Enabled",
            "so does automatic capture, when Captain approves it");

        appHost.Should().NotContain("Coach__Opportunities__OperatorSurface__Enabled",
            "an environment variable that could enable the evidence-decrypting surface is an " +
            "environment variable somebody can set on the wrong host");
        appHost.Should().NotContain("AllowCrossOwnerEvidence");
    }

    // ---------------------------------------------------------------- the reviewer path exists

    [Fact]
    public void TheReviewerPathShipsAsAScriptAToolAndAWorkflow()
    {
        var root = RepositoryRoot();

        File.Exists(Path.Combine(root, "scripts", "sam-opportunity-digest.sh"))
            .Should().BeTrue();
        File.Exists(Path.Combine(root, "tools", "SamOpportunityDigest", "SamOpportunityDigest.csproj"))
            .Should().BeTrue();
        File.Exists(Path.Combine(root, ".github", "workflows", "sam-opportunity-digest.yml"))
            .Should().BeTrue();
        File.Exists(Path.Combine(root, "docs", "sam-opportunity-digest.md"))
            .Should().BeTrue();
    }

    [Fact]
    public void TheRunbookNamesAnOperationalOwnerAndAReviewOwner()
    {
        var runbook = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "docs", "sam-opportunity-digest.md"));

        runbook.Should().Contain("**Operational owner:**",
            "a reviewer path with no named owner is a reviewer path nobody runs");
        runbook.Should().Contain("**Review owner:**");
        runbook.Should().Contain("**Cadence:**");
    }

    [Fact]
    public void TheBacklogLogPointsAtTheProductionReviewerPath()
    {
        var log = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "docs", "sam-future-opportunities.md"));

        log.Should().Contain("Who reviews it in Production",
            "the log is where a reader looks first, so the path has to be findable from it");
        log.Should().Contain("sam-opportunity-digest.md");
        log.Should().Contain("./scripts/sam-opportunity-digest.sh");
    }

    // ---------------------------------------------------------------- no credential anywhere

    [Theory]
    [InlineData(".github/workflows/sam-opportunity-digest.yml")]
    [InlineData("scripts/sam-opportunity-digest.sh")]
    [InlineData("docs/sam-opportunity-digest.md")]
    public void TheReviewerPathEmbedsNoCredential(string relativePath)
    {
        var content = File.ReadAllText(Path.Combine(
            RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        // A literal PostgreSQL password or a bare token-shaped assignment. The documentation is
        // allowed to name the environment variables and to show a placeholder in angle brackets;
        // it is not allowed to contain a value.
        var offenders = Regex.Matches(
                content,
                @"(?i)\b(password|pwd)\s*=\s*(?![<'""]|\.\.\.|\$)\S+",
                RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .ToList();

        offenders.Should().BeEmpty(
            "credentials arrive from the environment, Key Vault, or an Entra token at run time — " +
            "never from this repository");
    }

    [Fact]
    public void TheWorkflowReadsItsConnectionStringFromASecretAndSkipsWithoutOne()
    {
        var workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot(), ".github", "workflows", "sam-opportunity-digest.yml"));

        workflow.Should().Contain("${{ secrets.COACH_DIGEST_CONNECTION_STRING }}",
            "the only source of the credential is the repository secret");

        workflow.Should().Contain("workflow_dispatch");
        workflow.Should().Contain("schedule");

        workflow.Should().Contain("configured=false",
            "an unconfigured run must skip with a notice rather than fail every week");
        workflow.Should().Contain("skipped",
            "and it must say so, because a green scheduled job that read nothing is worse than " +
            "no job at all");

        // The connection string must never be echoed, and the digest step must never be run with
        // the secret interpolated into the command line, where it would land in the process table
        // and in the workflow log.
        workflow.Should().NotContain("echo \"$COACH_DIGEST_CONNECTION_STRING\"");
        workflow.Should().NotContain("--connection");
    }

    [Fact]
    public void TheScriptNeverEchoesTheCredential()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "sam-opportunity-digest.sh"));

        script.Should().NotContain("echo \"$COACH_DIGEST_CONNECTION_STRING\"");
        script.Should().NotContain("echo $COACH_DIGEST_CONNECTION_STRING");

        script.Should().Contain("set -euo pipefail");
        script.Should().Contain("exit 2",
            "no credential configured is a refusal with instructions, not a guess at a default");
    }

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
