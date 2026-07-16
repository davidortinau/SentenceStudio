using System.Reflection;
using FluentAssertions;

namespace SentenceStudio.UnitTests.Services;

public sealed class VocabQuizUiBoundaryContractTests
{
    [Fact]
    public void DirectRouteValidation_PrecedesSessionLookupAndVocabularyLoad()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "SentenceStudio.UI",
            "Pages",
            "VocabQuiz.razor"));

        var onInitializedStart = source.IndexOf(
            "protected override async Task OnInitializedAsync()",
            StringComparison.Ordinal);
        var loadVocabularyStart = source.IndexOf(
            "private async Task LoadVocabulary()",
            onInitializedStart,
            StringComparison.Ordinal);
        var onInitialized = source[onInitializedStart..loadVocabularyStart];

        var validationIndex = onInitialized.IndexOf(
            "LaunchValidator.ValidateRouteAsync",
            StringComparison.Ordinal);
        var sessionIndex = onInitialized.IndexOf(
            "SessionService.GetResumableAsync",
            StringComparison.Ordinal);
        var vocabularyIndex = onInitialized.IndexOf(
            "await LoadVocabulary()",
            StringComparison.Ordinal);

        validationIndex.Should().BeGreaterThanOrEqualTo(0);
        sessionIndex.Should().BeGreaterThan(validationIndex);
        vocabularyIndex.Should().BeGreaterThan(validationIndex);
        onInitialized.Should().NotContain(
            "ActivityTimer.StartSession",
            "the timer must not start before route ownership validation");
    }

    [Fact]
    public void ChooseOwnNavigation_ValidatesBeforeBuildingUrl()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "SentenceStudio.UI",
            "Pages",
            "Index.razor"));

        var methodStart = source.IndexOf(
            "private async Task NavigateToActivity(string route)",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private string ResolveActiveProfileId()",
            methodStart,
            StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        var reconciliationIndex = method.IndexOf(
            "ChooseOwnSelectionPreferences.Reconcile",
            StringComparison.Ordinal);
        var refusalIndex = method.IndexOf(
            "if (reconciliation.RejectedCount > 0)",
            StringComparison.Ordinal);
        var urlIndex = method.IndexOf(
            "QueryHelpers.AddQueryString",
            StringComparison.Ordinal);

        reconciliationIndex.Should().BeGreaterThanOrEqualTo(0);
        refusalIndex.Should().BeGreaterThan(reconciliationIndex);
        urlIndex.Should().BeGreaterThan(refusalIndex);
    }

    [Fact]
    public void ChooseOwnUiCallbacks_RefuseUnownedValuesBeforeSaving()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "SentenceStudio.UI",
            "Pages",
            "Index.razor"));

        var methodStart = source.IndexOf(
            "private async Task ApplyChooseOwnSelectionFromUiAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "// ---- Shared ----",
            methodStart,
            StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        var reconciliationIndex = method.IndexOf(
            "ChooseOwnSelectionPreferences.Reconcile",
            StringComparison.Ordinal);
        var refusalIndex = method.IndexOf(
            "if (reconciliation.RejectedCount > 0)",
            StringComparison.Ordinal);
        var clearIndex = method.IndexOf(
            "chooseOwnSelection.Replace(new ChooseOwnSelection([], null))",
            StringComparison.Ordinal);
        var saveIndex = method.IndexOf(
            "SaveChooseOwnSelection(activeProfileId)",
            StringComparison.Ordinal);

        reconciliationIndex.Should().BeGreaterThanOrEqualTo(0);
        refusalIndex.Should().BeGreaterThan(reconciliationIndex);
        clearIndex.Should().BeGreaterThan(refusalIndex);
        saveIndex.Should().BeGreaterThan(clearIndex);
    }

    [Fact]
    public void ResumeSnapshot_IsValidatedBeforeOfferingResume()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "SentenceStudio.UI",
            "Pages",
            "VocabQuiz.razor"));

        var onInitializedStart = source.IndexOf(
            "protected override async Task OnInitializedAsync()",
            StringComparison.Ordinal);
        var loadVocabularyStart = source.IndexOf(
            "private async Task LoadVocabulary()",
            onInitializedStart,
            StringComparison.Ordinal);
        var onInitialized = source[onInitializedStart..loadVocabularyStart];

        var sessionIndex = onInitialized.IndexOf(
            "SessionService.GetResumableAsync",
            StringComparison.Ordinal);
        var reachableWordsIndex = onInitialized.IndexOf(
            "LoadVocabularyWordsForCurrentLaunchAsync",
            sessionIndex,
            StringComparison.Ordinal);
        var validationIndex = onInitialized.IndexOf(
            "CountRejectedSnapshotReferences",
            sessionIndex,
            StringComparison.Ordinal);
        var promptIndex = onInitialized.IndexOf(
            "showResumePrompt = true",
            validationIndex,
            StringComparison.Ordinal);

        reachableWordsIndex.Should().BeGreaterThan(sessionIndex);
        validationIndex.Should().BeGreaterThan(reachableWordsIndex);
        promptIndex.Should().BeGreaterThan(validationIndex);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                ?? throw new InvalidOperationException("Test assembly location has no directory."));
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))
                && Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
