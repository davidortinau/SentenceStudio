using System.Runtime.CompilerServices;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeSwiftUIPresentationContractTests
{
    [Fact]
    public void NormalEntry_UsesManagedServicesInsideSwiftUIOwnedAppAndWindowGroup()
    {
        var program = Source("Program.cs");
        var scene = Source("App/ApplicationScene.cs");
        var controller = Source("App/ApplicationController.cs");
        var manifest = Source("Info.plist");

        Assert.Contains("SwiftUIApplication.Run(scene.Root)", program);
        Assert.Contains(".OnAppear(app.Start)", scene);
        Assert.Contains("NativeServices.Create()", controller);
        Assert.Contains("await _auth.RestoreAsync(_lifetime.Token)", controller);
        Assert.Contains("await _dashboard.LoadAsync(_lifetime.Token)", controller);
        Assert.DoesNotContain("UIApplication.Main(", program);
        Assert.DoesNotContain("UIApplicationSceneManifest", manifest);
        Assert.DoesNotContain("Platform.Init(", program);
        Assert.DoesNotContain("SwiftUIHost.CreateController", scene);
    }

    [Fact]
    public void CSharpFeatureDeclarations_AreSwiftUIAndDoNotAuthorUIKitControls()
    {
        foreach (var file in new[] { "App", "Features", "Styling" }
            .SelectMany(folder => Directory.EnumerateFiles(
                Path.Combine(HeadPath, folder), "*.cs",
                SearchOption.AllDirectories))
            .Append(Path.Combine(HeadPath, "Program.cs"))
            .Append(Path.Combine(HeadPath, "Native/AilohaDebugShim.swift")))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("using UIKit;", source);
            Assert.DoesNotContain("import UIKit", source);
            Assert.DoesNotContain("UIHostingController", source);
            Assert.DoesNotContain("UIViewRepresentable", source);
        }
        Assert.Contains("Views.SecureField(", Source("Features/Authentication/SignInView.cs"));
        Assert.Contains("text(NativeText.RememberMe), rememberMe",
            Source("Features/Authentication/SignInView.cs"));
        Assert.Contains("Views.Scroll(content)",
            Source("Features/LearningResources/LearningResourceView.cs"));
        Assert.Contains("Views.LazyVGrid(",
            Source("Features/LearningResources/LearningResourcesView.cs"));
    }

    [Fact]
    public void NavigationIsSwiftUIOwnedAndBackCallsExistingPresentersOnce()
    {
        var scene = Source("App/ApplicationScene.cs");
        var quiz = Source("App/ApplicationController.Quiz.cs");
        var resources = Source("Features/LearningResources/LearningResourcesCoordinator.cs");

        Assert.Contains("Views.TabView(_app.SelectedTab", scene);
        Assert.Contains("Views.NavigationStack(_selectorSheet", scene);
        Assert.Contains("Views.NavigationStack(_resourcesSlot", scene);
        Assert.Contains("_dashboardStack.Push(\"quiz\", _quizSheet)", scene);
        Assert.Contains("_resourcesStack.Push(\"resource-form\"", scene);
        Assert.Contains("_conflictAlert.WithTabBarHidden()", scene);
        Assert.Contains("_app.OnQuizPopped()", scene);
        Assert.Contains("await _quiz.BackAsync(_lifetime.Token)", quiz);
        Assert.Contains("_app.OnResourcesFormPopped()", scene);
        Assert.Contains("_presenter.CancelEdit()", resources);
        Assert.DoesNotContain("UINavigationController", scene);
        Assert.DoesNotContain("UITabBarController", scene);
    }

    [Fact]
    public void ResourcesFullForm_UsesBoundPickersAndExistingOwnerScopedPresenter()
    {
        var views = Source("Features/LearningResources/LearningResourceView.cs");
        var fields = Source("Features/LearningResources/LearningResourceFieldsView.cs");
        var browser = Source("Features/LearningResources/LearningResourcesView.cs");
        var resources = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var scene = Source("App/ApplicationScene.cs");

        foreach (var field in new[]
        {
            "Title", "Description", "MediaType", "Language",
            "MediaUrl", "Tags", "Transcript", "Translation"
        })
            Assert.Contains($"nameof(LearningResourceViewState.{field})", views);
        Assert.Contains("Views.Picker(", fields);
        Assert.Contains("Views.Menu(menuLabel, choices)", browser);
        Assert.Contains("choice == 0 ? null : values[choice]", browser);
        Assert.Contains("_presenter.SaveAsync(_lifetime.Token)", resources);
        Assert.Contains("_presenter.LoadListAsync(_lifetime.Token)", resources);
        Assert.Contains("_presenter.OpenAsync(id, _lifetime.Token)", resources);
        Assert.Contains("Views.TextField(", fields);
        Assert.DoesNotContain("Views.Searchable(_resourcesSlot", scene);
        Assert.Contains("Views.FileImporter(_formSlot", scene);
        Assert.Contains("Views.ResourceDeleteConfirmation(_fileImporter",
            scene);
        Assert.Contains("Views.Alert(_deleteConfirmation,", scene);
        Assert.Contains("_presenter.ConfirmDeleteAsync(_lifetime.Token)", resources);
    }

    [Fact]
    public void NativeFileCancel_ClearsPresenterFlagBeforeAnyFormRefresh()
    {
        var scene = Source("App/ApplicationScene.cs");
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");

        Assert.Contains("Views.FileImporter(_formSlot,", scene);
        Assert.Contains("_app.OnResourceFileImportError,", scene);
        Assert.Contains("_app.OnFileImporterDismissed);", scene);
        Assert.Contains("_fileImporter.IsPresented = _app.IsFileImporterPresented;", scene);
        Assert.Contains("_fileImporterPresented = false;", coordinator);
        Assert.Contains("FileImporterChanged?.Invoke(false);", coordinator);
        Assert.Contains("if (_fileImporterPresented)\n            OnFileImporterDismissed();",
            coordinator);
    }

    [Fact]
    public void BusyNavigation_BindsBothNativeBackLocksToOwnerActions()
    {
        var scene = Source("App/ApplicationScene.cs");
        var app = Source("App/ApplicationController.cs");
        var resources = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var quiz = Source("App/ApplicationController.Quiz.cs");

        Assert.Contains("_dashboardStack.CanNavigateBack =\n"
            + "                !_app.NavigationBlocked || _app.CanNavigateBackDuringQuizAudio;",
            scene);
        Assert.Contains("_resourcesStack.CanNavigateBack = !_app.NavigationBlocked;",
            scene);
        Assert.Contains("internal bool CanNavigateBackDuringQuizAudio =>", quiz);
        Assert.Contains("internal bool NavigationBlocked => _busy", app);
        Assert.Contains("_resources?.IsNavigationBlocked == true", app);
        Assert.Contains("detail.Phase is NativeResourceDetailPhase.Saving", resources);
        Assert.Contains("or NativeResourceDetailPhase.Deleting", resources);
        Assert.Contains("detail.Reloading || detail.VocabularyBusy", resources);
        Assert.Contains("|| _presenter.List.StarterBusy", resources);
        Assert.Contains("|| _generating || _importing || _quickAdding", resources);
        Assert.Contains("IsQuickAddAttaching = _quickAdding || state.VocabularyBusy",
            resources);
        Assert.Contains("if (!_formOpen || IsNavigationBlocked)", resources);
        var close = resources[resources.IndexOf("private void CloseForm(",
                StringComparison.Ordinal)
            ..resources.IndexOf("private async Task SaveAsync(",
                StringComparison.Ordinal)];
        Assert.Contains("if (!_formOpen || IsNavigationBlocked)", close);
        Assert.Contains("if (_app.IsResourcesFormOpen)\n                        UpdateResourceForm();",
            scene);
        Assert.Contains("if (_handlingNativePop || !_nativePopPending)", quiz);
    }

    [Fact]
    public void ResourcesAsyncCompletions_RequireTheSameOpenFormAndOwner()
    {
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        Assert.Contains("Interlocked.Increment(ref _formGeneration);", coordinator);
        Assert.Contains("private bool IsCurrentForm(long generation, string? selectedId)",
            coordinator);
        Assert.Contains("_selectedId == selectedId", coordinator);
        Assert.Contains("_presenter.Detail.Phase != NativeResourceDetailPhase.Blocked",
            coordinator);
        foreach (var operation in new[]
        {
            "SaveAsync", "ReloadDraftAsync", "DeleteResourceAsync",
            "GenerateVocabularyAsync", "MutateVocabularyAsync",
            "ImportVocabularyTextAsync", "ImportVocabularyFileAsync"
        })
        {
            var start = coordinator.IndexOf($"private async Task {operation}(",
                StringComparison.Ordinal);
            Assert.True(start >= 0, $"Missing Resources operation {operation}");
            var next = coordinator.IndexOf("\n    private ", start + 1,
                StringComparison.Ordinal);
            var body = coordinator[start..(next < 0 ? coordinator.Length : next)];
            Assert.Contains("IsCurrentForm(formGeneration, selectedId)", body);
        }
    }

    [Fact]
    public void ResourcesSearch_KeepsContentFieldMountedThroughSearchAndReset()
    {
        var scene = Source("App/ApplicationScene.cs");
        var app = Source("App/ApplicationController.cs");
        var coordinator = Source("Features/LearningResources/LearningResourcesCoordinator.cs");
        var browser = Source("Features/LearningResources/LearningResourcesView.cs");

        Assert.Contains("_resources?.ListState.SearchText ?? string.Empty", app);
        Assert.Contains("Views.TextField(", browser);
        Assert.Contains("state.SearchText, actions.Search", browser);
        Assert.Contains("native.resources.search", browser);
        Assert.Contains("search = content.Children[0];", scene);
        Assert.Contains("content.Add(child);", scene);
        Assert.DoesNotContain("Views.Searchable(_resourcesSlot,", scene);
        Assert.Contains("var query = _app.ResourcesSearchText;", scene);
        Assert.Contains("if (!string.Equals(search.Text, query, StringComparison.Ordinal))",
            scene);
        Assert.Contains("search.Text = query;", scene);
        Assert.Contains("if (_disposed || string.Equals(_presenter.List.SearchText,",
            coordinator);
        Assert.Contains("Publish(formChanged: false);", coordinator);
        Assert.Contains("if (field is NativeResourceField.MediaType or NativeResourceField.Language)",
            coordinator);
    }

    [Fact]
    public void ResourcesControlsKeepTheirOwnAxIdsInsteadOfInheritingAScreenWrapperId()
    {
        var browser = Source("Features/LearningResources/LearningResourcesView.cs");
        var dashboard = Source("Features/Dashboard/DashboardView.cs");
        var scene = Source("App/ApplicationScene.cs");

        Assert.Contains("WithAccessibilityIdentifier(\"native.resources.add\")", browser);
        Assert.Contains("WithAccessibilityIdentifier(NativeAccess.ResourceType)", browser);
        Assert.DoesNotContain("WithAccessibilityIdentifier(\"native.resources.screen\")", browser);
        Assert.DoesNotContain(
            "WithAccessibilityIdentifier(NativeAccess.ResourcesTab)", scene);
        Assert.Contains("WithAccessibilityIdentifier(\"native.dashboard.mode.today\")",
            dashboard);
        Assert.Contains("WithAccessibilityIdentifier(\"native.dashboard.mode.chooseown\")",
            dashboard);
        Assert.DoesNotContain(
            "WithAccessibilityIdentifier(\"native.dashboard.mode\")", dashboard);
    }

    [Fact]
    public void ChooseOwnSheetPinsDoneAboveVirtualizedOptions()
    {
        var scene = Source("App/ApplicationScene.cs");
        var selector = Source("Features/Dashboard/ChooseOwnSelectorView.cs");
        Assert.Contains("Views.NavigationStack(_selectorSearch)", scene);
        Assert.Contains("return Views.Toolbar(content,", selector);
        Assert.Contains("WithAccessibilityIdentifier($\"{prefix}.done\")", selector);
        Assert.DoesNotContain(
            "items.Add(Views.Button(text(\"Common_Done\")", selector);
    }

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(HeadPath, relative));

    private static string HeadPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));

    private static string CurrentFile([CallerFilePath] string path = "") => path;
}
