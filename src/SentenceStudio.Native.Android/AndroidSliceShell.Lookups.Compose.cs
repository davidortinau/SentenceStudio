using System.Globalization;
using AndroidX.Compose;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Native.Features.Lookups;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Recent lookups (decision 4 B; iOS RecentLookupsView), opened from the Vocabulary screen header
/// and drawn over it like the word editor (Back returns to the list). Cards follow the Vocabulary
/// cards; each has an "Add to vocabulary" menu of the learner's resources in that language. State
/// and rules live in the shared <see cref="RecentLookupsCoordinator"/>.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private RecentLookupsCoordinator? _lookups;

    private bool IsRecentLookupsShown => _lookups is not null;

    private void OpenRecentLookups() => Publish(() =>
    {
        if (_more != MoreScreen.Vocabulary || _vocabulary is not { IsFormOpen: false }
            || _vocabulary.IsNavigationBlocked || _lookups is not null || _scope is null)
            return;
        var owner = MoreOwner();
        if (owner is null)
        {
            global::Android.Util.Log.Warn("SentenceStudio.Native",
                "Recent lookups refused an absent or untrusted owner.");
            return;
        }
        _moreMenu = null;
        var provider = _scope.ServiceProvider;
        var lookups = new RecentLookupsCoordinator(
            provider.GetRequiredService<VocabularyLookupRepository>(),
            provider.GetRequiredService<DeviceUserScopeProvider>(),
            provider.GetRequiredService<NativePendingWrites>(),
            provider.GetRequiredService<ILogger<RecentLookupsCoordinator>>());
        lookups.Changed += OnMoreChanged;
        _lookups = lookups;
        // Thread-safe coordinator (locked state): opened off the UI thread.
        RunMore(async () =>
        {
            if (!await lookups.OpenAsync(owner, _lifetime.Token))
                Publish(() =>
                {
                    if (ReferenceEquals(_lookups, lookups))
                        CloseRecentLookups();
                });
        });
    });

    /// <summary>Back from Recent lookups: the open menu first, then the page (not during an add).</summary>
    private bool HandleRecentLookupsBack()
    {
        if (_lookups is not { } lookups)
            return false;
        if (lookups.IsBusy)
            return true;
        Publish(CloseRecentLookups);
        return true;
    }

    private void CloseRecentLookups()
    {
        if (_lookups is not { } lookups)
            return;
        _lookups = null;
        _moreMenu = null;
        lookups.Changed -= OnMoreChanged;
        lookups.Dispose();
    }

    private ComposableNode RecentLookupsHeaderAction(AndroidVisualPalette palette, bool enabled)
    {
        var label = Localize("Lookups_Title");
        var button = new TextButton(OpenRecentLookups, enabled: enabled)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(RecentLookupsAccess.Open)
                .Semantics(semantics => semantics.ContentDescription(label)),
        };
        button.Add(new Text(label) { Color = palette.Primary });
        return button;
    }

    private ComposableNode RecentLookupsPage(AndroidVisualPalette palette)
    {
        var lookups = _lookups!;
        var state = lookups.State;
        var page = new Column
        {
            Modifier.FillMaxSize().TestTag(RecentLookupsAccess.Page),
            ScreenHeader(Localize("Lookups_Title"), palette, back: true,
                backAutomationId: "native.lookups.back"),
        };
        var rows = new List<VocabularyRow>();
        if (state.Notice is { } notice)
            rows.Add(new("notice", () => notice.IsError
                ? MessageBanner(notice, palette, RecentLookupsAccess.Notice)
                : StatusAlert(notice.Format(Localize), palette, success: true,
                    automationId: RecentLookupsAccess.Notice)));
        switch (state.Phase)
        {
            case RecentLookupsPhase.Blocked:
                rows.Add(new("status", () => StatusAlert(Localize("Vocabulary_NoActiveProfile"),
                    palette, error: true, automationId: RecentLookupsAccess.Status)));
                break;
            case RecentLookupsPhase.LoadError:
                rows.Add(new("status", () => StatusAlert(Localize("Lookups_LoadFailed"),
                    palette, error: true, automationId: RecentLookupsAccess.Status)));
                rows.Add(new("retry", () => Action(Localize("Common_Retry"), RecentLookupsAccess.Retry,
                    () => RunMore(() => lookups.ReloadAsync(_lifetime.Token)), true, primary: false)));
                break;
            case RecentLookupsPhase.Empty:
                rows.Add(new("status", () =>
                {
                    var message = Body(Localize("Lookups_Empty"), palette, secondary: true,
                        modifier: Modifier.FillMaxWidth().Padding(48).TestTag(RecentLookupsAccess.Status));
                    message.Align = TextAlign.Center;
                    return message;
                }));
                break;
            case RecentLookupsPhase.Ready:
                foreach (var row in state.Rows)
                {
                    var item = row;
                    rows.Add(new($"lookup.{item.Id}", () => RecentLookupCard(item, state.AddingId, lookups, palette)));
                }
                break;
            default:
                rows.Add(new("status", () => new Row(Arrangement.SpacedBy(10), Alignment.Vertical.CenterVertically)
                {
                    Modifier.FillMaxWidth().TestTag(RecentLookupsAccess.Status),
                    new CircularProgressIndicator { Modifier = Modifier.Size(24) },
                    Body(Localize("Common_Loading"), palette, secondary: true),
                }));
                break;
        }
        page.Add(new LazyColumn<VocabularyRow>(rows, row => row.Content())
        {
            Key = row => row.Key,
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 8)
                .TestTag(RecentLookupsAccess.List),
            VerticalArrangement = Arrangement.SpacedBy(12),
        });
        return page;
    }

    private ComposableNode RecentLookupCard(RecentLookupRow row, string? addingId,
        RecentLookupsCoordinator lookups, AndroidVisualPalette palette)
    {
        var source = RecentLookupsCoordinator.SourceLine(row, Localize,
            CultureInfo.CurrentUICulture, TimeZoneInfo.Local);
        var summary = new Column(Arrangement.SpacedBy(2))
        {
            Modifier.FillMaxWidth().TestTag($"{RecentLookupsAccess.Row(row)}.summary")
                .Semantics(semantics => semantics.ContentDescription(
                    $"{row.Text}, {row.Translation}, {source}")),
            Heading(row.Text, palette, 20, Modifier.TestTag($"{RecentLookupsAccess.Row(row)}.text")),
            Body(row.Translation, palette, secondary: true,
                modifier: Modifier.TestTag($"{RecentLookupsAccess.Row(row)}.translation")),
            Caption(source, palette, $"{RecentLookupsAccess.Row(row)}.source"),
        };
        var card = new Surface
        {
            Modifier = Modifier.FillMaxWidth().TestTag(RecentLookupsAccess.Row(row)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        card.Add(new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().Padding(AndroidVisualTheme.CardPadding),
            summary,
            RecentLookupAddMenu(row, addingId, lookups, palette),
        });
        return card;
    }

    private ComposableNode RecentLookupAddMenu(RecentLookupRow row, string? addingId,
        RecentLookupsCoordinator lookups, AndroidVisualPalette palette)
    {
        if (row.Destinations.Count == 0)
            return Caption(Localize("Lookups_NoResource"), palette, RecentLookupsAccess.Add(row));
        var key = $"lookups.add.{row.Id}";
        var label = string.Format(CultureInfo.CurrentCulture,
            Localize("Lookups_AddToVocabularyFor"), row.Text);
        var adding = addingId == row.Id;
        var button = new TextButton(() => ToggleMoreMenu(key), enabled: addingId is null)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(RecentLookupsAccess.Add(row))
                .Semantics(semantics => semantics.ContentDescription(label)),
        };
        button.Add(new Row(Arrangement.SpacedBy(6), Alignment.Vertical.CenterVertically)
        {
            adding
                ? (ComposableNode)new CircularProgressIndicator { Modifier = Modifier.Size(18) }
                : new Icon(Icons.Default.Add, label) { Modifier = Modifier.Size(20), Tint = palette.Primary },
            new Text(Localize("Lookups_AddToVocabulary")) { Color = palette.Primary },
        });
        var menu = PopupMenu(expanded: _moreMenu == key, onDismissRequest: CloseMoreMenu);
        menu.Add(new DropdownMenuItem(
            text: new Text(Localize("Lookups_ChooseResource")) { Color = palette.SecondaryText },
            onClick: CloseMoreMenu)
        {
            Modifier = Modifier.TestTag($"{RecentLookupsAccess.Add(row)}.heading"),
        });
        foreach (var destination in row.Destinations)
        {
            var target = destination;
            menu.Add(new DropdownMenuItem(text: new Text(target.Title), onClick: () =>
            {
                CloseMoreMenu();
                RunMore(() => lookups.AddAsync(row.Id, target.ResourceId, _lifetime.Token));
            })
            {
                Modifier = Modifier.TestTag(RecentLookupsAccess.Destination(row, target))
                    .Semantics(semantics => semantics.ContentDescription(target.Title)),
            });
        }
        return new Box { button, menu };
    }
}
