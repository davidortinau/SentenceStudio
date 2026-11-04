using System.Reflection;
using System.Text.RegularExpressions;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android.Tests;

/// <summary>
/// W2: the Dashboard More menu reaches Profile, Settings, Skills, Vocabulary and
/// Activity Log (AF-N08) on the shared coordinators iOS uses, and Settings carries
/// every quiz preference with the approved 1-10 s auto-advance (AF-Q14, AF-Q15).
/// </summary>
public sealed class AndroidMoreScreensContractTests
{
    private static readonly string[] MoreFiles =
    [
        "AndroidSliceShell.More.cs", "AndroidSliceShell.Appearance.cs",
        "AndroidSliceShell.Settings.Compose.cs", "AndroidSliceShell.Profile.Compose.cs",
        "AndroidSliceShell.Skills.Compose.cs", "AndroidSliceShell.Vocabulary.Compose.cs",
        "AndroidSliceShell.ActivityLog.Compose.cs",
    ];

    [Fact]
    public void MenuListsTheSourceDestinationsInIosOrderBeforeSignOut()
    {
        var visual = Source("AndroidSliceShell.Visual.cs");
        var tags = new[]
        {
            "native.dashboard.menu.regenerate", "native.dashboard.menu.profile",
            "native.dashboard.menu.settings", "native.dashboard.menu.skills",
            "native.dashboard.menu.vocabulary", "native.dashboard.menu.activity-log",
            "native.dashboard.menu.signout",
        };
        var positions = tags.Select(tag => visual.IndexOf(tag, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order().ToArray(), positions);
        foreach (var key in new[] { "Nav_Profile", "Settings_Title", "Nav_Skills",
                     "Nav_Vocabulary", "Nav_Activity" })
            Assert.Contains($"(\"{key}\"", visual);
    }

    [Fact]
    public void MoreScreensUseTheSharedCoordinatorsAndComposeOnly()
    {
        var more = Source("AndroidSliceShell.More.cs");
        Assert.Contains("new SettingsCoordinator(", more);
        Assert.Contains("new ProfileCoordinator(", more);
        Assert.Contains("new SkillsCoordinator(", more);
        Assert.Contains("new VocabularyBrowser(", more);
        Assert.Contains("new ActivityLogCoordinator(", more);
        Assert.Contains("new ActivityLogReader(", more);
        foreach (var file in MoreFiles)
        {
            var source = Source(file);
            Assert.DoesNotContain("Android.Widget", source);
            Assert.DoesNotContain("AppCompat", source);
            Assert.DoesNotMatch(new Regex(@"new\s+Switch\("), source);
        }
        // No platform copy of the shared rules: the coordinators are not redefined here.
        Assert.DoesNotMatch(new Regex(@"class\s+(Settings|Profile|Skills|ActivityLog)Coordinator\b"),
            string.Concat(MoreFiles.Select(Source)));
    }

    [Fact]
    public void MoreScreensFailClosedForAnAbsentOrChangedOwner()
    {
        var more = Source("AndroidSliceShell.More.cs");
        var shell = Source("AndroidSliceShell.cs");
        Assert.Contains("return HasDashboardOwner(owner) ? owner : null;", more);
        Assert.Contains("case MoreScreen.Profile when owner is not null:", more);
        Assert.Contains("case MoreScreen.Skills when owner is not null:", more);
        Assert.Contains("case MoreScreen.Vocabulary when owner is not null:", more);
        Assert.Contains("case MoreScreen.ActivityLog when owner is not null:", more);
        Assert.Contains("NativeSettings_OwnerUnavailable", Source("AndroidSliceShell.Appearance.cs"));
        // Sign-out and every owner change drop the screens and their drafts.
        Assert.True(Regex.Matches(shell, @"ResetMoreScreens\(\);").Count >= 4);
        Assert.Contains("Post(ResetMoreScreens);", shell);
    }

    [Fact]
    public void BackClosesTheInnermostMoreLayerFirst()
    {
        var back = Between(Source("AndroidSliceShell.More.cs"),
            "private bool HandleMoreBack()", "\n    private void CloseMoreScreen()");
        int At(string text) => back.IndexOf(text, StringComparison.Ordinal);
        Assert.True(At("_moreMenu is not null") < At("ShowDeleteConfirmation"));
        Assert.True(At("skills.IsDeleteConfirmationPresented") < At("skills.IsFormOpen"));
        Assert.True(At("vocabulary.IsPhoneFilterPresented") < At("vocabulary.IsFormOpen"));
        Assert.True(At("vocabulary.IsFormOpen") < At("Publish(CloseMoreScreen)"));
        Assert.Contains("if (skills.IsPending)\n                    return true;", back);
        Assert.Contains("if (vocabulary.IsNavigationBlocked)\n                    return true;", back);
    }

    [Fact]
    public void QuizSettingsOfferEveryPreferenceAndTheApproved19Durations()
    {
        var quiz = Source("AndroidSliceShell.Settings.Compose.cs");
        foreach (var tag in new[]
                 {
                     "native.settings.quiz.voice-language", "native.settings.quiz.voice",
                     "native.settings.quiz.direction", "native.settings.quiz.autoplay",
                     "native.settings.quiz.mnemonics", "native.settings.quiz.text",
                     "native.settings.quiz.audio", "native.settings.quiz.photo",
                     "native.settings.quiz.duration", "native.settings.quiz.save",
                 })
            Assert.Contains($"\"{tag}\"", quiz);
        foreach (var direction in new[] { "TargetToNative", "NativeToTarget", "Mixed" })
            Assert.Contains($"(\"{direction}\"", quiz);
        Assert.Contains("ValueRange = new FloatRange(1f, 10f)", quiz);
        Assert.Contains("steps: NativeQuizTiming.ApprovedDurationsMs.Count - 2", quiz);
        Assert.Contains("QuizSettingsDraft.SnapDuration(raw)", quiz);
        Assert.Contains("_settings?.SaveQuiz()", Source("AndroidSliceShell.More.cs"));
        // 1 to 10 s in half-second steps: 19 values, 17 Compose steps between the ends.
        Assert.Equal(19, NativeQuizTiming.ApprovedDurationsMs.Count);
        Assert.Equal(1000, NativeQuizTiming.ApprovedDurationsMs[0]);
        Assert.Equal(10000, NativeQuizTiming.ApprovedDurationsMs[^1]);
        Assert.Contains(7500, NativeQuizTiming.ApprovedDurationsMs);
    }

    [Fact]
    public void ProfileSavesThroughTheSharedCoordinatorAndKeepsAccountActionsDisabledLikeIos()
    {
        var profile = Source("AndroidSliceShell.Profile.Compose.cs");
        var more = Source("AndroidSliceShell.More.cs");
        // Save (decision 8d) runs the shared owner-checked save off the UI thread and is
        // disabled while a save is in flight; the result shows under the button.
        Assert.Contains("\"native.profile.save\",\n            () => RunMore(() => profile.SaveAsync(_lifetime.Token)), enabled: state.CanSave)", profile);
        Assert.Contains("\"native.profile.save.status\"", profile);
        Assert.Contains("NativeProfile_Saving", profile);
        Assert.Contains("provider.GetRequiredService<INativeProfileRemoteStore>()", more);
        // Email is not part of the save, so it is read-only with an explanation.
        Assert.Contains("\"native.profile.email\", profile.SetEmail, enabled: false", profile);
        Assert.Contains("NativeProfile_EmailReadOnly", profile);
        Assert.DoesNotContain("NativeProfile_SaveUnavailable", profile);
        Assert.Contains("\"native.profile.password.change\",\n                static () => { }, enabled: false", profile);
        Assert.Contains("new TextButton(static () => { }, enabled: false)", profile);
        Assert.DoesNotContain("DeleteAccountAsync(", profile);
        Assert.DoesNotContain("ChangePasswordAsync(", profile);
    }

    [Fact]
    public void ListsUseNativeVirtualization()
    {
        Assert.Contains("new LazyVerticalGrid<SkillProfile>(", Source("AndroidSliceShell.Skills.Compose.cs"));
        Assert.Contains("new LazyColumn<ActivityLogRow>(", Source("AndroidSliceShell.ActivityLog.Compose.cs"));
        var vocabulary = Source("AndroidSliceShell.Vocabulary.Compose.cs");
        Assert.Contains("new LazyColumn<VocabularyRow>(", vocabulary);
        Assert.Contains("new LazyVerticalGrid<NativeVocabularyEntry>(", vocabulary);
        // Practice history: one lazy row per event.
        Assert.Contains("Add($\"history.{entry.Id}\"", vocabulary);
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, end);
        return text[from..to];
    }

    private static string Source(string name)
    {
        var root = typeof(AndroidMoreScreensContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!;
        return File.ReadAllText(Path.Combine(root, name));
    }
}
