using System.Runtime.CompilerServices;
using SentenceStudio.Contracts.Profiles;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// The profile choices have one definition (<see cref="LearnerProfileOptions"/>) that the web
/// Profile and Onboarding pages, native onboarding/profile and PUT /api/v1/profile/me read,
/// so a choice added in one place cannot be refused by another.
/// </summary>
public sealed class LearnerProfileOptionsTests
{
    [Fact]
    public void NativePickersReadTheSharedDefinition()
    {
        Assert.Equal(LearnerProfileOptions.Languages, NativeOnboardingOptions.NativeLanguages);
        Assert.Equal(LearnerProfileOptions.TargetLanguages, NativeOnboardingOptions.TargetLanguages);
        Assert.Equal(LearnerProfileOptions.DisplayLanguages, NativeOnboardingOptions.DisplayLanguages);
        Assert.Equal(LearnerProfileOptions.SessionMinutes, NativeOnboardingOptions.SessionMinutes);
        Assert.Equal(LearnerProfileOptions.CefrLevels, NativeOnboardingOptions.CefrLevels);
        Assert.Equal(LearnerProfileOptions.Languages.Order(), LearnerProfileOptions.TargetLanguages.Order());
        Assert.Equal("Korean", LearnerProfileOptions.TargetLanguages[0]);
    }

    [Theory]
    [InlineData(0, "", "", "", false, true)]
    [InlineData(1, "", "", "", false, false)]
    [InlineData(1, "", "English", "", false, true)]
    [InlineData(2, "", "English", "", false, false)]
    [InlineData(2, "", "English", "Korean", false, true)]
    [InlineData(3, " ", "English", "Korean", false, false)]
    [InlineData(3, "Min", "English", "Korean", false, true)]
    [InlineData(4, "Min", "English", "Korean", false, true)]
    [InlineData(4, "Min", "English", "Korean", true, false)]
    [InlineData(3, "Min", "English", "Korean", true, false)]
    public void OnboardingNext_NeedsTheStepsChoiceAndNeverRunsWhileBusy(
        int step, string name, string native, string target, bool busy, bool expected) =>
        Assert.Equal(expected, NativeOnboardingOptions.CanGoNext(step,
            new NativeOnboardingDraft(name, native, target), busy));

    [Theory]
    [InlineData("src/SentenceStudio.UI/Pages/Profile.razor")]
    [InlineData("src/SentenceStudio.UI/Pages/Onboarding.razor")]
    [InlineData("src/SentenceStudio.Api/ProfileEndpoints.cs")]
    [InlineData("src/SentenceStudio.Native/NativeSliceContracts.cs")]
    public void NoOtherCopyOfTheChoices(string path)
    {
        var source = Read(path);
        Assert.Contains("LearnerProfileOptions.", source);
        Assert.DoesNotContain("\"Japanese\", \"Chinese\"", source);
        Assert.DoesNotContain("5, 10, 15, 20, 30, 45", source);
        Assert.DoesNotContain("\"A1\", \"A2\", \"B1\"", source);
    }

    [Theory]
    [InlineData("Korean", "Korean", true)]     // listed
    [InlineData("Italian", "Italian", true)]   // unchanged, outside the list
    [InlineData("korean", "korean", true)]     // unchanged, other casing
    [InlineData("Italian", "Korean", false)]   // changed to an unlisted value
    [InlineData("korean", "Korean", false)]    // changed casing is a change
    [InlineData("", "", false)]                // empty is never a value
    [InlineData(null, null, false)]
    public void ListFieldRule(string? submitted, string? stored, bool accepted) =>
        Assert.Equal(accepted,
            LearnerProfileOptions.IsAcceptable(LearnerProfileOptions.Languages, submitted, stored));

    [Theory]
    [InlineData(15, 20, true)]
    [InlineData(25, 25, true)]
    [InlineData(25, 20, false)]
    [InlineData(0, 0, false)]
    public void SessionMinutesRule(int submitted, int stored, bool accepted) =>
        Assert.Equal(accepted, LearnerProfileOptions.IsAcceptableSessionMinutes(submitted, stored));

    private static string Read(string repositoryPath, [CallerFilePath] string testFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
        return File.ReadAllText(Path.Combine(root, repositoryPath));
    }
}
