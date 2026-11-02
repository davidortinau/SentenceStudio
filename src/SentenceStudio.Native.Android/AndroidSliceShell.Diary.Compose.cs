using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Text.Input;
using Microsoft.Extensions.DependencyInjection;
using ComposeButton = AndroidX.Compose.Button;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose Diary (source Diary.razor, DiaryEditor.razor, DiaryViewer.razor).
/// One activity destination with three in-activity pages: the list (Back
/// leaves to the Dashboard), today's editor and the entry viewer (Back, the
/// header arrow or system Back, returns to the list). The editor binds a
/// Compose TextFieldValue state, so typing (including Korean IME composition)
/// never goes through a presenter publish; the presenter receives the text
/// when a recomposition sees it change.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeDiaryPresenter? _diary;
    private bool _diaryDraftLoaded;
    private string _diarySentContent = string.Empty;

    internal NativeDiaryState Diary { get; private set; } = new(NativePhase.Loading);

    internal MutableState<TextFieldValue> DiaryDraft { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));

    private async Task OpenDiaryAsync(NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        if (ActivityServices is not { } services)
            return;
        var presenter = services.GetRequiredService<NativeDiaryPresenter>();
        presenter.StateChanged += OnDiaryChanged;
        _diary = presenter;
        Publish(() => Diary = new(NativePhase.Loading));
        var state = await presenter.OpenAsync(launch, cancellationToken);
        Publish(() => ApplyDiary(state));
    }

    private async Task ExitDiaryAsync(CancellationToken cancellationToken)
    {
        if (_diary is { } presenter)
        {
            SyncDiaryDraft(presenter);
            await presenter.ExitAsync(cancellationToken);
        }
    }

    private void ReleaseDiary()
    {
        if (_diary is { } presenter)
        {
            presenter.StateChanged -= OnDiaryChanged;
            presenter.Dispose();
        }
        _diary = null;
        _diaryDraftLoaded = false;
        _diarySentContent = string.Empty;
        DiaryDraft.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
    }

    /// <summary>Editor/viewer Back returns to the list; on the list, Back leaves the activity.</summary>
    private bool DiaryBack()
    {
        if (_diary is null || Diary.Destination != NativeDestination.Activity
            || Diary.Phase != NativePhase.Ready || Diary.Page == NativeDiaryPage.List)
            return false;
        DiaryCommand((presenter, token) => presenter.BackToListAsync(token));
        return true;
    }

    private void OnDiaryChanged(NativeDiaryState state) => Publish(() => ApplyDiary(state));

    /// <summary>The editor's text is loaded once per visit; later publishes never overwrite what is being typed.</summary>
    private void ApplyDiary(NativeDiaryState state)
    {
        Diary = state;
        if (state.Page != NativeDiaryPage.Editor || state.Editor is not { IsLoading: false } editor)
        {
            _diaryDraftLoaded = false;
            return;
        }
        if (!_diaryDraftLoaded)
        {
            _diaryDraftLoaded = true;
            _diarySentContent = editor.Content;
            DiaryDraft.Value = ComposeExtensions.NewTextFieldValue(editor.Content);
        }
    }

    private void SyncDiaryDraft(NativeDiaryPresenter presenter)
    {
        if (!_diaryDraftLoaded)
            return;
        var text = DiaryDraft.Value.Text;
        if (string.Equals(text, _diarySentContent, StringComparison.Ordinal))
            return;
        _diarySentContent = text;
        if (presenter.SetContent(text) is { } editor)
            Diary = Diary with { Editor = editor };
    }

    /// <summary>Diary commands bypass the shell's busy gate so AI calls never disable Back; the presenter serializes them.</summary>
    private void DiaryCommand(Func<NativeDiaryPresenter, CancellationToken, Task<NativeDiaryState>> command)
    {
        if (_diary is not { } presenter)
            return;
        SyncDiaryDraft(presenter);
        _ = RunDiaryAsync(presenter, command);
    }

    private async Task RunDiaryAsync(NativeDiaryPresenter presenter,
        Func<NativeDiaryPresenter, CancellationToken, Task<NativeDiaryState>> command)
    {
        try
        {
            var state = await command(presenter, _lifetime.Token);
            Publish(() =>
            {
                if (ReferenceEquals(_diary, presenter))
                    ApplyDiary(state);
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

    private ComposableNode RenderDiary(AndroidVisualPalette palette)
    {
        var state = Diary;
        var title = state.Page switch
        {
            NativeDiaryPage.Editor when state.Phase == NativePhase.Ready => Localize(NativeDiaryText.TodayTitle),
            NativeDiaryPage.Viewer when state.Viewer is { IsLoading: false, NotFound: false } viewer =>
                NativeDiaryText.FormatDate(viewer.EntryDate),
            _ => Localize(NativeDiaryText.Title),
        };
        var page = new Column
        {
            Modifier.FillMaxSize().TestTag(NativeDiaryAccess.Screen),
            ScreenHeader(title, palette, back: true, backAutomationId: NativeDiaryAccess.Back),
        };
        if (state.Phase is NativePhase.Blocked or NativePhase.Error)
        {
            page.Add(DiaryScroll(palette,
                Body(Localize(state.ErrorKey ?? NativeDiaryText.Unavailable), palette,
                    modifier: Modifier.TestTag(NativeDiaryAccess.Error)),
                Action(Localize(NativeDiaryText.GoBack), NativeDiaryAccess.GoBack, ExitActivity, true,
                    primary: false)));
            return page;
        }
        if (state.Phase != NativePhase.Ready)
        {
            page.Add(DiaryScroll(palette, DiarySpinner()));
            return page;
        }
        switch (state.Page)
        {
            case NativeDiaryPage.Editor:
                if (state.Editor is { IsLoading: false } editor)
                {
                    page.Add(DiaryEditorBody(state, editor, palette));
                    page.Add(DiaryInputBar(editor, palette));
                }
                else
                    page.Add(DiaryScroll(palette, DiarySpinner()));
                break;
            case NativeDiaryPage.Viewer:
                page.Add(DiaryViewer(state, palette));
                break;
            default:
                page.Add(DiaryList(state, palette));
                break;
        }
        return page;
    }

    private ComposableNode DiaryScroll(AndroidVisualPalette palette, params ComposableNode[] children)
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

    private static ComposableNode DiarySpinner() => new CircularProgressIndicator
    {
        Modifier = Modifier.Size(28).TestTag(NativeDiaryAccess.Loading)
    };

    private ComposableNode DiaryList(NativeDiaryState state, AndroidVisualPalette palette)
    {
        var items = new List<ComposableNode>();
        if (!state.Notices.IsDefaultOrEmpty)
            if (DiaryNotices(state, palette) is { } notices)
                items.Add(notices);
        var list = state.List;
        if (list is null || list.IsLoading)
            items.Add(DiarySpinner());
        else
        {
            var title = Localize(list.Today is null ? NativeDiaryText.StartToday : NativeDiaryText.ContinueToday);
            var detail = list.Today is null ? Localize(NativeDiaryText.StartTodayHint)
                : string.Format(CultureInfo.CurrentCulture, Localize(NativeDiaryText.WordCount), list.Today.WordCount);
            items.Add(DiaryTappableCard(palette, NativeDiaryAccess.Today, $"{title}. {detail}",
                () => DiaryCommand((presenter, token) => presenter.OpenTodayAsync(token)),
                new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
                {
                    Modifier.FillMaxWidth(),
                    new Icon(Icons.Default.Edit, null) { Modifier = Modifier.Size(28), Tint = palette.Primary },
                    new Column(Arrangement.SpacedBy(2))
                    {
                        Modifier.Weight(1f),
                        new Text(title) { Color = palette.Text, FontSize = new Sp(16), FontWeight = FontWeight.SemiBold },
                        Body(detail, palette, secondary: true,
                            modifier: Modifier.TestTag(NativeDiaryAccess.TodayWordCount)),
                    },
                }));
            if (!list.PastEntries.IsDefaultOrEmpty)
            {
                items.Add(Heading(Localize(NativeDiaryText.PastEntries), palette, 22,
                    Modifier.Semantics(semantics => semantics.Heading())));
                foreach (var entry in list.PastEntries)
                    items.Add(DiaryEntryCard(entry, palette));
            }
            else if (list.Today is null)
                items.Add(Body(Localize(NativeDiaryText.EmptyState), palette, secondary: true,
                    modifier: Modifier.TestTag(NativeDiaryAccess.Empty)));
        }
        var scroll = DiaryScroll(palette, [.. items]);
        return scroll;
    }

    private ComposableNode DiaryEntryCard(NativeDiaryEntrySummary entry, AndroidVisualPalette palette)
    {
        var id = entry.AccessibilityId;
        var date = entry.DateKey is null ? NativeDiaryText.FormatDate(entry.EntryDate) : Localize(entry.DateKey);
        var words = string.Format(CultureInfo.CurrentCulture, Localize(NativeDiaryText.WordCount), entry.WordCount);
        var left = new Column(Arrangement.SpacedBy(4))
        {
            Modifier.Weight(1f),
            new Text(date)
            {
                Modifier = Modifier.TestTag($"{id}.date"),
                Color = palette.Text, FontSize = new Sp(16), FontWeight = FontWeight.SemiBold,
            },
        };
        if (!string.IsNullOrWhiteSpace(entry.Snippet))
            left.Add(new Text(entry.Snippet)
            {
                Modifier = Modifier.TestTag($"{id}.snippet"),
                Color = palette.SecondaryText, FontSize = new Sp(14), MaxLines = 1,
            });
        var right = new Column(Arrangement.SpacedBy(4))
        {
            Body(words, palette, secondary: true, modifier: Modifier.TestTag($"{id}.word-count")),
        };
        var feedbackLabel = Localize(NativeDiaryText.HasFeedback);
        if (entry.HasFeedback)
            right.Add(new Icon(Icons.Default.Star, feedbackLabel)
            {
                Modifier = Modifier.Size(18).TestTag($"{id}.feedback"),
                Tint = palette.Primary,
            });
        var label = entry.HasFeedback ? $"{date}. {words}. {feedbackLabel}" : $"{date}. {words}";
        return DiaryTappableCard(palette, id, label,
            () => DiaryCommand((presenter, token) => presenter.OpenEntryAsync(entry.Id, token)),
            new Row(Arrangement.SpacedBy(12))
            {
                Modifier.FillMaxWidth(),
                left,
                right,
            });
    }

    private static ComposableNode DiaryTappableCard(AndroidVisualPalette palette, string id, string label,
        Action onClick, ComposableNode content) =>
        new Column
        {
            Modifier.FillMaxWidth().TestTag(id)
                .Clickable(onClick)
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(label);
                    semantics.Role(SemanticsRole.Button);
                }),
            Card(palette, null, content),
        };

    private ComposableNode DiaryEditorBody(NativeDiaryState state, NativeDiaryEditorState editor,
        AndroidVisualPalette palette)
    {
        var body = new Column(Arrangement.SpacedBy(12))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 12)
                .TestTag(NativeDiaryAccess.Editor),
        };
        if (!state.Notices.IsDefaultOrEmpty)
            if (DiaryNotices(state, palette) is { } notices)
                body.Add(notices);
        body.Add(DiaryPromptCard(editor, palette));
        var placeholder = Localize(NativeDiaryText.EditorPlaceholder);
        body.Add(new OutlinedTextField(DiaryDraft, enabled: true, singleLine: false, minLines: 6)
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f).TestTag(NativeDiaryAccess.Input)
                .Semantics(semantics => semantics.ContentDescription(placeholder)),
            Placeholder = new Text(placeholder),
            TextStyle = new TextStyle { FontSize = new Sp(editor.FontSize) },
        });
        return body;
    }

    private ComposableNode DiaryPromptCard(NativeDiaryEditorState editor, AndroidVisualPalette palette)
    {
        var collapsed = editor.IsPromptCollapsed;
        var toggleLabel = Localize(NativeDiaryText.TogglePrompt);
        var toggle = new Row(Arrangement.SpacedBy(6), Alignment.Vertical.CenterVertically)
        {
            Modifier.Weight(1f).TestTag(NativeDiaryAccess.PromptToggle)
                .Clickable(() => DiaryCommand((presenter, token) => presenter.TogglePromptCollapsedAsync(token)))
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(toggleLabel);
                    semantics.Role(SemanticsRole.Button);
                    semantics.Selected(!collapsed);
                }),
            new Icon(collapsed ? Icons.Default.KeyboardArrowDown : Icons.Default.KeyboardArrowUp, null)
            {
                Modifier = Modifier.Size(18),
                Tint = palette.SecondaryText,
            },
            new Text(Localize(NativeDiaryText.Prompt).ToUpper(CultureInfo.CurrentCulture))
            {
                Color = palette.SecondaryText, FontSize = new Sp(13),
            },
        };
        if (collapsed && !string.IsNullOrWhiteSpace(editor.PromptText))
            toggle.Add(new Text(editor.PromptText)
            {
                Modifier = Modifier.Weight(1f),
                Color = palette.SecondaryText, FontSize = new Sp(13), MaxLines = 1,
            });
        var header = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            toggle,
        };
        var children = new List<ComposableNode> { header };
        if (!collapsed)
        {
            var refreshLabel = Localize(NativeDiaryText.NewPrompt);
            var refresh = new IconButton(
                () => DiaryCommand((presenter, token) => presenter.RefreshPromptAsync(token)),
                enabled: !editor.IsLoadingPrompt)
            {
                Modifier = Modifier.TestTag(NativeDiaryAccess.NewPrompt)
                    .Semantics(semantics => semantics.ContentDescription(refreshLabel)),
            };
            if (editor.IsLoadingPrompt)
                refresh.Add(new CircularProgressIndicator { Modifier = Modifier.Size(20) });
            else
                refresh.Add(new Icon(Icons.Default.Refresh, refreshLabel)
                {
                    Modifier = Modifier.Size(22),
                    Tint = palette.Text,
                });
            header.Add(refresh);
            if (!string.IsNullOrWhiteSpace(editor.PromptText))
            {
                children.Add(Body(editor.PromptText, palette, modifier: Modifier.TestTag(NativeDiaryAccess.PromptText)));
                if (!string.IsNullOrWhiteSpace(editor.PromptHint))
                    children.Add(Body(editor.PromptHint, palette, secondary: true,
                        modifier: Modifier.TestTag(NativeDiaryAccess.PromptHint)));
            }
            else
                children.Add(Body(Localize(NativeDiaryText.NoPromptYet), palette, secondary: true,
                    modifier: Modifier.TestTag(NativeDiaryAccess.PromptEmpty)));
        }
        return Card(palette, NativeDiaryAccess.PromptCard, [.. children]);
    }

    private ComposableNode DiaryInputBar(NativeDiaryEditorState stateEditor, AndroidVisualPalette palette)
    {
        // Reading the draft state here recomposes the bar on each keystroke;
        // the presenter gets the text (word count, stars, autosave restart).
        if (_diary is { } presenter)
            SyncDiaryDraft(presenter);
        var editor = Diary.Editor ?? stateEditor;
        var starsLabel = $"{string.Format(CultureInfo.CurrentCulture, Localize(NativeDiaryText.StarsLabel), editor.Stars)}. {Localize(NativeDiaryText.StarsTooltip)}";
        var stars = new Row(Arrangement.SpacedBy(2), Alignment.Vertical.CenterVertically)
        {
            Modifier.TestTag(NativeDiaryAccess.Stars)
                .Semantics(semantics => semantics.ContentDescription(starsLabel)),
        };
        for (var index = 1; index <= 3; index++)
            stars.Add(new Icon(Icons.Default.Star, null)
            {
                Modifier = Modifier.Size(20),
                Tint = index <= editor.Stars ? palette.WarningText : palette.Border,
            });
        var top = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            stars,
            Body(string.Format(CultureInfo.CurrentCulture, Localize(NativeDiaryText.Stars), editor.WordCount),
                palette, secondary: true, modifier: Modifier.TestTag(NativeDiaryAccess.WordCount)),
        };
        if (editor.SaveStatus == NativeDiarySaveStatus.Saving)
        {
            top.Add(new CircularProgressIndicator { Modifier = Modifier.Size(16) });
            top.Add(Body(Localize(NativeDiaryText.Saving), palette, secondary: true,
                modifier: Modifier.TestTag(NativeDiaryAccess.SaveStatus)));
        }
        else if (editor.SaveStatus == NativeDiarySaveStatus.Saved)
        {
            top.Add(new Icon(Icons.Default.CheckCircle, null) { Modifier = Modifier.Size(16), Tint = palette.Success });
            top.Add(new Text(Localize(NativeDiaryText.Saved))
            {
                Modifier = Modifier.TestTag(NativeDiaryAccess.SaveStatus),
                Color = palette.Success, FontSize = new Sp(14),
            });
        }

        ComposableNode FontButton(string label, string id, string glyph, bool enabled,
            Func<NativeDiaryPresenter, CancellationToken, Task<NativeDiaryState>> command)
        {
            var button = new OutlinedButton(onClick: () => DiaryCommand(command), enabled: enabled)
            {
                Modifier = Modifier.TestTag(id).Semantics(semantics => semantics.ContentDescription(label)),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            button.Add(new Text(glyph));
            return button;
        }
        var saveLabel = Localize(NativeDiaryText.Save);
        var save = new ComposeButton(
            onClick: () => DiaryCommand((presenter, token) => presenter.SaveAsync(false, token)),
            enabled: editor.CanSave)
        {
            Modifier = Modifier.TestTag(NativeDiaryAccess.Save)
                .Semantics(semantics => semantics.ContentDescription(saveLabel)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        save.Add(new Text(saveLabel));
        var sizeLabel = $"{Localize("Reading_FontSize")} {editor.FontSize}";
        var bottom = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            FontButton(Localize(NativeDiaryText.DecreaseFontSize), NativeDiaryAccess.FontDecrease, "A-",
                editor.CanDecreaseFont, (presenter, token) => presenter.DecreaseFontSizeAsync(token)),
            new Text($"{editor.FontSize}px")
            {
                Modifier = Modifier.TestTag(NativeDiaryAccess.FontSize)
                    .Semantics(semantics => semantics.ContentDescription(sizeLabel)),
                Color = palette.Text, FontSize = new Sp(14),
            },
            FontButton(Localize(NativeDiaryText.IncreaseFontSize), NativeDiaryAccess.FontIncrease, "A+",
                editor.CanIncreaseFont, (presenter, token) => presenter.IncreaseFontSizeAsync(token)),
            new Column { Modifier.Weight(1f) },
            save,
        };
        return new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth()
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 8)
                .TestTag("native.diary.input-bar"),
            top,
            bottom,
        };
    }

    private ComposableNode DiaryViewer(NativeDiaryState state, AndroidVisualPalette palette)
    {
        var items = new List<ComposableNode>();
        if (!state.Notices.IsDefaultOrEmpty)
            if (DiaryNotices(state, palette) is { } notices)
                items.Add(notices);
        var viewer = state.Viewer;
        if (viewer is null || viewer.IsLoading)
            items.Add(DiarySpinner());
        else if (viewer.NotFound)
        {
            items.Add(Body(Localize(NativeDiaryText.NotFound), palette, secondary: true,
                modifier: Modifier.TestTag(NativeDiaryAccess.NotFound)));
            items.Add(Action(Localize(NativeDiaryText.GoBack), NativeDiaryAccess.GoBack,
                () => DiaryCommand((presenter, token) => presenter.BackToListAsync(token)), true, primary: false));
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(viewer.PromptText))
            {
                var prompt = new List<ComposableNode>
                {
                    new Text(Localize(NativeDiaryText.Prompt).ToUpper(CultureInfo.CurrentCulture))
                    {
                        Color = palette.SecondaryText, FontSize = new Sp(13),
                    },
                    Body(viewer.PromptText, palette),
                };
                if (!string.IsNullOrWhiteSpace(viewer.PromptHint))
                    prompt.Add(Body(viewer.PromptHint, palette, secondary: true));
                items.Add(Card(palette, NativeDiaryAccess.ViewerPrompt, [.. prompt]));
            }
            items.Add(Card(palette, null,
                new Row(Arrangement.SpacedBy(8))
                {
                    Modifier.FillMaxWidth(),
                    Body(NativeDiaryText.FormatDate(viewer.EntryDate), palette, secondary: true,
                        modifier: Modifier.Weight(1f).TestTag(NativeDiaryAccess.ViewerDate)),
                    Body(string.Format(CultureInfo.CurrentCulture, Localize(NativeDiaryText.WordCount),
                        viewer.WordCount), palette, secondary: true,
                        modifier: Modifier.TestTag(NativeDiaryAccess.ViewerWordCount)),
                },
                Body(viewer.Content, palette, modifier: Modifier.TestTag(NativeDiaryAccess.ViewerContent))));
            items.Add(DiaryFeedbackCard(viewer, palette));
        }
        return DiaryScroll(palette, [.. items]);
    }

    private ComposableNode DiaryFeedbackCard(NativeDiaryViewerState viewer, AndroidVisualPalette palette)
    {
        var buttonText = viewer.IsLoadingFeedback ? Localize(NativeDiaryText.Analyzing)
            : Localize(viewer.HasFeedback ? NativeDiaryText.Regenerate : NativeDiaryText.GetFeedback);
        var button = new OutlinedButton(
            onClick: () => DiaryCommand((presenter, token) => presenter.RequestFeedbackAsync(token)),
            enabled: !viewer.IsLoadingFeedback)
        {
            Modifier = Modifier.TestTag(NativeDiaryAccess.FeedbackButton)
                .Semantics(semantics => semantics.ContentDescription(buttonText)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        if (viewer.IsLoadingFeedback)
            button.Add(new CircularProgressIndicator { Modifier = Modifier.Size(16) });
        button.Add(new Text(buttonText));
        var children = new List<ComposableNode>
        {
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                new Icon(Icons.Default.Star, null) { Modifier = Modifier.Size(20), Tint = palette.Primary },
                Heading(Localize(NativeDiaryText.AiFeedback), palette, 20,
                    Modifier.Weight(1f).Semantics(semantics => semantics.Heading())),
                button,
            },
        };
        if (viewer.HasFeedback)
        {
            void Section(string key, string? value, string id)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return;
                children.Add(new Column(Arrangement.SpacedBy(2))
                {
                    Modifier.FillMaxWidth().TestTag(id),
                    new Text(Localize(key).ToUpper(CultureInfo.CurrentCulture))
                    {
                        Color = palette.SecondaryText, FontSize = new Sp(13),
                    },
                    Body(value, palette),
                });
            }
            Section(NativeDiaryText.Recommended, viewer.FeedbackRecommended, NativeDiaryAccess.FeedbackRecommended);
            Section(NativeDiaryText.Notes, viewer.FeedbackNotes, NativeDiaryAccess.FeedbackNotes);
            Section(NativeDiaryText.Strengths, viewer.FeedbackStrengths, NativeDiaryAccess.FeedbackStrengths);
        }
        else if (!viewer.IsLoadingFeedback)
            children.Add(Body(Localize(NativeDiaryText.FeedbackHint), palette, secondary: true,
                modifier: Modifier.TestTag(NativeDiaryAccess.FeedbackHint)));
        return Card(palette, NativeDiaryAccess.Feedback, [.. children]);
    }

    /// <summary>Errors and warnings stay inline until the next action; success and info are the shell's snackbar.</summary>
    private ComposableNode? DiaryNotices(NativeDiaryState state, AndroidVisualPalette palette) =>
        MessageBanners(state.Notices, palette, index => $"{NativeDiaryAccess.Notice}.{index}",
            () => DiaryCommand((presenter, token) => presenter.DismissNoticesAsync(token)),
            NativeDiaryAccess.DismissNotices, Localize(NativeDiaryText.Dismiss));
}
