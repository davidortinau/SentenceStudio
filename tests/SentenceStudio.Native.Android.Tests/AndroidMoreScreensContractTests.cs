using System.Reflection;
using System.Text.RegularExpressions;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android.Tests;

/// <summary>
/// W2: Profile, Settings, Skills, Vocabulary and Activity Log (AF-N08, now reached through
/// the tab shell) use the shared coordinators iOS uses, and Settings carries
/// every quiz preference with the approved 1-10 s auto-advance (AF-Q14, AF-Q15).
/// </summary>
public sealed class AndroidMoreScreensContractTests
{
    private static readonly string[] MoreFiles =
    [
        "AndroidSliceShell.More.cs", "AndroidSliceShell.Appearance.cs",
        "AndroidSliceShell.Settings.Compose.cs", "AndroidSliceShell.Profile.Compose.cs",
        "AndroidSliceShell.Skills.Compose.cs", "AndroidSliceShell.Vocabulary.Compose.cs",
        "AndroidSliceShell.ActivityLog.Compose.cs", "AndroidSliceShell.Tabs.cs",
        "AndroidSliceShell.Tabs.Compose.cs",
    ];

    /// <summary>
    /// Tab shell trial (iOS 7a09daaf, 0a8e1b16, 25fef602): every former More menu destination
    /// stays reachable. Study's header opens History; the Library lists Resources, Vocabulary,
    /// Skills and Minimal Pairs, then Quick add in iOS order; Settings starts with the
    /// profile row and ends with Sign out.
    /// </summary>
    [Fact]
    public void TabShellReachesEverySourceDestinationInIosOrder()
    {
        var tabs = Source("AndroidSliceShell.Tabs.Compose.cs");
        var shell = Source("AndroidSliceShell.Tabs.cs");
        AssertInOrder(tabs, "NativeAccess.DashboardTab", "\"native.tab.library\"",
            "\"native.tab.settings\"", "\"native.tab.sam\"");
        AssertInOrder(tabs, "\"native.library.row.resources\"", "\"native.library.row.vocabulary\"",
            "\"native.library.row.skills\"", "\"native.library.row.minimal-pairs\"");
        AssertInOrder(shell, "new(\"resource\"", "new(\"skill\"", "new(\"minimal-pair\"",
            "new(\"import-content\"", "new(\"youtube-import\"");
        Assert.Contains("OpenActivityLog", Source("AndroidSliceShell.Visual.cs"));
        var settings = Source("AndroidSliceShell.Appearance.cs");
        AssertInOrder(settings, "SettingsProfileRow(palette)", "RenderAppearanceSection(palette)",
            "RenderSamSettings(palette)", "SettingsSignOut(palette)");
        Assert.Contains(".Clickable(OpenProfile)", tabs);
        Assert.DoesNotContain("native.dashboard.menu.", Source("AndroidSliceShell.Visual.cs"));
    }

    private static void AssertInOrder(string source, params string[] markers)
    {
        var positions = markers.Select(marker => source.IndexOf(marker, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order().ToArray(), positions);
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

    /// <summary>
    /// Captain 2026-11-10 (as iOS): every editor has one Save, a plain text app-bar action at
    /// every width, with no Save in the overflow menu and no Save or Cancel under the form; the
    /// word editor lists only the word's resources (Remove, Add to Resource) and has the
    /// source's Reference Sentences on the shared browser.
    /// </summary>
    [Fact]
    public void Editors_HaveOnePlainAppBarSave_AndTheWordEditorHasItsSourceSections()
    {
        var actions = Source("AndroidSliceShell.VocabularyDuplicates.Compose.cs");
        var word = Between(actions, "private ComposableNode VocabularyWordActions(",
            "private DropdownMenuItem VocabularyMenuItem(");
        Assert.Contains("new TextButton(() => RunMoreOnUi(vocabulary.SaveAsync), enabled: state.CanSave)", word);
        Assert.DoesNotContain("WindowWidthDp", word);
        Assert.DoesNotContain("save.menu", word);
        Assert.DoesNotContain("save.toolbar", word);

        var vocabulary = Source("AndroidSliceShell.Vocabulary.Compose.cs");
        Assert.DoesNotContain("native.vocabulary.edit.actions", vocabulary);
        Assert.Contains("Add(\"examples\", () => VocabularyExamplesCard(vocabulary, state, palette));", vocabulary);
        var resources = Between(vocabulary, "private ComposableNode VocabularyResourcesCard(",
            "return Card(palette, \"native.vocabulary.edit.resources\"");
        Assert.DoesNotContain("SettingSwitch(", resources);
        Assert.Contains("vocabulary.ToggleResource(id, false)", resources);
        Assert.Contains("vocabulary.ToggleResource(id, true)", resources);
        // The rules are the shared browser's, not a copy per head.
        Assert.Contains("state.SelectedResources", resources);
        Assert.Contains("state.AddableResources", resources);

        var examples = Source("AndroidSliceShell.VocabularyExamples.Compose.cs");
        foreach (var call in new[] { "vocabulary.GenerateExamplesAsync", "vocabulary.AddExampleAsync",
                     "vocabulary.KeepSuggestionAsync(key)", "vocabulary.DiscardSuggestion(key)",
                     "vocabulary.ToggleExampleCoreAsync(sentenceId)", "vocabulary.StartEditExample(sentenceId)",
                     "vocabulary.ToggleExampleFlagAsync(sentenceId)", "vocabulary.DeleteExampleAsync(sentenceId)",
                     "vocabulary.SaveEditedExampleAsync", "vocabulary.CancelEditExample" })
            Assert.Contains(call, examples);
        // Typing keeps IME composition: the field redraws at once, through the shared draft.
        Assert.Contains("RenderMoreNow();", examples);
        // Labels come from the shared keys, not a copy of the switches.
        Assert.Contains("VocabularyExampleText.RegisterKey(", examples);
        Assert.Contains("VocabularyExampleText.SourceKey(", examples);

        var skills = Source("AndroidSliceShell.Skills.Compose.cs");
        Assert.Contains(".TestTag(\"native.skills.form.save\")", skills);
        Assert.DoesNotContain("native.skills.form.save.bottom", skills);
        Assert.DoesNotContain("native.skills.form.cancel", skills);
        var resourceForm = Source("AndroidSliceShell.ResourceForm.Compose.cs");
        Assert.DoesNotContain("native.resources.add.save.bottom", resourceForm);
        Assert.Contains("var save = new TextButton(resources.Save, enabled: canSave)", resourceForm);
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
