using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.iOS.Features;
using SentenceStudio.Native.Tests;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeResourceContractTests
{
    [Fact]
    public async Task TwoNativeTabs_ResourceJourneyCancelsAndSavesAgainstOwnerScopedStorage()
    {
        Assert.Equal([NativeDestination.Dashboard, NativeDestination.Resources],
            IosDashboardNavigation.TopLevelDestinations.ToArray());
        Assert.True(IosDashboardNavigation.UsesBottomTabs);
        Assert.DoesNotContain(NativeDestination.ResourceDetail,
            IosDashboardNavigation.TopLevelDestinations);
        Assert.DoesNotContain(NativeDestination.ResourceEdit,
            IosDashboardNavigation.TopLevelDestinations);

        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        var presenter = fixture.Resources;
        var list = await presenter.LoadListAsync();
        Assert.Equal(NativeResourceListPhase.Populated, list.Phase);
        Assert.Single(list.Items);
        Assert.Equal(NativeResourceListPhase.NoMatches,
            presenter.Search("not-an-owned-title").Phase);
        presenter.Search("synthetic");
        Assert.Single(presenter.Filter("Vocabulary List", "Korean").Items);

        var owned = fixture.ResourceId(NativeSliceFixture.UserA);
        var foreign = fixture.ResourceId(NativeSliceFixture.UserB);
        Assert.Equal(NativeResourceDetailPhase.NotFound,
            (await presenter.OpenAsync(foreign)).Phase);
        Assert.Equal(NativeResourceDetailPhase.Ready,
            (await presenter.OpenAsync(owned)).Phase);
        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Discarded");
        presenter.CancelEdit();
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal("Synthetic vocabulary",
                (await db.LearningResources.AsNoTracking()
                    .SingleAsync(row => row.Id == owned)).Title);
        }

        Assert.True(presenter.BeginEdit());
        presenter.ChangeDraft(NativeResourceField.Title, "Native Resources edit");
        presenter.ChangeDraft(NativeResourceField.Description, "Owner-scoped");
        presenter.ChangeDraft(NativeResourceField.Tags, "korean, native");
        Assert.True(await presenter.SaveAsync());
        Assert.Equal(NativeResourceSyncPhase.Pending, presenter.Detail.Sync);
        Assert.Equal("Native Resources edit", presenter.Detail.Resource!.Title);
        Assert.Equal(NativeResourceListPhase.NoMatches, presenter.List.Phase);
        Assert.Equal("Native Resources edit",
            Assert.Single(presenter.Search(null).Items).Title);
        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        var reopened = fixture.Resources;
        Assert.Equal(NativeResourceSyncPhase.Pending,
            (await reopened.LoadListAsync()).Sync);
        Assert.Equal("Native Resources edit", (await reopened.OpenAsync(owned))
            .Resource!.Title);
        Assert.Equal(NativeResourceDetailPhase.NotFound,
            (await reopened.OpenAsync(foreign)).Phase);
    }

    [Fact]
    public void ResourcesStatesControlsAndFeedbackHaveEnglishAndKoreanCopy()
    {
        var basePath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Shared/Resources/Strings"));
        var keys = new[]
        {
            "Resources_Title", "Resources_AllTypes", "Resources_AllLanguages",
            "Resources_SearchPlaceholder", "ResourceEdit_Title",
            "ResourceEdit_Description", "ResourceEdit_Tags",
            "ResourceEdit_AutoUpdated", "ResourceEdit_TitleRequired",
            "NativeResources_Empty", "NativeResources_NoMatches",
            "NativeResources_LoadError", "NativeResources_SaveError",
            "NativeResources_SaveRefused", "NativeResources_SavedReloadError",
            "NativeResources_Conflict", "NativeResources_ConflictTitle",
            "NativeResources_KeepEditing", "NativeResources_ReloadDiscard",
            "NativeResources_ReloadError", "NativeResources_SaveInvalid",
            "NativeResources_SyncUnconfirmed", "NativeResources_SyncConfirmed",
            "NativeResources_TypeFilter", "NativeResources_LanguageFilter",
            "NativeResources_Preview", "NativeResources_Unavailable",
            "NativeResources_NextField", "Common_Save", "Common_Cancel",
        };
        foreach (var locale in new[] { "AppResources.resx", "AppResources.ko.resx" })
        {
            var resources = XDocument.Load(Path.Combine(basePath, locale))
                .Descendants("data").ToArray();
            Assert.All(keys, key =>
            {
                var value = Assert.Single(resources.Where(element =>
                    element.Attribute("name")?.Value == key))
                    .Element("value")?.Value;
                Assert.False(string.IsNullOrWhiteSpace(value),
                    $"{locale} lacks copy for {key}");
            });
        }
    }

    [Theory]
    [InlineData("AppResources.resx", "All Types", "All Languages")]
    [InlineData("AppResources.ko.resx", "모든 유형", "모든 언어")]
    public void FilterDefaultsRemainCompleteInBothLanguages(
        string resourceFile, string allTypes, string allLanguages)
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Shared/Resources/Strings", resourceFile));
        var resources = XDocument.Load(path).Descendants("data").ToArray();
        string Value(string key) => Assert.Single(resources,
            item => item.Attribute("name")?.Value == key).Element("value")!.Value;

        Assert.Equal(allTypes, Value("Resources_AllTypes"));
        Assert.Equal(allLanguages, Value("Resources_AllLanguages"));
    }

    [Fact]
    public void FiltersUseIntrinsicSizingAndWrapFullSelectedValues()
    {
        var headPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));
        var view = File.ReadAllText(Path.Combine(
            headPath, "Platform", "NativeResourceViewControllers.cs"));
        var list = view[..view.IndexOf("internal sealed class NativeResourceDetailViewController",
            StringComparison.Ordinal)];

        Assert.Contains("Distribution = UIStackViewDistribution.FillProportionally", list);
        Assert.Contains(".IsAccessibilityCategory()", list);
        Assert.Contains("naturalWidth > availableWidth", list);
        Assert.Contains("UIButtonConfiguration.PlainButtonConfiguration", list);
        Assert.Contains("configuration.TitleLineBreakMode = UILineBreakMode.WordWrap;", list);
        Assert.Contains("configuration.CornerStyle = UIButtonConfigurationCornerStyle.Fixed;", list);
        Assert.Contains("configuration.Background.CornerRadius = NativeVisualTheme.ControlRadius;", list);
        Assert.Contains("button.SizeThatFits(", list);
        Assert.Contains("button.TitleLabel.Lines = 0;", list);
        Assert.Contains("button.TitleLabel.LineBreakMode = UILineBreakMode.WordWrap;", list);
        Assert.DoesNotContain("UIStackViewDistribution.FillEqually", list);
        Assert.DoesNotContain("UILineBreakMode.TailTruncation", list);
        Assert.DoesNotContain("button.ContentEdgeInsets", list);
        Assert.Contains("button.AccessibilityValue = current ?? all;", list);
        Assert.Contains("button.HeightAnchor.ConstraintGreaterThanOrEqualTo(", list);
    }

    [Fact]
    public void ListAndDetailUseTheSameLocalTimestampFormatter()
    {
        var headPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));
        var view = File.ReadAllText(Path.Combine(
            headPath, "Platform", "NativeResourceViewControllers.cs"));

        Assert.Contains("NativeResourceDateFormatter.ListDate(resource.CreatedAt, resource.IsSmartResource)", view);
        Assert.Matches(
            @"NativeResourceDateFormatter\.DetailCreatedDate\(\s*resource\.CreatedAt,\s*resource\.IsSmartResource\)",
            view);
        Assert.Contains("NativeResourceDateFormatter.DetailUpdatedDate(resource.UpdatedAt)", view);
    }

    [Fact]
    public void Conflict_UsesNativeAlertActionAndPersistentAccessibleReload()
    {
        var headPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));
        var coordinator = File.ReadAllText(Path.Combine(
            headPath, "Features", "IosResourcesCoordinator.cs"));
        var view = File.ReadAllText(Path.Combine(
            headPath, "Platform", "NativeResourceViewControllers.cs"));

        Assert.Equal("native.resources.edit.conflict", NativeAccess.ResourceConflict);
        Assert.Equal("native.resources.edit.conflict.alert",
            NativeAccess.ResourceConflictAlert);
        Assert.Equal("native.resources.edit.reload", NativeAccess.ResourceReload);
        Assert.Contains("UIAlertController.Create(text(\"NativeResources_ConflictTitle\")", view);
        Assert.Contains("UIAlertControllerStyle.Alert", view);
        Assert.Contains("alert.View.AccessibilityIdentifier = NativeAccess.ResourceConflictAlert", view);
        Assert.Contains("UIAlertAction.Create(text(\"NativeResources_ReloadDiscard\")", view);
        Assert.Contains("UIAlertAction.Create(text(\"NativeResources_KeepEditing\")", view);
        Assert.Contains("_reload.AccessibilityIdentifier = NativeAccess.ResourceReload", view);
        Assert.Contains("_reload.TouchUpInside += (_, _) => reload();", view);
        Assert.Contains("NavigationItem.HidesBackButton = busy;", view);
        Assert.Contains("editor.CommitComposingText(draft);", coordinator);
        Assert.Contains("_presenter.ReloadDraftAsync(_lifetime.Token)", coordinator);
        Assert.Contains("editor.SetDraft(_presenter.Detail);", coordinator);
        Assert.DoesNotContain("editor.CaptureDraft()", coordinator);
    }

    [Fact]
    public void SyncDefersDetailReloadUntilEditorClosesAndLoadErrorOffersRetry()
    {
        var headPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));
        var coordinator = File.ReadAllText(Path.Combine(
            headPath, "Features", "IosResourcesCoordinator.cs"));
        var list = File.ReadAllText(Path.Combine(
            headPath, "Platform", "NativeResourceViewControllers.cs"));
        Assert.Contains("if (_editor is not null)", coordinator);
        Assert.Contains("_detailReloadDeferred = true;", coordinator);
        Assert.Contains("Run(() => ReloadDetailAsync(id));", coordinator);
        Assert.Contains("native.resources.retry", list);
        Assert.Contains("_retry.TouchUpInside += (_, _) => onAppear();", list);
    }

    [Fact]
    public void FailedPendingSync_IsReadableInNativeListAndRetryCallsSync()
    {
        var headPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));
        var coordinator = File.ReadAllText(Path.Combine(
            headPath, "Features", "IosResourcesCoordinator.cs"));
        var view = File.ReadAllText(Path.Combine(
            headPath, "Platform", "NativeResourceViewControllers.cs"));

        Assert.Contains("_syncStatus.Text = NativeResourceSyncStatus.Describe(", view);
        Assert.Contains("_syncStatus.AccessibilityLabel = _syncStatus.Text;", view);
        Assert.Contains("UIAccessibility.PostNotification(UIAccessibilityPostNotification.Announcement,",
            view);
        Assert.Contains("new NSString(_syncStatus.Text)", view);
        Assert.Contains("? text(\"NativeResources_Sync\") : text(NativeText.Retry);", view);
        Assert.Contains("_sync.SetTitle(syncActionLabel, UIControlState.Normal);", view);
        Assert.Contains("_sync.TouchUpInside += (_, _) => synchronize();", view);
        Assert.Contains("_sync.Enabled = state.Phase is not", view);
        Assert.Contains("synchronize: () => Run(SynchronizeAsync)", coordinator);
        Assert.Contains("private readonly NativeResourceSyncAction _syncAction = new();",
            coordinator);
        Assert.Contains("_syncAction.RunAsync(SynchronizeAndRefreshAsync)", coordinator);
        Assert.Contains(
            "var synchronization = _presenter.SynchronizeAsync(_lifetime.Token);",
            coordinator);
        Assert.Contains("await synchronization;", coordinator);
        Assert.Contains(
            "_presenter.RefreshAfterSyncAsync(synchronization, _lifetime.Token)",
            coordinator);
        Assert.Contains("_presenter.IsLatestSynchronization(synchronization)",
            coordinator);

        var stringsPath = Path.GetFullPath(Path.Combine(
            headPath, "../SentenceStudio.Shared/Resources/Strings"));
        foreach (var locale in new[] { "AppResources.resx", "AppResources.ko.resx" })
        {
            var strings = XDocument.Load(Path.Combine(stringsPath, locale))
                .Descendants("data").ToArray();
            string Text(string key) => Assert.Single(strings,
                item => item.Attribute("name")?.Value == key)
                .Element("value")!.Value;
            var pending = $"{Text("DebugHealth_CoreSync")}: {Text("DebugHealth_Pending")}";
            var error = Text("NativeResources_SyncUnconfirmed");

            Assert.Equal($"{pending}\n{error}",
                NativeResourceSyncStatus.Describe(NativeResourceSyncPhase.Pending,
                    "NativeResources_SyncUnconfirmed", Text));
            Assert.Contains(Text(NativeText.Retry), error,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(pending, NativeResourceSyncStatus.Describe(
                NativeResourceSyncPhase.Pending, null, Text));
            Assert.Equal(Text("NativeResources_SyncConfirmed"),
                NativeResourceSyncStatus.Describe(
                    NativeResourceSyncPhase.Confirmed, null, Text));
            Assert.Equal(error, NativeResourceSyncStatus.Describe(
                NativeResourceSyncPhase.Unconfirmed,
                "NativeResources_SyncUnconfirmed", Text));
            Assert.Equal(error, NativeResourceSyncStatus.Describe(
                NativeResourceSyncPhase.Confirmed,
                "NativeResources_SyncUnconfirmed", Text));
        }
    }

    [Fact]
    public void NativePopAndDispose_InvalidateOutstandingDetailAndStaleRetry()
    {
        var headPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!, "../../src/SentenceStudio.Native.iOS"));
        var coordinator = File.ReadAllText(Path.Combine(
            headPath, "Features", "IosResourcesCoordinator.cs"));
        var pop = coordinator.IndexOf(
            "if (_detail is { } detail && !Navigation.ViewControllers.Contains(detail))",
            StringComparison.Ordinal);
        var dispose = coordinator.IndexOf("public void Dispose()", StringComparison.Ordinal);

        Assert.True(pop >= 0 && dispose > pop);
        Assert.Contains("_presenter.InvalidateDetail();", coordinator[pop..dispose]);
        Assert.Contains("_presenter.InvalidateSync();", coordinator[dispose..]);
        Assert.Contains("_presenter.InvalidateDetail();", coordinator[dispose..]);
        Assert.Contains("if (_disposed || _selectedId != id || _detail is not { } detail)",
            coordinator);
    }

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
