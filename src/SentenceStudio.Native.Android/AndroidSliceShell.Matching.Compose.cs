using System.Globalization;
using AndroidX.Compose;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose Vocabulary Matching (source VocabMatching.razor): New game in the
/// app bar, Show all / Hide native in the overflow menu, 2 tile columns up to
/// 768 dp wide and 4 above (source CSS).
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeMatchingPresenter? _matching;
    private bool _matchingMenuOpen;

    internal NativeMatchingState Matching { get; private set; } = new(NativePhase.Loading);

    private async Task OpenMatchingAsync(NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        if (_scope is null)
            return;
        var presenter = _scope.ServiceProvider.GetRequiredService<NativeMatchingPresenter>();
        presenter.StateChanged += OnMatchingChanged;
        _matching = presenter;
        Publish(() => Matching = new(NativePhase.Loading));
        var state = await presenter.OpenAsync(launch, cancellationToken);
        Publish(() => Matching = state);
    }

    private async Task ExitMatchingAsync(CancellationToken cancellationToken)
    {
        if (_matching is { } presenter)
            await presenter.ExitAsync(cancellationToken);
    }

    private void ReleaseMatching()
    {
        if (_matching is { } presenter)
        {
            presenter.StateChanged -= OnMatchingChanged;
            presenter.Dispose();
        }
        _matching = null;
        _matchingMenuOpen = false;
    }

    private void OnMatchingChanged(NativeMatchingState state) =>
        Publish(() => Matching = state);

    /// <summary>Tile taps bypass the shell's busy gate so the 600 ms check does not disable Back; the presenter serializes them.</summary>
    private void MatchingCommand(Func<NativeMatchingPresenter, CancellationToken, Task<NativeMatchingState>> command)
    {
        if (_matching is not { } presenter)
            return;
        _ = RunMatchingAsync(presenter, command);
    }

    private async Task RunMatchingAsync(NativeMatchingPresenter presenter,
        Func<NativeMatchingPresenter, CancellationToken, Task<NativeMatchingState>> command)
    {
        try
        {
            var state = await command(presenter, _lifetime.Token);
            Publish(() =>
            {
                if (ReferenceEquals(_matching, presenter))
                    Matching = state;
            });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowFailure(ex);
        }
    }

    private ComposableNode RenderMatching(AndroidVisualPalette palette)
    {
        var state = Matching;
        var viewport = new Column(Arrangement.SpacedBy(12))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_quizScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 16)
                .TestTag(NativeMatchingAccess.Screen),
        };
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                viewport.Add(new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(28).TestTag(NativeMatchingAccess.Loading)
                });
                viewport.Add(Body(Localize(NativeMatchingText.Loading), palette, secondary: true));
                break;
            case NativePhase.Summary:
                viewport.Add(MatchingComplete(state, palette));
                break;
            case NativePhase.Ready:
                if (state.ErrorKey is { } error)
                    viewport.Add(StatusAlert(Localize(error), palette, error: true,
                        automationId: NativeMatchingAccess.Error));
                viewport.Add(MatchingStatus(state, palette));
                viewport.Add(MatchingGrid(state, palette));
                break;
            default:
                var empty = state.Phase == NativePhase.Empty;
                viewport.Add(StatusAlert(
                    Localize(state.ErrorKey ?? NativeMatchingText.NoVocabulary), palette,
                    error: !empty,
                    automationId: empty ? NativeMatchingAccess.Empty : NativeMatchingAccess.Error));
                viewport.Add(Action(Localize(NativeMatchingText.GoBack),
                    NativeMatchingAccess.EmptyBack, ExitActivity, !Busy.Value, primary: false));
                break;
        }

        var header = (TopAppBar)ScreenHeader(Localize(NativeMatchingText.Title), palette,
            back: true, backAutomationId: NativeMatchingAccess.Back);
        if (state.Phase != NativePhase.Blocked)
            header.Actions = MatchingActions(state, palette);
        return new Column
        {
            Modifier.FillMaxSize().TestTag("native.matching.page"),
            header,
            viewport,
        };
    }

    private ComposableNode MatchingActions(NativeMatchingState state, AndroidVisualPalette palette)
    {
        var newGameLabel = Localize(NativeMatchingText.NewGame);
        var newGame = new IconButton(
            () => MatchingCommand((presenter, token) => presenter.RestartAsync(token)),
            enabled: !Busy.Value)
        {
            Modifier = Modifier.TestTag(NativeMatchingAccess.NewGame)
                .Semantics(semantics => semantics.ContentDescription(newGameLabel)),
        };
        newGame.Add(new Icon(Icons.Default.Refresh, newGameLabel)
        {
            Modifier = Modifier.Size(24),
            Tint = palette.Text,
        });
        var more = new IconButton(() => Publish(() => _matchingMenuOpen = true),
            enabled: !Busy.Value)
        {
            Modifier = Modifier.TestTag(NativeMatchingAccess.Menu)
                .Semantics(semantics => semantics.ContentDescription(Localize("More"))),
        };
        more.Add(new Icon(Icons.Default.MoreVert, Localize("More"))
        {
            Modifier = Modifier.Size(24),
            Tint = palette.Text,
        });
        var menu = new DropdownMenu(expanded: _matchingMenuOpen,
            onDismissRequest: () => Publish(() => _matchingMenuOpen = false));
        menu.Add(new DropdownMenuItem(
            text: new Text(Localize(state.HideNativeWords
                ? NativeMatchingText.ShowAll : NativeMatchingText.HideNative)),
            onClick: () =>
            {
                Publish(() => _matchingMenuOpen = false);
                MatchingCommand((presenter, token) => presenter.ToggleHideNativeAsync(token));
            })
        {
            Modifier = Modifier.TestTag(NativeMatchingAccess.HideToggle),
        });
        return new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
        {
            newGame,
            new Box { more, menu },
        };
    }

    private ComposableNode MatchingStatus(NativeMatchingState state, AndroidVisualPalette palette)
    {
        var matched = $"{state.MatchedPairs}/{state.TotalPairs}";
        var misses = state.Misses.ToString(CultureInfo.CurrentCulture);
        var matchedLabel = $"{Localize(NativeMatchingText.Matched)} {matched}";
        var missesLabel = $"{Localize(NativeMatchingText.Misses)} {misses}";
        return new Row(Arrangement.SpacedBy(20), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().TestTag(NativeMatchingAccess.Status),
            new Row(Arrangement.SpacedBy(6), Alignment.Vertical.CenterVertically)
            {
                Modifier.TestTag(NativeMatchingAccess.MatchedCount)
                    .Semantics(semantics => semantics.ContentDescription(matchedLabel)),
                new Icon(Icons.Default.CheckCircle, null) { Modifier = Modifier.Size(20), Tint = palette.Success },
                Body(matched, palette),
            },
            new Row(Arrangement.SpacedBy(6), Alignment.Vertical.CenterVertically)
            {
                Modifier.TestTag(NativeMatchingAccess.MissCount)
                    .Semantics(semantics => semantics.ContentDescription(missesLabel)),
                new Icon(Icons.Default.Close, null) { Modifier = Modifier.Size(20), Tint = palette.Danger },
                Body(misses, palette),
            },
        };
    }

    private ComposableNode MatchingGrid(NativeMatchingState state, AndroidVisualPalette palette)
    {
        var tiles = state.Tiles.IsDefault ? [] : state.Tiles;
        var columns = WindowWidthDp > 768 ? 4 : 2;
        var grid = new Column(Arrangement.SpacedBy(6))
        {
            Modifier.FillMaxWidth().TestTag(NativeMatchingAccess.Grid),
        };
        for (var start = 0; start < tiles.Length; start += columns)
        {
            var row = new Row(Arrangement.SpacedBy(6), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
            };
            for (var index = start; index < start + columns; index++)
                row.Add(index < tiles.Length
                    ? MatchingTile(tiles[index], state, palette)
                    : new Box { Modifier.Weight(1f).HeightIn(min: 56) });
            grid.Add(row);
        }
        return grid;
    }

    private ComposableNode MatchingTile(NativeMatchingTile tile, NativeMatchingState state,
        AndroidVisualPalette palette)
    {
        if (!tile.IsVisible && !tile.IsMatched)
            // Hidden native tile keeps its cell but exposes no text or semantics.
            return new Box { Modifier.Weight(1f).HeightIn(min: 56) };
        var label = tile.IsMatched
            ? $"{tile.Text}, {Localize(NativeMatchingText.Matched)}" : tile.Text;
        var border = tile.IsSelected ? palette.Primary
            : tile.IsTarget && !tile.IsMatched ? palette.Primary : palette.Border;
        var modifier = Modifier.Weight(1f).HeightIn(min: 56)
            .Alpha(tile.IsMatched ? 0.3f : 1f)
            .Border(2, border, AndroidVisualTheme.ControlRadius)
            .TestTag(tile.AccessibilityId);
        var selected = tile.IsSelected;
        modifier = !tile.IsMatched && !state.IsChecking
            ? modifier.Clickable(() => MatchingCommand(
                    (presenter, token) => presenter.TapAsync(tile.Id, token)))
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(label);
                    semantics.Role(SemanticsRole.Button);
                    semantics.Selected(selected);
                })
            : modifier.Semantics(semantics =>
            {
                semantics.ContentDescription(label);
                semantics.Selected(selected);
            });
        var surface = new Surface
        {
            Modifier = modifier,
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = tile.IsSelected ? palette.Primary : palette.Background,
        };
        // Surface is not a Box scope in the facade: center the label in a Box.
        surface.Add(new Box
        {
            Modifier.FillMaxWidth().HeightIn(min: 56),
            new Text(tile.Text)
            {
                Modifier = Modifier.Align(Alignment.Center).FillMaxWidth().Padding(8),
                Color = tile.IsSelected ? palette.OnPrimary
                    : tile.IsMatched ? palette.SecondaryText : palette.Text,
                FontSize = new Sp(16),
                FontWeight = tile.IsTarget ? FontWeight.SemiBold : FontWeight.Normal,
                Align = TextAlign.Center,
            },
        });
        return surface;
    }

    private ComposableNode MatchingComplete(NativeMatchingState state, AndroidVisualPalette palette)
    {
        ComposableNode Stat(int value, string key, Color color, string tag)
        {
            var label = $"{Localize(key)} {value}";
            return new Column(Arrangement.SpacedBy(2))
            {
                Modifier.TestTag(tag).Semantics(semantics => semantics.ContentDescription(label)),
                new Text(value.ToString(CultureInfo.CurrentCulture))
                {
                    Color = color, FontSize = new Sp(28), FontWeight = FontWeight.SemiBold,
                },
                Body(Localize(key), palette, secondary: true),
            };
        }
        return Card(palette, NativeMatchingAccess.Complete,
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                new Icon(Icons.Default.Star, null) { Modifier = Modifier.Size(28), Tint = palette.WarningText },
                Heading(Localize(NativeMatchingText.Congratulations), palette, 24,
                    Modifier.Semantics(semantics => semantics.Heading())),
            },
            Body(Localize(NativeMatchingText.AllPairsMatched), palette, secondary: true),
            new Row(Arrangement.SpacedBy(32))
            {
                Stat(state.MatchedPairs, NativeMatchingText.Matched, palette.Success,
                    NativeMatchingAccess.CompleteMatched),
                Stat(state.Misses, NativeMatchingText.Misses, palette.Danger,
                    NativeMatchingAccess.CompleteMisses),
            },
            Action(Localize(NativeMatchingText.PlayAgain), NativeMatchingAccess.PlayAgain,
                () => MatchingCommand((presenter, token) => presenter.RestartAsync(token)),
                !Busy.Value));
    }
}
