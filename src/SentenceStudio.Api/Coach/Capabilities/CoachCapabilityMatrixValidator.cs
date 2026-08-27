using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Theme;

namespace SentenceStudio.Api.Coach.Capabilities;

/// <summary>Raised when a capability declaration is not a legal §5.4 row.</summary>
public sealed class CoachCapabilityMatrixException : InvalidOperationException
{
    public CoachCapabilityMatrixException(string message) : base(message) { }
}

/// <summary>
/// The plan §5.4 legal matrix, asserted at startup.
/// </summary>
/// <remarks>
/// <para>
/// The table, verbatim from plan lines 185–192:
/// </para>
/// <code>
/// | EffectClass             | Reversal              | Confirmation        | ReceiptKind | Scope        | Steps     |
/// | Read                    | None                  | None                | None        | any          | 1         |
/// | PresentationState       | ClientRevert          | Gesture             | Client      | not Account  | 1         |
/// | LearnerData             | LedgerUndo or None    | Accept or Confirm   | Ledger      | Account      | 1         |
/// | CompositeReversiblePair | LedgerUndo            | Accept              | Ledger      | Account      | exactly 2 |
/// | ExternalEffect          | None                  | Confirm             | Ledger      | Account      | 1         |
/// | ActivityLaunch          | ServerDiscard         | Gesture or Imperative | Client    | Session      | 1         |
/// </code>
/// <para>
/// <b>One explicit switch per effect class, no negations.</b> §5.4: "Each condition is an explicit
/// switch, not the negation of another switch. A member added later falls out of both switches,
/// and the test fails." The switch below has no <c>default:</c> arm that accepts, so a seventh
/// effect class stops the host until someone writes its row.
/// </para>
/// <para>
/// Every rule has a passing and a failing fixture in <c>CoachCapabilityMatrixTests</c>.
/// </para>
/// </remarks>
public static class CoachCapabilityMatrixValidator
{
    /// <summary>
    /// Validates every capability and the two side assertions.
    /// </summary>
    /// <returns>The population examined, so a caller can assert non-vacuity.</returns>
    public static int Validate(ICoachCapabilityManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        AssertThemeCatalogIsUsable();

        var examined = 0;
        foreach (var descriptor in manifest.All)
        {
            ValidateRow(descriptor);
            examined++;
        }

        if (examined == 0)
        {
            // A validator that passes over zero rows is indistinguishable from one that was never
            // wired up, and the second is the failure that actually happens.
            throw new CoachCapabilityMatrixException(
                "The capability matrix examined zero capabilities. Everything below it proves nothing.");
        }

        return examined;
    }

    /// <summary>One row of the §5.4 table, plus the two side assertions that ride beside it.</summary>
    public static void ValidateRow(CoachCapabilityDescriptor d)
    {
        ArgumentNullException.ThrowIfNull(d);

        switch (d.EffectClass)
        {
            case CoachCapabilityEffectClass.Read:
                Require(d, d.Reversal is CoachCapabilityReversal.None, "Reversal must be None");
                Require(d, d.Confirmation is CoachCapabilityConfirmation.None, "Confirmation must be None");
                Require(d, d.ReceiptKind is CoachCapabilityReceiptKind.None, "ReceiptKind must be None");
                // Scope: any.
                Require(d, d.DeclaredStepCount == 1, "DeclaredStepCount must be 1");
                break;

            case CoachCapabilityEffectClass.PresentationState:
                Require(d, d.Reversal is CoachCapabilityReversal.ClientRevert, "Reversal must be ClientRevert");
                Require(d, d.Confirmation is CoachCapabilityConfirmation.Gesture, "Confirmation must be Gesture");
                Require(d, d.ReceiptKind is CoachCapabilityReceiptKind.Client, "ReceiptKind must be Client");
                Require(d, d.Scope is not CoachCapabilityScope.Account, "Scope must not be Account");
                Require(d, d.DeclaredStepCount == 1, "DeclaredStepCount must be 1");
                break;

            case CoachCapabilityEffectClass.LearnerData:
                Require(d, d.Reversal is CoachCapabilityReversal.LedgerUndo or CoachCapabilityReversal.None,
                    "Reversal must be LedgerUndo or None");
                Require(d, d.Confirmation is CoachCapabilityConfirmation.Accept or CoachCapabilityConfirmation.Confirm,
                    "Confirmation must be Accept or Confirm");
                Require(d, d.ReceiptKind is CoachCapabilityReceiptKind.Ledger, "ReceiptKind must be Ledger");
                Require(d, d.Scope is CoachCapabilityScope.Account, "Scope must be Account");
                Require(d, d.DeclaredStepCount == 1, "DeclaredStepCount must be 1");
                break;

            case CoachCapabilityEffectClass.CompositeReversiblePair:
                Require(d, d.Reversal is CoachCapabilityReversal.LedgerUndo, "Reversal must be LedgerUndo");
                Require(d, d.Confirmation is CoachCapabilityConfirmation.Accept, "Confirmation must be Accept");
                Require(d, d.ReceiptKind is CoachCapabilityReceiptKind.Ledger, "ReceiptKind must be Ledger");
                Require(d, d.Scope is CoachCapabilityScope.Account, "Scope must be Account");
                // Side assertion 2: a composite pair declares exactly two atomic steps.
                Require(d, d.DeclaredStepCount == 2, "DeclaredStepCount must be exactly 2");
                break;

            case CoachCapabilityEffectClass.ExternalEffect:
                Require(d, d.Reversal is CoachCapabilityReversal.None, "Reversal must be None");
                Require(d, d.Confirmation is CoachCapabilityConfirmation.Confirm, "Confirmation must be Confirm");
                Require(d, d.ReceiptKind is CoachCapabilityReceiptKind.Ledger, "ReceiptKind must be Ledger");
                Require(d, d.Scope is CoachCapabilityScope.Account, "Scope must be Account");
                Require(d, d.DeclaredStepCount == 1, "DeclaredStepCount must be 1");
                break;

            case CoachCapabilityEffectClass.ActivityLaunch:
                Require(d, d.Reversal is CoachCapabilityReversal.ServerDiscard, "Reversal must be ServerDiscard");
                Require(d, d.Confirmation is CoachCapabilityConfirmation.Gesture or CoachCapabilityConfirmation.Imperative,
                    "Confirmation must be Gesture or Imperative");
                Require(d, d.ReceiptKind is CoachCapabilityReceiptKind.Client, "ReceiptKind must be Client");
                Require(d, d.Scope is CoachCapabilityScope.Session, "Scope must be Session");
                Require(d, d.DeclaredStepCount == 1, "DeclaredStepCount must be 1");
                break;

            default:
                // No accepting default. A seventh effect class falls out of the switch and stops
                // the host until its row is written.
                throw new CoachCapabilityMatrixException(
                    $"Capability '{d.Name}' declares effect class '{d.EffectClass}', which has no row in the "
                    + "§5.4 matrix. Add the row rather than widening this switch.");
        }

        // Side assertion 1: a registration whose RequiredStage is above the promoted stage must not
        // resolve to Present. Declaring a Present ceiling on such a row is a reviewer trap — the
        // resolver would still cap it, but the declaration reads as shipped.
        if (d.RequiredStage > CoachCapabilityStage.Off
            && d.MaxAvailability == CoachCapabilityAvailability.Present
            && !d.IsToolBacked)
        {
            throw new CoachCapabilityMatrixException(
                $"Capability '{d.Name}' is a planned declaration with RequiredStage '{d.RequiredStage}' and a "
                + "ceiling of Present. §5.3: register every planned capability with AbsentUnimplemented until "
                + "its workstream lands and its stage is promoted.");
        }
    }

    /// <summary>
    /// The frozen catalogue the theme capability is declared against is usable.
    /// </summary>
    /// <remarks>
    /// P1 is read-only here. This restates nothing from the catalogue and validates none of its
    /// contents; it asserts only the fact the declaration depends on.
    /// </remarks>
    private static void AssertThemeCatalogIsUsable()
    {
        if (ThemeCatalog.All.Count == 0)
        {
            throw new CoachCapabilityMatrixException(
                "The theme capability is declared against ThemeCatalog, which is empty.");
        }

        if (!ThemeCatalog.Contains(ThemeCatalog.DefaultThemeId))
        {
            throw new CoachCapabilityMatrixException(
                $"ThemeCatalog's default '{ThemeCatalog.DefaultThemeId}' is not in the catalogue.");
        }
    }

    private static void Require(CoachCapabilityDescriptor d, bool condition, string requirement)
    {
        if (!condition)
        {
            throw new CoachCapabilityMatrixException(
                $"Capability '{d.Name}' is {d.EffectClass}, so {requirement}. "
                + $"Declared: Reversal={d.Reversal}, Confirmation={d.Confirmation}, "
                + $"ReceiptKind={d.ReceiptKind}, Scope={d.Scope}, Steps={d.DeclaredStepCount}.");
        }
    }
}
