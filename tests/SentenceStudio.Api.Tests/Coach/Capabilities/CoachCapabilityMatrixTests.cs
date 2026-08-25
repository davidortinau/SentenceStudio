using FluentAssertions;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Theme;

namespace SentenceStudio.Api.Tests.Coach.Capabilities;

/// <summary>
/// The plan §5.4 legal matrix, swept non-vacuously, with a failing fixture behind every cell.
/// </summary>
public class CoachCapabilityMatrixTests
{
    // ---------------------------------------------------------------- the sweeps

    [Fact]
    public void Every_shipped_capability_is_legal_and_the_whole_population_was_examined()
    {
        var manifest = CapabilityFixtures.ShippedManifest();

        var examined = CoachCapabilityMatrixValidator.Validate(manifest);

        examined.Should().Be(manifest.All.Count).And.BeGreaterThan(0);
    }

    [Fact]
    public void Every_effect_class_in_the_table_has_a_legal_row_and_the_sweep_covers_them_all()
    {
        // Non-vacuity for the table itself: six classes, six legal rows, none skipped.
        var rows = CapabilityFixtures.OneLegalRowPerEffectClass();

        rows.Select(r => r.EffectClass).Should().BeEquivalentTo(Enum.GetValues<CoachCapabilityEffectClass>());
        rows.Should().HaveCount(Enum.GetValues<CoachCapabilityEffectClass>().Length).And.HaveCount(6);

        foreach (var row in rows)
        {
            var act = () => CoachCapabilityMatrixValidator.ValidateRow(row);
            act.Should().NotThrow($"the {row.EffectClass} row is copied from §5.4");
        }
    }

    [Fact]
    public void The_manifest_census_is_the_registry_plus_the_standalone_declarations()
    {
        var registry = CapabilityFixtures.FrozenRegistry();
        var manifest = new CoachCapabilityManifest(registry);

        registry.All.Count.Should().BeGreaterThan(0);
        CoachCapabilityDeclarations.All.Count.Should().BeGreaterThan(0);
        manifest.All.Count.Should().Be(registry.All.Count + CoachCapabilityDeclarations.All.Count);
    }

    // ---------------------------------------------------------------- a failing fixture per cell

    [Theory]
    // Read: None / None / None / any / 1
    [InlineData(CoachCapabilityEffectClass.Read, nameof(CoachCapabilityDescriptor.Reversal))]
    [InlineData(CoachCapabilityEffectClass.Read, nameof(CoachCapabilityDescriptor.Confirmation))]
    [InlineData(CoachCapabilityEffectClass.Read, nameof(CoachCapabilityDescriptor.ReceiptKind))]
    [InlineData(CoachCapabilityEffectClass.Read, nameof(CoachCapabilityDescriptor.DeclaredStepCount))]
    // PresentationState: ClientRevert / Gesture / Client / not Account / 1
    [InlineData(CoachCapabilityEffectClass.PresentationState, nameof(CoachCapabilityDescriptor.Reversal))]
    [InlineData(CoachCapabilityEffectClass.PresentationState, nameof(CoachCapabilityDescriptor.Confirmation))]
    [InlineData(CoachCapabilityEffectClass.PresentationState, nameof(CoachCapabilityDescriptor.ReceiptKind))]
    [InlineData(CoachCapabilityEffectClass.PresentationState, nameof(CoachCapabilityDescriptor.Scope))]
    [InlineData(CoachCapabilityEffectClass.PresentationState, nameof(CoachCapabilityDescriptor.DeclaredStepCount))]
    // LearnerData: LedgerUndo|None / Accept|Confirm / Ledger / Account / 1
    [InlineData(CoachCapabilityEffectClass.LearnerData, nameof(CoachCapabilityDescriptor.Reversal))]
    [InlineData(CoachCapabilityEffectClass.LearnerData, nameof(CoachCapabilityDescriptor.Confirmation))]
    [InlineData(CoachCapabilityEffectClass.LearnerData, nameof(CoachCapabilityDescriptor.ReceiptKind))]
    [InlineData(CoachCapabilityEffectClass.LearnerData, nameof(CoachCapabilityDescriptor.Scope))]
    [InlineData(CoachCapabilityEffectClass.LearnerData, nameof(CoachCapabilityDescriptor.DeclaredStepCount))]
    // CompositeReversiblePair: LedgerUndo / Accept / Ledger / Account / exactly 2
    [InlineData(CoachCapabilityEffectClass.CompositeReversiblePair, nameof(CoachCapabilityDescriptor.Reversal))]
    [InlineData(CoachCapabilityEffectClass.CompositeReversiblePair, nameof(CoachCapabilityDescriptor.Confirmation))]
    [InlineData(CoachCapabilityEffectClass.CompositeReversiblePair, nameof(CoachCapabilityDescriptor.ReceiptKind))]
    [InlineData(CoachCapabilityEffectClass.CompositeReversiblePair, nameof(CoachCapabilityDescriptor.Scope))]
    [InlineData(CoachCapabilityEffectClass.CompositeReversiblePair, nameof(CoachCapabilityDescriptor.DeclaredStepCount))]
    // ExternalEffect: None / Confirm / Ledger / Account / 1
    [InlineData(CoachCapabilityEffectClass.ExternalEffect, nameof(CoachCapabilityDescriptor.Reversal))]
    [InlineData(CoachCapabilityEffectClass.ExternalEffect, nameof(CoachCapabilityDescriptor.Confirmation))]
    [InlineData(CoachCapabilityEffectClass.ExternalEffect, nameof(CoachCapabilityDescriptor.ReceiptKind))]
    [InlineData(CoachCapabilityEffectClass.ExternalEffect, nameof(CoachCapabilityDescriptor.Scope))]
    [InlineData(CoachCapabilityEffectClass.ExternalEffect, nameof(CoachCapabilityDescriptor.DeclaredStepCount))]
    // ActivityLaunch: ServerDiscard / Gesture|Imperative / Client / Session / 1
    [InlineData(CoachCapabilityEffectClass.ActivityLaunch, nameof(CoachCapabilityDescriptor.Reversal))]
    [InlineData(CoachCapabilityEffectClass.ActivityLaunch, nameof(CoachCapabilityDescriptor.Confirmation))]
    [InlineData(CoachCapabilityEffectClass.ActivityLaunch, nameof(CoachCapabilityDescriptor.ReceiptKind))]
    [InlineData(CoachCapabilityEffectClass.ActivityLaunch, nameof(CoachCapabilityDescriptor.Scope))]
    [InlineData(CoachCapabilityEffectClass.ActivityLaunch, nameof(CoachCapabilityDescriptor.DeclaredStepCount))]
    public void Breaking_one_cell_of_a_row_is_refused(CoachCapabilityEffectClass effectClass, string cell)
    {
        var legal = LegalRowFor(effectClass);
        var illegal = BreakCell(legal, cell);

        // Guard: the mutation actually changed something, so a no-op fixture cannot pass silently.
        illegal.Should().NotBe(legal);

        var act = () => CoachCapabilityMatrixValidator.ValidateRow(illegal);
        act.Should().Throw<CoachCapabilityMatrixException>()
            .Which.Message.Should().Contain(effectClass.ToString());
    }

    [Fact]
    public void The_cell_sweep_covers_every_constrained_cell_of_every_row()
    {
        // Census for the theory above: Read constrains 4 cells (Scope is "any"), the other five
        // constrain 5 each. 4 + 25 = 29 InlineData rows.
        var cases = typeof(CoachCapabilityMatrixTests)
            .GetMethod(nameof(Breaking_one_cell_of_a_row_is_refused))!
            .GetCustomAttributes(typeof(Xunit.InlineDataAttribute), false);

        cases.Should().HaveCount(29, "Read constrains four cells and the other five rows constrain five each");
    }

    [Fact]
    public void Scope_is_unconstrained_on_the_read_row_because_the_table_says_any()
    {
        foreach (var scope in Enum.GetValues<CoachCapabilityScope>())
        {
            var act = () => CoachCapabilityMatrixValidator.ValidateRow(
                CapabilityFixtures.LegalRead() with { Scope = scope });
            act.Should().NotThrow($"§5.4 gives the Read row scope '{scope}' as 'any'");
        }
    }

    [Fact]
    public void LearnerData_accepts_both_planned_reversals_and_both_planned_confirmations()
    {
        // The two "or" cells in the table, proven in both directions rather than one.
        foreach (var reversal in new[] { CoachCapabilityReversal.LedgerUndo, CoachCapabilityReversal.None })
        foreach (var confirmation in new[] { CoachCapabilityConfirmation.Accept, CoachCapabilityConfirmation.Confirm })
        {
            var act = () => CoachCapabilityMatrixValidator.ValidateRow(
                CapabilityFixtures.LegalLearnerData() with { Reversal = reversal, Confirmation = confirmation });
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void ActivityLaunch_accepts_both_planned_confirmations()
    {
        foreach (var confirmation in new[] { CoachCapabilityConfirmation.Gesture, CoachCapabilityConfirmation.Imperative })
        {
            var act = () => CoachCapabilityMatrixValidator.ValidateRow(
                CapabilityFixtures.LegalActivityLaunch() with { Confirmation = confirmation });
            act.Should().NotThrow();
        }
    }

    // ---------------------------------------------------------------- the side assertions

    [Fact]
    public void A_planned_declaration_above_the_bottom_stage_may_not_declare_a_present_ceiling()
    {
        // §5.3: register every planned capability with AbsentUnimplemented.
        var illegal = CapabilityFixtures.LegalPresentationState() with
        {
            IsToolBacked = false,
            MaxAvailability = CoachCapabilityAvailability.Present
        };

        var act = () => CoachCapabilityMatrixValidator.ValidateRow(illegal);
        act.Should().Throw<CoachCapabilityMatrixException>()
            .WithMessage("*AbsentUnimplemented*");
    }

    [Fact]
    public void A_composite_pair_must_declare_exactly_two_steps_in_both_directions()
    {
        foreach (var wrong in new[] { 1, 3 })
        {
            var act = () => CoachCapabilityMatrixValidator.ValidateRow(
                CapabilityFixtures.LegalCompositePair() with { DeclaredStepCount = wrong });
            act.Should().Throw<CoachCapabilityMatrixException>().WithMessage("*exactly 2*");
        }
    }

    [Fact]
    public void The_frozen_theme_catalogue_the_theme_capability_declares_against_is_usable()
    {
        ThemeCatalog.All.Should().NotBeEmpty();
        ThemeCatalog.Contains(ThemeCatalog.DefaultThemeId).Should().BeTrue();
    }

    // ---------------------------------------------------------------- manifest integrity

    [Fact]
    public void A_manifest_over_an_unfrozen_registry_is_refused()
    {
        var unfrozen = new CoachToolRegistry(CapabilityFixtures.AllToolsEnabled());
        unfrozen.IsFrozen.Should().BeFalse();

        var act = () => new CoachCapabilityManifest(unfrozen);
        act.Should().Throw<InvalidOperationException>().WithMessage("*frozen*");
    }

    [Fact]
    public void Two_capabilities_with_one_name_stop_the_host()
    {
        var duplicate = CapabilityFixtures.LegalRead("duplicated_name");
        var act = () => CapabilityFixtures.ManifestWith(duplicate, duplicate);
        act.Should().Throw<InvalidOperationException>().WithMessage("*declared twice*");
    }

    // ---------------------------------------------------------------- helpers

    private static CoachCapabilityDescriptor LegalRowFor(CoachCapabilityEffectClass effectClass) => effectClass switch
    {
        CoachCapabilityEffectClass.Read => CapabilityFixtures.LegalRead(),
        CoachCapabilityEffectClass.PresentationState => CapabilityFixtures.LegalPresentationState(),
        CoachCapabilityEffectClass.LearnerData => CapabilityFixtures.LegalLearnerData(),
        CoachCapabilityEffectClass.CompositeReversiblePair => CapabilityFixtures.LegalCompositePair(),
        CoachCapabilityEffectClass.ExternalEffect => CapabilityFixtures.LegalExternalEffect(),
        CoachCapabilityEffectClass.ActivityLaunch => CapabilityFixtures.LegalActivityLaunch(),
        _ => throw new ArgumentOutOfRangeException(nameof(effectClass))
    };

    /// <summary>Moves one cell to a value the row forbids, leaving every other cell legal.</summary>
    private static CoachCapabilityDescriptor BreakCell(CoachCapabilityDescriptor legal, string cell) => cell switch
    {
        nameof(CoachCapabilityDescriptor.Reversal) => legal with
        {
            Reversal = legal.Reversal == CoachCapabilityReversal.ServerDiscard
                ? CoachCapabilityReversal.ClientRevert
                : CoachCapabilityReversal.ServerDiscard
        },
        // Confirmation is the one cell where two rows accept a *pair* of values, so a blind flip
        // can land on the row's other legal member and prove nothing. Break to a value outside the
        // row's legal set: Gesture where Accept is legal, Accept everywhere else.
        nameof(CoachCapabilityDescriptor.Confirmation) => legal with
        {
            Confirmation = legal.EffectClass is CoachCapabilityEffectClass.LearnerData
                or CoachCapabilityEffectClass.CompositeReversiblePair
                ? CoachCapabilityConfirmation.Gesture
                : CoachCapabilityConfirmation.Accept
        },
        nameof(CoachCapabilityDescriptor.ReceiptKind) => legal with
        {
            ReceiptKind = legal.ReceiptKind == CoachCapabilityReceiptKind.Ledger
                ? CoachCapabilityReceiptKind.Client
                : CoachCapabilityReceiptKind.Ledger
        },
        nameof(CoachCapabilityDescriptor.Scope) => legal with
        {
            Scope = legal.Scope == CoachCapabilityScope.Account
                ? CoachCapabilityScope.Device
                : CoachCapabilityScope.Account
        },
        nameof(CoachCapabilityDescriptor.DeclaredStepCount) => legal with
        {
            DeclaredStepCount = legal.DeclaredStepCount == 2 ? 1 : 2
        },
        _ => throw new ArgumentOutOfRangeException(nameof(cell))
    };
}
