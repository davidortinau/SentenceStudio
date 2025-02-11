using MauiReactor.Shapes;
using System.Collections.ObjectModel;
using SentenceStudio.Resources.Styles;
using The49.Maui.BottomSheet;

namespace SentenceStudio.Pages.Scene;

class DescribeAScenePageState
{
    public string Description { get; set; }
    public Uri ImageUrl { get; set; } = new Uri("https://fdczvxmwwjwpwbeeqcth.supabase.co/storage/v1/object/public/images/239cddf0-4406-4bb7-9326-23511fe938cd/6ed5384c-8025-4395-837c-dd4a73c0a0c1.png");
    public string UserInput { get; set; }
    public bool IsBusy { get; set; }
    public ObservableCollection<Sentence> Sentences { get; set; } = new();
    public ObservableCollection<SceneImage> Images { get; set; } = new();
    public ObservableCollection<SceneImage> SelectedImages { get; set; } = new();
    public SelectionMode SelectionMode { get; set; }
    public bool IsDeleteVisible { get; set; }
    public bool IsSelecting { get; set; }
    public bool IsExplanationShown { get; set; }
    public string ExplanationText { get; set; }
    public bool IsGalleryVisible { get; set; }
}

partial class DescribeAScenePage : Component<DescribeAScenePageState>
{
    [Inject] AiService _aiService;
    [Inject] TeacherService _teacherService;
    [Inject] SceneImageService _sceneImageService;
    LocalizationManager _localize => LocalizationManager.Instance;

    public override VisualNode Render()
    {
        return ContentPage($"{_localize["DescribeAScene"]}",
            ToolbarItem()
                .IconImageSource(SegoeFluentIcons.Info.ToImageSource())
                        // .AppThemeColorBinding(FontImageSource.ColorProperty,
                        //     (Color)Application.Current.Resources["DarkOnLightBackground"],
                        //     (Color)Application.Current.Resources["LightOnDarkBackground"]))
                .OnClicked(ViewDescription),

            ToolbarItem()
                .IconImageSource(SegoeFluentIcons.ImageExport.ToImageSource())
                .OnClicked(LoadImage),

            ToolbarItem()
                .IconImageSource(SegoeFluentIcons.SwitchApps.ToImageSource())
                        // .AppThemeColorBinding(FontImageSource.ColorProperty,
                        //     (Color)Application.Current.Resources["DarkOnLightBackground"],
                        //     (Color)Application.Current.Resources["LightOnDarkBackground"]))
                .OnClicked(ManageImages),
            Grid("Auto,*,Auto", "*",
                RenderMainContent(),
                RenderInput(),
                RenderExplanationPopup(),
                RenderGalleryPopup(),
                RenderLoadingOverlay()
            )
        ).OnAppearing(LoadScene);
    }

    private VisualNode RenderMainContent()
    {
        return Grid("","*,*",
            Grid(
                Image()
                    .Source(State.ImageUrl)
                    .Aspect(Aspect.AspectFit)
                    .HorizontalOptions(LayoutOptions.Fill)
                    .VerticalOptions(LayoutOptions.Start)
                    .Margin(ApplicationTheme.Size160)
                    .Background(Colors.BlueViolet)
            ).GridColumn(0),

            CollectionView()
                .ItemsSource(State.Sentences, RenderSentence)
                .Header(
                    ContentView(
                        Label($"{_localize["ISee"]}")
                            .Padding((Double)Application.Current.Resources["size160"])
                    )
                )
                .GridColumn(1)
        ).Background(Colors.LightBlue)
        .GridRow(1);
    }

    private VisualNode RenderSentence(Sentence sentence)
    {
        return VStack(spacing: 2,
            Label(sentence.Answer)
                .FontSize(18),
            Label($"Accuracy: {sentence.Accuracy}")
                .FontSize(12)
        )
        .Padding((Double)Application.Current.Resources["size160"])
        .OnTapped(() => ShowExplanation(sentence));
    }

    private VisualNode RenderInput()
    {
        return Grid(
                Grid("","*,Auto",
                    Border(
                        Entry()
                            .Text(State.UserInput)
                            .Placeholder($"{_localize["WhatDoYouSee"]}")
                            .OnTextChanged((s, e) => SetState(s => s.UserInput = e.NewTextValue))
                            .ReturnType(ReturnType.Next)
                            .OnCompleted(GradeMyDescription)
                            .GridColumn(0)
                            .FontSize(18)
		            )
                    .GridColumn(0)
                    .Background(Colors.Transparent)
                    .Stroke(ApplicationTheme.Gray300)
                    .StrokeShape(new RoundRectangle().CornerRadius(4))
                    .StrokeThickness(1),                   

                    Button()
                        .Background(Colors.Transparent)
                        .ImageSource(SegoeFluentIcons.LanguageKor.ToImageSource())
                        .OnClicked(TranslateInput)
                        .GridColumn(1),

                    Button()
                        .Background(Colors.Transparent)
                        .ImageSource(SegoeFluentIcons.EraseTool.ToImageSource())
                        .OnClicked(ClearInput)
                        .GridColumn(0)
                        .HEnd()
                )
                .ColumnSpacing(2)
            )
            
        // )
        .GridRow(2)
        .Margin(ApplicationTheme.Size160);
    }

    private CommunityToolkit.Maui.Views.Popup? _popup;

    private VisualNode RenderExplanationPopup()
    {
        return new PopupHost(r => _popup = r)
        {
            VStack(spacing: 10,
                Label(State.Description),
                Button("Close", () => {
                    SetState(s => s.IsExplanationShown = false);
                    _popup?.Close();
                })
            ).Padding(20)
            .BackgroundColor((Color)Application.Current.Resources["LightBackground"])
        }
        .IsShown(State.IsExplanationShown);
    }

    private VisualNode RenderGalleryPopup()
    {
        return new ImageGalleryPopup()
            .State(State)
            .OnClose(async result => 
            {
                SetState(s => s.IsGalleryVisible = false);
                if (result)
                {
                    await LoadScene();
                }
            })
            .IsShown(State.IsGalleryVisible && DeviceInfo.Idiom != DeviceIdiom.Phone);
    }

    private VisualNode RenderLoadingOverlay()
    {
        return Grid(
            Label("Analyzing the image...")
                .FontSize(64)
                .TextColor((Color)Application.Current.Resources["DarkOnLightBackground"])
                .Center()
        )
        .BackgroundColor(Color.FromArgb("#80000000"))
        .IsVisible(State.IsBusy)
        .GridRowSpan(3);
    }

    // Event handlers and other methods...
    private async Task LoadScene()
    {
        SetState(s => s.IsBusy = true);

        try
        {
            var image = await _sceneImageService.GetRandomAsync();
            if (image != null)
            {
                SetState(s =>
                {
                    s.ImageUrl = new Uri(image.Url);
                    s.Description = image.Description;
                });

                if (string.IsNullOrWhiteSpace(State.Description))
                {
                    await GetDescription();
                }
            }
        }
        finally
        {
            SetState(s => s.IsBusy = false);
        }
    }

    private async Task ManageImages()
    {
        var imgs = await _sceneImageService.ListAsync();
        SetState(s => s.Images = new ObservableCollection<SceneImage>(imgs));

        if (DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            await BottomSheetManager.ShowAsync(
                () => new ImageGalleryBottomSheet()
                    .State(State),
                sheet =>
                {
                    sheet.HasBackdrop = true;
                    sheet.HasHandle = true;
                    sheet.CornerRadius = (Double)Application.Current.Resources["size120"];
                    sheet.Detents = new Detent[]
                    {
                        new FullscreenDetent(),
                        new MediumDetent()
                    };
                }
            );
        }
        else
        {
            SetState(s => s.IsGalleryVisible = true);
        }
    }

    private async void ViewDescription()
    {
        SetState(s => s.IsExplanationShown = true);
    }

    private void ShowExplanation(Sentence sentence)
    {
        SetState(s => 
        {
            s.ExplanationText = $"Original: {sentence.Answer}\n\n" +
                               $"Recommended: {sentence.RecommendedSentence}\n\n" +
                               $"Accuracy: {sentence.AccuracyExplanation}\n\n" +
                               $"Fluency: {sentence.FluencyExplanation}\n\n" +
                               $"Grammar Notes: {sentence.GrammarNotes}";
            s.IsExplanationShown = true;
        });
    }

    private async void GradeMyDescription()
    {
        if (string.IsNullOrWhiteSpace(State.UserInput)) return;

        SetState(s => s.IsBusy = true);
        try
        {
            var grade = await _teacherService.GradeDescription(State.UserInput, State.Description);
            var sentence = new Sentence 
            { 
                Answer = State.UserInput,
                Accuracy = grade.Accuracy,
                Fluency = grade.Fluency,
                FluencyExplanation = grade.FluencyExplanation,
                AccuracyExplanation = grade.AccuracyExplanation,
                RecommendedSentence = grade.GrammarNotes.RecommendedTranslation,
                GrammarNotes = grade.GrammarNotes.Explanation
            };
            // State.Sentences.Insert(0, sentence);
            SetState(s =>
            {
                s.UserInput = string.Empty;
                s.Sentences.Insert(0, sentence);
            });
        }
        finally
        {
            SetState(s => s.IsBusy = false);
        }
    }

    private async void TranslateInput()
    {
        if (string.IsNullOrWhiteSpace(State.UserInput)) return;

        SetState(s => s.IsBusy = true);
        try
        {
            var translation = await _teacherService.Translate(State.UserInput);
            State.Sentences.Insert(0, new Sentence { Answer = translation, Accuracy = 100 });
            SetState(s => s.UserInput = string.Empty);
        }
        finally
        {
            SetState(s => s.IsBusy = false);
        }
    }

    private void ClearInput()
    {
        SetState(s => s.UserInput = string.Empty);
    }

    private async Task GetDescription()
    {
        SetState(s => s.IsBusy = true);
        try
        {
            var prompt = string.Empty;     
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("DescribeThisImage.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(reader.ReadToEnd());
                prompt = await template.RenderAsync();
            }
            
            var description = await _aiService.SendImage(State.ImageUrl.AbsolutePath, prompt);
            SetState(s => s.Description = description);
        }
        finally
        {
            SetState(s => s.IsBusy = false);
        }
    }

    [RelayCommand]
    private async Task LoadImage()
    {
        var result = await Application.Current.MainPage.DisplayPromptAsync(
            "Enter Image URL", 
            "Please enter the URL of the image you would like to describe.", 
            "OK", 
            "Cancel", 
            "https://example.com/something.jpg"
        );
        
        if (result != null)
        {
            SetState(s => 
            {
                s.ImageUrl = new Uri(result);
                s.Sentences?.Clear();
            });
            var sceneImage = new SceneImage { Url = result };
            State.Images.Add(sceneImage);
            await _sceneImageService.SaveAsync(sceneImage);
            await GetDescription();
        }
    }

    private async void OnImageSelected(SceneImage image)
    {
        if (State.SelectionMode != SelectionMode.None)
        {             
            if (State.SelectedImages.Contains(image))
            {
                State.SelectedImages.Remove(image);
                image.IsSelected = false;
            }
            else
            {
                State.SelectedImages.Add(image);
                image.IsSelected = true;
            }
        }
        else
        {
            SetState(s => 
            {
                s.ImageUrl = new Uri(image.Url);
                s.Description = image.Description;
                s.Sentences?.Clear();
            });
            
            if(string.IsNullOrWhiteSpace(image.Description))
                await GetDescription();
        }
    }

    private void ToggleSelection()
    {
        SetState(s => 
        {
            s.SelectionMode = s.SelectionMode == SelectionMode.None ? 
                SelectionMode.Multiple : SelectionMode.None;
            s.IsDeleteVisible = s.SelectionMode != SelectionMode.None;
            s.IsSelecting = s.SelectionMode != SelectionMode.None;
            
            foreach(var img in s.SelectedImages)
            {
                img.IsSelected = false;
            }
            s.SelectedImages.Clear();
        });
    }

    private async Task DeleteImages()
    {
        if(State.SelectedImages.Count == 0)
            return;

        foreach(var img in State.SelectedImages)
        {
            await _sceneImageService.DeleteAsync(img);
            State.Images.Remove(img);
        }
        SetState(s => s.SelectedImages.Clear());
    }

    private async Task ShowError()
    {
        await Application.Current.MainPage.DisplayAlert(
            "Error",
            "Something went wrong. Check the server.",
            "OK"
        );
    }

    // Add other methods converted from the ViewModel...
    // (ViewDescription, GradeMyDescription, TranslateInput, etc.)
}