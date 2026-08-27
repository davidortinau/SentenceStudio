using System.Reflection;
using FluentAssertions;
using FluentAssertions.Execution;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Api.Tests.Coach.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Gate;

/// <summary>
/// The W9 Learning Value Gate register: six blockers and LVG-W9-8, all closed, each bound to the
/// tests that closed it. Runbook §8.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>What replaced what.</b> This class was <c>CoachLearningValueGateBlockerTests</c>, and every
/// test in it asserted a defect so that closing the defect would turn it red. That worked for L4 —
/// it went red, and its own failure message dictated the replacement Wash then wrote. It did not
/// work for L1, L2, L3 and L5: the fix landed at a seam those tests never named
/// (<see cref="CoachRefusalLimitationProjection"/>, <c>CoachTurnResponse.Limitation</c> and the
/// client resource files, rather than <c>CoachDeterministicCopy</c> and
/// <c>CoachTurnGroundingResult</c>), so all four kept passing against a product gap that had
/// already been fixed. A tripwire that survives the fix is worse than no tripwire, because it
/// reports green with the authority of a test. They are gone.
/// </para>
/// <para>
/// <b>What this class is now.</b> A register that cannot rot: the closure named in the runbook is
/// checked to exist, by exact name, in the file the runbook cites. Rename or delete a closure test
/// and the gate record goes red rather than the documentation quietly becoming fiction. Beside the
/// register sit four assertions this workstream owns outright and that duplicate nobody: the
/// structural reasons server prose and take-up cannot reappear, and the shape of a refusal that
/// read nothing.
/// </para>
/// <para>
/// <b>Deliberately not re-asserted here.</b> The behaviour itself. <c>CoachRefusalContractTests</c>,
/// <c>CoachRefusalWithheldFactTests</c>, <c>CoachRefusalResumePostgresTests</c> and the three UI
/// suites are the acceptance matrix for the refusal surface; a second copy in the gate would be the
/// duplicate matrix W9 was told not to build, and would drift.
/// </para>
/// <para>
/// No production file is touched by this class.
/// </para>
/// </remarks>
public sealed class CoachLearningValueGateClosureTests
{
    // ═════════════════════════════════════════════════════════════════════════
    // The register — runbook §8.2, executable
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every closure the runbook claims exists, by exact name, in the suite it names.
    /// </summary>
    /// <remarks>
    /// The register is the load-bearing part of the sign-off: it is what turns "Zoe approves" into
    /// something a later reader can re-verify without taking anybody's word for it. Each row below
    /// is a claim of the form "blocker X is closed, and here is the test that says so"; this fails
    /// the moment one of those tests stops existing under that name.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    public void Every_closed_blocker_in_the_register_names_tests_that_exist()
    {
        var api = ApiTestNames();
        var ui = UiTestCorpus();

        using var _ = new AssertionScope();

        foreach (var (blocker, test) in Register)
        {
            var found = api.Contains(test) || ui.Contains(test, StringComparison.Ordinal);

            found.Should().BeTrue(
                $"runbook §8.2 records {blocker} as closed by {test}, and a register that names a "
                + "test nobody can find is a claim, not a closure");
        }
    }

    /// <summary>
    /// The register scan can fail, so a green above means something.
    /// </summary>
    /// <remarks>
    /// Both halves of the lookup are checked. A file-read that silently returned empty, or a
    /// reflection pass that found no test methods, would make every row above pass for the wrong
    /// reason — the same vacuity the grounding non-vacuity tests exist to prevent, pointed at the
    /// gate's own bookkeeping.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    public void The_register_scan_can_fail()
    {
        var api = ApiTestNames();
        var ui = UiTestCorpus();

        using var _ = new AssertionScope();

        api.Should().Contain(
            nameof(The_register_scan_can_fail),
            "the reflection half must find a test that is certainly present");

        ui.Should().Contain(
            "L3_a_refusal_names_a_real_screen_the_learner_can_go_to",
            "and the file half must find a UI test that is certainly present");

        api.Should().NotContain(
            "L9_a_closure_that_was_never_written",
            "a name nobody wrote must not resolve");

        ui.Should().NotContain(
            "L9_a_closure_that_was_never_written",
            "in either half");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // L4 — the one tripwire that worked, kept as its own closure
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// L4: disclosure is structural, so Korean and English get the same answer — and an unexplained
    /// count still fires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written per the instruction the red canary carried in its own failure message. The rule no
    /// longer reads prose for seven English verbs. A visible evidence item carrying a withheld count
    /// <em>together with</em> a known reason is the disclosure, because the client renders that pair
    /// in the learner's own language — so English, Korean, and a language nobody has added yet all
    /// get the same answer.
    /// </para>
    /// <para>
    /// Four halves, and the fourth is the one that matters. Without an answer that still fires, the
    /// three silences above would pass just as well against a deleted rule.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    public void L4_korean_disclosure_suppresses_withheld_and_korean_silence_still_fires()
    {
        // 단어 네 개를 보여 드립니다. / 열 개는 복습 예정이라 숨겼습니다.
        var korean = Scan(
            "\uB2E8\uC5B4 \uB124 \uAC1C\uB97C \uBCF4\uC5EC \uB4DC\uB9BD\uB2C8\uB2E4.",
            "\uC5F4 \uAC1C\uB294 \uBCF5\uC2B5 \uC608\uC815\uC774\uB77C \uC228\uACBC\uC2B5\uB2C8\uB2E4.");

        var english = Scan(
            "Here are four of your words.",
            "Ten more are held back because they are due for review.");

        // No prose in either language. The panel says it, so the answer does not have to.
        var silent = Scan("Here are your words.", "Let me know if you want more.");

        // A count with no reason the panel can render. Not disclosure in any language.
        var unexplained = Scan(
            "\uB2E8\uC5B4 \uB124 \uAC1C\uB97C \uBCF4\uC5EC \uB4DC\uB9BD\uB2C8\uB2E4.",
            "\uC5F4 \uAC1C\uB294 \uC228\uACBC\uC2B5\uB2C8\uB2E4.",
            reason: null);

        using var _ = new AssertionScope();

        korean.Should().NotContain(
            CoachClaimRuleCode.WithheldNotDisclosed,
            "the Korean learner's panel states the count and the reason, which is the disclosure");

        english.Should().NotContain(
            CoachClaimRuleCode.WithheldNotDisclosed,
            "and English gets the same answer, from the same structured pair rather than from a "
            + "list of English verbs");

        silent.Should().NotContain(
            CoachClaimRuleCode.WithheldNotDisclosed,
            "disclosure is a property of what was published, not of what the sentence repeated");

        unexplained.Should().Contain(
            CoachClaimRuleCode.WithheldNotDisclosed,
            "a count the panel cannot explain is still undisclosed \u2014 the non-vacuity half, without "
            + "which the three assertions above would pass on a deleted rule");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Structural bars this workstream owns
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// L1 and L6, structurally: the limitation the server ships has nowhere to put a sentence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The behavioural closures live in the UI suite, where the resource files are. This is the
    /// reason the defect cannot come back by accident rather than by decision: there is no
    /// string-typed member on the wire shape at all, so a future caller cannot pass English through
    /// it, and no learner content — a term, a gloss, an example — can ride along either.
    /// </para>
    /// <para>
    /// The route parameter's value is the deliberate exception and is checked separately by the
    /// contract suite: it carries a server-owned identifier or an ISO date, never learner text.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    public void The_shipped_limitation_carries_no_free_text()
    {
        typeof(CoachLimitationDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .Should().BeEmpty(
                "a refusal states a code and numbers; the sentence belongs to the client's resource "
                + "file, and a string member here is the seam server English came through before");
    }

    /// <summary>
    /// A refusal on a turn that read nothing is still a typed refusal, and states no number.
    /// </summary>
    /// <remarks>
    /// The no-evidence path is where an honest refusal is most tempted to invent something: an
    /// empty coverage reads as "none due", a zero count reads as a fact somebody checked, and a
    /// destination reads as a promise the read never supported. Unknown coverage with nothing
    /// beside it is the only truthful shape, and it is still <c>UnverifiedClaimWithheld</c> rather
    /// than silence.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    public void A_refusal_that_read_nothing_states_no_coverage_and_no_number()
    {
        var limitation = CoachRefusalLimitationProjection.Project(
            [],
            new DateTime(2026, 8, 22, 7, 30, 0, DateTimeKind.Utc));

        using var _ = new AssertionScope();

        limitation.Code.Should().Be(
            CoachLimitationCode.UnverifiedClaimWithheld,
            "the learner is still owed the reason, even when there is nothing underneath it");

        limitation.Coverage.Should().Be(
            CoachEvidenceCoverage.Unknown,
            "a turn that read nothing has unknown coverage, which is the honest answer");

        limitation.AffectedCount.Should().BeNull("a rendered zero is a fact the server checked");
        limitation.WithheldCount.Should().BeNull("nothing was held back because nothing was read");
        limitation.WithheldReason.Should().BeNull("and there is no reason to give");

        limitation.Destination.Should().BeNull(
            "no read means no definition, and a screen named without one is the fake destination "
            + "W7 was rejected for");
    }

    /// <summary>
    /// Plan §16.3 has no instrument, so a take-up rate cannot appear by accident.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assertion behind the runbook's §3.6 wording. The learner-visible substitute is
    /// now wired — a refusal carries its evidence and, where one genuinely follows, a typed
    /// destination — so §16.3 is no longer blocked on the product. It is blocked on measurement:
    /// nothing emits a take-up event, and nothing emits the refusal-with-destination population it
    /// would be divided by. No numerator, no denominator.
    /// </para>
    /// <para>
    /// Pinned here so the query's omission and the metric surface cannot drift apart. The day an
    /// instrument is added this goes red, and that is the signal to give §16.3 a numerator and a
    /// denominator in the query rather than to keep omitting it.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    public void No_take_up_or_destination_offered_instrument_exists()
    {
        var instruments = typeof(CoachGroundingMetrics)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        var takeUpish = new[] { "take_up", "takeup", "destination", "offer", "substitute_accepted" };

        using var _ = new AssertionScope();

        instruments.Should().NotBeEmpty("the scan must be able to see the meter's names at all");

        instruments
            .Where(name => takeUpish.Any(token =>
                name.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .Should().BeEmpty(
                "§16.3 substitution take-up has no numerator and no denominator on this build. It "
                + "is inactive, not zero: reporting a zero would say learners were offered a "
                + "substitute and declined it");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // P5/P6 repair disclosure — closed
    // ═════════════════════════════════════════════════════════════════════════

    // The scan that stood here asserted that no client source referenced RepairDisclosure, and
    // said in its own remarks: "close §9, add the §9.2 closure tests, and delete this scan". The
    // client now renders the disclosure, so the scan is structurally red and has done its job.
    // Its replacements live with the surface they describe, in
    // tests/SentenceStudio.UI.Tests/Coach/CoachRepairDisclosureWiringTests.cs.

    /// <summary>
    /// §9.3 names a test for each of R1–R6, and each of those tests exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §9 is kept out of <see cref="Register"/> on purpose. The register is the executable form of
    /// the refusal blocker list Zoe signed; the repair surface was closed separately and was not in
    /// that sign-off, so folding it in would let one borrow the other's approval. It still needs a
    /// tripwire, because a closed section with no test decays into a claim.
    /// </para>
    /// <para>
    /// This resolves in both directions — the runbook must cite the name and the suite must define
    /// it — so renaming a test without touching §9.3, or deleting a §9.3 row without deleting the
    /// test, both fail here rather than at the next review.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    public void The_repair_disclosure_closure_tests_named_in_the_runbook_all_exist()
    {
        var suite = Path.Combine(
            RepositoryRoot(), "tests", "SentenceStudio.UI.Tests", "Coach",
            "CoachRepairDisclosureWiringTests.cs");

        File.Exists(suite).Should().BeTrue("§9.3 cites this suite as the closure for P5/P6");

        var source = File.ReadAllText(suite);
        var runbook = ReadDoc("sam-foundation-gate-soak-runbook.md");

        using var _ = new AssertionScope();

        foreach (var (bar, test) in RepairRegister)
        {
            source.Should().Contain(
                test,
                $"{bar} is closed by {test}, which must exist in the suite §9.3 names");

            runbook.Should().Contain(
                test,
                $"§9.3 must keep naming {test} so {bar} can be audited without reading the suite");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Document guards — §16.3 must never be reported as a zero
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The runbook states §16.3 substitution take-up as inactive, and says which half is missing.
    /// </summary>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    public void The_runbook_reports_substitution_take_up_as_inactive()
    {
        var runbook = ReadDoc("sam-foundation-gate-soak-runbook.md");

        using var _ = new AssertionScope();

        runbook.Should().Contain(
            "inactive / not measurable",
            "§3.6 must state the classification in the words the report has to use");

        runbook.Should().Contain(
            "no numerator and no denominator",
            "and must say why, so nobody re-derives a rate from a missing population");

        runbook.Should().Contain(
            "never as \"0 percent take-up\"",
            "the prohibition must be explicit, because a monitored target with no baseline is "
            + "exactly the kind of row a reader fills in with a zero out of tidiness");

        runbook.Should().Contain(
            "the substitute is wired",
            "and must no longer say the wiring is missing, because it is not — the gap is the "
            + "event, and a reader who fixes the wrong thing fixes nothing");
    }

    /// <summary>
    /// The query omits substitution take-up entirely rather than emitting a zero row for it.
    /// </summary>
    /// <remarks>
    /// A zero row would read as "learners were offered a substitute and declined it". Omission with
    /// a stated reason is the only honest option until an instrument exists, and SECTION 5 of the
    /// query is where that reason lives.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    public void The_query_omits_substitution_take_up_and_says_why()
    {
        var kql = ReadDoc("sam-foundation-gate-soak-query.kql");

        using var _ = new AssertionScope();

        kql.Should().Contain(
            "INACTIVE / NOT MEASURABLE",
            "SECTION 5 must classify it beside the other things the query deliberately omits");

        kql.Should().Contain(
            "no take-up event",
            "and must name the missing event, not a missing feature");

        kql.Should().NotContain(
            "summarize TakeUp",
            "no aggregation over take-up may exist, because there is no take-up event to aggregate");

        kql.Should().NotContain(
            "not wired",
            "the substitute is wired; a stale note here would send a reader to rebuild something "
            + "that already exists");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // The register itself
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runbook §8.2, as data. Blocker to the test that closed it.
    /// </summary>
    /// <remarks>
    /// A subset, on purpose: one or two load-bearing tests per blocker rather than every test that
    /// touches the area. The register's job is to fail when a closure disappears, not to be a
    /// second index of the refusal suite.
    /// </remarks>
    private static readonly (string Blocker, string Test)[] Register =
    [
        // L1 — refusal copy was hardcoded English.
        ("L1", "An_Enforce_refusal_carries_a_typed_limitation_and_no_server_prose"),
        ("L1", "The_refusal_payload_carries_no_prose_in_any_language"),
        ("L1", "L1_the_refusal_copy_resolves_per_language"),
        ("L1", "Every_learner_visible_string_on_the_card_comes_from_the_client_resx"),

        // L2 — evidence was discarded on refusal.
        ("L2", "An_Enforce_refusal_preserves_the_turns_real_evidence"),
        ("L2", "L2_a_refused_turn_still_shows_the_evidence_it_read"),

        // L3 — no typed destination.
        ("L3", "Every_definition_code_has_a_decided_mapping"),
        ("L3", "No_destination_names_a_route_outside_the_six"),
        ("L3", "L3_a_refusal_names_a_real_screen_the_learner_can_go_to"),
        ("L3", "An_unknown_route_is_dropped_rather_than_guessed"),

        // L4 — Korean disclosure was unreadable. Closed in this class.
        ("L4", "L4_korean_disclosure_suppresses_withheld_and_korean_silence_still_fires"),

        // L5 — an altered or withheld answer announced nothing.
        ("L5", "L5_a_withheld_answer_announces_that_it_was_withheld"),

        // L6 — artifact safety: no answer leak, and reachable by assistive technology.
        ("L6", "The_refusal_leaks_no_terms_or_learner_text"),
        ("L6", "The_pair_carries_no_text_of_any_kind"),
        ("L6", "A_no_read_refusal_leaks_no_learner_content"),
        ("L6", "No_evidence_string_carries_a_term_a_gloss_or_an_example"),
        ("L6", "The_refusal_region_is_a_polite_status_not_an_alert"),

        // LVG-W9-8 — turn-scoped evidence.
        ("LVG-W9-8 turn-scoped", "A_refusal_after_an_answered_turn_shows_none_of_the_earlier_rows"),
        ("LVG-W9-8 turn-scoped",
            "The_evidence_list_takes_its_rows_from_the_caller_and_never_from_the_workspace"),

        // LVG-W9-8 — copy for the turn that read nothing.
        ("LVG-W9-8 no-evidence copy", "A_no_read_refusal_never_promises_evidence_that_is_not_there"),
        ("LVG-W9-8 no-evidence copy", "The_no_evidence_copy_reads_in_both_languages"),
        ("LVG-W9-8 no-evidence copy", "Both_hosts_render_a_no_read_refusal_identically"),

        // LVG-W9-8 — the withheld pair, persisted and coherent.
        ("LVG-W9-8 coherent pair", "One_incoherent_read_poisons_the_whole_turns_withheld_picture"),
        ("LVG-W9-8 coherent pair", "Two_reads_with_the_same_reason_still_state_nothing"),
        ("LVG-W9-8 coherent pair", "The_pair_survives_a_protected_outcome_round_trip"),
        ("LVG-W9-8 coherent pair", "The_stored_limitation_round_trips_exactly_at_version_three"),

        // LVG-W9-8 — latest-only restore.
        ("LVG-W9-8 latest-only", "A_later_normal_turn_clears_the_refusal_on_reload"),
        ("LVG-W9-8 latest-only",
            "An_unreadable_latest_outcome_fails_closed_rather_than_revealing_an_older_refusal"),
        ("LVG-W9-8 latest-only", "The_session_read_is_the_only_caller_and_the_lookback_is_one"),
        ("LVG-W9-8 latest-only", "A_refusal_does_not_cross_between_two_learners")
    ];

    /// <summary>The UI suites the register cites, which live in another assembly.</summary>
    private static readonly string[] UiSuites =
    [
        "CoachGroundingRefusalWiringTests.cs",
        "CoachLimitationWiringContractTests.cs",
        "CoachEvidenceLocalizationTests.cs"
    ];

    /// <summary>§9.3, as data — the repair-disclosure bar and the test that closes it.</summary>
    /// <remarks>
    /// Deliberately a subset. R5 shares a test with R1 because one scan answers both questions, and
    /// the silence and lifetime cases in the same suite are not listed: they make the notice
    /// trustworthy but no numbered bar depends on them, and listing everything would turn this into
    /// a second index that drifts against the first.
    /// </remarks>
    private static readonly (string Bar, string Test)[] RepairRegister =
    [
        ("R1 english announcement", "An_altered_answer_says_so_and_announces_it_politely"),
        ("R2 korean announcement", "A_korean_learner_reads_the_disclosure_in_korean"),
        ("R2 no english fallback", "The_korean_neutral_note_carries_no_english"),
        ("R3 both hosts", "Both_hosts_render_the_disclosure_identically"),
        ("R4 polite status region", "The_notice_is_a_polite_status_with_a_name"),
        ("R5 no answer leak", "The_notice_carries_no_counts_rule_codes_or_learner_content"),
        ("R6 suppression disclosed", "A_suppressed_repair_says_the_wording_was_left_alone")
    ];

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static HashSet<string> ApiTestNames() =>
        typeof(CoachLearningValueGateClosureTests).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(method => method.GetCustomAttributes()
                .Any(attribute => attribute is FactAttribute or TheoryAttribute))
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

    /// <remarks>
    /// Read as text rather than referenced. The UI suite is a different assembly with a different
    /// host, and taking a project reference on it to check three names would pull a Blazor test
    /// harness into the API suite.
    /// </remarks>
    private static string UiTestCorpus()
    {
        var directory = Path.Combine(RepositoryRoot(), "tests", "SentenceStudio.UI.Tests", "Coach");

        var text = UiSuites.Select(name =>
        {
            var path = Path.Combine(directory, name);

            File.Exists(path).Should().BeTrue($"{name} holds closures the register cites");

            return File.ReadAllText(path);
        });

        return string.Join("\n", text);
    }

    /// <remarks>
    /// The rule reads the published pair — a visible evidence item's withheld count and reason —
    /// so the fixture varies the prose and the reason, which are the two things that could change
    /// the verdict.
    /// </remarks>
    private static HashSet<CoachClaimRuleCode> Scan(
        string first,
        string second,
        CoachWithheldReason? reason = CoachWithheldReason.DueReviewEmbargo)
    {
        var rule = new CoachWithheldNotDisclosedRule();

        var context = new CoachClaimRuleContext
        {
            Answer = ClaimFixture.AnswerWith(first, second),
            Evidence = [WithheldEvidence(reason)]
        };

        return rule.Evaluate(context).Select(finding => finding.Rule).ToHashSet();
    }

    private static CoachEvidenceDto WithheldEvidence(
        CoachWithheldReason? reason = CoachWithheldReason.DueReviewEmbargo) => new()
    {
        Kind = CoachEvidenceKind.VocabularyDue,
        Label = "Vocabulary",
        Summary = "Words you are tracking.",
        WindowStartDate = new DateOnly(2026, 8, 1),
        WindowEndDate = new DateOnly(2026, 8, 21),
        Coverage = CoachEvidenceCoverage.PageOfOwnedSet,
        Order = CoachEvidenceOrder.MasteryDescending,
        MatchedCount = 14,
        ReturnedCount = 4,
        WithheldCount = 10,
        WithheldReason = reason
    };

    private static string ReadDoc(string name)
    {
        var path = Path.Combine(RepositoryRoot(), "docs", name);

        File.Exists(path).Should().BeTrue($"{name} is a W9 deliverable and must be present");

        return File.ReadAllText(path);
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
