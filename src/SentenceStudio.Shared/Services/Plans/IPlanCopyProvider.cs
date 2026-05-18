using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Services.Plans;

/// <summary>
/// Localized display copy for plan items. The v1 implementation
/// (<see cref="EnglishPlanCopyProvider"/>) returns hand-crafted English
/// strings keyed by <see cref="PlanActivityType"/>; the
/// <c>narrative-localization-resx</c> lane replaces this with a real
/// <c>IStringLocalizer&lt;PlanResources&gt;</c> backed by <c>PlanResources.{en,ko,...}.resx</c>.
/// Callers stay unchanged when localization lands — only the registration flips.
/// </summary>
public interface IPlanCopyProvider
{
    /// <summary>Returns localized (Title, Description) for a plan item.</summary>
    (string Title, string Description) GetItemCopy(PlanActivityType activityType, int? vocabDueCount, string? resourceTitle, string? skillName);

    /// <summary>Returns the already-localized resource-selection reason for
    /// the plan narrative. v1 passes the generator's reason through verbatim.</summary>
    string GetSelectionReason(string reasonKeyOrText);

    /// <summary>Returns the localized rationale string from language-neutral
    /// rationale facts. v1 passes the generator's rationale through verbatim.</summary>
    string GetRationale(string rationaleText);

    /// <summary>Returns a localized focus-area label for a focus-area key.</summary>
    string GetFocusArea(string focusAreaKey);
}
