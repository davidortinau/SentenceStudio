using System.Runtime.CompilerServices;
using System.Xml.Linq;
using SentenceStudio.Native.iOS.Platform;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeHeadContractTests
{
    [Theory]
    [InlineData("https://example.invalid/", false)]
    [InlineData("http://localhost:56301/", true)]
    [InlineData("http://127.0.0.1:56301/", true)]
    public void NativeApiEndpoint_AcceptsHttpsOrExplicitDebugLoopback(
        string input, bool allowLocalHttp) =>
        Assert.Equal(input, NativeApiEndpoint.Validate(input, allowLocalHttp).AbsoluteUri);

    [Theory]
    [InlineData("http://localhost:56301/", false)]
    [InlineData("http://api.example.invalid/", true)]
    [InlineData("https://person:secret@example.invalid/", true)]
    [InlineData("https://example.invalid/profile", true)]
    [InlineData("https://example.invalid/?token=x", true)]
    [InlineData("file:///tmp/api", true)]
    public void NativeApiEndpoint_RejectsInsecureOrNonRootConfiguration(
        string input, bool allowLocalHttp) =>
        Assert.Throws<ArgumentException>(() =>
            NativeApiEndpoint.Validate(input, allowLocalHttp));

    [Fact]
    public void NativeProject_IsIosSwiftUiWithIsolatedDebugAndReleaseIds()
    {
        var project = XDocument.Load(Path.Combine(HeadPath, "SentenceStudio.Native.iOS.csproj"));
        Assert.Equal("net11.0-ios", project.Descendants("TargetFramework").Single().Value);
        Assert.Contains(project.Descendants("ProjectReference"),
            reference => reference.Attribute("Include")?.Value
                == "../SentenceStudio.Native/SentenceStudio.Native.csproj");
        Assert.Contains(project.Descendants("ProjectReference"),
            reference => reference.Attribute("Include")?.Value
                == "../SentenceStudio.Shared/SentenceStudio.Shared.csproj");
        Assert.Contains(project.Descendants("PackageReference"),
            reference => reference.Attribute("Include")?.Value == "Microsoft.SwiftUI");
        Assert.Contains(project.Descendants("PackageReference"),
            reference => reference.Attribute("Include")?.Value == "Microsoft.Maui.Essentials");
        Assert.Equal("true", project.Descendants("UseMauiEssentials").Single().Value);
        Assert.Equal("0.1.0-preview.25", project.Descendants("PackageVersion")
            .Single(item => item.Attribute("Include")?.Value == "Microsoft.SwiftUI")
            .Attribute("Version")?.Value);
        Assert.DoesNotContain(project.Descendants("PackageReference"), reference =>
        {
            var packageId = reference.Attribute("Include")?.Value ?? string.Empty;
            return packageId.StartsWith("Microsoft.Maui.Controls", StringComparison.OrdinalIgnoreCase)
                || packageId.Contains("MauiReactor", StringComparison.OrdinalIgnoreCase)
                || packageId.Contains("Reactor.Maui", StringComparison.OrdinalIgnoreCase)
                || packageId.Contains("BlazorWebView", StringComparison.OrdinalIgnoreCase)
                || packageId.Contains("Comet", StringComparison.OrdinalIgnoreCase);
        });
        Assert.DoesNotContain(project.Descendants("ProjectReference"), reference =>
            (reference.Attribute("Include")?.Value ?? string.Empty).Contains(
                "MauiCompatibility", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, project.Descendants("ApplicationId").Count());
        Assert.All(project.Descendants("ApplicationId"), id =>
            Assert.Contains(".native.", id.Value, StringComparison.Ordinal));
        Assert.Contains(project.Descendants("ApplicationId"), id =>
            id.Attribute("Condition")?.Value.Contains("NativePreviewSurface",
                StringComparison.Ordinal) == true
            && id.Value.EndsWith(".fixture", StringComparison.Ordinal));
        Assert.Contains(project.Descendants("AssemblyAttribute"), item =>
            item.Attribute("Condition")?.Value.Contains("NativePreviewAppearance",
                StringComparison.Ordinal) == true
            && item.Elements("_Parameter1").Single().Value
                == "SentenceStudio.Native.PreviewAppearance");
    }

    [Fact]
    public void LauncherTitle_UsesDefaultAndAllowsPerBuildReviewLabelWithoutChangingIdentity()
    {
        var project = XDocument.Load(Path.Combine(
            HeadPath, "SentenceStudio.Native.iOS.csproj"));
        var manifest = XDocument.Load(Path.Combine(HeadPath, "Info.plist"));

        var titles = project.Descendants("ApplicationTitle").ToArray();
        Assert.Equal(2, titles.Length);
        Assert.Equal("SentenceStudio", titles[0].Value);
        Assert.Equal("SS Review", titles[1].Value);
        Assert.Equal(
            "'$(ApplicationId)' == 'com.simplyprofound.sentencestudio.native.pageui.kaylee'",
            titles[1].Attribute("Condition")?.Value);
        Assert.DoesNotContain(manifest.Descendants("key"),
            key => key.Value == "CFBundleDisplayName");
        Assert.Equal("com.simplyprofound.sentencestudio.native.debug",
            project.Descendants("ApplicationId").Single(id =>
                id.Attribute("Condition")?.Value
                    == "'$(Configuration)' == 'Debug'").Value);
        Assert.DoesNotContain("NativeApplicationTitle",
            File.ReadAllText(Path.Combine(HeadPath,
                "SentenceStudio.Native.iOS.csproj")));
    }

    [Fact]
    public void NativePreferences_UsesEssentialsWithoutChangingCredentialNamespaceOrAllowingBulkClear()
    {
        var preferences = File.ReadAllText(Path.Combine(
            HeadPath, "Platform", "NativePreferences.cs"));
        Assert.Contains("Preferences.Default.Get(key, defaultValue)", preferences);
        Assert.Contains("Preferences.Default.Set(key, value)", preferences);
        Assert.Contains("Preferences.Default.Remove(key)", preferences);
        Assert.DoesNotContain("Preferences.Default.Clear(", preferences);

        var program = File.ReadAllText(Path.Combine(HeadPath, "Program.cs"));
        Assert.Contains("SwiftUIApplication.Run(scene.Root)", program);
        Assert.DoesNotContain("Platform.Init(", program);
        Assert.DoesNotContain("SceneDelegate", program);

        var services = File.ReadAllText(Path.Combine(HeadPath, "NativeServices.cs"));
        Assert.Contains("services.AddSingleton<IPreferencesService, NativePreferences>();", services);
        Assert.Contains("services.AddSingleton<ISecureStorageService, NativeKeychain>();", services);
        Assert.Contains("new IdentityAuthService(", services);
        Assert.Contains("services.AddSingleton<IAuthService, NativeProfileAuthService>();", services);
    }

    [Fact]
    public void CanonicalHead_UsesExistingCoreSyncAndGeneratesMissingPlanAfterOwnerBoundRestore()
    {
        var services = File.ReadAllText(Path.Combine(HeadPath, "NativeServices.cs"));
        var controller = File.ReadAllText(Path.Combine(
            HeadPath, "App/ApplicationController.cs"));

        Assert.Contains("services.AddSyncServices(database, endpoint, downloadOnlyProfile: true)", services);
        Assert.Contains("services.AddSingleton<ISyncService, SyncService>();", services);
        Assert.Contains("services.AddOwnerBoundNativeSyncReadiness();", services);
        Assert.DoesNotContain("services.AddSingleton<INativeSyncReadiness, NativeApiSyncReadiness>();",
            services);
        Assert.Contains("await _dashboard.LoadAsync(_lifetime.Token)", controller);
        Assert.Contains("await _dashboard.RefreshAsync(_lifetime.Token)", controller);
        Assert.Contains("services.AddSingleton<IAuthService, NativeProfileAuthService>();", services);
    }

    [Fact]
    public void AgentAndLocalNetworking_AreDebugOnlyAndReleaseHasNoReferences()
    {
        var project = XDocument.Load(Path.Combine(HeadPath, "SentenceStudio.Native.iOS.csproj"));
        var agentReferences = project.Descendants("NativeReference").ToList();
        Assert.Equal(2, agentReferences.Count);
        Assert.All(agentReferences, reference =>
            Assert.Contains("'$(Configuration)' == 'Debug'",
                reference.Parent!.Attribute("Condition")!.Value, StringComparison.Ordinal));
        var debugNetworking = Assert.Single(project.Descendants("PartialAppManifest"));
        Assert.Contains("'$(Configuration)' == 'Debug'",
            debugNetworking.Attribute("Condition")!.Value, StringComparison.Ordinal);
        var shim = File.ReadAllText(Path.Combine(HeadPath, "Native", "AilohaDebugShim.swift"));
        Assert.Contains("portFallbackCount: 32", shim);
    }

    [Fact]
    public void NamedFeatureViews_AreMountedDirectlyUnderSwiftUIOwnedScene()
    {
        var scene = File.ReadAllText(Path.Combine(
            HeadPath, "App/ApplicationScene.cs"));
        var dashboard = File.ReadAllText(Path.Combine(
            HeadPath, "Features", "Dashboard", "DashboardView.cs"));
        var quiz = File.ReadAllText(Path.Combine(
            HeadPath, "Features", "Quiz", "QuizView.cs"));
        var controller = File.ReadAllText(Path.Combine(
            HeadPath, "App/ApplicationController.Quiz.cs"));

        Assert.Contains("Views.VStack(_app.RenderDashboardContent(_style))", scene);
        Assert.Contains("_app.RenderQuizContent(_style, out _quizPhotoCover)", scene);
        Assert.Contains("Views.VStack(_app.RenderResources", scene);
        Assert.Contains("Views.VStack(ResourceFormContent())", scene);
        Assert.Contains("SelectedAnswer = value", controller);
        Assert.Contains("WithAccessibilityIdentifier(NativeAccess.Plan)", dashboard);
        Assert.Contains("WithAccessibilityIdentifier(\"native.quiz.screen\")", quiz);
        Assert.DoesNotContain("SwiftUIHost.CreateController", scene);
        Assert.DoesNotContain("UIStackView", scene);
    }

    [Fact]
    public void SwiftUIStack_PushesQuizAndRoutesSystemBackThroughPresenterOnce()
    {
        var scene = File.ReadAllText(Path.Combine(HeadPath, "App/ApplicationScene.cs"));
        var controller = File.ReadAllText(Path.Combine(
            HeadPath, "App/ApplicationController.Quiz.cs"));
        var feature = File.ReadAllText(Path.Combine(
            HeadPath, "Features", "Quiz", "QuizView.cs"));

        Assert.Contains("Views.NavigationStack(_selectorSheet", scene);
        Assert.Contains("_dashboardStack.Push(\"quiz\", _quizSheet)", scene);
        Assert.Contains("_app.OnQuizPopped()", scene);
        Assert.Contains("if (routes.Count != 0)", scene);
        Assert.Contains("if (_app.IsQuizOpen)", scene);
        Assert.Contains("if (_handlingNativePop || !_nativePopPending)", controller);
        Assert.Contains("await BackFromQuizAsync()", controller);
        Assert.Contains("_nativePopPending", controller);
        Assert.DoesNotContain("UINavigationController", scene);
        Assert.DoesNotContain("WithAccessibilityIdentifier(\"native.quiz.back\")", feature);
    }

    [Fact]
    public void ResumeChoiceBack_UsesOwnerCheckedPresenterWithoutUnopenedRoundSnapshot()
    {
        var source = File.ReadAllText(Path.Combine(
            HeadPath, "App/ApplicationController.Quiz.cs"));
        var start = source.IndexOf("private async Task BackFromQuizAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("private async Task RetryQuizAsync()", start,
            StringComparison.Ordinal);
        Assert.True(end > start);
        var back = source[start..end];

        Assert.DoesNotContain("NativePhase.ResumeChoice", back);
        Assert.Equal(1, back.Split("await _quiz.BackAsync(",
            StringSplitOptions.None).Length - 1);
        Assert.Contains("await ReturnToDashboardAsync();", back);
        Assert.True(back.LastIndexOf("await _quiz.BackAsync(", StringComparison.Ordinal)
            < back.LastIndexOf("await ReturnToDashboardAsync()", StringComparison.Ordinal));
        Assert.DoesNotContain("SuspendAsync(", back);
        Assert.DoesNotContain("SaveSnapshotAsync(", back);
    }

    [Fact]
    public void QuizKeepsChoicesButShowsStatusAndAnswerBeforeThem()
    {
        var feature = File.ReadAllText(Path.Combine(
            HeadPath, "Features", "Quiz", "QuizQuestionView.cs"));
        var questionStart = feature.IndexOf(
            "internal static void Add(", StringComparison.Ordinal);
        var feedbackStart = feature.IndexOf(
            "private static SwiftView QuizChoice(", StringComparison.Ordinal);
        Assert.True(questionStart >= 0 && feedbackStart > questionStart);
        var question = feature[questionStart..feedbackStart];

        var term = question.IndexOf(
            "native.quiz.correct-answer", StringComparison.Ordinal);
        var feedback = question.IndexOf(
            "if (state.Phase == NativePhase.Feedback)", StringComparison.Ordinal);
        var choices = question.IndexOf(
            "if (state.Mode == \"MultipleChoice\")", StringComparison.Ordinal);
        Assert.True(feedback >= 0 && term > feedback && choices > term);
        Assert.Contains("QuizChoice(style, state, choice.Text,", question);
        Assert.Contains("audioChoices && !canSelectAudioChoice(index)", question);
        Assert.Contains("else if (state.Mode == \"Text\")", question);
        Assert.Contains("state.SelectedAnswer ?? answer", question);
        Assert.Contains("NativeText.CorrectionRequired : NativeText.AnswerRequired",
            question);
        Assert.Contains("answerChanged(value);", question);
        Assert.Contains("!string.IsNullOrWhiteSpace(value)", question);
        Assert.Contains("|| string.IsNullOrWhiteSpace(_answer)",
            File.ReadAllText(Path.Combine(
                HeadPath, "App/ApplicationController.Quiz.cs")));
        Assert.Contains("if (_busy || _quizState.Phase != NativePhase.Ready)",
            File.ReadAllText(Path.Combine(
                HeadPath, "App/ApplicationController.Quiz.cs")));
    }

    [Fact]
    public void QuizSummary_ProjectsWordMeaningAndLocalizedTextualResult()
    {
        var feature = File.ReadAllText(Path.Combine(
            HeadPath, "Features", "Quiz", "QuizSummaryView.cs"));
        var quiz = File.ReadAllText(Path.Combine(
            HeadPath, "Features", "Quiz", "QuizView.cs"));
        var feedback = File.ReadAllText(Path.Combine(
            HeadPath, "Features", "Quiz", "QuizFeedbackView.cs"));
        Assert.Contains("state.Results.IsDefault ? [] : state.Results", feature);
        Assert.Contains("case NativePhase.Error when state.Destination == NativeDestination.QuizSummary:",
            quiz);
        Assert.Contains("result.TargetLanguageTerm", feature);
        Assert.Contains("result.NativeLanguageTerm", feature);
        Assert.Contains("result.WasCorrect", feature);
        Assert.Contains("? result.ReadyToRotateOut", feature);
        Assert.Contains(": NativeText.QuizCorrect", feature);
        Assert.Contains(": NativeText.QuizIncorrect", feature);
        Assert.Contains("NativeAccess.QuizSummaryResult", feature);
        Assert.Contains("Views.Text(result.TargetLanguageTerm)", feature);
        Assert.Contains("text(NativeText.QuizOverride)", feedback);
        Assert.DoesNotContain("if (state.Prompt?.ShowPhoto == true)", feature);
    }

    [Fact]
    public void QuizPrompt_UsesSwiftUIDynamicTypeAndKeepsSourcePromptSemantics()
    {
        var reference = File.ReadAllText(Path.Combine(
            HeadPath, "..", "SentenceStudio.UI", "Pages", "VocabQuiz.razor"));
        var feature = File.ReadAllText(Path.Combine(
            HeadPath, "Features", "Quiz", "QuizQuestionView.cs"));
        var quiz = File.ReadAllText(Path.Combine(
            HeadPath, "Features", "Quiz", "QuizView.cs"));
        Assert.Contains("ss-display mb-2\" style=\"color: var(--bs-primary);\"",
            reference);
        Assert.Contains("WithAccessibilityIdentifier(NativeAccess.QuizQuestion)", feature);
        Assert.Contains("SourceTextRole.Display, SourceTextTone.Primary", feature);
        Assert.Contains("Views.BottomInset(page, footer)", quiz);
        Assert.DoesNotContain("UIFontMetrics", feature);
    }

    [Fact]
    public void SignInUsesSwiftUIScrollAndSecureFieldWithoutUIKitKeyboardGuide()
    {
        var source = File.ReadAllText(Path.Combine(
            HeadPath, "Features", "Authentication", "SignInView.cs"));
        Assert.Contains("Views.Scroll(content.ToArray())", source);
        Assert.Contains("Views.SecureField(text(\"Common_PasswordPlaceholder\")",
            source);
        Assert.Contains("WithAccessibilityIdentifier(NativeAccess.Email)", source);
        Assert.Contains("WithAccessibilityIdentifier(NativeAccess.Password)", source);
        Assert.DoesNotContain("View.KeyboardLayoutGuide", source);
    }

    [Fact]
    public void LearningDetails_AreARealInformationSheetWithoutPlaceholderCaption()
    {
        Assert.DoesNotContain("\"mc-details\"", File.ReadAllText(Path.Combine(
            HeadPath, "Platform", "NativePreviewFixture.cs")));
        Assert.DoesNotContain("QuizDetailsLocalized",
            File.ReadAllText(Path.Combine(
                HeadPath, "Features", "Quiz", "QuizLearningDetailsView.cs")));
        Assert.DoesNotContain("native.quiz.details.unavailable",
            File.ReadAllText(Path.Combine(
                HeadPath, "Features", "Quiz", "QuizLearningDetailsView.cs")));
        Assert.Contains("_quiz.OpenDetails()",
            File.ReadAllText(Path.Combine(
                HeadPath, "App/ApplicationController.Quiz.cs")));
    }

    [Fact]
    public void NativeSignInControls_HaveRealVisibilityAndRememberBehavior()
    {
        var signIn = File.ReadAllText(Path.Combine(
            HeadPath, "Features", "Authentication", "SignInView.cs"));
        Assert.Contains("NativeAccess.PasswordVisibility", signIn);
        Assert.Contains("? Views.TextField(text(\"Common_PasswordPlaceholder\")",
            signIn);
        Assert.Contains(": Views.SecureField(text(\"Common_PasswordPlaceholder\")",
            signIn);
        Assert.Contains("NativeAccess.RememberMe", signIn);
        Assert.Contains("_email, password, _rememberMe, _lifetime.Token)",
            File.ReadAllText(Path.Combine(
                HeadPath, "App/ApplicationController.cs")));
    }

    [Fact]
    public void AuthenticatedSample_IsOptInAndDebugOnly()
    {
        var project = XDocument.Load(Path.Combine(HeadPath, "SentenceStudio.Native.iOS.csproj"));
        var owner = Assert.Single(project.Descendants("AssemblyAttribute")
            .Where(item => item.Element("_Parameter1")?.Value
                == "SentenceStudio.Native.AuthenticatedSampleOwnerId"));
        Assert.Contains("'$(Configuration)' == 'Debug'",
            owner.Parent!.Attribute("Condition")!.Value, StringComparison.Ordinal);
        Assert.Contains("NativeAuthenticatedSampleOwnerId",
            owner.Parent.Attribute("Condition")!.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void SwiftUiPreview_UsesOnlyTheStandaloneLocalArtifactFeed()
    {
        var config = XDocument.Load(Path.Combine(HeadPath, "NuGet.Config"));
        var source = Assert.Single(config.Descendants("packageSources")
            .Descendants("add"));
        Assert.Equal("swiftui-preview", source.Attribute("key")?.Value);
        Assert.EndsWith("/Microsoft.SwiftUI/artifacts",
            source.Attribute("value")?.Value, StringComparison.Ordinal);

        var mapping = Assert.Single(config.Descendants("packageSource"));
        Assert.Equal("swiftui-preview", mapping.Attribute("key")?.Value);
        Assert.Equal("Microsoft.SwiftUI",
            Assert.Single(mapping.Descendants("package")).Attribute("pattern")?.Value);
    }

    [Fact]
    public void InfoPlist_EnablesFullScreenIphoneAndIpadOnIos26()
    {
        var manifest = XDocument.Load(Path.Combine(HeadPath, "Info.plist"));
        var fields = manifest.Descendants("key").Select(key => key.Value).ToArray();
        Assert.Contains("UILaunchScreen", fields);
        Assert.DoesNotContain("UIApplicationSceneManifest", fields);
        Assert.Contains("UIDeviceFamily", fields);
        Assert.Equal(["1", "2"], manifest.Descendants("integer").Select(element => element.Value));
        Assert.Equal("26.0", XDocument.Load(Path.Combine(HeadPath, "SentenceStudio.Native.iOS.csproj"))
            .Descendants("SupportedOSPlatformVersion").Single().Value);
    }

    private static string HeadPath => Path.GetFullPath(
        Path.Combine(Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
