using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.SamTools;
using SentenceStudio.Contracts.Coach;
using Xunit;

namespace SentenceStudio.Api.Tests.Coach.Capabilities;

/// <summary>
/// The read-capability metadata table describes what Sam can look up. When a row drifts from the
/// tool it describes, the result is not a wrong log line — it is Sam telling a learner something
/// confident and false about its own limits.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these tests exist for.</b> The table recorded <c>MaxPageSize: null</c> for
/// <c>GetVocabularyDueSummary</c> and explained the gap with "the tool declares no constant ceiling
/// of its own". <see cref="VocabularyDueSummaryTool.MaxTagCount"/> had been twenty the whole time,
/// and the tool rejects anything outside one to twenty. Two claims were false at once: the number,
/// and a comment in the same file asserting that a validator checked all of this at startup. No
/// such validator existed.
/// </para>
/// <para>
/// <b>Why the equality checks are not circular.</b> The table now cites each tool constant by
/// reference, so <c>row.MaxPageSize == Tool.Constant</c> is true by construction — and that is the
/// point: the class of defect is unrepresentable rather than merely corrected. What remains
/// checkable, and is checked below, is that nobody re-introduces a literal
/// (<see cref="No_ceiling_in_the_table_is_a_transcribed_number"/>), that the table and registry
/// still agree in both directions, and that a doctored row genuinely stops the host.
/// </para>
/// </remarks>
public sealed class CoachReadCapabilityMetadataValidatorTests
{
    // ---------------------------------------------------------------------------------------
    // The blocker itself
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Due_summary_states_the_ceiling_the_tool_actually_enforces()
    {
        var row = CoachReadCapabilityMetadataTable.Find(CoachToolNames.GetVocabularyDueSummary);

        row.Should().NotBeNull();
        row!.MaxPageSize.Should().Be(VocabularyDueSummaryTool.MaxTagCount);
        row.MaxPageSize.Should().Be(20, "the enforced ceiling is twenty, whatever the table cites");
        row.RangeSupport.Should().Be(CoachReadRangeSupport.ResultLimit);
    }

    [Fact]
    public void The_due_summary_ceiling_is_a_real_rejection_not_a_documented_preference()
    {
        // If the tool merely clamped, "no ceiling" would have been a defensible row. It rejects,
        // so the number is part of the contract a learner can be told.
        var source = File.ReadAllText(ToolSourcePath("VocabularyDueSummaryTool.cs"));

        source.Should().Contain(
            "if (maxCategoryTags is < MinTagCount or > MaxTagCount)",
            "the metadata claims a hard bound, so the tool must enforce one");
        source.Should().Contain("throw InvalidArgument(");
    }

    // ---------------------------------------------------------------------------------------
    // Transcription fidelity, made structural
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Every_bounded_read_cites_the_constant_its_tool_declares()
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [CoachToolNames.GetResourceCatalog] = ResourceCatalogTool.MaxResults,
            [CoachToolNames.GetVocabularyDueSummary] = VocabularyDueSummaryTool.MaxTagCount,
            [CoachToolNames.ListUserVocabularies] = VocabularySearchTool.MaxResults,
            [CoachToolNames.GetSkillList] = SkillListTool.MaxResults,
            [CoachToolNames.GetLearningResourceList] = LearningResourceListTool.MaxResults
        };

        CoachReadCapabilityMetadataValidator.DeclaredCeilings
            .Should().BeEquivalentTo(expected, "the validator's map is the set of bounded reads");

        var compared = 0;
        foreach (var (toolName, ceiling) in expected)
        {
            CoachReadCapabilityMetadataTable.Find(toolName)!.MaxPageSize.Should().Be(ceiling);
            compared++;
        }

        compared.Should().Be(5, "there are exactly five bounded reads; a sweep of four proves less");
    }

    [Fact]
    public void No_ceiling_in_the_table_is_a_transcribed_number()
    {
        // This is the non-tautological half. Equality against the constant cannot fail while the
        // table cites the constant — so what is worth asserting is that it still cites it, and
        // that a future edit cannot quietly go back to copying the number in.
        var source = File.ReadAllText(MetadataSourcePath());

        var declarations = Regex.Matches(source, @"MaxPageSize:\s*(?<value>[^,\r\n]+)");
        declarations.Count.Should().Be(
            15, "every row in the table states a page size, present or absent");

        var cited = 0;
        var absent = 0;
        foreach (Match declaration in declarations)
        {
            var value = declaration.Groups["value"].Value.Trim();

            if (value == "null")
            {
                absent++;
                continue;
            }

            value.Should().MatchRegex(
                @"^\w+Tool\.Max\w+$",
                "a ceiling must be the tool's constant, not a number copied beside it");
            cited++;
        }

        cited.Should().Be(5);
        absent.Should().Be(10);
    }

    [Fact]
    public void The_five_ceiling_constants_are_reachable_so_there_is_one_literal_each()
    {
        var sources = new (string File, string Member, int Value)[]
        {
            ("ResourceCatalogTool.cs", "MaxResults", ResourceCatalogTool.MaxResults),
            ("VocabularyDueSummaryTool.cs", "MaxTagCount", VocabularyDueSummaryTool.MaxTagCount),
            ("SamTools/VocabularySearchTool.cs", "MaxResults", VocabularySearchTool.MaxResults),
            ("SamTools/SkillTools.cs", "MaxResults", SkillListTool.MaxResults),
            ("SamTools/LearningResourceTools.cs", "MaxResults", LearningResourceListTool.MaxResults)
        };

        var checkedCount = 0;
        foreach (var (file, member, value) in sources)
        {
            var text = File.ReadAllText(ToolSourcePath(file));
            text.Should().Contain(
                $"public const int {member} = {value};",
                $"{file} must be the single place the number {value} is written");
            checkedCount++;
        }

        checkedCount.Should().Be(5);
    }

    // ---------------------------------------------------------------------------------------
    // Bidirectional completeness against the frozen registry
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_shipped_table_and_the_frozen_registry_agree_in_both_directions()
    {
        var registry = CapabilityFixtures.FrozenRegistry();

        var examined = CoachReadCapabilityMetadataValidator.Validate(registry);

        var registeredReads = registry.All
            .Count(r => r.EffectClass == CoachCapabilityEffectClass.Read);

        examined.Should().Be(registeredReads);
        examined.Should().Be(CoachReadCapabilityMetadataTable.All.Count);
        examined.Should().BeGreaterThan(0, "a sweep of nothing proves nothing");
    }

    [Fact]
    public void A_registered_read_with_no_row_is_rejected()
    {
        var registry = CapabilityFixtures.FrozenRegistry();
        var missingOne = CoachReadCapabilityMetadataTable.All
            .Where(kv => kv.Key != CoachToolNames.GetSkillDetail)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var act = () => CoachReadCapabilityMetadataValidator.Validate(registry, missingOne);

        act.Should().Throw<CoachReadCapabilityMetadataException>()
            .WithMessage($"*{CoachToolNames.GetSkillDetail}*no row*");
    }

    [Fact]
    public void A_row_for_a_tool_that_is_not_a_registered_read_is_rejected()
    {
        var registry = CapabilityFixtures.FrozenRegistry();
        var withGhost = Doctored(
            "ghost_read",
            CoachReadCapabilityMetadataTable.All[CoachToolNames.GetSkillDetail]);

        var act = () => CoachReadCapabilityMetadataValidator.Validate(registry, withGhost);

        act.Should().Throw<CoachReadCapabilityMetadataException>()
            .WithMessage("*ghost_read*not a registered read*");
    }

    // ---------------------------------------------------------------------------------------
    // Bound fidelity: the doctored rows the blocker would have produced
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData(21)]
    [InlineData(19)]
    public void A_due_summary_row_that_does_not_match_the_tool_is_rejected(int? doctoredCeiling)
    {
        var registry = CapabilityFixtures.FrozenRegistry();
        var row = CoachReadCapabilityMetadataTable.All[CoachToolNames.GetVocabularyDueSummary];
        var table = Doctored(
            CoachToolNames.GetVocabularyDueSummary,
            row with { MaxPageSize = doctoredCeiling });

        var act = () => CoachReadCapabilityMetadataValidator.Validate(registry, table);

        act.Should().Throw<CoachReadCapabilityMetadataException>()
            .WithMessage($"*{CoachToolNames.GetVocabularyDueSummary}*");
    }

    [Fact]
    public void A_caller_bound_with_no_ceiling_is_rejected_on_an_unbounded_read()
    {
        var registry = CapabilityFixtures.FrozenRegistry();
        var row = CoachReadCapabilityMetadataTable.All[CoachToolNames.GetSkillDetail];
        var table = Doctored(
            CoachToolNames.GetSkillDetail,
            row with { RangeSupport = CoachReadRangeSupport.ResultLimit });

        var act = () => CoachReadCapabilityMetadataValidator.Validate(registry, table);

        act.Should().Throw<CoachReadCapabilityMetadataException>()
            .WithMessage("*must state the ceiling*");
    }

    [Fact]
    public void A_ceiling_on_a_read_that_declares_no_constant_is_rejected()
    {
        var registry = CapabilityFixtures.FrozenRegistry();
        var row = CoachReadCapabilityMetadataTable.All[CoachToolNames.GetSkillDetail];
        var table = Doctored(CoachToolNames.GetSkillDetail, row with { MaxPageSize = 12 });

        var act = () => CoachReadCapabilityMetadataValidator.Validate(registry, table);

        act.Should().Throw<CoachReadCapabilityMetadataException>()
            .WithMessage("*declares no constant of its own*");
    }

    [Fact]
    public void A_non_positive_ceiling_is_rejected()
    {
        var act = () => CoachReadCapabilityMetadataValidator.ValidateRow(
            "synthetic_read",
            new CoachReadCapabilityMetadata(
                CoachScopeCoverage.PageOfOwnedSet,
                [CoachScopeOrder.Unordered],
                CoachScopeFilters.OwnerScoped,
                CoachReadDateSupport.None,
                CoachReadRangeSupport.ResultLimit,
                MaxPageSize: 0,
                Source: "synthetic"));

        act.Should().Throw<CoachReadCapabilityMetadataException>()
            .WithMessage("*can never return anything*");
    }

    [Fact]
    public void An_empty_sweep_is_rejected_rather_than_reported_as_success()
    {
        var act = () => CoachReadCapabilityMetadataValidator.Validate(
            new EmptyRegistry(),
            new Dictionary<string, CoachReadCapabilityMetadata>(StringComparer.Ordinal));

        act.Should().Throw<CoachReadCapabilityMetadataException>()
            .WithMessage("*examined zero reads*");
    }

    // ---------------------------------------------------------------------------------------
    // The startup claim, made watchable
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_shipped_table_starts_the_host()
    {
        using var host = BuildHost(metadata: null);

        var act = async () => await host.StartAsync();

        await act.Should().NotThrowAsync();
        await host.StopAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(21)]
    public async Task A_doctored_due_summary_row_stops_the_host(int? doctoredCeiling)
    {
        var row = CoachReadCapabilityMetadataTable.All[CoachToolNames.GetVocabularyDueSummary];
        using var host = BuildHost(
            Doctored(
                CoachToolNames.GetVocabularyDueSummary,
                row with { MaxPageSize = doctoredCeiling }));

        var act = async () => await host.StartAsync();

        (await act.Should().ThrowAsync<CoachReadCapabilityMetadataException>())
            .WithMessage($"*{CoachToolNames.GetVocabularyDueSummary}*");
    }

    // ---------------------------------------------------------------------------------------

    private static IReadOnlyDictionary<string, CoachReadCapabilityMetadata> Doctored(
        string toolName,
        CoachReadCapabilityMetadata replacement)
    {
        var copy = CoachReadCapabilityMetadataTable.All
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        copy[toolName] = replacement;
        return copy;
    }

    private static IHost BuildHost(IReadOnlyDictionary<string, CoachReadCapabilityMetadata>? metadata) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IOptions<CoachOptions>>(
                    Options.Create(CapabilityFixtures.AllToolsEnabled()));
                services.AddSingleton<ICoachToolRegistry>(sp =>
                    CoachToolServiceCollectionExtensions.BuildValidatedRegistry(
                        sp.GetRequiredService<IOptions<CoachOptions>>().Value));
                services.AddSingleton<ICoachCapabilityManifest>(sp =>
                    new CoachCapabilityManifest(sp.GetRequiredService<ICoachToolRegistry>()));

                if (metadata is not null)
                {
                    services.AddSingleton<ICoachReadCapabilityMetadataSource>(
                        new FixedMetadataSource(metadata));
                }

                services.AddHostedService<CoachToolRegistryStartupValidator>();
            })
            .Build();

    private sealed class FixedMetadataSource(
        IReadOnlyDictionary<string, CoachReadCapabilityMetadata> all)
        : ICoachReadCapabilityMetadataSource
    {
        public IReadOnlyDictionary<string, CoachReadCapabilityMetadata> All { get; } = all;
    }

    /// <summary>A registry offering no reads at all, so the zero-sweep guard is reachable.</summary>
    private sealed class EmptyRegistry : ICoachToolRegistry
    {
        public IReadOnlyList<CoachToolRegistration> All => [];
        public IReadOnlyList<CoachToolRegistration> Enabled => [];
        public IReadOnlyList<string> EnabledNames => [];
        public bool IsRegistered(string name) => false;
        public bool IsEnabled(string name) => false;
        public CoachToolRegistration? Find(string name) => null;
        public bool IsFrozen => true;
    }

    private static string MetadataSourcePath() =>
        Path.Combine(RepositoryRoot(), "src", "SentenceStudio.Api", "Coach", "Capabilities",
            "CoachReadCapabilityMetadata.cs");

    private static string ToolSourcePath(string relative) =>
        Path.Combine(RepositoryRoot(), "src", "SentenceStudio.Api", "Coach", "Tools",
            relative.Replace('/', Path.DirectorySeparatorChar));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests read tool source to prove there is one literal each");
        return directory!.FullName;
    }
}
