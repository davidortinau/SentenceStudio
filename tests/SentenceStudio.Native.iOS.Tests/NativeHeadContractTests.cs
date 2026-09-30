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
        Assert.Equal("0.1.0-preview.3", project.Descendants("PackageVersion")
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

        var scene = File.ReadAllText(Path.Combine(HeadPath, "SceneDelegate.cs"));
        Assert.Contains("Platform.Init(() => Window?.RootViewController", scene);
        Assert.True(scene.IndexOf("Platform.Init(", StringComparison.Ordinal)
            < scene.IndexOf("_app.Start()", StringComparison.Ordinal));

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
        var controller = File.ReadAllText(Path.Combine(HeadPath, "NativeAppController.cs"));

        Assert.Contains("services.AddSyncServices(database, endpoint, downloadOnlyProfile: true)", services);
        Assert.Contains("services.AddSingleton<ISyncService, SyncService>();", services);
        Assert.Contains("services.AddOwnerBoundNativeSyncReadiness();", services);
        Assert.DoesNotContain("services.AddSingleton<INativeSyncReadiness, NativeApiSyncReadiness>();",
            services);
        Assert.Contains("await _dashboard.GenerateAsync(_lifetime.Token)", controller);
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
    public void VisualHost_KeepsSwiftUiActionsAccessibleAndOwnsInsetsOnce()
    {
        var host = File.ReadAllText(Path.Combine(
            HeadPath, "Platform", "NativeVisualViewController.cs"));
        Assert.Contains("SwiftUIHost.CreateController(node)", host);
        Assert.Contains("UIButton.FromType(UIButtonType.System)", host);
        Assert.Contains("button.TouchUpInside += (_, _) => node.InvokeAction();", host);
        Assert.Contains("field.EditingChanged += (_, _) =>", host);
        Assert.Contains("_answerChanged?.Invoke(field.Text ?? string.Empty)", host);
        Assert.Contains("Card(field, null, compact: true)", host);
        Assert.Contains("Card(row, id + \".container\", compact: true)", host);
        Assert.Contains("selected ? NativeVisualTheme.Primary", host);
        Assert.Contains("SelectedAnswer = value", File.ReadAllText(Path.Combine(
            HeadPath, "NativeAppController.cs")));
        Assert.Contains("View.SafeAreaLayoutGuide.TopAnchor", host);
        Assert.Contains("View.KeyboardLayoutGuide.TopAnchor", host);
        Assert.Contains("View.KeyboardLayoutGuide.UsesBottomSafeArea = false", host);
        Assert.Contains(
            "ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never",
            host);
        Assert.Contains("_scroll.ContentInset = new UIEdgeInsets(0, 0, scrollBottom, 0)", host);
        Assert.Contains(
            "_scroll.VerticalScrollIndicatorInsets = new UIEdgeInsets(0, 0, scrollBottom, 0)",
            host);
        Assert.Contains("SwiftUIHost.DetachController(node, host)", host);
    }

    [Fact]
    public void UIKitStack_PushesQuizAndKeepsTheSystemBackItemForTapAndSwipe()
    {
        var scene = File.ReadAllText(Path.Combine(HeadPath, "SceneDelegate.cs"));
        var controller = File.ReadAllText(Path.Combine(HeadPath, "NativeAppController.cs"));
        var feature = File.ReadAllText(Path.Combine(
            HeadPath, "Features", "NativeFeatureViews.cs"));

        Assert.Contains("RootViewController = _navigationController", scene);
        Assert.Contains("_navigation.SetViewControllers([dashboardController], false)", controller);
        Assert.Contains("_navigation.PushViewController(_quizController, true)", controller);
        Assert.Contains("_navigation.PopToRootViewController(true)", controller);
        Assert.Contains("_navigation.SetViewControllers([_signInModal], false)", controller);
        Assert.Contains("new UIBarButtonItem(string.Empty, UIBarButtonItemStyle.Plain, null, null)",
            controller);
        Assert.Contains("navigationItem.BackBarButtonItem = backItem", controller);
        Assert.Contains("backItem.AccessibilityLabel = Text(\"Dashboard_Title\")", controller);
        Assert.Contains("_quizBackAction ??= UIAction.Create(_ => Submit(BackFromQuizAsync))",
            controller);
        Assert.Contains("_quizController!.NavigationItem.BackAction = _quizBackAction",
            controller);
        Assert.Equal(1, controller.Split("Submit(BackFromQuizAsync)",
            StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("NavigationItem.LeftBarButtonItem", controller);
        Assert.DoesNotContain("NavigationItem.HidesBackButton = true", controller);
        Assert.DoesNotContain("chevron.backward", controller);
        Assert.Contains("_navigation.InteractivePopGestureRecognizer", controller);
        Assert.Contains("!ReferenceEquals(_navigation.TopViewController, shown)", controller);
        Assert.Contains("_quizController.NavigationItem.BackAction = null", controller);
        Assert.Contains("if (_handlingNativePop || !_nativePopPending)", controller);
        Assert.Contains("await BackFromQuizAsync()", controller);
        Assert.Contains("_nativePopPending", controller);
        Assert.DoesNotContain("WithAccessibilityIdentifier(\"native.quiz.back\")", feature);
    }

    [Fact]
    public void ResumeChoiceBack_ReturnsWithoutSuspendingAnUnopenedRound()
    {
        var source = File.ReadAllText(Path.Combine(HeadPath, "NativeAppController.cs"));
        var start = source.IndexOf("private async Task BackFromQuizAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("private async Task RetryQuizAsync()", start,
            StringComparison.Ordinal);
        Assert.True(end > start);
        var back = source[start..end];

        Assert.Contains("NativePhase.ResumeChoice", back);
        Assert.Contains("await ReturnToDashboardAsync();", back);
        Assert.True(back.IndexOf("await ReturnToDashboardAsync()", StringComparison.Ordinal)
            < back.IndexOf("await _quiz.BackAsync(", StringComparison.Ordinal));
    }

    [Fact]
    public void QuizKeepsTheSameChoiceButtonsAndShowsFeedbackAfterThem()
    {
        var feature = File.ReadAllText(Path.Combine(
            HeadPath, "Features", "NativeFeatureViews.cs"));
        var host = File.ReadAllText(Path.Combine(
            HeadPath, "Platform", "NativeVisualViewController.cs"));
        var questionStart = feature.IndexOf(
            "private static void RenderQuestion(", StringComparison.Ordinal);
        var feedbackStart = feature.IndexOf(
            "private static void RenderFeedback(", StringComparison.Ordinal);
        Assert.True(questionStart >= 0 && feedbackStart > questionStart);
        var question = feature[questionStart..feedbackStart];

        var choice = question.IndexOf(
            "items.Add(Views.Button(choice.Text", StringComparison.Ordinal);
        var feedback = question.IndexOf(
            "if (state.Phase == NativePhase.Feedback)", StringComparison.Ordinal);
        Assert.True(choice >= 0 && feedback > choice);
        Assert.DoesNotContain("Views.Text(label)", question);
        Assert.Contains("else if (state.Mode == \"Text\")", question);
        Assert.Contains("state.SelectedAnswer ?? throw new InvalidOperationException(", question);
        Assert.Contains(
            "Views.TextField(text(NativeText.AnswerRequired), displayedAnswer, answerChanged)",
            question);
        Assert.Contains("button.Enabled = !_quizBusy && _quizCanAnswer", host);
        Assert.Contains("content.AccessibilityValue = correct", host);
        Assert.Contains("status.WidthAnchor.ConstraintEqualTo(20)", host);
        Assert.Contains("button.TitleLabel.Lines = 0", host);
    }

    [Fact]
    public void QuizSummary_ProjectsWordMeaningAndLocalizedTextualResult()
    {
        var feature = File.ReadAllText(Path.Combine(
            HeadPath, "Features", "NativeFeatureViews.cs"));
        var host = File.ReadAllText(Path.Combine(
            HeadPath, "Platform", "NativeVisualViewController.cs"));
        Assert.Contains("state.Results.IsDefault ? [] : state.Results", feature);
        Assert.Contains("case NativePhase.Error when state.Destination == NativeDestination.QuizSummary:",
            feature);
        Assert.Contains("result.TargetLanguageTerm", feature);
        Assert.Contains("result.NativeLanguageTerm", feature);
        Assert.Contains("result.WasCorrect", feature);
        Assert.Contains("NativeText.QuizCorrect : NativeText.QuizIncorrect", feature);
        Assert.Contains("NativeAccess.QuizSummaryResult", feature);
        Assert.Contains("NativeVisualTheme.Primary", host);
        Assert.Contains("text(NativeText.QuizOverride)", feature);
        Assert.Contains("state.Prompt?.ShowPhoto == true", feature);
    }

    [Fact]
    public void QuizPrompt_UsesCenteredScaledReferenceDisplayAndSemanticPrimary()
    {
        var reference = File.ReadAllText(Path.Combine(
            HeadPath, "..", "SentenceStudio.UI", "Pages", "VocabQuiz.razor"));
        var feature = File.ReadAllText(Path.Combine(
            HeadPath, "Features", "NativeFeatureViews.cs"));
        var host = File.ReadAllText(Path.Combine(
            HeadPath, "Platform", "NativeVisualViewController.cs"));
        var theme = File.ReadAllText(Path.Combine(
            HeadPath, "Platform", "NativeVisualTheme.cs"));
        Assert.Contains("ss-display mb-2\" style=\"color: var(--bs-primary);\"",
            reference);
        Assert.Contains("WithAccessibilityIdentifier(NativeAccess.QuizQuestion)", feature);
        Assert.Contains("UIFontMetrics.GetMetrics(", host);
        Assert.Contains("UIFontTextStyle.LargeTitle.GetConstant()!.ToString()", host);
        Assert.Contains("GetScaledFont(UIFont.BoldSystemFontOfSize(NativeVisualTheme.QuizPromptSize))", host);
        Assert.Contains("label.TextAlignment = UITextAlignment.Center", host);
        Assert.Contains("label.TextColor = NativeVisualTheme.Primary", host);
        Assert.Contains("internal const int QuizPromptSize = 42", theme);
        Assert.Contains("ThemeCatalog.Default.PaletteFor(dark ?", theme);
    }

    [Fact]
    public void SignInScrollContentDefinesBothEdgesAndViewportWidth()
    {
        var source = File.ReadAllText(Path.Combine(
            HeadPath, "Platform", "NativeSignInViewController.cs"));
        Assert.Matches(
            @"content\.LeadingAnchor\.ConstraintEqualTo\(\s*_scroll\.ContentLayoutGuide\.LeadingAnchor",
            source);
        Assert.Matches(
            @"content\.TrailingAnchor\.ConstraintEqualTo\(\s*_scroll\.ContentLayoutGuide\.TrailingAnchor",
            source);
        Assert.Matches(
            @"content\.WidthAnchor\.ConstraintEqualTo\(\s*_scroll\.FrameLayoutGuide\.WidthAnchor,\s*1,\s*-2 \* NativeVisualTheme\.PagePadding",
            source);
        Assert.Contains("View.KeyboardLayoutGuide.UsesBottomSafeArea = false", source);
        Assert.Contains(
            "ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never",
            source);
        Assert.Contains("_scroll.BottomAnchor.ConstraintEqualTo(View.KeyboardLayoutGuide.TopAnchor)",
            source);
        Assert.Contains("_scroll.ContentInset = new UIEdgeInsets(0, 0, bottom, 0)", source);
        Assert.Contains(
            "_scroll.VerticalScrollIndicatorInsets = new UIEdgeInsets(0, 0, bottom, 0)",
            source);
    }

    [Fact]
    public void DeferredLearningDetailsPreview_CannotReportFalseUiCoverage()
    {
        Assert.DoesNotContain("\"mc-details\"", File.ReadAllText(Path.Combine(
            HeadPath, "Platform", "NativePreviewFixture.cs")));
        Assert.DoesNotContain("OpenDetails()", File.ReadAllText(Path.Combine(
            HeadPath, "NativeAppController.cs")));
    }

    [Fact]
    public void NativeSignInControls_HaveRealVisibilityAndRememberBehavior()
    {
        var signIn = File.ReadAllText(Path.Combine(
            HeadPath, "Platform", "NativeSignInViewController.cs"));
        Assert.Contains("NativeAccess.PasswordVisibility", signIn);
        Assert.Contains("_password.SecureTextEntry = !_showPassword", signIn);
        Assert.Contains("NativeAccess.RememberMe", signIn);
        Assert.Contains("signIn(_email.Text ?? string.Empty, password, _remember.On)", signIn);
        Assert.Contains("rememberMe: rememberMe", File.ReadAllText(Path.Combine(
            HeadPath, "NativeAppController.cs")));
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
        Assert.Contains("UIApplicationSceneManifest", fields);
        Assert.Contains("UIDeviceFamily", fields);
        Assert.Equal(["1", "2"], manifest.Descendants("integer").Select(element => element.Value));
        Assert.Equal("26.0", XDocument.Load(Path.Combine(HeadPath, "SentenceStudio.Native.iOS.csproj"))
            .Descendants("SupportedOSPlatformVersion").Single().Value);
    }

    private static string HeadPath => Path.GetFullPath(
        Path.Combine(Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
