using FluentAssertions;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Api.Tests.Coach.Capabilities;
using SentenceStudio.Api.Tests.Coach.Claims;
using SentenceStudio.Api.Tests.Coach.Tools;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Theme;

namespace SentenceStudio.Api.Tests.Coach.Gate;

/// <summary>
/// The eight foundation acceptance cases from plan §14.1, and the four §14.2 foundation bars.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are synthetic test gates. None of them is a production precondition.</b> Plan §14.1 is
/// explicit: AC-F1, AC-F2, AC-F3 and AC-F5 run against a <em>synthetic</em> client handshake and
/// synthetic registrations, because no shipped client advertises a client capability until C1 ships
/// the <c>Contracts/AppOperation</c> channel and C1 is post-gate. Reading them as production bars
/// makes the gate circular — it would require the thing the gate is the precondition for.
/// </para>
/// <para>
/// <b>This file is not a second acceptance matrix.</b> Plan §14 holds the single copy. Each test
/// here asserts the bar §14 states and names the row it is asserting. Where an owning suite already
/// proves the bar in depth — AC-F4's catalogue, AC-F7's startup validator, AC-F8's boundary scan —
/// the test here asserts the same guard is armed and cites that suite rather than restating its
/// coverage. A gate row with no test is an unevidenced row; a gate row with a paraphrased copy of
/// somebody else's suite is two things that can drift.
/// </para>
/// <para>
/// <b>What passing this file proves.</b> Gate condition (a) — synthetic acceptance — only. Gate
/// condition (b) is the section 16.1 invariants reading zero across a Captain-named production soak
/// window and cannot be produced by any test. See <c>docs/sam-foundation-gate-soak-runbook.md</c>.
/// </para>
/// </remarks>
public sealed class CoachFoundationGateAcceptanceTests
{
    private const string ThemeCapability = CoachCapabilityDeclarations.ThemeMetadataCapabilityName;

    /// <summary>An engine over a resolver and manifest the test steers. Synthetic by design.</summary>
    private static CoachClaimRuleEngine Engine(
        ICoachCapabilityResolver resolver,
        ICoachCapabilityManifest? manifest = null) =>
        new(resolver, manifest ?? new StubCapabilityManifest());

    // ─────────────────────────────────────────────────────────────────────────
    // AC-F1 — the manifest declares the theme capability, its RequiredStage is
    // met, and a synthetic handshake advertises it. Lookup returns Present.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC-F1. Tier 5.
    /// </summary>
    /// <remarks>
    /// The shipped <see cref="CoachCapabilityDeclarations.ThemeMetadata"/> row is capped at
    /// <c>AbsentUnimplemented</c> until C1, so this case uses a synthetic descriptor carrying the
    /// same §5.4 <c>PresentationState</c> shape with the ceiling lifted. That substitution is the
    /// case, not a shortcut around it: §14.1 asks whether the resolver grants <c>Present</c> when
    /// all three gates open, and the shipped ceiling is one of the three.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Capability)]
    [Trait(CoachGateCase.Key, CoachGateCase.F1)]
    public void AC_F1_a_declared_capability_at_stage_with_a_handshake_resolves_present()
    {
        var descriptor = CapabilityFixtures.LegalPresentationState(ThemeCapability);
        var manifest = CapabilityFixtures.ManifestWith(descriptor);
        var resolver = new CoachCapabilityResolver(manifest);

        var availability = resolver.Resolve(
            ThemeCapability,
            CoachCapabilityStage.Presentation,
            CapabilityFixtures.Handshake(codes: [CoachClientCapabilityCode.ThemeMetadata]));

        availability.Should().Be(
            CoachCapabilityAvailability.Present,
            "AC-F1: declared, RequiredStage met, and a synthetic handshake advertising it are the "
            + "three gates. All three open, so the lookup grants Present");
    }

    /// <summary>
    /// AC-F1, the shipped half: the real declaration is present in the manifest and honestly absent.
    /// </summary>
    /// <remarks>
    /// The synthetic case above proves the resolver grants. This proves the capability the case is
    /// about actually exists in this build and is capped where §5.3 says it is — so a reader cannot
    /// mistake AC-F1 for a claim that Sam can change a theme today.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Capability)]
    [Trait(CoachGateCase.Key, CoachGateCase.F1)]
    public void AC_F1_the_shipped_theme_declaration_is_declared_and_still_capped()
    {
        var declaration = CoachCapabilityDeclarations.ThemeMetadata;

        declaration.Name.Should().Be(ThemeCapability);
        declaration.EffectClass.Should().Be(CoachCapabilityEffectClass.PresentationState);
        declaration.Surface.Should().Be(CoachCapabilitySurface.Client);
        declaration.RequiredStage.Should().Be(CoachCapabilityStage.Presentation);

        declaration.MaxAvailability.Should().Be(
            CoachCapabilityAvailability.AbsentUnimplemented,
            "the shipped ceiling is the reason AC-F1's positive case is synthetic. If this ever "
            + "reads Present the gate's synthetic framing is stale and C1 has landed");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AC-F2 — the synthetic handshake advertises no theme capability.
    // CapabilityAbsent repairs to PresentOnAnotherSurface. No flat "I cannot".
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>AC-F2, the resolution half. Tier 5.</summary>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Capability)]
    [Trait(CoachGateCase.Key, CoachGateCase.F2)]
    public void AC_F2_an_unadvertised_client_capability_resolves_to_another_surface()
    {
        var manifest = CapabilityFixtures.ManifestWith(
            CapabilityFixtures.LegalPresentationState(ThemeCapability));
        var resolver = new CoachCapabilityResolver(manifest);

        resolver.Resolve(
                ThemeCapability,
                CoachCapabilityStage.Presentation,
                CapabilityFixtures.Handshake())
            .Should().Be(
                CoachCapabilityAvailability.PresentOnAnotherSurface,
                "AC-F2: the app does the thing, this client did not say it could. That is a surface "
                + "fact, not an absence, and AbsentUnimplemented here would make Sam lie about the "
                + "product");
    }

    /// <summary>AC-F2, the repair half. Tier 2.</summary>
    /// <remarks>
    /// The rule fires and the limitation carries <c>AvailableOnAnotherSurface</c> — which is what
    /// forbids the flat "I cannot". A refusal and a redirection are different answers and §14.1
    /// asks for the second.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateCase.Key, CoachGateCase.F2)]
    public void AC_F2_capability_absent_repairs_to_an_available_on_another_surface_limitation()
    {
        var resolver = new StubCapabilityResolver()
            .Declare(ThemeCapability, CoachCapabilityAvailability.PresentOnAnotherSurface);

        var outcome = Engine(resolver).Evaluate(
            new CoachClaimRuleContext
            {
                Answer = ClaimFixture.Answer("I'll switch you to the light theme now."),
                ProposedCapabilities = [ThemeCapability],
                Stage = CoachCapabilityStage.Presentation,
                Trace = ClaimFixture.EmptyTrace()
            },
            CoachGroundingStage.Enforce);

        outcome.Findings.Should().Contain(
            finding => finding.Rule == CoachClaimRuleCode.CapabilityAbsent,
            "the answer proposed something this build will not grant");

        outcome.Limitation.Should().NotBeNull();
        outcome.Limitation!.Code.Should().Be(
            CoachLimitationCode.AvailableOnAnotherSurface,
            "AC-F2 forbids a flat 'I cannot'. The honest boundary is the screen that does it");
    }

    /// <summary>
    /// AC-F2's destination half is deferred to C1, and this test pins the deferral so it is visible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §14.1 states "the answer names <c>/settings</c>". This build does not name it. W7's
    /// <c>CoachClaimLimitationProjection</c> sets <c>Destination = null</c> deliberately, because no
    /// capability declares a route yet and a destination the build cannot derive is a destination it
    /// must not state — naming a plausible screen is the fluent-invention failure the grounding
    /// layer exists to stop.
    /// </para>
    /// <para>
    /// <b>Re-arm condition C1.</b> When a capability declares a route, this test fails, and the
    /// person who lands that change updates it to assert
    /// <see cref="CoachRouteName.Settings"/>. Deleting this test instead would remove the only
    /// record that a §14.1 clause is outstanding.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateCase.Key, CoachGateCase.F2)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.InactiveUntilC1)]
    public void AC_F2_naming_the_destination_is_outstanding_until_a_capability_declares_a_route()
    {
        var resolver = new StubCapabilityResolver()
            .Declare(ThemeCapability, CoachCapabilityAvailability.PresentOnAnotherSurface);

        var outcome = Engine(resolver).Evaluate(
            new CoachClaimRuleContext
            {
                Answer = ClaimFixture.Answer("I'll switch you to the light theme now."),
                ProposedCapabilities = [ThemeCapability],
                Stage = CoachCapabilityStage.Presentation,
                Trace = ClaimFixture.EmptyTrace()
            },
            CoachGroundingStage.Enforce);

        outcome.Limitation!.Destination.Should().BeNull(
            "the route half of AC-F2 is outstanding until C1. This assertion is the record of that, "
            + "and it fails the moment a capability declares a route — at which point it must be "
            + "changed to assert CoachRouteName.Settings, not deleted");

        outcome.Limitation.Code.Should().Be(
            CoachLimitationCode.AvailableOnAnotherSurface,
            "the structural half of AC-F2 holds today. Only the route naming is outstanding");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AC-F3 — the answer states inability while the manifest resolves Present.
    // FalseLimitation fires and repairs to a capable answer.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>AC-F3, the firing half. Tiers 2 and 5.</summary>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateCase.Key, CoachGateCase.F3)]
    public void AC_F3_claimed_inability_against_a_present_capability_fires_and_repairs()
    {
        var resolver = new StubCapabilityResolver()
            .Declare(ThemeCapability, CoachCapabilityAvailability.Present);

        var context = new CoachClaimRuleContext
        {
            Answer = ClaimFixture.Answer("I can't change your theme."),
            ProposedCapabilities = [ThemeCapability],
            Stage = CoachCapabilityStage.Presentation,
            Trace = ClaimFixture.EmptyTrace()
        };

        var outcome = Engine(resolver).Evaluate(context, CoachGroundingStage.Enforce);

        outcome.Findings.Should().Contain(
            finding => finding.Rule == CoachClaimRuleCode.FalseLimitation,
            "under-claiming looks like caution and costs the learner a feature they have");

        outcome.Refused.Should().BeFalse(
            "AC-F3 asks for repair to a capable answer, not a refusal");

        outcome.Answer.Should().NotBeNull();
        outcome.Answer!.PlainText.Should().NotBe(
            context.Answer!.PlainText,
            "a repaired answer that reads identically repaired nothing");
    }

    /// <summary>AC-F3's negative control: the same sentence with no capable capability is silent.</summary>
    /// <remarks>
    /// Without this, the rule could be matching inability language alone — which would fire on
    /// "I can't tell you today's answers", a correct refusal and a W7 boundary.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.ClaimRulesAndRepair)]
    [Trait(CoachGateCase.Key, CoachGateCase.F3)]
    public void AC_F3_the_same_sentence_is_silent_when_nothing_capable_was_proposed()
    {
        var resolver = new StubCapabilityResolver()
            .Declare(ThemeCapability, CoachCapabilityAvailability.AbsentUnimplemented);

        var outcome = Engine(resolver).Evaluate(
            new CoachClaimRuleContext
            {
                Answer = ClaimFixture.Answer("I can't change your theme."),
                ProposedCapabilities = [ThemeCapability],
                Stage = CoachCapabilityStage.Presentation,
                Trace = ClaimFixture.EmptyTrace()
            },
            CoachGroundingStage.Enforce);

        outcome.Findings.Should().NotContain(
            finding => finding.Rule == CoachClaimRuleCode.FalseLimitation,
            "an honest statement of a real limitation is not a false limitation");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AC-F4 — theme catalogue snapshot. A new theme without a display key, both
    // palettes, and a contrast value fails the build. No silent fallback path
    // stays reachable.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC-F4. Tier 1. Build-time evidence.
    /// </summary>
    /// <remarks>
    /// Depth lives in <c>tests/SentenceStudio.UI.Tests/Theme/ThemeCatalogTests.cs</c> — ten cases
    /// covering picker order, id uniqueness, mode behaviour, and per-palette readability. This
    /// asserts the three properties §14.1 names, over the shipped catalogue, from the assembly the
    /// capability declaration lives in. AC-F4 is a foundation row and the theme capability is
    /// declared here; a row whose only evidence sits in another assembly is a row this gate cannot
    /// see fail.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    [Trait(CoachGateCase.Key, CoachGateCase.F4)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.BuildTime)]
    public void AC_F4_every_catalogue_theme_carries_a_display_key_both_palettes_and_contrast()
    {
        ThemeCatalog.All.Should().NotBeEmpty(
            "an empty catalogue would pass every per-theme assertion below and prove nothing");

        foreach (var theme in ThemeCatalog.All)
        {
            theme.LocalizationKey.Should().NotBeNullOrWhiteSpace(
                "{0} must name itself through a display key, never a hardcoded English string",
                theme.Id);

            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                var palette = theme.PaletteFor(mode);

                palette.Should().NotBeNull("{0} must define a {1} palette", theme.Id, mode);

                palette.PrimaryOnSurface.Ratio.Should().BeGreaterThan(
                    0,
                    "{0}/{1} must carry a computed contrast value. Accessibility is upstream of "
                    + "reading, and a palette with no measured contrast is one nobody checked",
                    theme.Id,
                    mode);
            }
        }
    }

    /// <summary>AC-F4's second clause: no silent fallback path stays reachable.</summary>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    [Trait(CoachGateCase.Key, CoachGateCase.F4)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.BuildTime)]
    public void AC_F4_an_unknown_theme_id_is_rejected_rather_than_silently_substituted()
    {
        ThemeCatalog.TryGet("sakura", out _).Should().BeFalse(
            "AC-F4 forbids a silent fallback. A miss must be visible to the caller");

        ThemeCatalog.Contains(ThemeCatalog.DefaultThemeId).Should().BeTrue(
            "the default must be a catalogue member, or the fallback is itself the silent path");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AC-F5 — the synthetic handshake carries an unknown code. It is ignored.
    // No exception. The turn renders.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>AC-F5. Tier 5.</summary>
    /// <remarks>
    /// The failure mode this forbids is a client one version ahead taking a turn away from a
    /// learner. An unrecognised code is ordinary and must cost nothing.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Capability)]
    [Trait(CoachGateCase.Key, CoachGateCase.F5)]
    public void AC_F5_an_unknown_handshake_code_is_ignored_and_the_turn_still_resolves()
    {
        var manifest = CapabilityFixtures.ManifestWith(
            CapabilityFixtures.LegalPresentationState(ThemeCapability));
        var resolver = new CoachCapabilityResolver(manifest);

        var handshake = CapabilityFixtures.Handshake(
            codes: [CoachClientCapabilityCode.Unknown, CoachClientCapabilityCode.ThemeMetadata]);

        var resolve = () => resolver.Resolve(
            ThemeCapability, CoachCapabilityStage.Presentation, handshake);

        resolve.Should().NotThrow("an unrecognised code must never cost a learner a turn");

        resolve().Should().Be(
            CoachCapabilityAvailability.Present,
            "the unknown code is ignored, and the code that was understood still counts");
    }

    /// <summary>AC-F5, the harder half: an unknown code alone grants nothing.</summary>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Capability)]
    [Trait(CoachGateCase.Key, CoachGateCase.F5)]
    public void AC_F5_an_unknown_code_alone_authorizes_nothing()
    {
        var manifest = CapabilityFixtures.ManifestWith(
            CapabilityFixtures.LegalPresentationState(ThemeCapability));
        var resolver = new CoachCapabilityResolver(manifest);

        resolver.Resolve(
                ThemeCapability,
                CoachCapabilityStage.Presentation,
                CapabilityFixtures.Handshake(codes: [CoachClientCapabilityCode.Unknown]))
            .Should().Be(
                CoachCapabilityAvailability.PresentOnAnotherSurface,
                "'ignored' must mean ignored. A code the server cannot name must not be able to "
                + "authorize the capability it happens to sit next to");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AC-F6 — a scope sets DefinitionCode and leaves MinimumEvidence null. The
    // contract test fails.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC-F6. Tier 1. A mutation test of the guard, which is the only way to read this row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §14.1's pass condition is that the <em>contract test</em> fails — so the subject here is the
    /// guard, not the scope. <c>CoachResultScopeContractTests.Every_tool_completes_the_foundation_members_it_holds_back</c>
    /// applies this predicate to live tool output; this applies it to a hand-built defect, because a
    /// guard that has never seen a defect is a guard nobody has tested.
    /// </para>
    /// <para>
    /// <c>MinimumEvidence</c> is an enum, so "null" in the plan's prose is
    /// <see cref="CoachScopeMinimumEvidence.Unspecified"/> in the type — the member that exists so
    /// an unset value cannot masquerade as <c>None</c>.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    [Trait(CoachGateCase.Key, CoachGateCase.F6)]
    public void AC_F6_a_scope_that_states_a_definition_and_no_minimum_evidence_is_rejected()
    {
        var defective = CoachResultScopeSamples.Any() with
        {
            DefinitionCode = CoachScopeDefinition.DeterministicPlanPreview,
            MinimumEvidence = CoachScopeMinimumEvidence.Unspecified
        };

        FoundationMembersMissingFrom(defective).Should().Contain(
            nameof(CoachResultScope.MinimumEvidence),
            "AC-F6: a scope that names its definition and not its evidence floor tells the model "
            + "what population it queried and not which rows were allowed to count");
    }

    /// <summary>AC-F6's positive control: a complete scope passes the same predicate.</summary>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    [Trait(CoachGateCase.Key, CoachGateCase.F6)]
    public void AC_F6_a_complete_scope_passes_the_same_predicate()
    {
        FoundationMembersMissingFrom(CoachResultScopeSamples.Any()).Should().BeEmpty(
            "the negative case above only means something if the predicate is not simply strict");
    }

    /// <summary>
    /// The foundation members a scope must complete, per plan §14.1 AC-F6 and the shipped contract.
    /// </summary>
    private static IReadOnlyList<string> FoundationMembersMissingFrom(CoachResultScope scope)
    {
        var missing = new List<string>();

        if (scope.DefinitionCode == CoachScopeDefinition.Unspecified)
        {
            missing.Add(nameof(CoachResultScope.DefinitionCode));
        }

        if (scope.MinimumEvidence == CoachScopeMinimumEvidence.Unspecified)
        {
            missing.Add(nameof(CoachResultScope.MinimumEvidence));
        }

        if (scope.TieBreak == CoachScopeTieBreak.Unspecified)
        {
            missing.Add(nameof(CoachResultScope.TieBreak));
        }

        if (scope.ClockBasis == CoachScopeClockBasis.Unspecified)
        {
            missing.Add(nameof(CoachResultScope.ClockBasis));
        }

        if (scope.ReferenceMode == CoachScopeReferenceMode.Unspecified)
        {
            missing.Add(nameof(CoachResultScope.ReferenceMode));
        }

        return missing;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AC-F7 — a registration outside the legal matrix, or one whose
    // RequiredStage exceeds the promoted stage.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC-F7, first half. Tier 5. Build-time evidence, and §8.2 invariant 11.
    /// </summary>
    /// <remarks>
    /// The host-level proof lives in
    /// <c>CoachCapabilityStartupValidationTests.An_illegal_capability_row_stops_the_host_at_startup</c>.
    /// This asserts the validator itself refuses, without a host, so the gate row fails in
    /// milliseconds and names the matrix rather than a startup timeout.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Capability)]
    [Trait(CoachGateCase.Key, CoachGateCase.F7)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.BuildTime)]
    public void AC_F7_a_registration_outside_the_legal_matrix_is_refused()
    {
        // A LearnerData row carrying a Client receipt. §5.4 requires Ledger.
        var illegal = CapabilityFixtures.LegalLearnerData("illegal_for_the_gate") with
        {
            ReceiptKind = CoachCapabilityReceiptKind.Client
        };

        var validate = () => CoachCapabilityMatrixValidator.Validate(
            CapabilityFixtures.ManifestWith(illegal));

        validate.Should().Throw<CoachCapabilityMatrixException>(
            "§8.2 invariant 11 reads zero because startup refuses, not because nobody tried");
    }

    /// <summary>AC-F7's control: the shipped declarations pass the same validator.</summary>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.Capability)]
    [Trait(CoachGateCase.Key, CoachGateCase.F7)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.BuildTime)]
    public void AC_F7_the_shipped_manifest_passes_the_validator_over_a_positive_population()
    {
        var manifest = CapabilityFixtures.ShippedManifest();

        CoachCapabilityMatrixValidator.Validate(manifest)
            .Should().Be(manifest.All.Count)
            .And.BePositive(
                "a validator that swept nothing must not be able to report success");
    }

    /// <summary>AC-F7, second half: a staged row never resolves to Present. Tier 5.</summary>
    [Theory]
    [InlineData(CoachCapabilityStage.Off)]
    [InlineData(CoachCapabilityStage.Read)]
    [Trait(CoachGateTier.Key, CoachGateTier.Capability)]
    [Trait(CoachGateCase.Key, CoachGateCase.F7)]
    public void AC_F7_a_capability_above_the_promoted_stage_never_resolves_present(
        CoachCapabilityStage promoted)
    {
        var manifest = CapabilityFixtures.ManifestWith(
            CapabilityFixtures.LegalPresentationState(ThemeCapability));
        var resolver = new CoachCapabilityResolver(manifest);

        var availability = resolver.Resolve(
            ThemeCapability,
            promoted,
            CapabilityFixtures.Handshake(codes: [CoachClientCapabilityCode.ThemeMetadata]));

        availability.Should().NotBe(
            CoachCapabilityAvailability.Present,
            "the handshake advertises it and the row is legal. Only the stage is holding it back, "
            + "and one field must never be enough to ship a capability");

        availability.Should().BeOneOf(
            CoachCapabilityAvailability.PresentOnAnotherSurface,
            CoachCapabilityAvailability.AbsentUnimplemented);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AC-F8 — any type under Coach/Tools/** refers to ApplicationDbContext.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC-F8. Tier 1. Build-time evidence, and §8.2 invariant 12.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Re-run as regression, not re-implemented. The owning guard is
    /// <c>CoachToolBoundaryArchitectureTests</c>, which scans the shipped sources and the loaded
    /// types. This asserts the guard is present and armed in this assembly, so a future refactor
    /// that deletes it fails the gate row rather than quietly reducing AC-F8 to nothing.
    /// </para>
    /// <para>
    /// §14.1 notes this row passes only after all thirteen P3 tools move. It does — P3 landed — and
    /// the assertion below is the standing regression.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    [Trait(CoachGateCase.Key, CoachGateCase.F8)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.BuildTime)]
    public void AC_F8_the_tool_persistence_boundary_guard_exists_and_is_armed()
    {
        var guard = typeof(CoachFoundationGateAcceptanceTests).Assembly
            .GetType("SentenceStudio.Api.Tests.Coach.Tools.CoachToolBoundaryArchitectureTests");

        guard.Should().NotBeNull(
            "AC-F8's evidence is a boundary scan. If the scanning suite is gone the gate row has no "
            + "evidence, and a deleted guard reads exactly like a passing one");

        foreach (var name in new[]
                 {
                     "CoachToolSources_DoNotReferenceApplicationDbContext",
                     "CoachToolTypes_DoNotDeclareApplicationDbContextMembers"
                 })
        {
            guard!.GetMethod(name).Should().NotBeNull(
                "{0} is one of the two halves of AC-F8: the source scan catches a using nobody "
                + "called, the reflection scan catches a member the source scan's regex missed",
                name);
        }
    }

    /// <summary>AC-F8, executed rather than merely located.</summary>
    /// <remarks>
    /// The census above proves the guard exists. This proves it still reports clean, so the gate row
    /// carries a result and not just a reference.
    /// </remarks>
    [Fact]
    [Trait(CoachGateTier.Key, CoachGateTier.SemanticsAndScope)]
    [Trait(CoachGateCase.Key, CoachGateCase.F8)]
    [Trait(CoachGateEvidence.Key, CoachGateEvidence.BuildTime)]
    public void AC_F8_no_coach_tool_type_declares_an_application_db_context_member()
    {
        var offenders = typeof(ICoachToolRegistry).Assembly
            .GetTypes()
            .Where(type => type.Namespace?.StartsWith(
                "SentenceStudio.Api.Coach.Tools", StringComparison.Ordinal) == true)
            .SelectMany(type => type
                .GetMembers(System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.DeclaredOnly)
                .Select(member => (Type: type, Member: member)))
            .Where(pair => MemberTypeName(pair.Member).Contains(
                "ApplicationDbContext", StringComparison.Ordinal))
            .Select(pair => $"{pair.Type.Name}.{pair.Member.Name}")
            .ToList();

        offenders.Should().BeEmpty(
            "§8.2 invariant 12 reads zero structurally: a tool that can reach the context can reach "
            + "a row nobody scoped. Offending: {0}",
            string.Join(", ", offenders));
    }

    private static string MemberTypeName(System.Reflection.MemberInfo member) => member switch
    {
        System.Reflection.FieldInfo field => field.FieldType.FullName ?? string.Empty,
        System.Reflection.PropertyInfo property => property.PropertyType.FullName ?? string.Empty,
        System.Reflection.MethodInfo method =>
            (method.ReturnType.FullName ?? string.Empty)
            + string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName)),
        System.Reflection.ConstructorInfo constructor =>
            string.Join(",", constructor.GetParameters().Select(p => p.ParameterType.FullName)),
        _ => string.Empty
    };
}
