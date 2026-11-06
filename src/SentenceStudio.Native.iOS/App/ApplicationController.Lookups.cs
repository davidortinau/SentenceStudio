using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Data;
using SentenceStudio.Native.Features.Lookups;
using SentenceStudio.Native.iOS.Features.Lookups;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Services;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// Recent lookups (decision 4 B): pushed from the Vocabulary list onto the same navigation stack,
/// on the shared owner-gated <see cref="RecentLookupsCoordinator"/>.
/// </summary>
internal sealed partial class ApplicationController
{
    private RecentLookupsCoordinator? _recentLookups;

    internal bool IsRecentLookupsOpen => _recentLookups?.IsOpen == true;
    internal int RecentLookupsViewVersion => _recentLookups?.ViewVersion ?? -1;

    internal void OpenRecentLookups()
    {
        if (!IsVocabularyOpen || IsVocabularyWordOpen || IsRecentLookupsOpen
            || _vocabulary?.IsNavigationBlocked == true || _services is null)
            return;
        var provider = _services.Provider;
        var scope = provider.GetRequiredService<DeviceUserScopeProvider>();
        if (!scope.TryGetUserProfileId(out var owner) || string.IsNullOrWhiteSpace(owner))
        {
            _logger?.LogWarning("Recent lookups navigation refused an absent owner.");
            return;
        }
        _recentLookups ??= new RecentLookupsCoordinator(
            provider.GetRequiredService<VocabularyLookupRepository>(), scope,
            provider.GetRequiredService<NativePendingWrites>(),
            provider.GetRequiredService<ILogger<RecentLookupsCoordinator>>());
        _recentLookups.Changed -= Publish;
        _recentLookups.Changed += Publish;
        RunLibrary(async () => await _recentLookups.OpenAsync(owner, _lifetime.Token));
    }

    internal SwiftView RenderRecentLookups(SourceUiStyle style) =>
        _recentLookups is { IsOpen: true } lookups
            ? RecentLookupsView.Page(lookups.State, style, Text,
                new RecentLookupsActions(
                    Reload: () => RunLibrary(() => lookups.ReloadAsync(_lifetime.Token)),
                    Add: (lookupId, resourceId) =>
                        RunLibrary(() => lookups.AddAsync(lookupId, resourceId, _lifetime.Token)),
                    DismissNotice: lookups.DismissNotice))
            : Views.Text(Text("Common_Loading"));

    internal void OnRecentLookupsPopped()
    {
        _recentLookups?.Close();
        Publish();
    }

    private void ReleaseRecentLookups()
    {
        if (_recentLookups is null)
            return;
        _recentLookups.Changed -= Publish;
        _recentLookups.Dispose();
        _recentLookups = null;
    }
}
