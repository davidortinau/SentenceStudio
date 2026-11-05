using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// SwiftUI Describe a Scene (source Scene.razor). The gallery toolbar button
/// sits in the navigation bar (source PageHeader toolbar); the scene image and
/// the graded results sit side by side from 768 points (source col-md-6) and
/// stack below; the source activity-input-bar (description field, new scene,
/// grade) is a bottom inset. The gallery overlay is a SwiftUI sheet, and the
/// Captain-approved system photo picker (PhotosPicker) is presented from it.
/// Typing never re-renders, so Korean IME composition is not interrupted.
/// </summary>
internal sealed class SceneScreen : IActivityScreen
{
    private const string KoreanSpeech = "ko-KR";
    private readonly ActivityScreenHost _host;
    private readonly NativeScenePresenter _presenter;
    private NativeSceneState _state = new(NativePhase.Loading);
    private SwiftView? _grade;
    private SwiftView? _add;
    private bool _pickerOpen;
    private bool _publishWhilePicking;

    public SceneScreen(ActivityScreenHost host)
    {
        _host = host;
        _presenter = host.Services.GetRequiredService<NativeScenePresenter>();
        _presenter.StateChanged += OnStateChanged;
    }

    public string TitleKey => NativeSceneText.Title;

    public NativeMessage? Toast => NativeMessages.LatestToast(_state.Notices);

    public bool HasExited => _state.Destination == NativeDestination.TodaysPlan;

    public bool HasSheet => true;

    public bool IsSheetPresented => _state.Gallery is not null && !HasExited;

    public double SheetDetentFraction => 0.9;

    public void OnSheetDismissed()
    {
        if (_state.Gallery is not null)
            Fire(_presenter.CloseGalleryAsync);
    }

    public async Task OpenAsync(NativeActivityLaunch launch, CancellationToken cancellationToken) =>
        _state = await _presenter.OpenAsync(launch, cancellationToken);

    public async Task ExitAsync(CancellationToken cancellationToken) =>
        _state = await _presenter.ExitAsync(cancellationToken);

    private void OnStateChanged(NativeSceneState state)
    {
        _state = state;
        Publish();
    }

    /// <summary>A re-render while the photo picker is up would remount its host and dismiss it; defer until it closes.</summary>
    private void Publish()
    {
        if (_pickerOpen)
        {
            _publishWhilePicking = true;
            return;
        }
        _host.Publish();
    }

    /// <summary>Commands do not take the controller's busy gate: grading and downloads can take seconds and must not disable Back.</summary>
    private void Fire(Func<CancellationToken, Task<NativeSceneState>> action) =>
        _ = FireAsync(action);

    private async Task FireAsync(Func<CancellationToken, Task<NativeSceneState>> action)
    {
        try
        {
            _state = await action(_host.Lifetime);
        }
        catch (OperationCanceledException) when (_host.Lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _host.Logger?.LogError(ex, "Native iOS Describe a Scene action failed.");
            _state = _state with
            {
                Notices = [new(NativeMessageKind.Error, NativeText.UnexpectedError)]
            };
        }
        Publish();
    }

    public SwiftView Render(SourceUiStyle style)
    {
        var text = _host.Text;
        var state = _state;
        var items = new List<SwiftView>();
        _grade = null;
        // Back published the exit state (no image); the controller pops the page next.
        if (HasExited)
            return style.Page(Views.VStack());
        if (!state.Notices.IsDefaultOrEmpty && state.Phase is NativePhase.Ready or NativePhase.Empty)
            if (Notices(state, style, text) is { } notices)
                items.Add(notices);
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                items.Add(Views.IndeterminateProgress(text(NativeSceneText.Loading))
                    .WithAccessibilityIdentifier(NativeSceneAccess.Loading));
                items.Add(style.Text(Views.Text(text(NativeSceneText.Loading)),
                    SourceTextRole.Body1, SourceTextTone.Secondary));
                break;
            case NativePhase.Ready when state.Image is not null:
                var columns = Views.HStack(SceneImage(state, style, text), Results(state, style, text))
                    .WithAdaptiveStack(768)
                    .WithFlexibleWidth();
                columns.Spacing = 16;
                items.Add(columns);
                break;
            case NativePhase.Empty:
                // Source empty state: message, Browse Gallery, Go Back.
                items.Add(style.Text(Views.Text(text(NativeSceneText.NoSceneImages)),
                        SourceTextRole.Body1, SourceTextTone.Secondary)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier(NativeSceneAccess.Empty));
                var buttons = Views.HStack(
                    style.PrimaryButton(text(NativeSceneText.BrowseGallery), () => Fire(_presenter.OpenGalleryAsync))
                        .WithAccessibilityIdentifier(NativeSceneAccess.EmptyGallery),
                    style.SecondaryButton(text(NativeSceneText.GoBack), _host.Exit)
                        .WithAccessibilityIdentifier(NativeSceneAccess.EmptyBack));
                buttons.Spacing = 8;
                items.Add(buttons.WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center));
                break;
            default:
                items.Add(style.Text(Views.Text(text(state.ErrorKey ?? NativeSceneText.Unavailable)),
                        SourceTextRole.Body1, SourceTextTone.Danger)
                    .WithAccessibilityIdentifier(NativeSceneAccess.Error));
                items.Add(style.SecondaryButton(text(NativeSceneText.GoBack), _host.Exit)
                    .WithAccessibilityIdentifier(NativeSceneAccess.EmptyBack));
                break;
        }

        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var content = Views.VStack(items.ToArray())
            .WithPadding(16, gutter, 16, gutter)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.scene.content");
        content.Spacing = SourceUiLayout.PageGap;
        var page = Views.Scroll(content).WithAccessibilityIdentifier(NativeSceneAccess.Screen);
        var body = style.Page(state.Phase == NativePhase.Ready && state.Image is not null
            ? Views.BottomInset(page, InputBar(state, style, text))
            : page);
        if (state.Phase is not (NativePhase.Ready or NativePhase.Empty))
            return body;
        // Source PageHeader ToolbarActions: the image gallery icon.
        var gallery = Views.Button(Views.Image("photo.on.rectangle.angled"), () => Fire(_presenter.OpenGalleryAsync))
            .WithAccessibilityLabel(text(NativeSceneText.ImageGallery))
            .WithAccessibilityIdentifier(NativeSceneAccess.OpenGallery);
        return Views.Toolbar(body, gallery);
    }

    /// <summary>Errors and warnings stay inline until the next action; success and info are the scene's toast.</summary>
    private SwiftView? Notices(NativeSceneState state, SourceUiStyle style, Func<string, string> text)
    {
        return style.MessageBanners(state.Notices, text, index => $"{NativeSceneAccess.Notice}.{index}",
            () => Fire(_presenter.DismissNoticesAsync), NativeSceneAccess.DismissNotices,
            text(NativeSceneText.Dismiss));
    }

    /// <summary>Source scene image card (max-height 400, object-fit contain).</summary>
    private static SwiftView SceneImage(NativeSceneState state, SourceUiStyle style, Func<string, string> text)
    {
        var image = state.Image!;
        SwiftView content = image.Load switch
        {
            NativeSceneImageLoad.Loaded when image.Bytes is { } bytes =>
                Views.ImageFromBytes(bytes, text(NativeSceneText.SceneImage), 400)
                    .WithAccessibilityIdentifier(NativeSceneAccess.Image),
            NativeSceneImageLoad.Failed =>
                Views.VStack(
                        Views.Image("photo"),
                        style.Text(Views.Text(text(NativeSceneText.ImageFailed)), SourceTextRole.Body2,
                            SourceTextTone.Secondary))
                    .WithMinimumHeight(160)
                    .WithAccessibilityIdentifier(NativeSceneAccess.ImageFailed),
            _ => Views.IndeterminateProgress(text(NativeSceneText.Loading))
                .WithMinimumHeight(160)
                .WithAccessibilityIdentifier(NativeSceneAccess.ImageLoading)
        };
        return style.Card(content.WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                .WithPadding(8))
            .WithFlexibleWidth();
    }

    private SwiftView Results(NativeSceneState state, SourceUiStyle style, Func<string, string> text)
    {
        var rows = new List<SwiftView>
        {
            style.Text(Views.Text(text(NativeSceneText.Results)), SourceTextRole.Title3, sourceHeading: true)
        };
        if (state.Results.IsDefaultOrEmpty)
            rows.Add(style.Text(Views.Text(text(NativeSceneText.SubmitDescription)), SourceTextRole.Body2,
                    SourceTextTone.Secondary)
                .WithAccessibilityIdentifier(NativeSceneAccess.ResultsEmpty));
        else
            foreach (var result in state.Results)
                rows.Add(ResultCard(result, state.ExpandedIndex == result.Index, style, text));
        var column = Views.VStack(rows.ToArray()).WithFlexibleWidth()
            .WithAccessibilityIdentifier(NativeSceneAccess.Results);
        column.Spacing = 8;
        return column;
    }

    /// <summary>Source result card: answer, colored accuracy badge, explanation, suggestion, More/Less details.</summary>
    private SwiftView ResultCard(NativeSceneResult result, bool expanded, SourceUiStyle style,
        Func<string, string> text)
    {
        var id = result.AccessibilityId;
        var appearance = style.Appearance;
        var color = result.Accuracy >= 80 ? appearance.Success
            : result.Accuracy >= 50 ? appearance.Warning : appearance.Danger;
        var percent = $"{(int)result.Accuracy}%";
        var badge = style.Text(Views.Text(percent), SourceTextRole.Badge)
            .WithForegroundColor("#FFFFFF", "#FFFFFF")
            .WithPadding(2, 8, 2, 8)
            .WithRoundedSurface(color.Light, color.Dark, appearance.BootstrapRadius)
            .WithAccessibilityLabel($"{text("Common_Accuracy")} {percent}")
            .WithAccessibilityIdentifier($"{id}.accuracy");
        var top = Views.HStack(
            style.Text(Views.Text(result.Answer), SourceTextRole.Body1Strong)
                .WithSpeechLanguage(KoreanSpeech)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier($"{id}.answer"),
            badge);
        top.Spacing = 8;
        var rows = new List<SwiftView> { top };
        if (!string.IsNullOrEmpty(result.AccuracyExplanation))
            rows.Add(style.Text(Views.Text(result.AccuracyExplanation), SourceTextRole.Small,
                    SourceTextTone.Secondary)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier($"{id}.explanation"));
        if (!string.IsNullOrEmpty(result.RecommendedSentence))
        {
            var suggested = Views.HStack(
                    Views.Image("lightbulb").WithForegroundColor(appearance.Success.Light, appearance.Success.Dark),
                    style.Text(Views.Text(result.RecommendedSentence), SourceTextRole.Small)
                        .WithForegroundColor(appearance.Success.Light, appearance.Success.Dark)
                        .WithSpeechLanguage(KoreanSpeech))
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier($"{id}.suggested");
            suggested.Spacing = 4;
            rows.Add(suggested);
        }
        if (expanded)
        {
            var details = new List<SwiftView>();
            if (result.Fluency > 0)
                details.Add(style.Text(Views.Text(
                        $"{text(NativeSceneText.Fluency)}: {(int)result.Fluency}%"),
                    SourceTextRole.Small, SourceTextTone.Secondary));
            if (!string.IsNullOrEmpty(result.FluencyExplanation))
                details.Add(style.Text(Views.Text(result.FluencyExplanation), SourceTextRole.Small,
                    SourceTextTone.Secondary));
            if (!string.IsNullOrEmpty(result.GrammarNotes))
            {
                var notes = Views.HStack(Views.Image("pencil"),
                    style.Text(Views.Text(result.GrammarNotes), SourceTextRole.Small, SourceTextTone.Secondary));
                notes.Spacing = 4;
                details.Add(notes);
            }
            var detailStack = Views.VStack(details.ToArray()).WithFlexibleWidth()
                .WithAccessibilityIdentifier($"{id}.details");
            detailStack.Spacing = 4;
            rows.Add(Views.Divider());
            rows.Add(detailStack);
        }
        var toggleLabel = text(expanded ? NativeSceneText.Less : NativeSceneText.More);
        rows.Add(Views.Button(
                Views.HStack(
                    style.Text(Views.Text(toggleLabel), SourceTextRole.Caption1, SourceTextTone.Secondary),
                    Views.Image(expanded ? "chevron.up" : "chevron.down")),
                () => Fire(token => _presenter.ToggleDetailsAsync(result.Index, token)))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithAccessibilityLabel(toggleLabel)
            .WithAccessibilitySelected(expanded)
            .WithAccessibilityIdentifier($"{id}.toggle"));
        var card = Views.VStack(rows.ToArray()).WithPadding(SourceUiLayout.CardInset).WithFlexibleWidth();
        card.Spacing = 6;
        return style.Card(card).WithAccessibilityIdentifier(id);
    }

    private SwiftView InputBar(NativeSceneState state, SourceUiStyle style, Func<string, string> text)
    {
        var field = style.TextInput(Views.TextField(text(NativeSceneText.DescribeWhatYouSee), state.Draft,
                value =>
                {
                    _presenter.SetDraft(value);
                    UpdateGradeAvailability(style);
                }))
            .WithFlexibleWidth()
            .WithAccessibilityLabel(text(NativeSceneText.DescribeWhatYouSee))
            .WithAccessibilityIdentifier(NativeSceneAccess.Input)
            .OnSubmit(() => Fire(_presenter.GradeAsync));
        var newScene = style.LabelButton(Views.Image("arrow.clockwise"),
                () => Fire(_presenter.NewSceneAsync), SourceButtonRole.FilledSecondary, 12)
            .WithAccessibilityLabel(text(NativeSceneText.Loading))
            .WithAccessibilityIdentifier(NativeSceneAccess.NewScene);
        SourceUiStyle.SetCustomButtonAvailability(newScene, style.Appearance,
            SourceButtonRole.FilledSecondary, state.CanChangeScene);
        SwiftView gradeLabel = state.IsGrading
            ? Views.IndeterminateProgress(text(NativeSceneText.Grade))
            : Views.Image("paperplane.fill");
        var grade = style.LabelButton(gradeLabel, () => Fire(_presenter.GradeAsync),
                SourceButtonRole.Primary, 14)
            .WithAccessibilityLabel(text(NativeSceneText.Grade))
            .WithAccessibilityIdentifier(NativeSceneAccess.Grade);
        _grade = grade;
        UpdateGradeAvailability(style);
        var row = Views.HStack(field, newScene, grade).WithFlexibleWidth();
        row.Spacing = 8;
        return style.DockedBar(row);
    }

    private void UpdateGradeAvailability(SourceUiStyle style)
    {
        if (_grade is not null)
            SourceUiStyle.SetCustomButtonAvailability(_grade, style.Appearance,
                SourceButtonRole.Primary, _presenter.State.CanGrade);
    }

    /// <summary>Source scene-gallery-overlay: header, add bar, image grid; the photo picker is presented from here.</summary>
    public SwiftView RenderSheet(SourceUiStyle style)
    {
        var text = _host.Text;
        var appearance = style.Appearance;
        _add = null;
        if (_state.Gallery is not { } gallery)
            return Views.VStack();
        var headerItems = new List<SwiftView>
        {
            style.Text(Views.Text(text(NativeSceneText.ImageGallery)), SourceTextRole.Title2, sourceHeading: true)
                .WithFlexibleWidth()
        };
        if (gallery.IsSelecting)
        {
            var label = string.Format(CultureInfo.CurrentCulture, text(NativeSceneText.Delete), gallery.SelectedCount);
            var delete = Views.Button(
                    Views.HStack(Views.Image("trash"), style.Text(Views.Text(label), SourceTextRole.Button))
                        .WithForegroundColor(appearance.DangerButtonText.Light, appearance.DangerButtonText.Dark)
                        .WithPadding(6, 10, 6, 10),
                    () => Fire(_presenter.DeleteSelectedAsync))
                .WithButtonAppearance(appearance.DangerButtonFill.Light, appearance.DangerButtonFill.Dark,
                    appearance.DangerButtonFill.Light, appearance.DangerButtonFill.Dark, 8)
                .WithAccessibilityLabel(label)
                .WithAccessibilityIdentifier(NativeSceneAccess.GalleryDelete);
            delete.IsEnabled = gallery.CanDelete;
            if (!gallery.CanDelete)
                delete = delete.WithOpacity(0.5);
            headerItems.Add(delete);
        }
        headerItems.Add(Views.Button(Views.Image(gallery.IsSelecting ? "checkmark.square.fill" : "checkmark.square")
                    .WithPadding(8), () => Fire(_presenter.ToggleSelectModeAsync))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithAccessibilityLabel(text(NativeSceneText.SelectMultiple))
            .WithAccessibilitySelected(gallery.IsSelecting)
            .WithAccessibilityIdentifier(NativeSceneAccess.GallerySelect));
        headerItems.Add(Views.Button(Views.Image("xmark").WithPadding(8), () => Fire(_presenter.CloseGalleryAsync))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithAccessibilityLabel(text(NativeSceneText.Cancel))
            .WithAccessibilityIdentifier(NativeSceneAccess.GalleryClose));
        var header = Views.HStack(headerItems.ToArray()).WithFlexibleWidth();
        header.Spacing = 4;

        var urlField = style.TextInput(Views.TextField(text(NativeSceneText.PasteImageUrl), gallery.NewImageUrl,
                value =>
                {
                    _presenter.SetNewImageUrl(value);
                    UpdateAddAvailability(style);
                }))
            .WithFlexibleWidth()
            .WithAccessibilityLabel(text(NativeSceneText.PasteImageUrl))
            .WithAccessibilityIdentifier(NativeSceneAccess.GalleryUrl)
            .OnSubmit(() => Fire(_presenter.AddImageUrlAsync));
        var add = style.LabelButton(
                Views.HStack(Views.Image("plus"), Views.Text(text(NativeSceneText.AddImage))),
                () => Fire(_presenter.AddImageUrlAsync), SourceButtonRole.Primary, 12)
            .WithAccessibilityLabel(text(NativeSceneText.AddImage))
            .WithAccessibilityIdentifier(NativeSceneAccess.GalleryAdd);
        _add = add;
        UpdateAddAvailability(style);
        var addRow = Views.HStack(urlField, add).WithFlexibleWidth();
        addRow.Spacing = 8;
        var pick = style.LabelButton(
                Views.HStack(Views.Image("photo.badge.plus"), Views.Text(text(NativeSceneText.ChoosePhoto))),
                OpenPicker, SourceButtonRole.FilledSecondary, 12)
            .WithAccessibilityLabel(text(NativeSceneText.ChoosePhoto))
            .WithAccessibilityIdentifier(NativeSceneAccess.GalleryPickPhoto);
        SourceUiStyle.SetCustomButtonAvailability(pick, appearance, SourceButtonRole.FilledSecondary,
            !gallery.IsSaving);

        var rows = new List<SwiftView> { header, addRow, pick };
        if (!_state.Notices.IsDefaultOrEmpty)
            if (Notices(_state, style, text) is { } notices)
                rows.Add(notices);
        if (gallery.IsLoading)
            rows.Add(Views.IndeterminateProgress(text(NativeSceneText.Loading))
                .WithAccessibilityIdentifier(NativeSceneAccess.GalleryLoading));
        else if (gallery.Items.IsDefaultOrEmpty)
            rows.Add(style.Text(Views.Text(text(NativeSceneText.GalleryEmpty)), SourceTextRole.Body2,
                    SourceTextTone.Secondary)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                .WithAccessibilityIdentifier(NativeSceneAccess.GalleryEmpty));
        else
        {
            var grid = Views.LazyVGrid(style.ViewportWidth < 600 ? 3 : 5,
                gallery.Items.Select(item => GalleryTile(item, gallery.IsSelecting, style, text)).ToArray());
            grid.Spacing = 8;
            rows.Add(grid);
        }
        var stack = Views.VStack(rows.ToArray()).WithPadding(16).WithFlexibleWidth()
            .WithAccessibilityIdentifier(NativeSceneAccess.Gallery);
        stack.Spacing = 12;
        var picker = Views.PhotoPicker(style.Page(Views.Scroll(stack)), OnPhotoPicked, OnPickerCancelled, OnPickerFailed);
        picker.IsPresented = _pickerOpen;
        return picker;
    }

    private SwiftView GalleryTile(NativeSceneGalleryItem item, bool selecting, SourceUiStyle style,
        Func<string, string> text)
    {
        var appearance = style.Appearance;
        SwiftView picture = item.Load switch
        {
            NativeSceneImageLoad.Loaded when item.Bytes is { } bytes =>
                Views.ImageFromBytes(bytes, text(NativeSceneText.SceneImage), 96),
            NativeSceneImageLoad.Failed => Views.Image("photo").WithMinimumHeight(96),
            _ => Views.IndeterminateProgress(text(NativeSceneText.Loading)).WithMinimumHeight(96)
        };
        var parts = new List<SwiftView> { picture.WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center) };
        var marks = new List<SwiftView>();
        if (item.IsCurrent)
            marks.Add(style.Text(Views.Text(text(NativeSceneText.Current)), SourceTextRole.BadgeSmall)
                .WithForegroundColor("#FFFFFF", "#FFFFFF")
                .WithPadding(2, 6, 2, 6)
                .WithRoundedSurface(appearance.Primary.Light, appearance.Primary.Dark, appearance.BootstrapRadius)
                .WithAccessibilityIdentifier($"{item.AccessibilityId}.current"));
        if (selecting)
            marks.Add(Views.Image(item.IsChecked ? "checkmark.circle.fill" : "circle")
                .WithForegroundColor(appearance.Primary.Light, appearance.Primary.Dark)
                .WithAccessibilityIdentifier($"{item.AccessibilityId}.checked"));
        if (marks.Count > 0)
        {
            var markRow = Views.HStack(marks.ToArray());
            markRow.Spacing = 4;
            parts.Add(markRow);
        }
        var tile = Views.VStack(parts.ToArray()).WithPadding(4);
        tile.Spacing = 4;
        var label = item.IsCurrent
            ? $"{text(NativeSceneText.SceneImage)} {item.Number}, {text(NativeSceneText.Current)}"
            : $"{text(NativeSceneText.SceneImage)} {item.Number}";
        var button = Views.Button(item.IsChecked || item.IsCurrent
                    ? style.BorderedCard(tile) : style.Card(tile),
                () => Fire(token => _presenter.TapGalleryItemAsync(item.Id, token)))
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithAccessibilityLabel(label)
            .WithAccessibilitySelected(selecting ? item.IsChecked : item.IsCurrent)
            .WithAccessibilityIdentifier(item.AccessibilityId);
        button.IsEnabled = selecting || !_state.IsGrading;
        return button;
    }

    private void UpdateAddAvailability(SourceUiStyle style)
    {
        if (_add is not null)
            SourceUiStyle.SetCustomButtonAvailability(_add, style.Appearance, SourceButtonRole.Primary,
                _presenter.State.Gallery?.CanAddUrl == true);
    }

    private void OpenPicker()
    {
        if (_state.Gallery is not { IsSaving: false })
            return;
        _pickerOpen = true;
        _host.Publish();
    }

    private void ClosePicker()
    {
        _pickerOpen = false;
        _publishWhilePicking = false;
    }

    private void OnPhotoPicked(SwiftPickedPhoto photo)
    {
        ClosePicker();
        var normalized = NativeScenePhotoNormalizer.Normalize(photo.Data);
        if (normalized is null)
        {
            Fire(token => _presenter.PhotoPickFailedAsync("loadFailed", token));
            return;
        }
        Fire(token => _presenter.AddPhotoAsync(normalized, token));
    }

    private void OnPickerCancelled()
    {
        ClosePicker();
        _host.Publish();
    }

    private void OnPickerFailed(string code)
    {
        ClosePicker();
        Fire(token => _presenter.PhotoPickFailedAsync(code, token));
    }

    public void Dispose()
    {
        _presenter.StateChanged -= OnStateChanged;
        _presenter.Dispose();
    }
}
