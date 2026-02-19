using SentenceStudio.Shared.Models;
using MauiReactor.Shapes;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;

namespace SentenceStudio.Pages.LearningResources;

class VocabularyWordEditorSheetState
{
    public VocabularyWord Word { get; set; } = new();
    public bool IsVisible { get; set; } = false;
    public bool IsNew { get; set; } = true;
}

partial class VocabularyWordEditorSheet : Component<VocabularyWordEditorSheetState>
{
    private readonly Action<VocabularyWord> _onSave;
    private readonly Action _onCancel;

    public VocabularyWordEditorSheet(VocabularyWord word, bool isNew, Action<VocabularyWord> onSave, Action onCancel)
    {
        _onSave = onSave;
        _onCancel = onCancel;

        SetState(s =>
        {
            s.Word = word;
            s.IsNew = isNew;
            s.IsVisible = true;
        });
    }

    public override VisualNode Render()
    {
        var theme = BootstrapTheme.Current;

        return Grid(
            // Semi-transparent background
            BoxView()
                .Background(Colors.Black.WithAlpha(0.5f))
                .ZIndex(0)
                .OnTapped(() => _onCancel?.Invoke()),

            // Editor panel
            Border(
                VStack(
                    Label(State.IsNew ? "Add Vocabulary Word" : "Edit Vocabulary Word")
                        .H5()
                        .FontAttributes(FontAttributes.Bold)
                        .HorizontalTextAlignment(TextAlignment.Center),

                    VStack(
                        Label("Target Language Term")
                            .Class("form-label")
                            .HStart(),
                        Entry()
                            .Text(State.Word.TargetLanguageTerm)
                            .OnTextChanged(text => SetState(s => s.Word.TargetLanguageTerm = text))
                            .Class("form-control")
                    )
                    .Spacing(5),

                    VStack(
                        Label("Native Language Term")
                            .Class("form-label")
                            .HStart(),
                        Entry()
                            .Text(State.Word.NativeLanguageTerm)
                            .OnTextChanged(text => SetState(s => s.Word.NativeLanguageTerm = text))
                            .Class("form-control")
                    )
                    .Spacing(5),

                    HStack(
                        Button("Save")
                            .Class("btn-primary")
                            .OnClicked(SaveWord),

                        Button("Cancel")
                            .Class("btn-outline-secondary")
                            .OnClicked(Cancel)
                    )
                    .Spacing(10)
                )
                .Padding(20)
                .Spacing(15)
            )
            .StrokeShape(new RoundRectangle().CornerRadius(10))
            .StrokeThickness(1)
            .Stroke(theme.GetOutline())
            .BackgroundColor(theme.GetSurface())
            .MaximumWidthRequest(400)
            .WidthRequest(DeviceInfo.Idiom == DeviceIdiom.Phone ? 320 : 400)
            .HCenter()
            .VCenter()
            .ZIndex(1)
        )
        .IsVisible(State.IsVisible);
    }

    void SaveWord()
    {
        if (string.IsNullOrWhiteSpace(State.Word.TargetLanguageTerm) ||
            string.IsNullOrWhiteSpace(State.Word.NativeLanguageTerm))
        {
            _ = IPopupService.Current.PushAsync(new SimpleActionPopup
            {
                Title = "Validation Error",
                Text = "Both target and native terms are required",
                ActionButtonText = "OK",
                ShowSecondaryActionButton = false
            });
            return;
        }

        _onSave?.Invoke(State.Word);
        SetState(s => s.IsVisible = false);
    }

    void Cancel()
    {
        _onCancel?.Invoke();
        SetState(s => s.IsVisible = false);
    }
}