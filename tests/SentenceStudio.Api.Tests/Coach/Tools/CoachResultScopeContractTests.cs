using System.Text.Json;
using Microsoft.Extensions.AI;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// The contract that makes a read answer say what it looked at.
/// </summary>
/// <remarks>
/// <para>
/// A tool answer has always reported what it found. It has never reported what it looked for, and
/// the model fills that silence with the most fluent available assumption: a page reads as a
/// shelf, a filtered set reads as everything, and a search that quietly dropped four due words
/// reads as "here is your vocabulary". Nothing in the rows is false; the sentence built from them
/// is.
/// </para>
/// <para>
/// So the scope is enforced structurally rather than reviewed. Three things have to hold and each
/// is asserted here: every registered read carries a scope, the scope shape cannot carry anything
/// but flags, counts, dates, and closed enums, and each tool's scope agrees with what that tool
/// actually did. The third is the one that rots first, which is why every tool is exercised
/// against a seeded database rather than inspected.
/// </para>
/// </remarks>
public class CoachResultScopeContractTests
{
    private static readonly JsonSerializerOptions ModelSerializerOptions = new(AIJsonUtilities.DefaultOptions);

    /// <summary>
    /// The ceiling a scope's serialized projection must stay under, in characters.
    /// </summary>
    /// <remarks>
    /// Unchanged from the original budget. What changed is that it is now measured against the
    /// bytes production emits rather than against a shorter string that only ever existed in a
    /// fixture.
    /// </remarks>
    private const int ScopeCharacterCeiling = 320;

    /// <summary>
    /// A clock shaped like the one production hands the reads: sub-second, and worst-case so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IPlanDateContext.UtcNow</c> is <see cref="DateTime.UtcNow"/>, which carries sub-second
    /// ticks. Every fixture in this file used to be built from whole seconds, so every measurement
    /// taken here described a string production never produced — <c>"…T12:00:00Z"</c> against the
    /// real <c>"…T12:00:00.4821593Z"</c>, eight characters shorter on every scope. The token budget
    /// was being enforced against the fixture rather than against the deployment, which is the
    /// definition of a vacuous test: it could not fail for the reason it existed.
    /// </para>
    /// <para>
    /// The fractional part is chosen to be the longest <c>System.Text.Json</c> will render: seven
    /// significant digits with no trailing zero to trim. A clock ending in <c>.5</c> would render
    /// as one digit and would understate the cost by six characters, which is most of the headroom.
    /// </para>
    /// </remarks>
    internal static readonly DateTime TickPreciseNow =
        new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc).AddTicks(4_821_593);

    private static ICoachToolRegistry FullRegistry() =>
        CoachToolServiceCollectionExtensions.BuildValidatedRegistry(new CoachOptions
        {
            SamOverlay = new CoachFeatureSwitch { Enabled = true },
            SamReadTools = new CoachFeatureSwitch { Enabled = true },
            SamWriteTools = new CoachFeatureSwitch { Enabled = true }
        });

    // =====================================================================
    // Coverage of the registry
    // =====================================================================

    [Fact]
    public void Every_registered_read_result_states_its_scope()
    {
        var unscoped = FullRegistry().All
            .Where(r => r.RiskClass == CoachToolRiskClass.Read)
            .Where(r => !typeof(ICoachScopedResult).IsAssignableFrom(r.ResultType))
            .Select(r => $"{r.Name} -> {r.ResultType.Name}")
            .ToList();

        unscoped.Should().BeEmpty(
            "a read that cannot say what it covered is the shape the model over-claims from");
    }

    [Fact]
    public void Startup_validation_refuses_a_read_result_that_states_no_scope()
    {
        var registry = new CoachToolRegistry(new CoachOptions());
        registry.Register(new CoachToolRegistration
        {
            Name = "get_unscoped_thing",
            ResultType = typeof(UnscopedResult),
            RiskClass = CoachToolRiskClass.Read,
            Description = "A read whose envelope says nothing about what it looked at."
        });
        registry.Freeze();

        var approvals = new Dictionary<Type, CoachEmbargoScope>(CoachOutputContract.ApprovedResultEnvelopes)
        {
            [typeof(UnscopedResult)] = CoachEmbargoScope.ModelVisible
        };

        var result = CoachOutputContract.ScanRegistry(registry, approvals);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Code == "missing_result_scope");
    }

    [Fact]
    public void Startup_validation_refuses_a_scope_shape_that_can_carry_text()
    {
        var violations = new CoachEmbargoScanner()
            .ScanTypes([typeof(LeakyScopedResult)], CoachEmbargoScope.ModelVisible)
            .Violations;

        violations.Should().Contain(
            v => v.Code == "scope_member_type",
            "a scope shape that can hold a string can hold a term, a gloss, or the model's own query");
    }

    [Fact]
    public void The_shipped_scope_shape_carries_nothing_but_flags_counts_dates_and_closed_enums()
    {
        var result = new CoachEmbargoScanner()
            .ScanTypes([typeof(CoachResultScope)], CoachEmbargoScope.ResultScope);

        result.IsValid.Should().BeTrue(
            "the scope is the one shape on the read surface that must be incapable of carrying "
            + "learner content: {0}",
            string.Join("; ", result.Violations.Select(v => v.Message)));
    }

    [Fact]
    public void No_public_client_contract_can_reach_a_result_scope()
    {
        // The scope's enums are new vocabulary. Until the client wire tolerance gate says a client
        // can meet an unknown enum value without failing, none of them may appear on a shape the
        // server sends to one.
        var reaching = CoachOutputContract.PublicClientContractTypes
            .Where(ReachesScope)
            .Select(t => t.FullName!)
            .ToList();

        reaching.Should().BeEmpty(
            "result scopes stay model-facing until the client adoption gate opens");
    }

    [Fact]
    public void The_scope_property_the_interface_pins_is_itself_a_scope_shape()
    {
        var property = typeof(ICoachScopedResult).GetProperty(nameof(ICoachScopedResult.Scope));

        property.Should().NotBeNull();
        property!.PropertyType.Should().Be(
            typeof(CoachResultScope),
            "the interface is what stops a result declaring a scope-shaped thing of its own choosing");

        property.PropertyType.GetCustomAttributes(typeof(CoachScopeShapeAttribute), inherit: false)
            .Should().ContainSingle(
                "without the marker the scanner would judge the scope under whichever result it "
                + "hangs off, which is the laxer of the two every time");
    }

    // =====================================================================
    // Pinned ordinals
    // =====================================================================

    [Fact]
    public void Every_scope_enum_pins_its_ordinals()
    {
        Pinned<CoachScopeCoverage>(new()
        {
            ["Unspecified"] = 0, ["CompleteOwnedSet"] = 1, ["PageOfOwnedSet"] = 2,
            ["WindowBounded"] = 3, ["SingleItem"] = 4, ["SingleDay"] = 5,
            ["SettingsSnapshot"] = 6, ["DerivedProjection"] = 7,
            ["CompleteAggregateWithBreakdown"] = 8
        });

        Pinned<CoachScopeOrder>(new()
        {
            ["Unspecified"] = 0, ["NotApplicable"] = 1, ["Unordered"] = 2,
            ["LastUsedAscending"] = 3, ["UpdatedDescending"] = 4, ["MasteryDescending"] = 5,
            ["MinutesDescending"] = 6, ["PriorityAscending"] = 7, ["FrequencyDescending"] = 8,
            ["BandLabelAscending"] = 9
        });

        Pinned<CoachScopeFilters>(new()
        {
            ["None"] = 0, ["OwnerScoped"] = 1, ["ExcludeArchived"] = 2, ["ExcludeDue"] = 4,
            ["ProgressRowExists"] = 8, ["TextQuery"] = 16, ["DateWindow"] = 32,
            ["SingleIdentifier"] = 64, ["CalendarDay"] = 128, ["MinimumEvidence"] = 256
        });

        Pinned<CoachScopeWithheldReason>(new()
        {
            ["None"] = 0, ["DueReviewEmbargo"] = 1, ["ResultLimit"] = 2, ["ArchivedExcluded"] = 3,
            ["BelowMinimumEvidence"] = 4
        });

        Pinned<CoachScopeDefinition>(new()
        {
            ["Unspecified"] = 0, ["OwnedResourceCatalog"] = 1, ["OwnedResourceList"] = 2,
            ["OwnedResourceDetail"] = 3, ["ActiveSkillList"] = 4, ["ActiveSkillDetail"] = 5,
            ["TrackedVocabularyDueSummary"] = 6, ["UndueVocabularySearch"] = 7,
            ["TrackedVocabularyDetail"] = 8, ["LearnerSettingsSnapshot"] = 9,
            ["LearnerOverviewSummary"] = 10, ["PlanDaySummary"] = 11,
            ["PracticeWindowBalance"] = 12, ["DeterministicPlanPreview"] = 13,
            ["LatestPracticeSummary"] = 14
        });

        Pinned<CoachScopeMinimumEvidence>(new()
        {
            ["Unspecified"] = 0, ["None"] = 1, ["ProgressRowRequired"] = 2,
            ["LoggedMinutesRequired"] = 3, ["GradedAttemptRequired"] = 4, ["LoggedWorkRequired"] = 5
        });

        Pinned<CoachScopeTieBreak>(new()
        {
            ["Unspecified"] = 0, ["NotApplicable"] = 1, ["None"] = 2, ["TitleOrdinal"] = 3,
            ["ActivityTypeOrdinal"] = 4, ["TagOrdinal"] = 5, ["BandOrdinal"] = 6
        });

        Pinned<CoachScopeClockBasis>(new()
        {
            ["Unspecified"] = 0, ["NotApplicable"] = 1, ["ServerUtcInstant"] = 2,
            ["LearnerLocalDay"] = 3
        });

        Pinned<CoachScopeReferenceMode>(new()
        {
            ["Unspecified"] = 0, ["NotApplicable"] = 1, ["AsOfInstant"] = 2,
            ["CalendarDay"] = 3, ["DateWindow"] = 4
        });
    }

    // =====================================================================
    // Model-visible projection
    // =====================================================================

    /// <summary>
    /// The names the model may see on a scope. Anything else is either a leak or a token the model
    /// cannot act on, and the two failures cost different things but are caught the same way.
    /// </summary>
    private static readonly string[] ShippedScopeKeys =
    [
        "coverage", "order", "orderHonored", "filters", "asOfUtc",
        "windowStartDate", "windowEndDate", "requestedCount", "returnedCount",
        "matchedCount", "withheldCount", "withheldReason", "truncated"
    ];

    [Fact]
    public async Task The_model_sees_only_the_shipped_scope_members()
    {
        using var fixture = SeededFixture();

        foreach (var (name, scopeJson) in await SerializedScopesAsync(fixture))
        {
            foreach (var property in scopeJson.EnumerateObject())
            {
                ShippedScopeKeys.Should().Contain(
                    property.Name,
                    "{0} put '{1}' in front of the model; the foundation members stay server-side "
                    + "until the client adoption gate opens",
                    name,
                    property.Name);
            }
        }
    }

    [Fact]
    public async Task The_foundation_members_never_reach_the_model()
    {
        using var fixture = SeededFixture();

        string[] withheld =
        [
            "definitionCode", "eligiblePopulationCount", "minimumEvidence",
            "tieBreak", "clockBasis", "referenceMode"
        ];

        foreach (var (name, scopeJson) in await SerializedScopesAsync(fixture))
        {
            foreach (var key in withheld)
            {
                scopeJson.TryGetProperty(key, out _).Should().BeFalse(
                    "{0} must not spend the model's context on '{1}' before a client can read it",
                    name,
                    key);
            }
        }
    }

    [Fact]
    public async Task Every_scope_the_model_sees_reads_as_words_rather_than_numbers()
    {
        using var fixture = SeededFixture();

        foreach (var (name, scopeJson) in await SerializedScopesAsync(fixture))
        {
            scopeJson.GetProperty("coverage").ValueKind.Should().Be(
                JsonValueKind.String, "{0} must describe its coverage in words a model can read", name);
            scopeJson.GetProperty("order").ValueKind.Should().Be(JsonValueKind.String);
            scopeJson.GetProperty("filters").ValueKind.Should().Be(JsonValueKind.String);
        }
    }

    [Fact]
    public async Task Every_tool_completes_the_foundation_members_it_holds_back()
    {
        using var fixture = SeededFixture();

        foreach (var (name, scope) in await ScopesAsync(fixture))
        {
            scope.DefinitionCode.Should().NotBe(CoachScopeDefinition.Unspecified, "{0}", name);
            scope.MinimumEvidence.Should().NotBe(CoachScopeMinimumEvidence.Unspecified, "{0}", name);
            scope.TieBreak.Should().NotBe(CoachScopeTieBreak.Unspecified, "{0}", name);
            scope.ClockBasis.Should().NotBe(CoachScopeClockBasis.Unspecified, "{0}", name);
            scope.ReferenceMode.Should().NotBe(CoachScopeReferenceMode.Unspecified, "{0}", name);
            scope.Coverage.Should().NotBe(CoachScopeCoverage.Unspecified, "{0}", name);
            scope.Order.Should().NotBe(CoachScopeOrder.Unspecified, "{0}", name);
            scope.Filters.Should().HaveFlag(CoachScopeFilters.OwnerScoped, "{0}", name);
        }
    }

    [Fact]
    public void A_count_outside_the_bounds_is_refused_rather_than_reported()
    {
        var build = () => CoachResultScopeSamples.Any() with { ReturnedCount = -1 };
        build.Should().Throw<ArgumentOutOfRangeException>();

        var tooLarge = () => CoachResultScopeSamples.Any() with { MatchedCount = CoachResultScope.MaxCount + 1 };
        tooLarge.Should().Throw<ArgumentOutOfRangeException>();
    }

    // =====================================================================
    // Per-tool truthfulness
    // =====================================================================

    [Fact]
    public async Task Vocabulary_search_reports_fourteen_matched_ten_returned_and_four_withheld()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        // Ten words the learner may see, four that are due and therefore embargoed. The model is
        // told the four exist so it can say "four are due" instead of describing a ten-word
        // vocabulary the learner does not have.
        for (var i = 0; i < 10; i++)
        {
            var word = fixture.SeedWord($"안전{i}", $"safe {i}");
            fixture.SeedProgress(user, word.Id, masteryScore: 0.5f, nextReviewDate: fixture.Now.AddDays(3));
        }

        for (var i = 0; i < 4; i++)
        {
            var word = fixture.SeedWord($"만기{i}", $"due {i}");
            fixture.SeedProgress(user, word.Id, masteryScore: 0.9f, nextReviewDate: fixture.Now.AddDays(-1));
        }

        var result = await fixture.VocabularySearchTool.SearchAsync(maxResults: 25);

        result.Words.Should().HaveCount(10);
        result.TotalMatchCount.Should().Be(10, "the existing count keeps meaning 'eligible to show'");

        result.Scope.MatchedCount.Should().Be(14);
        result.Scope.ReturnedCount.Should().Be(10);
        result.Scope.WithheldCount.Should().Be(4);
        result.Scope.WithheldReason.Should().Be(CoachScopeWithheldReason.DueReviewEmbargo);
        result.Scope.EligiblePopulationCount.Should().Be(10);
        result.Scope.Truncated.Should().BeFalse();
        result.Scope.Coverage.Should().Be(CoachScopeCoverage.CompleteOwnedSet);
        result.Scope.Order.Should().Be(CoachScopeOrder.MasteryDescending);
        result.Scope.Filters.Should().Be(
            CoachScopeFilters.OwnerScoped
            | CoachScopeFilters.ProgressRowExists
            | CoachScopeFilters.ExcludeDue);

        // The embargo still holds: the count crossed and the words did not.
        var json = JsonSerializer.Serialize(result, ModelSerializerOptions);
        for (var i = 0; i < 4; i++)
        {
            json.Should().NotContain($"만기{i}");
            json.Should().NotContain($"due {i}");
        }
    }

    [Fact]
    public async Task Vocabulary_search_names_its_query_filter_without_carrying_the_query()
    {
        using var fixture = new CoachToolTestFixture();
        var word = fixture.SeedWord("사과", "apple");
        fixture.SeedProgress(CoachToolTestFixture.UserA, word.Id);

        var result = await fixture.VocabularySearchTool.SearchAsync(query: "apple");

        result.Scope.Filters.Should().HaveFlag(CoachScopeFilters.TextQuery);

        var scopeJson = JsonSerializer.Serialize(result.Scope, ModelSerializerOptions);
        scopeJson.Should().NotContain("apple", "a scope reports that a query was applied, never what it was");
    }

    [Fact]
    public async Task Vocabulary_search_reports_truncation_separately_from_the_embargo()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        for (var i = 0; i < 5; i++)
        {
            var word = fixture.SeedWord($"단어{i}", $"word {i}");
            fixture.SeedProgress(user, word.Id, masteryScore: 0.1f * i, nextReviewDate: fixture.Now.AddDays(5));
        }

        var result = await fixture.VocabularySearchTool.SearchAsync(maxResults: 2);

        result.Scope.MatchedCount.Should().Be(5);
        result.Scope.EligiblePopulationCount.Should().Be(5);
        result.Scope.ReturnedCount.Should().Be(2);
        result.Scope.Truncated.Should().BeTrue();
        result.Scope.RequestedCount.Should().Be(2);
        result.Scope.WithheldCount.Should().Be(0);
        result.Scope.WithheldReason.Should().Be(CoachScopeWithheldReason.None);
        result.Scope.Coverage.Should().Be(CoachScopeCoverage.PageOfOwnedSet);
    }

    [Fact]
    public async Task Word_detail_does_not_claim_to_exclude_due_words()
    {
        using var fixture = new CoachToolTestFixture();
        var word = fixture.SeedWord("만기", "due");
        fixture.SeedProgress(CoachToolTestFixture.UserA, word.Id, nextReviewDate: fixture.Now.AddDays(-1));

        var detail = await fixture.VocabularyWordDetailTool.GetAsync(word.Id);

        detail.TargetTerm.Should().Be("만기", "naming one word is the sanctioned route to a due term");
        detail.Scope.Filters.Should().NotHaveFlag(CoachScopeFilters.ExcludeDue);
        detail.Scope.Filters.Should().HaveFlag(CoachScopeFilters.SingleIdentifier);
        detail.Scope.Coverage.Should().Be(CoachScopeCoverage.SingleItem);
        detail.Scope.WithheldCount.Should().Be(0);
    }

    [Fact]
    public async Task Practice_balance_reports_a_bounded_window_with_exact_dates()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "Reading", 12, daysAgo: 0);

        var balance = await fixture.BalanceTool.GetAsync(CoachPracticeWindow.FourteenDays);

        balance.Scope.Coverage.Should().Be(CoachScopeCoverage.WindowBounded);
        balance.Scope.WindowStartDate.Should().Be(fixture.Today.AddDays(-13));
        balance.Scope.WindowEndDate.Should().Be(fixture.Today);
        balance.Scope.WindowStartDate.Should().Be(balance.WindowStartDate, "the scope and the answer describe one window");
        balance.Scope.WindowEndDate.Should().Be(balance.WindowEndDate);
        balance.Scope.AsOfUtc.Should().Be(
            CoachResultScope.NormalizeAsOf(fixture.Now),
            "the scope states the clock it was handed, normalized to the second it is accurate to");
        balance.Scope.AsOfUtc.Should().BeOnOrBefore(
            fixture.Now,
            "'as of' is a claim the answer was already true, so normalization may only move the "
            + "instant backwards");
        balance.Scope.ClockBasis.Should().Be(CoachScopeClockBasis.LearnerLocalDay);
        balance.Scope.ReferenceMode.Should().Be(CoachScopeReferenceMode.DateWindow);
        balance.Scope.Filters.Should().Be(
            CoachScopeFilters.OwnerScoped
            | CoachScopeFilters.DateWindow
            | CoachScopeFilters.MinimumEvidence,
            "the evidence bar is one of the predicates that shaped this answer, so it is named "
            + "alongside ownership and the window rather than left to be inferred");
        balance.Scope.Order.Should().Be(CoachScopeOrder.MinutesDescending);
        balance.Scope.TieBreak.Should().Be(CoachScopeTieBreak.ActivityTypeOrdinal);
        balance.Scope.MinimumEvidence.Should().Be(CoachScopeMinimumEvidence.LoggedWorkRequired);
    }

    [Fact]
    public async Task Practice_balance_states_the_evidence_bar_that_dropped_an_activity_type()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        fixture.SeedCompletion(user, "Reading", minutesSpent: 12, daysAgo: 0);
        fixture.SeedCompletion(user, "Writing", minutesSpent: 0, daysAgo: 1, isCompleted: false);

        var balance = await fixture.BalanceTool.GetAsync(CoachPracticeWindow.SevenDays);

        balance.ByActivityType.Should().ContainSingle("an untouched activity is not practice");

        // One population throughout — activity types that appeared in the window — so the
        // arithmetic closes in front of the model instead of leaving a two-versus-one gap it has
        // to explain to itself.
        balance.Scope.MatchedCount.Should().Be(2, "two activity types appeared in the window");
        balance.Scope.WithheldCount.Should().Be(1, "one of them had no logged work");
        balance.Scope.WithheldReason.Should().Be(CoachScopeWithheldReason.BelowMinimumEvidence);
        balance.Scope.ReturnedCount.Should().Be(1);
        balance.Scope.EligiblePopulationCount.Should().Be(
            1, "eligible counts the types that cleared the bar, not the completion rows behind them");
        balance.Scope.Truncated.Should().BeFalse("nothing was paged; asking for more returns nothing new");

        balance.Scope.Filters.Should().HaveFlag(
            CoachScopeFilters.MinimumEvidence,
            "the model-visible half of the bar: without it the gap has no stated cause");

        CoachScopeInvariants.Violations(balance.Scope).Should().BeEmpty();
    }

    [Fact]
    public async Task Practice_balance_names_the_evidence_bar_even_when_it_drops_nothing()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedCompletion(CoachToolTestFixture.UserA, "Reading", minutesSpent: 12, daysAgo: 0);

        var balance = await fixture.BalanceTool.GetAsync(CoachPracticeWindow.SevenDays);

        // Filter present with no count means "a bar was applied and nothing failed it". Filter
        // absent would mean the read has no bar at all, and the two must stay distinguishable —
        // the same convention every other filter on this shape follows.
        balance.Scope.Filters.Should().HaveFlag(CoachScopeFilters.MinimumEvidence);
        balance.Scope.WithheldCount.Should().Be(0);
        balance.Scope.WithheldReason.Should().Be(CoachScopeWithheldReason.None);
        balance.Scope.MatchedCount.Should().Be(1);
        balance.Scope.ReturnedCount.Should().Be(1);

        CoachScopeInvariants.Violations(balance.Scope).Should().BeEmpty();
    }

    [Fact]
    public async Task Practice_balance_counts_activity_types_not_the_completion_rows_behind_them()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        // Three logged sessions, two activity types. Counting rows where types belong reported
        // more eligible than matched, which is arithmetic no learner's account can produce.
        fixture.SeedCompletion(user, "Reading", minutesSpent: 12, daysAgo: 0);
        fixture.SeedCompletion(user, "Reading", minutesSpent: 8, daysAgo: 1);
        fixture.SeedCompletion(user, "Writing", minutesSpent: 5, daysAgo: 2);

        var balance = await fixture.BalanceTool.GetAsync(CoachPracticeWindow.SevenDays);

        balance.Scope.MatchedCount.Should().Be(2);
        balance.Scope.EligiblePopulationCount.Should().Be(2);
        balance.Scope.ReturnedCount.Should().Be(2);

        CoachScopeInvariants.Violations(balance.Scope).Should().BeEmpty();
    }

    [Fact]
    public async Task Resource_catalog_says_when_it_is_a_page_rather_than_the_shelf()
    {
        using var fixture = new CoachToolTestFixture();
        for (var i = 0; i < 5; i++)
        {
            fixture.SeedResource(CoachToolTestFixture.UserA, title: $"Resource {i}");
        }

        var page = await fixture.ResourceTool.GetAsync(maxResults: 2);

        page.Scope.Coverage.Should().Be(CoachScopeCoverage.PageOfOwnedSet);
        page.Scope.MatchedCount.Should().Be(5);
        page.Scope.ReturnedCount.Should().Be(2);
        page.Scope.RequestedCount.Should().Be(2);
        page.Scope.Truncated.Should().BeTrue();
        page.Scope.Order.Should().Be(CoachScopeOrder.LastUsedAscending);
        page.Scope.TieBreak.Should().Be(CoachScopeTieBreak.TitleOrdinal);

        var whole = await fixture.ResourceTool.GetAsync(maxResults: 50);
        whole.Scope.Coverage.Should().Be(CoachScopeCoverage.CompleteOwnedSet);
        whole.Scope.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Skill_reads_declare_that_the_archive_is_out_of_scope()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;
        var active = fixture.SeedSkill(user, title: "Ordering food");
        fixture.SeedSkill(user, title: "Archived", archived: true);

        var list = await fixture.SkillListTool.GetAsync();
        list.Scope.Filters.Should().HaveFlag(CoachScopeFilters.ExcludeArchived);
        list.Scope.MatchedCount.Should().Be(1);
        list.Scope.Coverage.Should().Be(CoachScopeCoverage.CompleteOwnedSet);
        list.Scope.DefinitionCode.Should().Be(CoachScopeDefinition.ActiveSkillList);

        var detail = await fixture.SkillDetailTool.GetAsync(active.Id);
        detail.Scope.Filters.Should().HaveFlag(CoachScopeFilters.ExcludeArchived);
        detail.Scope.Coverage.Should().Be(CoachScopeCoverage.SingleItem);
    }

    [Fact]
    public async Task The_due_summary_separates_its_complete_aggregates_from_its_paged_breakdown()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        foreach (var tag in new[] { "a", "b", "c", "d", "e" })
        {
            var word = fixture.SeedWord($"단어-{tag}", $"word {tag}", tags: tag);
            fixture.SeedProgress(user, word.Id, nextReviewDate: fixture.Now.AddDays(-1));
        }

        var summary = await fixture.VocabularyTool.GetAsync(maxCategoryTags: 2);

        summary.CategoryTags.Should().HaveCount(2);

        // One coverage value naming both populations. CompleteOwnedSet with Truncated set said
        // "you have all of it" and "you do not" in the same breath; PageOfOwnedSet would have
        // understated word counts that really are complete.
        summary.Scope.Coverage.Should().Be(CoachScopeCoverage.CompleteAggregateWithBreakdown);
        summary.Scope.Truncated.Should().BeTrue();

        // The aggregates are complete and live on the answer body, where they cannot be confused
        // with the scope's counts.
        summary.TrackedWordCount.Should().Be(5);
        summary.DueNowCount.Should().Be(5);

        // Every count on the scope is about the tag breakdown, which is what the coverage says.
        summary.Scope.MatchedCount.Should().Be(5, "five distinct tags were found on the due words");
        summary.Scope.EligiblePopulationCount.Should().Be(
            5, "nothing is withheld from the tag list, so every matched tag is eligible");
        summary.Scope.ReturnedCount.Should().Be(2);
        summary.Scope.RequestedCount.Should().Be(2);
        summary.Scope.Order.Should().Be(CoachScopeOrder.FrequencyDescending);
        summary.Scope.TieBreak.Should().Be(CoachScopeTieBreak.TagOrdinal);

        CoachScopeInvariants.Violations(summary.Scope).Should().BeEmpty();
    }

    [Fact]
    public async Task The_due_summary_keeps_its_mixed_population_coverage_when_the_breakdown_fits()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        var word = fixture.SeedWord("사과", "apple", tags: "food");
        fixture.SeedProgress(user, word.Id, nextReviewDate: fixture.Now.AddDays(-1));

        var summary = await fixture.VocabularyTool.GetAsync(maxCategoryTags: 8);

        // Deliberately not data-dependent. A coverage that flipped to CompleteOwnedSet whenever
        // the tags happened to fit would leave the model unable to tell, on any given call, which
        // population MatchedCount was counting.
        summary.Scope.Coverage.Should().Be(CoachScopeCoverage.CompleteAggregateWithBreakdown);
        summary.Scope.Truncated.Should().BeFalse();
        summary.Scope.MatchedCount.Should().Be(1);
        summary.Scope.EligiblePopulationCount.Should().Be(1);
        summary.Scope.ReturnedCount.Should().Be(1);

        CoachScopeInvariants.Violations(summary.Scope).Should().BeEmpty();
    }

    [Fact]
    public async Task The_due_summary_scope_counts_tags_even_when_the_learner_owns_more_words()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;

        // Six words, two tags. The scope used to report six eligible against two matched, which
        // is two populations wearing one set of counts.
        for (var i = 0; i < 6; i++)
        {
            var word = fixture.SeedWord($"단어{i}", $"word {i}", tags: i % 2 == 0 ? "food" : "travel");
            fixture.SeedProgress(user, word.Id, nextReviewDate: fixture.Now.AddDays(-1));
        }

        var summary = await fixture.VocabularyTool.GetAsync(maxCategoryTags: 8);

        summary.TrackedWordCount.Should().Be(6);
        summary.Scope.MatchedCount.Should().Be(2);
        summary.Scope.EligiblePopulationCount.Should().Be(2);
        summary.Scope.ReturnedCount.Should().Be(2);

        CoachScopeInvariants.Violations(summary.Scope).Should().BeEmpty();
    }

    [Fact]
    public async Task The_plan_summary_scopes_itself_to_one_local_day_and_claims_no_order()
    {
        using var fixture = new CoachToolTestFixture();
        var user = CoachToolTestFixture.UserA;
        fixture.SeedPlan(user);
        fixture.SeedCompletion(user, "Reading", 10, daysAgo: 0);
        fixture.SeedCompletion(user, "Writing", 0, daysAgo: 0, isCompleted: false);

        var summary = await fixture.CurrentPlanSummaryTool.GetAsync();

        summary.Scope.Coverage.Should().Be(CoachScopeCoverage.SingleDay);
        summary.Scope.WindowStartDate.Should().Be(fixture.Today);
        summary.Scope.WindowEndDate.Should().Be(fixture.Today);
        summary.Scope.Filters.Should().Be(CoachScopeFilters.OwnerScoped | CoachScopeFilters.CalendarDay);
        summary.Scope.Order.Should().Be(
            CoachScopeOrder.Unordered, "the items come back in storage order and nothing claims otherwise");
        summary.Scope.ReturnedCount.Should().Be(2);
        summary.Scope.ClockBasis.Should().Be(CoachScopeClockBasis.LearnerLocalDay);

        var empty = await new CoachToolTestFixture().CurrentPlanSummaryTool.GetAsync();
        empty.Scope.Coverage.Should().Be(CoachScopeCoverage.SingleDay);
        empty.Scope.ReturnedCount.Should().Be(0);
    }

    [Fact]
    public async Task The_settings_reads_present_themselves_as_a_snapshot_not_a_set()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        foreach (var scope in new[]
        {
            (await fixture.ProfileTool.GetAsync()).Scope,
            (await fixture.LearnerSettingsSummaryTool.GetAsync()).Scope,
            (await fixture.CurrentProfileSummaryTool.GetAsync()).Scope
        })
        {
            scope.Coverage.Should().Be(CoachScopeCoverage.SettingsSnapshot);
            scope.Order.Should().Be(CoachScopeOrder.NotApplicable);
            scope.TieBreak.Should().Be(CoachScopeTieBreak.NotApplicable);
            scope.ReturnedCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task The_overview_summary_names_every_predicate_that_shaped_a_count()
    {
        using var fixture = new CoachToolTestFixture();
        fixture.SeedProfile(CoachToolTestFixture.UserA);

        var summary = await fixture.CurrentProfileSummaryTool.GetAsync();

        summary.Scope.Filters.Should().HaveFlag(CoachScopeFilters.ExcludeArchived,
            "the skill count leaves out the archive");
        summary.Scope.Filters.Should().HaveFlag(CoachScopeFilters.ProgressRowExists,
            "the word count only sees words the learner has practised at least once");
        summary.Scope.DefinitionCode.Should().Be(CoachScopeDefinition.LearnerOverviewSummary);
    }

    [Fact]
    public async Task The_resource_and_detail_reads_state_the_calendar_they_counted_days_on()
    {
        using var fixture = new CoachToolTestFixture();
        var resource = fixture.SeedResource(CoachToolTestFixture.UserA);

        var catalog = await fixture.ResourceTool.GetAsync();
        catalog.Scope.ClockBasis.Should().Be(CoachScopeClockBasis.LearnerLocalDay);
        catalog.Scope.ReferenceMode.Should().Be(CoachScopeReferenceMode.CalendarDay);

        var detail = await fixture.LearningResourceDetailTool.GetAsync(resource.Id);
        detail.Scope.ClockBasis.Should().Be(CoachScopeClockBasis.LearnerLocalDay);
        detail.Scope.Coverage.Should().Be(CoachScopeCoverage.SingleItem);
        detail.Scope.Filters.Should().Be(CoachScopeFilters.OwnerScoped | CoachScopeFilters.SingleIdentifier);

        var list = await fixture.LearningResourceListTool.GetAsync();
        list.Scope.Order.Should().Be(CoachScopeOrder.UpdatedDescending);
        list.Scope.DefinitionCode.Should().Be(CoachScopeDefinition.OwnedResourceList);
    }

    [Fact]
    public async Task The_scope_the_model_receives_is_pinned_to_its_shipped_projection()
    {
        using var fixture = new CoachToolTestFixture(TickPreciseNow);
        var user = CoachToolTestFixture.UserA;
        var word = fixture.SeedWord("사과", "apple", tags: "food");
        fixture.SeedProgress(user, word.Id);

        var result = await fixture.VocabularySearchTool.SearchAsync();

        var compact = new JsonSerializerOptions(ModelSerializerOptions) { WriteIndented = false };
        var json = JsonSerializer.Serialize(result.Scope, compact);

        // Pinned because the cost is paid on every tool call of every turn, up to the twenty-call
        // budget, and because a field added here reaches the model before any review notices. The
        // absent members are the point: no withheldCount when nothing was withheld, no truncated
        // when nothing was, and none of the six foundation members at all.
        //
        // The fixture clock carries sub-second ticks and the pinned text does not, which is the
        // normalizer being demonstrated rather than described.
        json.Should().Be(
            """
            {"coverage":"CompleteOwnedSet","order":"MasteryDescending","orderHonored":true,"filters":"OwnerScoped, ExcludeDue, ProgressRowExists","asOfUtc":"2026-08-14T12:00:00Z","requestedCount":10,"returnedCount":1,"matchedCount":1}
            """.Trim());

        json.Length.Should().BeLessThan(
            ScopeCharacterCeiling,
            "a scope is metadata about an answer, not a second answer; twenty of these ride along "
            + "with a single turn's tool budget");
    }

    [Fact]
    public async Task The_revised_scopes_are_pinned_to_the_exact_text_the_model_reads()
    {
        using var fixture = new CoachToolTestFixture(TickPreciseNow);
        var user = CoachToolTestFixture.UserA;
        var compact = new JsonSerializerOptions(ModelSerializerOptions) { WriteIndented = false };

        foreach (var tag in new[] { "a", "b", "c" })
        {
            var word = fixture.SeedWord($"단어-{tag}", $"word {tag}", tags: tag);
            fixture.SeedProgress(user, word.Id, nextReviewDate: fixture.Now.AddDays(-1));
        }

        fixture.SeedCompletion(user, "Reading", minutesSpent: 12, daysAgo: 0);
        fixture.SeedCompletion(user, "Writing", minutesSpent: 0, daysAgo: 1, isCompleted: false);

        var due = await fixture.VocabularyTool.GetAsync(maxCategoryTags: 2);
        var balance = await fixture.BalanceTool.GetAsync(CoachPracticeWindow.SevenDays);

        var dueJson = JsonSerializer.Serialize(due.Scope, compact);
        var balanceJson = JsonSerializer.Serialize(balance.Scope, compact);

        // Pinned rather than asserted field by field, because the thing under review is the
        // sentence a model reads off this object. "CompleteAggregateWithBreakdown" beside
        // "truncated": true is a coherent statement; "CompleteOwnedSet" beside it was not.
        dueJson.Should().Be(
            """
            {"coverage":"CompleteAggregateWithBreakdown","order":"FrequencyDescending","orderHonored":true,"filters":"OwnerScoped, ProgressRowExists","asOfUtc":"2026-08-14T12:00:00Z","requestedCount":2,"returnedCount":2,"matchedCount":3,"truncated":true}
            """.Trim());

        // Two matched, one returned, one withheld, a named reason and the filter that produced it.
        // The arithmetic closes on the wire, so there is no gap left for the model to explain.
        balanceJson.Should().Be(
            """
            {"coverage":"WindowBounded","order":"MinutesDescending","orderHonored":true,"filters":"OwnerScoped, DateWindow, MinimumEvidence","asOfUtc":"2026-08-14T12:00:00Z","windowStartDate":"2026-08-08","windowEndDate":"2026-08-14","returnedCount":1,"matchedCount":2,"withheldCount":1,"withheldReason":"BelowMinimumEvidence"}
            """.Trim());

        // The ceiling is unchanged and still enforced by Every_scope_stays_within_its_token_budget.
        // The revision spends its budget on one longer coverage name and one extra filter flag,
        // not on new fields.
        //
        // These two lengths are for THIS fixture, which has three activity types and single-digit
        // counts. They are not the worst case and must not be read as it: the worst case is the
        // practice balance with every activity type in the window, pinned at 316 by
        // The_widest_scope_is_pinned_with_its_name_and_its_remaining_headroom. Reporting 315 as
        // "the closest to the line" is what the previous revision did, and it was two mistakes at
        // once — a fixture clock with no sub-second component, and a fixture with two activity
        // types where a learner has thirteen.
        dueJson.Length.Should().Be(242);
        balanceJson.Length.Should().Be(315);
        dueJson.Length.Should().BeLessThan(ScopeCharacterCeiling);
        balanceJson.Length.Should().BeLessThan(ScopeCharacterCeiling);

        // The clock this fixture runs on is sub-second, so the whole-second text pinned above is
        // the normalizer's output rather than the clock's.
        fixture.Now.Ticks.Should().NotBe(
            CoachResultScope.NormalizeAsOf(fixture.Now).Ticks,
            "the pin is only evidence of normalization if the input needed normalizing");
    }

    [Fact]
    public async Task Every_scope_stays_within_its_token_budget()
    {
        using var fixture = SeededFixture();
        var compact = new JsonSerializerOptions(ModelSerializerOptions) { WriteIndented = false };

        foreach (var (name, scope) in await ScopesAsync(fixture))
        {
            var json = JsonSerializer.Serialize(scope, compact);
            json.Length.Should().BeLessThan(
                ScopeCharacterCeiling,
                "{0} spends {1} characters of the model's context on scope metadata",
                name,
                json.Length);
        }
    }

    /// <summary>
    /// The widest scope any registered read produces, pinned with its name and its headroom.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ceiling test above says nobody is over the line. This one says how close the closest one
    /// is, which is the number that was wrong before: the practice balance was reported at 315 of
    /// 320 against a whole-second fixture clock, and the same read on a production clock was 323 —
    /// already over, in every deployment, while the suite stayed green.
    /// </para>
    /// <para>
    /// Pinning the worst case by name and by value means the next field, the next filter flag, and
    /// the next longer enum name all arrive here as a diff with a number attached, rather than as a
    /// threshold that quietly stopped having room.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_widest_scope_is_pinned_with_its_name_and_its_remaining_headroom()
    {
        using var fixture = SeededFixture();
        var compact = new JsonSerializerOptions(ModelSerializerOptions) { WriteIndented = false };

        var measured = (await ScopesAsync(fixture))
            .Select(s => (s.Name, Json: JsonSerializer.Serialize(s.Scope, compact)))
            .OrderByDescending(s => s.Json.Length)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        var widest = measured[0];

        widest.Name.Should().Be(
            CoachToolNames.GetPracticeBalance,
            "it is the only scope carrying both window dates, a withheld count and a withheld "
            + "reason at once");

        widest.Json.Length.Should().Be(
            316,
            "the practice balance on a production clock with every activity type in the window is "
            + "the true worst case; it was measured at 315 only because the fixture clock had no "
            + "sub-second component and only two activity types");

        (ScopeCharacterCeiling - widest.Json.Length).Should().Be(
            4,
            "four characters is the real headroom. It is thin, and it is now the number under "
            + "review rather than a larger one produced by a fixture");
    }

    /// <summary>
    /// The budget fixture is shaped like production, and would fail if it were quietly narrowed.
    /// </summary>
    /// <remarks>
    /// The guard on the guard. Every assertion about the token ceiling is only worth the fixture
    /// behind it, and the two ways this one can be disarmed without anybody noticing are reverting
    /// the clock to a whole second and thinning the seed data back to single-digit counts. Both are
    /// asserted here, so either one fails a test with a name that says what happened.
    /// </remarks>
    [Fact]
    public async Task The_budget_fixture_is_shaped_like_production()
    {
        using var fixture = SeededFixture();

        (fixture.Now.Ticks % TimeSpan.TicksPerSecond).Should().NotBe(
            0,
            "a whole-second fixture clock produces a scope eight characters shorter than any "
            + "deployment does, which is what made the ceiling unfalsifiable");

        var scopes = await ScopesAsync(fixture);

        var balance = scopes.Single(s => s.Name == CoachToolNames.GetPracticeBalance).Scope;

        balance.MatchedCount.Should().BeGreaterThanOrEqualTo(
            10, "a single-digit matched count understates what the count field costs");
        balance.ReturnedCount.Should().BeGreaterThanOrEqualTo(2);
        balance.WithheldCount.Should().BeGreaterThanOrEqualTo(
            2, "the withheld count has to be wide enough to cost a second digit too");

        // The population the practice balance counts is activity types, so the enum is the honest
        // ceiling on how wide those counts can get. Asserting the fixture reaches it is what makes
        // "worst case" a fact rather than a hope.
        balance.MatchedCount.Should().Be(
            Enum.GetNames<SentenceStudio.Services.Progress.PlanActivityType>().Length,
            "every activity type the generator can emit appears in this window");
    }

    /// <summary>
    /// Sub-second precision never reaches the model, on any registered read.
    /// </summary>
    /// <remarks>
    /// Asserted on the serialized text rather than on the property, because the text is what costs
    /// tokens and what the model reads. A normalizer that ran on construction but was undone by a
    /// converter, a <c>with</c> expression, or a tool that assigned the field twice would satisfy a
    /// property check and fail this one.
    /// </remarks>
    [Fact]
    public async Task No_registered_read_sends_sub_second_precision_to_the_model()
    {
        using var fixture = SeededFixture();
        var compact = new JsonSerializerOptions(ModelSerializerOptions) { WriteIndented = false };

        foreach (var (name, scope) in await ScopesAsync(fixture))
        {
            (scope.AsOfUtc.Ticks % TimeSpan.TicksPerSecond).Should().Be(
                0, "{0} states an instant the read is not accurate to", name);

            scope.AsOfUtc.Kind.Should().Be(DateTimeKind.Utc, "{0}", name);

            var asOf = JsonSerializer.SerializeToElement(scope, compact)
                .GetProperty("asOfUtc").GetString();

            asOf.Should().Be(
                "2026-08-14T12:00:00Z",
                "{0} must render the fixture's sub-second clock as a whole second", name);
        }
    }

    /// <summary>
    /// Without the normalizer the widest scope would be over the ceiling, not near it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterfactual, so the fix is demonstrably load-bearing rather than tidy. The string
    /// built here is exactly what the practice balance emitted before
    /// <c>CoachResultScope.NormalizeAsOf</c> existed: the same scope with the clock's sub-second
    /// component restored.
    /// </para>
    /// <para>
    /// If someone removes the normalizer, the ceiling sweep fails — but it fails with "323 &gt; 320"
    /// and no explanation. This test fails with a name that says which eight characters came back.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Without_normalization_the_widest_scope_would_breach_the_ceiling()
    {
        using var fixture = SeededFixture();
        var compact = new JsonSerializerOptions(ModelSerializerOptions) { WriteIndented = false };

        var balance = (await ScopesAsync(fixture))
            .Single(s => s.Name == CoachToolNames.GetPracticeBalance).Scope;

        var normalized = JsonSerializer.Serialize(balance, compact);

        var unnormalized = normalized.Replace(
            "\"asOfUtc\":\"2026-08-14T12:00:00Z\"",
            "\"asOfUtc\":\"2026-08-14T12:00:00.4821593Z\"",
            StringComparison.Ordinal);

        unnormalized.Should().NotBe(normalized, "the counterfactual must actually differ");

        (unnormalized.Length - normalized.Length).Should().Be(
            8, "seven fractional digits and the point they hang off");

        unnormalized.Length.Should().BeGreaterThan(
            ScopeCharacterCeiling,
            "this is the string every deployment was already sending, on every one of a turn's "
            + "twenty tool calls, while the suite measured a shorter one");
    }

    [Fact]
    public async Task A_withheld_count_and_a_withheld_reason_never_disagree()
    {
        using var fixture = SeededFixture();

        foreach (var (name, scope) in await ScopesAsync(fixture))
        {
            (scope.WithheldCount > 0).Should().Be(
                scope.WithheldReason != CoachScopeWithheldReason.None,
                "{0} reports {1} withheld rows for reason '{2}'; a count with no reason is unexplained "
                + "and a reason with no count is a warning about nothing. Both are omitted from the "
                + "model's view when they are zero, so a disagreement between them would be invisible.",
                name,
                scope.WithheldCount,
                scope.WithheldReason);
        }
    }

    [Fact]
    public async Task Every_scope_satisfies_the_count_invariants()
    {
        using var fixture = SeededFixture();

        var failures = new List<string>();
        foreach (var (name, scope) in await ScopesAsync(fixture))
        {
            failures.AddRange(CoachScopeInvariants.Violations(scope).Select(v => $"{name} {v}"));
        }

        failures.Should().BeEmpty(
            "a scope whose counts do not add up is a scope the model reconciles by guessing");
    }

    [Fact]
    public async Task The_invariant_sweep_actually_exercises_truncation_and_withholding()
    {
        // The rule this guards is the one that made the previous sweep worthless: it asserted
        // that a truncated answer never claims a complete set, over a fixture holding one word,
        // one tag, one resource and one activity type — so nothing ever truncated, nothing was
        // ever withheld, and the assertion passed without being evaluated even once. A sweep that
        // cannot fail is a sweep that is not a test, and the failure it was written to catch
        // shipped underneath it.
        using var fixture = SeededFixture();
        var scopes = await ScopesAsync(fixture);

        scopes.Where(s => s.Scope.Truncated).Select(s => s.Name).Should().NotBeEmpty(
            "at least one tool in the sweep fixture must actually page its answer");

        scopes.Where(s => s.Scope.WithheldCount > 0).Select(s => s.Name).Should().NotBeEmpty(
            "at least one tool in the sweep fixture must actually withhold rows");

        scopes.Select(s => s.Scope.WithheldReason)
            .Where(r => r != CoachScopeWithheldReason.None)
            .Distinct()
            .Should().HaveCountGreaterThanOrEqualTo(
                2,
                "the fixture must exercise more than one reason, or a rule about the wrong one "
                + "passes for the right one");

        scopes.Select(s => s.Scope.Coverage).Should().Contain(
            CoachScopeCoverage.CompleteAggregateWithBreakdown,
            "the mixed-population coverage must be reached, not merely declared");
    }

    [Fact]
    public async Task Every_registered_read_participates_in_the_sweep()
    {
        // PlanPreviewSummary was registered, stated a scope, and was absent from this list, so
        // every rule below held over twelve of the thirteen reads and said nothing about the
        // thirteenth. Deriving the expected set from the registry means the next tool added
        // cannot slip past the same way.
        using var fixture = SeededFixture();

        var swept = (await ScopesAsync(fixture)).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        var registered = FullRegistry().All
            .Where(r => r.RiskClass == CoachToolRiskClass.Read)
            .Select(r => r.Name)
            .ToList();

        registered.Should().NotBeEmpty();
        swept.Should().BeEquivalentTo(
            registered,
            "a read that is not in the sweep is a read whose scope nothing checks");
    }

    // =====================================================================
    // Mutation: the checker must reject the shapes that were rejected
    // =====================================================================

    [Fact]
    public void The_checker_rejects_a_complete_set_that_also_reports_truncation()
    {
        // The due summary's rejected shape, rebuilt by hand: aggregate counts covering every
        // tracked word, a paged tag list, and one coverage value forced to describe both. Each
        // field is individually true; together they say the answer is complete and incomplete.
        var contradiction = CoachResultScopeSamples.Any() with
        {
            Coverage = CoachScopeCoverage.CompleteOwnedSet,
            RequestedCount = 2,
            ReturnedCount = 2,
            MatchedCount = 5,
            EligiblePopulationCount = 5,
            Truncated = true
        };

        CoachScopeInvariants.Violations(contradiction).Should().NotBeEmpty(
            "this is the shape Simon rejected; a checker that passes it checks nothing");

        string.Join(" ", CoachScopeInvariants.Violations(contradiction))
            .Should().Contain("truncation");
    }

    [Fact]
    public void The_checker_rejects_a_scope_whose_counts_span_two_populations()
    {
        // The due summary's other half: MatchedCount counting tags while EligiblePopulationCount
        // counted words. Harmless while a learner owns as many tags as words, which is exactly
        // how it survived a seeded fixture.
        var mixed = CoachResultScopeSamples.Any() with
        {
            Coverage = CoachScopeCoverage.CompleteAggregateWithBreakdown,
            RequestedCount = 8,
            ReturnedCount = 2,
            MatchedCount = 2,
            EligiblePopulationCount = 6
        };

        CoachScopeInvariants.Violations(mixed).Should().NotBeEmpty(
            "six eligible out of two matched counts two different sets of rows");
    }

    [Fact]
    public void The_checker_rejects_an_unexplained_gap_between_matched_and_returned()
    {
        // The practice balance's rejected shape: two activity types matched, one returned, nothing
        // paged, and no withheld count, reason or filter to say where the other one went.
        var unexplained = CoachResultScopeSamples.Any() with
        {
            Coverage = CoachScopeCoverage.WindowBounded,
            Filters = CoachScopeFilters.OwnerScoped | CoachScopeFilters.DateWindow,
            ReturnedCount = 1,
            MatchedCount = 2,
            Truncated = false
        };

        CoachScopeInvariants.Violations(unexplained).Should().NotBeEmpty(
            "a matched row that is neither returned, withheld, nor paged has no stated fate");

        string.Join(" ", CoachScopeInvariants.Violations(unexplained))
            .Should().Contain("unaccounted");
    }

    [Fact]
    public void The_checker_rejects_withholding_that_names_no_predicate()
    {
        // Reporting the count without the filter would leave the model told that something was
        // held back and not why, which reads as a paging boundary it can ask past.
        var unnamed = CoachResultScopeSamples.Any() with
        {
            Coverage = CoachScopeCoverage.WindowBounded,
            Filters = CoachScopeFilters.OwnerScoped | CoachScopeFilters.DateWindow,
            ReturnedCount = 1,
            MatchedCount = 2,
            WithheldCount = 1,
            WithheldReason = CoachScopeWithheldReason.BelowMinimumEvidence
        };

        string.Join(" ", CoachScopeInvariants.Violations(unnamed))
            .Should().Contain("MinimumEvidence");
    }

    [Fact]
    public void The_checker_accepts_the_shapes_the_revision_produces()
    {
        // The negative cases above only mean something if the checker is not simply strict. These
        // are the two revised shapes and the reference implementation they were made to match.
        var dueSummary = CoachResultScopeSamples.Any() with
        {
            Coverage = CoachScopeCoverage.CompleteAggregateWithBreakdown,
            RequestedCount = 2,
            ReturnedCount = 2,
            MatchedCount = 5,
            EligiblePopulationCount = 5,
            Truncated = true
        };

        var practiceBalance = CoachResultScopeSamples.Any() with
        {
            Coverage = CoachScopeCoverage.WindowBounded,
            Filters = CoachScopeFilters.OwnerScoped
                | CoachScopeFilters.DateWindow
                | CoachScopeFilters.MinimumEvidence,
            ReturnedCount = 1,
            MatchedCount = 2,
            WithheldCount = 1,
            WithheldReason = CoachScopeWithheldReason.BelowMinimumEvidence,
            EligiblePopulationCount = 1
        };

        // The vocabulary search, which already satisfied all four rules and is what the others
        // were generalized from: fourteen matched, four embargoed, ten eligible, ten returned.
        var vocabularySearch = CoachResultScopeSamples.Any() with
        {
            Coverage = CoachScopeCoverage.CompleteOwnedSet,
            Filters = CoachScopeFilters.OwnerScoped
                | CoachScopeFilters.ExcludeDue
                | CoachScopeFilters.ProgressRowExists,
            RequestedCount = 10,
            ReturnedCount = 10,
            MatchedCount = 14,
            WithheldCount = 4,
            WithheldReason = CoachScopeWithheldReason.DueReviewEmbargo,
            EligiblePopulationCount = 10
        };

        CoachScopeInvariants.Violations(dueSummary).Should().BeEmpty();
        CoachScopeInvariants.Violations(practiceBalance).Should().BeEmpty();
        CoachScopeInvariants.Violations(vocabularySearch).Should().BeEmpty();
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    /// <summary>
    /// A fixture that reaches every branch the sweep is supposed to police, on a production-shaped
    /// clock and with counts wide enough to cost what they really cost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first version seeded one of everything, so no read ever paged and no read ever withheld.
    /// This one deliberately exceeds a page limit and deliberately fails an evidence bar, and
    /// <c>The_invariant_sweep_actually_exercises_truncation_and_withholding</c> holds it to that.
    /// </para>
    /// <para>
    /// Two further things it now does, both aimed at the token budget. It runs on
    /// <see cref="TickPreciseNow"/>, so the scopes it produces are the byte-for-byte shape a
    /// deployment produces. And it seeds every activity type the plan generator can emit, most of
    /// them without logged work, so the practice balance reports two-digit matched, returned and
    /// withheld counts instead of the single digits a three-row fixture produced. A budget measured
    /// against <c>"matchedCount":2</c> is not measuring the answer a real learner gets.
    /// </para>
    /// </remarks>
    private static CoachToolTestFixture SeededFixture()
    {
        var fixture = new CoachToolTestFixture(TickPreciseNow);
        var user = CoachToolTestFixture.UserA;

        fixture.SeedProfile(user);
        fixture.SeedSkill(user);
        fixture.SeedPlan(user);

        // Six resources against a default page of five: the catalog and the resource list page.
        for (var i = 0; i < 6; i++)
        {
            fixture.SeedResource(user, title: $"Resource {i}");
        }

        // Nine tagged due words against the default eight-tag breakdown: the due summary pages its
        // tag list while its word counts stay complete.
        for (var i = 0; i < 9; i++)
        {
            var due = fixture.SeedWord($"단어-due-{i}", $"due word {i}", tags: $"tag{i}");
            fixture.SeedProgress(user, due.Id, nextReviewDate: fixture.Now.AddDays(-1));
        }

        // One undue word, so the vocabulary search has something to return while the nine due ones
        // above are embargoed out of it.
        var undue = fixture.SeedWord("사과", "apple", tags: "food");
        fixture.SeedProgress(user, undue.Id, nextReviewDate: fixture.Now.AddDays(30));

        // Every activity type the plan generator emits, appearing in the window. Six carry logged
        // work and seven do not, so the practice balance withholds for want of evidence and every
        // one of its counts is two digits wide — which is the widest they can honestly be, because
        // PlanActivityType has thirteen members and the population it counts is activity types.
        SeedEveryActivityType(fixture, user);
        fixture.SeedActivity(user, daysAgo: 0);

        return fixture;
    }

    /// <summary>
    /// Seeds one completion per <c>PlanActivityType</c>, alternating logged work and none.
    /// </summary>
    /// <remarks>
    /// Derived from the enum rather than from a hand-written list, so a fourteenth activity type
    /// widens this fixture — and therefore the budget measurement — without anyone remembering to
    /// come back here. The counts the practice balance reports are counts of activity types, so the
    /// enum is exactly the bound on how large they can get.
    /// </remarks>
    private static void SeedEveryActivityType(CoachToolTestFixture fixture, string user)
    {
        var types = Enum.GetNames<SentenceStudio.Services.Progress.PlanActivityType>();

        for (var i = 0; i < types.Length; i++)
        {
            var hasEvidence = i % 2 == 0;
            fixture.SeedCompletion(
                user,
                types[i],
                minutesSpent: hasEvidence ? 10 + i : 0,
                daysAgo: i % 7,
                isCompleted: hasEvidence);
        }
    }

    /// <summary>Every read tool's answer, reduced to the scope it stated.</summary>
    private static async Task<IReadOnlyList<(string Name, CoachResultScope Scope)>> ScopesAsync(
        CoachToolTestFixture fixture)
    {
        var resource = (await fixture.LearningResourceListTool.GetAsync()).Resources[0].ResourceId;
        var skill = (await fixture.SkillListTool.GetAsync()).Skills[0].SkillId;
        var word = (await fixture.VocabularySearchTool.SearchAsync()).Words[0].WordId;

        return
        [
            (CoachToolNames.GetLearnerProfileSummary, (await fixture.ProfileTool.GetAsync()).Scope),
            (CoachToolNames.GetPracticeBalance, (await fixture.BalanceTool.GetAsync(CoachPracticeWindow.SevenDays)).Scope),
            (CoachToolNames.GetVocabularyDueSummary, (await fixture.VocabularyTool.GetAsync()).Scope),
            (CoachToolNames.GetResourceCatalog, (await fixture.ResourceTool.GetAsync(maxResults: 5)).Scope),
            (CoachToolNames.PreviewPracticePlan, (await fixture.PreviewTool().PreviewAsync(new CoachPlanPreviewArguments { AvailableMinutes = 10 })).Scope),
            (CoachToolNames.ListUserVocabularies, (await fixture.VocabularySearchTool.SearchAsync()).Scope),
            (CoachToolNames.GetVocabularyWordDetail, (await fixture.VocabularyWordDetailTool.GetAsync(word)).Scope),
            (CoachToolNames.GetSkillList, (await fixture.SkillListTool.GetAsync()).Scope),
            (CoachToolNames.GetSkillDetail, (await fixture.SkillDetailTool.GetAsync(skill)).Scope),
            (CoachToolNames.GetLearningResourceList, (await fixture.LearningResourceListTool.GetAsync(maxResults: 5)).Scope),
            (CoachToolNames.GetLearningResourceDetail, (await fixture.LearningResourceDetailTool.GetAsync(resource)).Scope),
            (CoachToolNames.GetCurrentProfileSummary, (await fixture.CurrentProfileSummaryTool.GetAsync()).Scope),
            (CoachToolNames.GetLearnerSettingsSummary, (await fixture.LearnerSettingsSummaryTool.GetAsync()).Scope),
            (CoachToolNames.GetCurrentPlanSummary, (await fixture.CurrentPlanSummaryTool.GetAsync()).Scope),
            (CoachToolNames.GetPracticeHistorySummary, (await fixture.HistorySummaryTool.GetAsync()).Scope)
        ];
    }

    /// <summary>The same scopes as the model receives them: serialized with the tool options.</summary>
    private static async Task<IReadOnlyList<(string Name, JsonElement Scope)>> SerializedScopesAsync(
        CoachToolTestFixture fixture)
    {
        var scopes = await ScopesAsync(fixture);
        return scopes
            .Select(s => (
                s.Name,
                JsonSerializer.SerializeToElement(s.Scope, ModelSerializerOptions)))
            .ToList();
    }

    private static bool ReachesScope(Type root)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>([root]);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == typeof(CoachResultScope))
            {
                return true;
            }

            foreach (var property in current.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (type.IsGenericType && type.GetGenericArguments().Length == 1)
                {
                    type = type.GetGenericArguments()[0];
                }

                if (type.Namespace?.StartsWith("SentenceStudio", StringComparison.Ordinal) == true
                    && seen.Add(type))
                {
                    queue.Enqueue(type);
                }
            }
        }

        return false;
    }

    private static void Pinned<TEnum>(Dictionary<string, int> expected) where TEnum : struct, Enum
    {
        var actual = Enum.GetValues<TEnum>()
            .ToDictionary(v => v.ToString(), v => Convert.ToInt32(v));

        actual.Should().BeEquivalentTo(
            expected,
            "{0} is a closed vocabulary a stored or transmitted value can be read back against; "
            + "renumbering it silently changes the meaning of every value already written down",
            typeof(TEnum).Name);
    }

    /// <summary>A read envelope that forgot to say what it looked at.</summary>
    private sealed record UnscopedResult(int Count);

    /// <summary>A scope shape that could carry a term, a gloss, or the model's own query.</summary>
    [CoachScopeShape]
    private sealed record LeakyScope(int ReturnedCount, string Note);

    private sealed record LeakyScopedResult(int Count, LeakyScope Scope);
}
