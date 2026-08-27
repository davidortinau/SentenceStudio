using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Progress;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests;

/// <summary>
/// Guards the extraction that replaced two divergent activity maps (one localized on the
/// Dashboard, one hardcoded English on the activity log) before the coach plan canvas became
/// a third.
/// </summary>
public class PlanActivityPresentationTests
{
    [Fact]
    public void Icon_IsDefinedForEveryActivityType()
    {
        foreach (var type in Enum.GetValues<PlanActivityType>())
        {
            PlanActivityPresentation.Icon(type)
                .Should().StartWith("bi-", "coach and dashboard iconography is Bootstrap Icons only");
        }
    }

    [Fact]
    public void Icon_NeverFallsBackToTheGenericIconForAKnownType()
    {
        var generic = Enum.GetValues<PlanActivityType>()
            .Where(t => PlanActivityPresentation.Icon(t) == "bi-check-circle")
            .ToArray();

        generic.Should().BeEmpty("every declared activity type deserves a distinct icon");
    }

    [Fact]
    public void Route_IsDefinedForEveryActivityType()
    {
        foreach (var type in Enum.GetValues<PlanActivityType>())
        {
            PlanActivityPresentation.Route(type).Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Label_ResolvesThroughTheLocalizationServiceForEveryActivityType()
    {
        var localize = new BlazorLocalizationService();

        foreach (var type in Enum.GetValues<PlanActivityType>())
        {
            var label = PlanActivityPresentation.Label(localize, type);

            label.Should().NotBeNullOrWhiteSpace();
            // A missing resource would surface as the key itself, so this proves every type
            // actually resolves through the resx rather than falling through.
            label.Should().NotStartWith("Activity_");
        }
    }

    [Fact]
    public void Label_IsLocalizedRatherThanTheRawEnumName()
    {
        var localize = new BlazorLocalizationService();

        // This is the exact case the hardcoded English map got wrong on the activity log.
        PlanActivityPresentation.Label(localize, PlanActivityType.VocabularyReview)
            .Should().NotBe(nameof(PlanActivityType.VocabularyReview));
    }

    [Fact]
    public void Label_ChangesWithTheCulture()
    {
        var localize = new BlazorLocalizationService();
        var english = PlanActivityPresentation.Label(localize, PlanActivityType.Reading);

        localize.SetCulture("ko");
        var korean = PlanActivityPresentation.Label(localize, PlanActivityType.Reading);

        korean.Should().NotBe(english, "the shared map must honour the display language");
    }

    [Fact]
    public void ToPlanActivityType_MapsEveryCoachWireValue()
    {
        foreach (var coachType in Enum.GetValues<CoachPlanActivityType>())
        {
            var mapped = PlanActivityPresentation.ToPlanActivityType(coachType);

            // Names are identical between the two enums by design; the mapping must not
            // silently collapse an unmapped value onto the VocabularyReview default.
            mapped.ToString().Should().Be(coachType.ToString());
        }
    }

    [Theory]
    [InlineData(PlanActivityType.Reading, ActivityCategory.Input)]
    [InlineData(PlanActivityType.Listening, ActivityCategory.Input)]
    [InlineData(PlanActivityType.VocabularyReview, ActivityCategory.Input)]
    [InlineData(PlanActivityType.Writing, ActivityCategory.Output)]
    [InlineData(PlanActivityType.Conversation, ActivityCategory.Output)]
    public void Category_DelegatesToTheSharedDomainMapper(PlanActivityType type, ActivityCategory expected)
    {
        PlanActivityPresentation.Category(type).Should().Be(expected);
    }

    [Fact]
    public void DotClass_MatchesTheSharedActivityDotModifiers()
    {
        PlanActivityPresentation.DotClass(ActivityCategory.Input).Should().Be("activity-dot-input");
        PlanActivityPresentation.DotClass(ActivityCategory.Output).Should().Be("activity-dot-output");
    }
}
