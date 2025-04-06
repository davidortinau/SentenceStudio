using SentenceStudio.Models;
using MauiReactor.Shapes;

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
        
        SetState(s => {
            s.Word = word;
            s.IsNew = isNew;
            s.IsVisible = true;
        });
    }
    
    public override VisualNode Render()
    {
        return Grid(
            // Semi-transparent background
            BoxView()
                .BackgroundColor(Colors.Black.WithAlpha(0.5f))
                .ZIndex(0)
                .OnTapped(() => _onCancel?.Invoke()),
                
            // Editor panel
            Border(
                VStack(
                    Label(State.IsNew ? "Add Vocabulary Word" : "Edit Vocabulary Word")
                        .FontSize(20)
                        .FontAttributes(FontAttributes.Bold)
                        .HorizontalTextAlignment(TextAlignment.Center),
                        
                    VStack(
                        Label("Target Language Term")
                            .FontAttributes(FontAttributes.Bold)
                            .HStart(),
                        Border(
                            Entry()
                                .Text(State.Word.TargetLanguageTerm)
                                .OnTextChanged(text => SetState(s => s.Word.TargetLanguageTerm = text))
                        )
                        .Style((Style)Application.Current.Resources["InputWrapper"])
                    )
                    .Spacing(5),
                    
                    VStack(
                        Label("Native Language Term")
                            .FontAttributes(FontAttributes.Bold)
                            .HStart(),
                        Border(
                            Entry()
                                .Text(State.Word.NativeLanguageTerm)
                                .OnTextChanged(text => SetState(s => s.Word.NativeLanguageTerm = text))
                        )
                        .Style((Style)Application.Current.Resources["InputWrapper"])
                    )
                    .Spacing(5),
                    
                    HStack(
                        Button("Save")
                            .BackgroundColor(Colors.Green)
                            .TextColor(Colors.White)
                            .OnClicked(SaveWord),
                            
                        Button("Cancel")
                            .BackgroundColor(Colors.Gray)
                            .TextColor(Colors.White)
                            .OnClicked(Cancel)
                    )
                    .Spacing(10)
                )
                .Padding(20)
                .Spacing(15)
            )
            .StrokeShape(new RoundRectangle().CornerRadius(10))
            .StrokeThickness(1)
            .Stroke(Colors.LightGray)
            .BackgroundColor(Colors.White)
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
            App.Current.MainPage.DisplayAlert("Validation Error", "Both target and native terms are required", "OK");
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