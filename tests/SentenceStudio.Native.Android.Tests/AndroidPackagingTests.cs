using System.Reflection;
using System.Xml.Linq;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidPackagingTests
{
    private static readonly string HeadRoot = Path.GetFullPath(typeof(AndroidPackagingTests)
        .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!);

    [Fact]
    public void DebugHasAnIsolatedIdentityAndReleaseKeepsProductionIdentity()
    {
        var project = XDocument.Load(Path.Combine(
            HeadRoot, "SentenceStudio.Native.Android.csproj"));
        var ids = project.Descendants("ApplicationId").ToDictionary(
            node => (string?)node.Attribute("Condition") ?? string.Empty,
            node => node.Value);
        Assert.Equal("com.simplyprofound.sentencestudio.compose.debug",
            ids["'$(Configuration)' == 'Debug'"]);
        Assert.Equal("com.simplyprofound.sentencestudio",
            ids["'$(Configuration)' == 'Release'"]);
        Assert.Contains(project.Descendants("ProjectReference"), reference =>
            ((string?)reference.Attribute("Include"))?.EndsWith(
                "/SentenceStudio.Native.csproj", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void AilohaBindingIsReferencedOnlyForDebug()
    {
        var project = XDocument.Load(Path.Combine(
            HeadRoot, "SentenceStudio.Native.Android.csproj"));
        var bindings = project.Descendants("ProjectReference").Where(reference =>
            ((string?)reference.Attribute("Include"))?.Contains(
                "AilohaBinding/", StringComparison.Ordinal) == true);
        var binding = Assert.Single(bindings);
        Assert.Equal("'$(Configuration)' == 'Debug'",
            (string?)binding.Attribute("Condition"));
        Assert.StartsWith("#if DEBUG", File.ReadAllText(
            Path.Combine(HeadRoot, "DebugAgentApplication.cs")));
    }

    [Fact]
    public void NativeTheme_UsesSystemLightAndDarkBarAppearances()
    {
        var activity = File.ReadAllText(Path.Combine(HeadRoot, "MainActivity.cs"));
        Assert.Contains("Theme = \"@style/SentenceStudioNativeTheme\"", activity);
        foreach (var (folder, expectedLight) in new[]
        {
            ("values", "true"), ("values-night", "false")
        })
        {
            var resources = XDocument.Load(Path.Combine(
                HeadRoot, "Resources", folder, "styles.xml"));
            var style = Assert.Single(resources.Descendants("style"));
            Assert.Equal("SentenceStudioNativeTheme",
                (string?)style.Attribute("name"));
            var items = style.Elements("item").ToDictionary(
                item => (string?)item.Attribute("name") ?? string.Empty,
                item => item.Value);
            Assert.Equal(expectedLight, items["android:windowLightStatusBar"]);
            Assert.Equal(expectedLight, items["android:windowLightNavigationBar"]);
        }
        Assert.Contains("UseDynamicColor = false", File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs")));
    }

    [Fact]
    public void QuizChoices_ShowPendingSelectionWithoutScoringEarly()
    {
        Assert.Contains("Quiz = Quiz with { SelectedAnswer = response }",
            File.ReadAllText(Path.Combine(HeadRoot, "AndroidSliceShell.cs")));
        Assert.Contains("semantics.Selected(selected)",
            File.ReadAllText(Path.Combine(HeadRoot, "AndroidSliceShell.Visual.cs")));
    }

    [Fact]
    public void SignInControls_UseSharedRememberAndPasswordVisibilityContracts()
    {
        var shell = File.ReadAllText(Path.Combine(HeadRoot, "AndroidSliceShell.cs"));
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));
        Assert.Contains("rememberMe: Remember.Value", shell);
        Assert.Contains("new Checkbox(@checked: Remember.Value", composition);
        Assert.Contains("NativeAccess.PasswordVisibility", composition);
        Assert.Contains("VisualTransformation = ShowPassword.Value", composition);
        Assert.Contains("NativeText.ShowPasswordShort", composition);
        Assert.Contains("new TextButton(", composition);
        Assert.DoesNotContain("TrailingIcon = new IconButton", composition);
        foreach (var culture in new[] { "AppResources.resx", "AppResources.ko.resx" })
        {
            var resources = XDocument.Load(Path.Combine(HeadRoot, "..",
                "SentenceStudio.Shared", "Resources", "Strings", culture));
            foreach (var key in new[]
            {
                "Login_ShowPassword", "Login_HidePassword",
                "Login_ShowPasswordShort", "Login_HidePasswordShort",
            })
                Assert.Contains(resources.Descendants("data"),
                    entry => (string?)entry.Attribute("name") == key);
        }
    }

    [Fact]
    public void PasswordReveal_UsesExplicitNoOpTransformationOnRecomposition()
    {
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));
        Assert.Matches(
            @"VisualTransformation = ShowPassword\.Value\s*\?\s*AndroidX\.Compose\.UI\.Text\.Input\.VisualTransformation\.Companion\.None\s*:\s*new PasswordVisualTransformation",
            composition);
    }

    [Fact]
    public void QuizCorrectionOverride_UsesLocalizedPresenterCommand()
    {
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));
        var shell = File.ReadAllText(Path.Combine(HeadRoot, "AndroidSliceShell.cs"));
        Assert.Contains("Action(Localize(NativeText.QuizOverride)", composition);
        Assert.Contains("\"native.quiz.override\", OverrideQuiz", composition);
        Assert.Contains("RequiredQuiz().OverrideAsCorrectAsync(cancellationToken)", shell);
        Assert.Contains("state.Phase == NativePhase.Feedback", shell);
    }

    [Fact]
    public void QuizSummary_RendersOwnedWordMeaningAndTextualVerdict()
    {
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));
        Assert.Contains("state.Results.IsDefault ? [] : state.Results", composition);
        Assert.Contains("result.TargetLanguageTerm", composition);
        Assert.Contains("result.NativeLanguageTerm", composition);
        Assert.Contains("result.WasCorrect", composition);
        Assert.Contains("NativeText.QuizCorrect : NativeText.QuizIncorrect", composition);
        Assert.Contains("NativeAccess.QuizSummaryResult", composition);
    }

    [Theory]
    [InlineData(NativePhase.Ready)]
    [InlineData(NativePhase.Feedback)]
    public void UiModeChange_MidTurnKeepsRouteAndAnswerOrCorrection(
        NativePhase phase)
    {
        var activity = File.ReadAllText(Path.Combine(HeadRoot, "MainActivity.cs"));
        var shell = File.ReadAllText(Path.Combine(HeadRoot, "AndroidSliceShell.cs"));
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));
        Assert.Contains("ConfigurationChanges = ConfigChanges.UiMode", activity);
        var callback = activity[activity.IndexOf(
            "public override void OnConfigurationChanged", StringComparison.Ordinal)..activity.IndexOf(
            "internal void UpdateBackHandling", StringComparison.Ordinal)];
        Assert.Contains("base.OnConfigurationChanged(newConfig)", callback);
        Assert.Contains("_shell?.RefreshForUiMode()", callback);
        Assert.DoesNotContain("new AndroidSliceShell", callback);
        var refresh = shell[shell.IndexOf("internal void RefreshForUiMode", StringComparison.Ordinal)
            ..shell.IndexOf("public void Start()", StringComparison.Ordinal)];
        Assert.Contains("_revision.Value =", refresh);
        Assert.DoesNotContain("Destination =", refresh);
        Assert.DoesNotContain("Answer.Value =", refresh);
        Assert.DoesNotContain("Correction.Value =", refresh);
        Assert.Contains("MaterialTheme.IsSystemInDarkTheme(composer)", composition);
        Assert.False(AndroidQuizInputPolicy.ShouldClearForNewTurn(
            "same-turn", new NativeQuizState(
                phase, NativeDestination.VocabularyQuiz, TurnId: "same-turn")));
    }

    [Fact]
    public void QuizPrompt_PreservesReferenceDisplayHierarchyInLightAndDark()
    {
        var source = File.ReadAllText(Path.Combine(
            HeadRoot, "..", "SentenceStudio.UI", "Pages", "VocabQuiz.razor"));
        var theme = File.ReadAllText(Path.Combine(HeadRoot, "AndroidVisualTheme.cs"));
        var visual = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Visual.cs"));
        var composition = File.ReadAllText(Path.Combine(
            HeadRoot, "AndroidSliceShell.Compose.cs"));
        Assert.Contains("ss-display mb-2\" style=\"color: var(--bs-primary);\"",
            source);
        Assert.Contains("ThemeCatalog.Default.PaletteFor(dark ?", theme);
        Assert.Contains("internal const int QuizPromptSize = 42", theme);
        Assert.Contains("Color = palette.Primary", visual);
        Assert.Contains("FontWeight = FontWeight.Bold", visual);
        Assert.Contains("Align = TextAlign.Center", visual);
        Assert.Contains("viewport.Add(QuizPromptHeading(question, palette))", composition);
        Assert.DoesNotContain("OpenPhoto()", composition);
    }

    [Theory]
    [InlineData("Debug")]
    [InlineData("Release")]
    public void ManifestEnforcesApi36AndConfigurationSpecificHttp(string configuration)
    {
        var manifest = XDocument.Load(Path.Combine(
            HeadRoot, "Properties", $"AndroidManifest.{configuration}.xml"));
        XNamespace android = "http://schemas.android.com/apk/res/android";
        Assert.Equal("36",
            (string?)manifest.Descendants("uses-sdk").Single().Attribute(android + "minSdkVersion"));
        var application = manifest.Descendants("application").Single();
        if (configuration == "Release")
        {
            Assert.Equal("false",
                (string?)application.Attribute(android + "usesCleartextTraffic"));
            Assert.Null(application.Attribute(android + "networkSecurityConfig"));
        }
        else
        {
            Assert.Equal("@xml/native_network_security_config",
                (string?)application.Attribute(android + "networkSecurityConfig"));
            var rules = XDocument.Load(Path.Combine(
                HeadRoot, "Resources", "xml", "native_network_security_config.xml"));
            Assert.Equal("false",
                (string?)rules.Descendants("base-config").Single()
                    .Attribute("cleartextTrafficPermitted"));
            var exception = Assert.Single(rules.Descendants("domain-config"));
            Assert.Equal("true",
                (string?)exception.Attribute("cleartextTrafficPermitted"));
            Assert.Equal("10.0.2.2",
                Assert.Single(exception.Elements("domain")).Value);
            var project = XDocument.Load(Path.Combine(
                HeadRoot, "SentenceStudio.Native.Android.csproj"));
            Assert.Contains(project.Descendants("AndroidResource"), resource =>
                (string?)resource.Attribute("Remove")
                    == "Resources/xml/native_network_security_config.xml"
                && (string?)resource.Attribute("Condition")
                    == "'$(Configuration)' == 'Release'");
        }
    }
}
