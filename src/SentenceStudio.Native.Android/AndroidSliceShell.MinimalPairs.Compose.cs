using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Text.Input;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose Minimal Pairs (source MinimalPairs.razor, MinimalPairCreate.razor,
/// MinimalPairSession.razor). One activity destination with three in-activity
/// pages: the pair list (Back leaves to the tab it opened from, delete asks in a Compose
/// AlertDialog), the create page and the listening session (Back, the header
/// arrow or system Back, returns to the list). Search and contrast fields bind
/// Compose TextFieldValue state, so typing (including Korean IME composition)
/// never goes through a presenter publish; the rows recompose from the state.
/// Session tiles show and announce only the target term until Check Answer.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeMinimalPairsPresenter? _minimalPairs;
    private bool _minimalPairsCreateLoaded;
    /// <summary>Library quick add: the create page opens above the list once the list is read.</summary>
    private bool _minimalPairsOpenCreate;

    internal NativeMinimalPairsState MinimalPairs { get; private set; } = new(NativePhase.Loading);

    internal MutableState<TextFieldValue> MinimalPairsSearchA { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));

    internal MutableState<TextFieldValue> MinimalPairsSearchB { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));

    internal MutableState<TextFieldValue> MinimalPairsContrast { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));

    private async Task OpenMinimalPairsAsync(NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        if (ActivityServices is not { } services)
            return;
        var presenter = services.GetRequiredService<NativeMinimalPairsPresenter>();
        presenter.StateChanged += OnMinimalPairsChanged;
        _minimalPairs = presenter;
        Publish(() => MinimalPairs = new(NativePhase.Loading));
        var state = await presenter.OpenAsync(launch, cancellationToken);
        Publish(() =>
        {
            ApplyMinimalPairs(state);
            if (!_minimalPairsOpenCreate || !ReferenceEquals(_minimalPairs, presenter))
                return;
            _minimalPairsOpenCreate = false;
            if (state is { Phase: NativePhase.Ready, Page: NativeMinimalPairsPage.List })
                MinimalPairsCommand((pairs, token) => pairs.OpenCreateAsync(token));
        });
    }

    private async Task ExitMinimalPairsAsync(CancellationToken cancellationToken)
    {
        if (_minimalPairs is { } presenter)
            await presenter.ExitAsync(cancellationToken);
    }

    private void ReleaseMinimalPairs()
    {
        if (_minimalPairs is { } presenter)
        {
            presenter.StateChanged -= OnMinimalPairsChanged;
            presenter.Dispose();
        }
        _minimalPairs = null;
        _minimalPairsCreateLoaded = false;
        _minimalPairsOpenCreate = false;
    }

    /// <summary>Create/session Back returns to the list; on the list, Back leaves the activity.</summary>
    private bool MinimalPairsBack()
    {
        if (_minimalPairs is null || MinimalPairs.Destination != NativeDestination.Activity
            || MinimalPairs.Phase != NativePhase.Ready || MinimalPairs.Page == NativeMinimalPairsPage.List)
            return false;
        MinimalPairsCommand((presenter, token) => presenter.BackToListAsync(token));
        return true;
    }

    private void OnMinimalPairsChanged(NativeMinimalPairsState state) => Publish(() => ApplyMinimalPairs(state));

    /// <summary>The create fields are cleared once per visit; later publishes never overwrite typing.</summary>
    private void ApplyMinimalPairs(NativeMinimalPairsState state)
    {
        MinimalPairs = state;
        if (state.Page != NativeMinimalPairsPage.Create)
        {
            _minimalPairsCreateLoaded = false;
            return;
        }
        if (!_minimalPairsCreateLoaded)
        {
            _minimalPairsCreateLoaded = true;
            MinimalPairsSearchA.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
            MinimalPairsSearchB.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
            MinimalPairsContrast.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
        }
    }

    /// <summary>Commands bypass the shell's busy gate so speech never disables Back; the presenter serializes them.</summary>
    private void MinimalPairsCommand(
        Func<NativeMinimalPairsPresenter, CancellationToken, Task<NativeMinimalPairsState>> command)
    {
        if (_minimalPairs is not { } presenter)
            return;
        if (MinimalPairs.Page == NativeMinimalPairsPage.Create)
            presenter.SetContrastLabel(MinimalPairsContrast.Value.Text);
        _ = RunMinimalPairsAsync(presenter, command);
    }

    private async Task RunMinimalPairsAsync(NativeMinimalPairsPresenter presenter,
        Func<NativeMinimalPairsPresenter, CancellationToken, Task<NativeMinimalPairsState>> command)
    {
        try
        {
            var state = await command(presenter, _lifetime.Token);
            Publish(() =>
            {
                if (ReferenceEquals(_minimalPairs, presenter))
                    ApplyMinimalPairs(state);
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

    private ComposableNode RenderMinimalPairs(AndroidVisualPalette palette)
    {
        var state = MinimalPairs;
        var title = Localize(state.Page == NativeMinimalPairsPage.Create
            ? NativeMinimalPairsText.CreateTitle : NativeMinimalPairsText.Title);
        var page = new Column
        {
            Modifier.FillMaxSize().TestTag(NativeMinimalPairsAccess.Screen),
            ScreenHeader(title, palette, back: true, backAutomationId: NativeMinimalPairsAccess.Back),
        };
        if (state.Phase is NativePhase.Blocked or NativePhase.Error)
        {
            page.Add(MinimalPairsScroll(palette,
                StatusAlert(Localize(state.ErrorKey ?? NativeMinimalPairsText.Unavailable), palette, error: true,
                    automationId: NativeMinimalPairsAccess.Error),
                Action(Localize(NativeMinimalPairsText.GoBack), NativeMinimalPairsAccess.GoBack, ExitActivity, true,
                    primary: false)));
            return page;
        }
        if (state.Phase != NativePhase.Ready || state.List is null)
        {
            page.Add(MinimalPairsScroll(palette, MinimalPairsSpinner()));
            return page;
        }
        switch (state.Page)
        {
            case NativeMinimalPairsPage.Create when state.Create is { } create:
                page.Add(MinimalPairsCreate(state, create, palette));
                break;
            case NativeMinimalPairsPage.Session when state.Session is { } session:
                page.Add(MinimalPairsSession(state, session, palette));
                break;
            default:
                page.Add(MinimalPairsList(state, state.List, palette));
                if (state.List.PendingDelete is { } pending && !state.List.IsDeleting)
                    page.Add(MinimalPairsDeleteDialog(pending));
                break;
        }
        return page;
    }

    private ComposableNode MinimalPairsScroll(AndroidVisualPalette palette, params ComposableNode[] children)
    {
        var viewport = new Column(Arrangement.SpacedBy(12))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_quizScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 16),
        };
        foreach (var child in children)
            viewport.Add(child);
        return viewport;
    }

    private static ComposableNode MinimalPairsSpinner() => new CircularProgressIndicator
    {
        Modifier = Modifier.Size(28).TestTag(NativeMinimalPairsAccess.Loading)
    };

    // ---- List ----

    private ComposableNode MinimalPairsList(NativeMinimalPairsState state, NativeMinimalPairsListState list,
        AndroidVisualPalette palette)
    {
        var items = new List<ComposableNode>();
        if (!state.Notices.IsDefaultOrEmpty)
            if (MinimalPairsNotices(state, palette) is { } notices)
                items.Add(notices);
        items.Add(Action(Localize(NativeMinimalPairsText.CreatePair), NativeMinimalPairsAccess.CreatePair,
            () => MinimalPairsCommand((presenter, token) => presenter.OpenCreateAsync(token)),
            !list.IsLoading && !list.IsDeleting));

        var modes = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            new Text(Localize(NativeMinimalPairsText.Mode))
            {
                Modifier = Modifier.TestTag(NativeMinimalPairsAccess.ModeLabel),
                Color = palette.Text, FontSize = new Sp(16), FontWeight = FontWeight.Bold,
            },
            MinimalPairsModeButton(NativeMinimalPairsText.Focus, NativeMinimalPairsMode.Focus,
                NativeMinimalPairsAccess.ModeFocus, list),
            MinimalPairsModeButton(NativeMinimalPairsText.Mixed, NativeMinimalPairsMode.Mixed,
                NativeMinimalPairsAccess.ModeMixed, list),
        };
        items.Add(modes);
        if (list.ShowsStart)
        {
            var label = list.Mode == NativeMinimalPairsMode.Mixed
                ? string.Format(CultureInfo.CurrentCulture, Localize(NativeMinimalPairsText.StartSessionCount),
                    list.Pairs.Length)
                : Localize(NativeMinimalPairsText.StartSession);
            items.Add(Action(label, NativeMinimalPairsAccess.StartSession,
                () => MinimalPairsCommand((presenter, token) => presenter.StartSessionAsync(token)), list.CanStart));
        }

        if (list.IsLoading)
            items.Add(MinimalPairsSpinner());
        else if (list.IsEmpty)
        {
            items.Add(Body(Localize(NativeMinimalPairsText.NoPairs), palette, secondary: true,
                modifier: Modifier.TestTag(NativeMinimalPairsAccess.Empty)));
            items.Add(Action(Localize(NativeMinimalPairsText.CreateFirstPair), NativeMinimalPairsAccess.CreateFirstPair,
                () => MinimalPairsCommand((presenter, token) => presenter.OpenCreateAsync(token)), true));
        }
        else
        {
            if (list.IsDeleting)
                items.Add(new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(20).TestTag(NativeMinimalPairsAccess.Deleting)
                });
            var cards = new Column(Arrangement.SpacedBy(8))
            {
                Modifier = Modifier.FillMaxWidth().TestTag(NativeMinimalPairsAccess.Pairs),
            };
            foreach (var pair in list.Pairs)
                cards.Add(MinimalPairCard(pair, list, palette));
            items.Add(cards);
        }
        return MinimalPairsScroll(palette, [.. items]);
    }

    private ComposableNode MinimalPairsModeButton(string key, NativeMinimalPairsMode mode, string id,
        NativeMinimalPairsListState list)
    {
        var label = Localize(key);
        var selected = list.Mode == mode;
        Action select = () => MinimalPairsCommand((presenter, token) => presenter.SetModeAsync(mode, token));
        var modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight).TestTag(id)
            .Semantics(semantics =>
            {
                semantics.ContentDescription(label);
                semantics.Selected(selected);
            });
        if (selected)
        {
            var button = new AndroidX.Compose.Button(onClick: select, enabled: true)
            {
                Modifier = modifier, Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            button.Add(new Text(label));
            return button;
        }
        var outlined = new OutlinedButton(onClick: select, enabled: true)
        {
            Modifier = modifier, Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        outlined.Add(new Text(label));
        return outlined;
    }

    private ComposableNode MinimalPairCard(NativeMinimalPairItem pair, NativeMinimalPairsListState list,
        AndroidVisualPalette palette)
    {
        var vs = Localize(NativeMinimalPairsText.Vs);
        var highlighted = list.IsHighlighted(pair.Id);
        var label = string.IsNullOrEmpty(pair.ContrastLabel)
            ? $"{pair.WordA} {vs} {pair.WordB}" : $"{pair.WordA} {vs} {pair.WordB}. {pair.ContrastLabel}";
        var details = new Column(Arrangement.SpacedBy(4))
        {
            Modifier.Weight(1f).TestTag(pair.AccessibilityId)
                .Clickable(() => MinimalPairsCommand((presenter, token) => presenter.SelectPairAsync(pair.Id, token)))
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(label);
                    semantics.Role(SemanticsRole.Button);
                    semantics.Selected(highlighted);
                }),
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                new Text(pair.WordA) { Color = palette.Text, FontSize = new Sp(20), FontWeight = FontWeight.SemiBold },
                new Text(vs) { Color = palette.SecondaryText, FontSize = new Sp(13) },
                new Text(pair.WordB) { Color = palette.Text, FontSize = new Sp(20), FontWeight = FontWeight.SemiBold },
            },
        };
        if (!string.IsNullOrEmpty(pair.ContrastLabel))
            details.Add(new Text(pair.ContrastLabel) { Color = palette.SecondaryText, FontSize = new Sp(13) });
        var deleteLabel = $"{Localize(NativeMinimalPairsText.Delete)} {label}";
        var delete = new IconButton(
            () => MinimalPairsCommand((presenter, token) => presenter.RequestDeleteAsync(pair.Id, token)),
            enabled: !list.IsDeleting)
        {
            Modifier = Modifier.TestTag(NativeMinimalPairsAccess.PairDelete(pair.Id))
                .Border(1, palette.Danger, AndroidVisualTheme.ControlRadius)
                .Semantics(semantics => semantics.ContentDescription(deleteLabel)),
        };
        delete.Add(new Icon(Icons.Default.Delete, deleteLabel) { Modifier = Modifier.Size(22), Tint = palette.Danger });
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth()
                .Border(highlighted ? 2 : 1, highlighted ? palette.Primary : palette.Border, AndroidVisualTheme.CardRadius),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        surface.Add(new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().Padding(AndroidVisualTheme.CardPadding),
            details,
            delete,
        });
        return surface;
    }

    private ComposableNode MinimalPairsDeleteDialog(NativeMinimalPairItem pending)
    {
        var confirm = new TextButton(
            () => MinimalPairsCommand((presenter, token) => presenter.ConfirmDeleteAsync(token)), enabled: true)
        {
            Modifier = Modifier.TestTag(NativeMinimalPairsAccess.DeleteConfirm),
        };
        confirm.Add(new Text(Localize(NativeMinimalPairsText.Delete)));
        var cancel = new TextButton(
            () => MinimalPairsCommand((presenter, token) => presenter.CancelDeleteAsync(token)), enabled: true)
        {
            Modifier = Modifier.TestTag(NativeMinimalPairsAccess.DeleteCancel),
        };
        cancel.Add(new Text(Localize(NativeMinimalPairsText.Cancel)));
        return new AndroidX.Compose.AlertDialog(() =>
            MinimalPairsCommand((presenter, token) => presenter.CancelDeleteAsync(token)))
        {
            Modifier = Modifier.TestTag(NativeMinimalPairsAccess.DeleteDialog),
            Title = new Text(Localize(NativeMinimalPairsText.ConfirmDelete)),
            Text = new Text(string.Format(CultureInfo.CurrentCulture,
                Localize(NativeMinimalPairsText.DeleteMessage), pending.WordA, pending.WordB)),
            ConfirmButton = confirm,
            DismissButton = cancel,
        };
    }

    // ---- Create ----

    private ComposableNode MinimalPairsCreate(NativeMinimalPairsState state, NativeMinimalPairsCreateState create,
        AndroidVisualPalette palette)
    {
        var items = new List<ComposableNode>
        {
            Action(Localize(NativeMinimalPairsText.Create), NativeMinimalPairsAccess.CreateSubmit,
                () => MinimalPairsCommand((presenter, token) => presenter.CreatePairAsync(token)), create.CanCreate),
        };
        if (create.IsLoading)
        {
            items.Add(MinimalPairsSpinner());
            return MinimalPairsScroll(palette, [.. items]);
        }
        if (!state.Notices.IsDefaultOrEmpty)
            if (MinimalPairsNotices(state, palette) is { } notices)
                items.Add(notices);
        if (create.ErrorKey is { } error)
            items.Add(StatusAlert(Localize(error), palette, error: true,
                automationId: NativeMinimalPairsAccess.CreateError));
        // Reading the search states here recomposes the rows on each keystroke.
        var current = _minimalPairs?.SetSearch('A', MinimalPairsSearchA.Value.Text) ?? create;
        current = _minimalPairs?.SetSearch('B', MinimalPairsSearchB.Value.Text) ?? current;
        items.Add(MinimalPairsWordCard('A', current, palette));
        items.Add(MinimalPairsWordCard('B', current, palette));
        var contrastTitle = Localize(NativeMinimalPairsText.ContrastLabel);
        items.Add(Card(palette, null,
            Heading(contrastTitle, palette, 20, Modifier.Semantics(semantics => semantics.Heading())),
            new OutlinedTextField(MinimalPairsContrast, enabled: !create.IsSaving, singleLine: true)
            {
                Modifier = Modifier.FillMaxWidth().TestTag(NativeMinimalPairsAccess.ContrastLabel)
                    .Semantics(semantics => semantics.ContentDescription(contrastTitle)),
                Placeholder = new Text(NativeMinimalPairsText.ContrastPlaceholder),
            }));
        return MinimalPairsScroll(palette, [.. items]);
    }

    private ComposableNode MinimalPairsWordCard(char slot, NativeMinimalPairsCreateState create,
        AndroidVisualPalette palette)
    {
        var isA = slot == 'A';
        var title = Localize(isA ? NativeMinimalPairsText.SelectFirstWord : NativeMinimalPairsText.SelectSecondWord);
        var search = Localize(NativeMinimalPairsText.SearchVocabulary);
        var children = new List<ComposableNode>
        {
            Heading(title, palette, 20, Modifier.Semantics(semantics => semantics.Heading())),
            new OutlinedTextField(isA ? MinimalPairsSearchA : MinimalPairsSearchB, enabled: !create.IsSaving,
                singleLine: true)
            {
                Modifier = Modifier.FillMaxWidth()
                    .TestTag(isA ? NativeMinimalPairsAccess.SearchA : NativeMinimalPairsAccess.SearchB)
                    .Semantics(semantics => semantics.ContentDescription($"{title}. {search}")),
                Placeholder = new Text(search),
            },
        };
        var filtered = isA ? create.FilteredA : create.FilteredB;
        var selectedId = isA ? create.WordAId : create.WordBId;
        var rows = new Column(Arrangement.SpacedBy(4))
        {
            Modifier = Modifier.FillMaxWidth()
                .TestTag(isA ? NativeMinimalPairsAccess.WordsA : NativeMinimalPairsAccess.WordsB),
        };
        foreach (var word in filtered.Take(NativeMinimalPairsCreateState.MaxRows))
        {
            var selected = word.Id == selectedId;
            var rowLabel = $"{word.Target}, {word.Native}";
            var row = new Surface
            {
                Modifier = Modifier.FillMaxWidth()
                    .TestTag(NativeMinimalPairsAccess.WordOption(slot, word.Id))
                    .Clickable(() => MinimalPairsCommand(
                        (presenter, token) => presenter.SelectWordAsync(slot, word.Id, token)))
                    .Semantics(semantics =>
                    {
                        semantics.ContentDescription(rowLabel);
                        semantics.Role(SemanticsRole.Button);
                        semantics.Selected(selected);
                    }),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
                Color = selected ? palette.Primary : palette.Elevated,
            };
            row.Add(new Text($"{word.Target} — {word.Native}")
            {
                Modifier = Modifier.Padding(10),
                Color = selected ? palette.OnPrimary : palette.Text,
                FontSize = new Sp(15),
                MaxLines = 1,
            });
            rows.Add(row);
        }
        children.Add(rows);
        if (filtered.Length > NativeMinimalPairsCreateState.MaxRows)
            children.Add(Body(string.Format(CultureInfo.CurrentCulture, Localize(NativeMinimalPairsText.RefineSearch),
                    NativeMinimalPairsCreateState.MaxRows, filtered.Length), palette, secondary: true,
                modifier: Modifier.TestTag(NativeMinimalPairsAccess.RefineSearch)));
        if ((isA ? create.WordA : create.WordB) is { } chosen)
        {
            var badge = new Surface
            {
                Modifier = Modifier.TestTag(isA ? NativeMinimalPairsAccess.SelectedA : NativeMinimalPairsAccess.SelectedB),
                Shape = Shape.RoundedCorners(6),
                Color = palette.Primary,
            };
            badge.Add(new Text(chosen.Target)
            {
                Modifier = Modifier.Padding(horizontal: 8, vertical: 2),
                Color = palette.OnPrimary, FontSize = new Sp(13), FontWeight = FontWeight.Bold,
            });
            children.Add(badge);
        }
        return Card(palette, null, [.. children]);
    }

    // ---- Session ----

    private ComposableNode MinimalPairsSession(NativeMinimalPairsState state, NativeMinimalPairsSessionState session,
        AndroidVisualPalette palette)
    {
        var items = new List<ComposableNode>();
        if (!state.Notices.IsDefaultOrEmpty)
            if (MinimalPairsNotices(state, palette) is { } notices)
                items.Add(notices);
        if (session.ShowSummary)
        {
            items.Add(MinimalPairsSummary(session, palette));
            return MinimalPairsScroll(palette, [.. items]);
        }
        if (session.IsInitializing)
        {
            items.Add(MinimalPairsSpinner());
            return MinimalPairsScroll(palette, [.. items]);
        }
        if (!session.HasTrial)
        {
            items.Add(Body(Localize(NativeMinimalPairsText.SessionNoPairs), palette, secondary: true,
                modifier: Modifier.TestTag(NativeMinimalPairsAccess.SessionEmpty)));
            items.Add(Action(Localize(NativeMinimalPairsText.GoBack), NativeMinimalPairsAccess.GoBack,
                () => MinimalPairsCommand((presenter, token) => presenter.BackToListAsync(token)), true,
                primary: false));
            return MinimalPairsScroll(palette, [.. items]);
        }
        var header = new Column(Arrangement.SpacedBy(8), Alignment.Horizontal.CenterHorizontally)
        {
            Modifier.FillMaxWidth(),
            Body(string.Format(CultureInfo.CurrentCulture, Localize(NativeMinimalPairsText.Trial),
                    session.TrialNumber, session.TotalTrials), palette, secondary: true,
                modifier: Modifier.TestTag(NativeMinimalPairsAccess.Trial)),
            new Row(Arrangement.SpacedBy(24), Alignment.Vertical.CenterVertically)
            {
                MinimalPairsCount(Icons.Default.Check, session.CorrectCount, palette.Success,
                    NativeMinimalPairsText.Correct, NativeMinimalPairsAccess.CorrectCount),
                MinimalPairsCount(Icons.Default.Close, session.IncorrectCount, palette.Danger,
                    NativeMinimalPairsText.Incorrect, NativeMinimalPairsAccess.IncorrectCount),
            },
        };
        if (session.AudioNoticeKey is { } notice)
            header.Add(new Text(Localize(notice))
            {
                Modifier = Modifier.TestTag(NativeMinimalPairsAccess.AudioNotice),
                Color = palette.WarningText, FontSize = new Sp(13),
            });
        items.Add(header);
        items.Add(new Row(Arrangement.SpacedBy(16), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().Padding(vertical: 24),
            MinimalPairsTile(session.Left!, session, NativeMinimalPairsAccess.LeftTile, palette),
            MinimalPairsTile(session.Right!, session, NativeMinimalPairsAccess.RightTile, palette),
        });
        items.Add(new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            new Box
            {
                Modifier.Weight(1f),
                Action(Localize(NativeMinimalPairsText.Replay), NativeMinimalPairsAccess.Replay,
                    () => MinimalPairsCommand((presenter, token) => presenter.ReplayAsync(token)), session.CanReplay,
                    primary: false),
            },
            new Box
            {
                Modifier.Weight(1f),
                Action(Localize(NativeMinimalPairsText.CheckAnswer), NativeMinimalPairsAccess.CheckAnswer,
                    () => MinimalPairsCommand((presenter, token) => presenter.CheckAnswerAsync(token)),
                    session.CanCheck),
            },
        });
        return MinimalPairsScroll(palette, [.. items]);
    }

    private ComposableNode MinimalPairsCount(AndroidX.Compose.UI.Graphics.Vector.ImageVector icon, int count, Color color, string key, string id)
    {
        var label = $"{Localize(key)} {count}";
        return new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
        {
            Modifier.TestTag(id).Semantics(semantics => semantics.ContentDescription(label)),
            new Icon(icon, null) { Modifier = Modifier.Size(20), Tint = color },
            new Text(count.ToString(CultureInfo.CurrentCulture))
            {
                Color = color, FontSize = new Sp(20), FontWeight = FontWeight.SemiBold,
            },
        };
    }

    /// <summary>
    /// An answer tile: only the target term is shown and announced before
    /// Check Answer; afterwards the source borders and the check icon appear.
    /// </summary>
    private ComposableNode MinimalPairsTile(NativeMinimalPairTile tile, NativeMinimalPairsSessionState session,
        string id, AndroidVisualPalette palette)
    {
        var mark = session.MarkFor(tile);
        var border = mark switch
        {
            NativeMinimalPairTileMark.Selected => palette.Success,
            NativeMinimalPairTileMark.SelectedCorrect or NativeMinimalPairTileMark.Answer => palette.Accent,
            NativeMinimalPairTileMark.SelectedWrong => palette.Danger,
            _ => palette.Border,
        };
        var fill = mark switch
        {
            NativeMinimalPairTileMark.SelectedWrong => palette.DangerSurface,
            NativeMinimalPairTileMark.SelectedCorrect or NativeMinimalPairTileMark.Answer => palette.Elevated,
            _ => palette.Card,
        };
        var label = mark switch
        {
            NativeMinimalPairTileMark.SelectedCorrect or NativeMinimalPairTileMark.Answer =>
                $"{tile.Term}. {Localize(NativeMinimalPairsText.AnswerCorrect)}",
            NativeMinimalPairTileMark.SelectedWrong => $"{tile.Term}. {Localize(NativeMinimalPairsText.AnswerIncorrect)}",
            _ => tile.Term,
        };
        var selected = session.SelectedWordId == tile.WordId;
        var modifier = Modifier.Weight(1f).HeightIn(min: 150)
            .Border(3, border, AndroidVisualTheme.CardRadius)
            .TestTag(id);
        modifier = session.CanSelect
            ? modifier.Clickable(() => MinimalPairsCommand(
                    (presenter, token) => presenter.SelectAnswerAsync(tile.WordId, token)))
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
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = fill,
        };
        var box = new Box
        {
            Modifier.FillMaxWidth().HeightIn(min: 150),
            new Text(tile.Term)
            {
                Modifier = Modifier.Align(Alignment.Center).FillMaxWidth().Padding(8),
                Color = palette.Text, FontSize = new Sp(26), FontWeight = FontWeight.SemiBold,
                Align = TextAlign.Center,
            },
        };
        // The check icon exists only after Check Answer; before that both tiles are identical.
        if (session.ShowsCheck(tile))
            box.Add(new Icon(Icons.Default.Check, null)
            {
                Modifier = Modifier.Align(Alignment.TopEnd).Padding(8).Size(22),
                Tint = palette.Accent,
            });
        surface.Add(box);
        return surface;
    }

    private ComposableNode MinimalPairsSummary(NativeMinimalPairsSessionState session, AndroidVisualPalette palette)
    {
        ComposableNode Line(string key, string value, Color color, string id)
        {
            var label = $"{Localize(key)} {value}";
            return new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.TestTag(id).Semantics(semantics => semantics.ContentDescription(label)),
                Body(Localize(key), palette),
                new Text(value) { Color = color, FontSize = new Sp(22), FontWeight = FontWeight.SemiBold },
            };
        }
        return new Column(Arrangement.SpacedBy(16), Alignment.Horizontal.CenterHorizontally)
        {
            Modifier.FillMaxWidth().Padding(vertical: 24).TestTag(NativeMinimalPairsAccess.Summary),
            Heading(Localize(NativeMinimalPairsText.SessionSummary), palette, 26,
                Modifier.Semantics(semantics => semantics.Heading())),
            Card(palette, null,
                Line(NativeMinimalPairsText.Correct, session.CorrectCount.ToString(CultureInfo.CurrentCulture),
                    palette.Success, NativeMinimalPairsAccess.SummaryCorrect),
                Line(NativeMinimalPairsText.Incorrect, session.IncorrectCount.ToString(CultureInfo.CurrentCulture),
                    palette.Danger, NativeMinimalPairsAccess.SummaryIncorrect),
                Line(NativeMinimalPairsText.Accuracy,
                    $"{session.Accuracy.ToString("F1", CultureInfo.CurrentCulture)}%", palette.Text,
                    NativeMinimalPairsAccess.SummaryAccuracy),
                Line(NativeMinimalPairsText.Duration, string.Format(CultureInfo.CurrentCulture,
                        Localize(NativeMinimalPairsText.DurationValue), session.DurationMinutes), palette.Text,
                    NativeMinimalPairsAccess.SummaryDuration)),
            Action(Localize(NativeMinimalPairsText.Done), NativeMinimalPairsAccess.Done,
                () => MinimalPairsCommand((presenter, token) => presenter.BackToListAsync(token)), true),
        };
    }

    /// <summary>Errors and warnings stay inline until the next action; success and info are the shell's snackbar.</summary>
    private ComposableNode? MinimalPairsNotices(NativeMinimalPairsState state, AndroidVisualPalette palette) =>
        MessageBanners(state.Notices, palette, index => $"{NativeMinimalPairsAccess.Notice}.{index}",
            () => MinimalPairsCommand((presenter, token) => presenter.DismissNoticesAsync(token)),
            NativeMinimalPairsAccess.DismissNotices, Localize(NativeMinimalPairsText.Dismiss));
}
