using MauiReactor.Shapes;
using The49.Maui.BottomSheet;
using System.Collections.Immutable;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.Scene;

class DescribeAScenePageState
{
    public int Id { get; set; }
    public string Description { get; set; }
    public Uri ImageUrl { get; set; } = new Uri("https://fdczvxmwwjwpwbeeqcth.supabase.co/storage/v1/object/public/images/239cddf0-4406-4bb7-9326-23511fe938cd/6ed5384c-8025-4395-837c-dd4a73c0a0c1.png");
    public string UserInput { get; set; }
    public bool IsBusy { get; set; }
    public ImmutableList<Sentence> Sentences { get; set; } = ImmutableList<Sentence>.Empty;
    public ImmutableList<SceneImage> Images { get; set; } = ImmutableList<SceneImage>.Empty;
    public ImmutableList<SceneImage> SelectedImages { get; set; } = ImmutableList<SceneImage>.Empty;
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
    [Inject] TeacherService _teacherService; // still used for grading
    [Inject] TranslationService _translationService; // added for translation
    [Inject] SceneImageService _sceneImageService;
    [Inject] UserActivityRepository _userActivityRepository;
    LocalizationManager _localize => LocalizationManager.Instance;
    CommunityToolkit.Maui.Views.Popup? _popup;

    public override VisualNode Render()
    {
        return ContentPage($"{_localize["DescribeAScene"]}",
            ToolbarItem()
                .IconImageSource(SegoeFluentIcons.Info.ToImageSource())
                .OnClicked(ViewDescription),

            ToolbarItem()
                .IconImageSource(SegoeFluentIcons.ImageExport.ToImageSource())
                .OnClicked(LoadImage),

            ToolbarItem()
                .IconImageSource(SegoeFluentIcons.SwitchApps.ToImageSource())
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

    VisualNode RenderMainContent() => Grid("","*,*",
            Grid(
                Image()
                    .Source(State.ImageUrl)
                    .Aspect(Aspect.AspectFit)
                    .HorizontalOptions(LayoutOptions.Fill)
                    .VerticalOptions(LayoutOptions.Start)
                    .Margin(ApplicationTheme.Size160)
            ).GridColumn(0),

            CollectionView()
                .ItemsSource(State.Sentences, RenderSentence)
                .Header(
                    ContentView(
                        Label($"{_localize["ISee"]}")
                            .Padding(ApplicationTheme.Size160)
                    )
                )
                .GridColumn(1)
        )
        .GridRow(1);

    VisualNode RenderSentence(Sentence sentence) => VStack(spacing: 2,
            Label(sentence.Answer)
                .FontSize(18),
            Label($"Accuracy: {sentence.Accuracy}")
                .FontSize(12)
        )
        .Padding(ApplicationTheme.Size160)
        .OnTapped(() => ShowExplanation(sentence));

    VisualNode RenderInput() => new SfTextInputLayout(
            Entry()
                .Text(State.UserInput)
                .OnTextChanged((s, e) => SetState(s => s.UserInput = e.NewTextValue))
                .ReturnType(ReturnType.Next)
                .OnCompleted(GradeMyDescription)
                .GridColumn(0)
                .FontSize(18)
        )
        .Hint($"{_localize["WhatDoYouSee"]}")
        .TrailingView(
            HStack(
                Button()
                    .Background(Colors.Transparent)
                    .ImageSource(SegoeFluentIcons.LanguageKor.ToImageSource())
                    .OnClicked(TranslateInput),

                Button()
                    .Background(Colors.Transparent)
                    .ImageSource(SegoeFluentIcons.EraseTool.ToImageSource())
                    .OnClicked(ClearInput)
            ).Spacing(ApplicationTheme.Size40).HStart()
        )
        .GridRow(2)
        .Margin(ApplicationTheme.Size160);

    VisualNode RenderExplanationPopup() => new PopupHost(r => _popup = r)
        {
            VStack(spacing: 10,
                Label()
                    .Text(State.Description),
                Button("Close", () => {
                    SetState(s => s.IsExplanationShown = false);
                    _ = _popup?.CloseAsync();
                })
            ).Padding(20)
            .BackgroundColor(ApplicationTheme.LightBackground)
        }
        .IsShown(State.IsExplanationShown);

    VisualNode RenderGalleryPopup() => new PopupHost(r => _popup = r)
        {
            Grid("Auto,*,Auto", "",
                RenderHeader(),
                RenderGallery(),
                Button("Close")
                    .OnClicked(() => _ = _popup.CloseAsync())
                    .GridRow(2)
            )
                .Padding(ApplicationTheme.Size240)
                .RowSpacing(ApplicationTheme.Size120)
                .Margin(ApplicationTheme.Size240),
        }
        .IsShown(State.IsGalleryVisible && DeviceInfo.Idiom != DeviceIdiom.Phone);

    VisualNode RenderHeader() => Grid(
            Label("Choose an image")
                .ThemeKey(ApplicationTheme.Title1)
                .HStart(),

            HStack(spacing: ApplicationTheme.Size60,
                Button()
                    .ImageSource(SegoeFluentIcons.ImageExport.ToImageSource())
                    .Background(Colors.Transparent)
                    .Padding(0)
                    .Margin(0)
                    .VCenter()
                    .IsVisible(!State.IsDeleteVisible),

                Button()
                    .ImageSource(SegoeFluentIcons.CheckboxCompositeReversed.ToImageSource())
                    .Background(Colors.Transparent)
                    .Padding(0)
                    .Margin(0)
                    .VCenter(),

                Button()
                    .ImageSource(SegoeFluentIcons.Delete.ToImageSource())
                    .Background(Colors.Transparent)
                    .TextColor(Colors.Black)
                    .Padding(0)
                    .Margin(0)
                    .VCenter()
                    .IsVisible(State.IsDeleteVisible)
            )
            .HEnd()
        );

    VisualNode RenderGallery() => CollectionView()
            .ItemsSource(State.Images, RenderGalleryItem)
            .SelectionMode(State.SelectionMode)
            .SelectedItems(State.SelectedImages.Cast<object>().ToList())
            .ItemsLayout(
                new HorizontalGridItemsLayout(4)
                    .VerticalItemSpacing(ApplicationTheme.Size240)
                    .HorizontalItemSpacing(ApplicationTheme.Size240)
            )
            .GridRow(1);
    

    VisualNode RenderGalleryItem(SceneImage image) => Grid(
            Image()
                .Source(new Uri(image.Url))
                .Aspect(Aspect.AspectFit)
                .HeightRequest(100)
                .OnTapped(() => OnImageSelected(image)),

            Image()
                .Source(SegoeFluentIcons.Checkbox.ToImageSource())
                .VEnd()
                .HEnd()
                .IsVisible(State.IsSelecting)
                .Margin(4),

            Image()
                .Source(SegoeFluentIcons.CheckboxCompositeReversed.ToImageSource())
                .VEnd()
                .HEnd()
                .IsVisible(image.IsSelected)
                .Margin(4)
        );

    VisualNode RenderLoadingOverlay() => Grid(
            Label("Analyzing the image...")
                .FontSize(64)
                .TextColor(ApplicationTheme.DarkOnLightBackground)
                .Center()
        )
        .BackgroundColor(Color.FromArgb("#80000000"))
        .IsVisible(State.IsBusy)
        .GridRowSpan(3);

    // Event handlers and other methods...
    async Task LoadScene()
    {
        SetState(s => s.IsBusy = true);

        try
        {
            var image = await _sceneImageService.GetRandomAsync();
            if (image != null)
            {
                SetState(s =>
                {
                    s.Id = image.Id;
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

    async Task ManageImages()
    {
        var imgs = await _sceneImageService.ListAsync();
        SetState(s => s.Images = ImmutableList.CreateRange(imgs));

        if (DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            await BottomSheetManager.ShowAsync(
                () => new ImageGalleryBottomSheet()
                    .State(State),
                sheet =>
                {
                    sheet.HasBackdrop = true;
                    sheet.HasHandle = true;
                    sheet.CornerRadius = ApplicationTheme.Size120;
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

    async Task ViewDescription()
    {
        SetState(s => s.IsExplanationShown = true);
    }

    void ShowExplanation(Sentence sentence)
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

    async Task GradeMyDescription()
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
            
            // Track user activity
            await _userActivityRepository.SaveAsync(new UserActivity
            {
                Activity = SentenceStudio.Shared.Models.Activity.SceneDescription.ToString(),
                Input = State.UserInput,
                Accuracy = grade.Accuracy,
                Fluency = grade.Fluency,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            });
            
            SetState(s =>
            {
                s.UserInput = string.Empty;
                s.Sentences = s.Sentences.Insert(0, sentence);
            });
        }
        finally
        {
            SetState(s => s.IsBusy = false);
        }
    }

    async Task TranslateInput()
    {
        if (string.IsNullOrWhiteSpace(State.UserInput)) return;

        SetState(s => s.IsBusy = true);
        try
        {
            var translation = await _translationService.TranslateAsync(State.UserInput);
            await AppShell.DisplayToastAsync(translation);
            SetState(s => {
                s.Sentences = s.Sentences.Insert(0, new Sentence { Answer = translation, Accuracy = 100 });
                s.UserInput = string.Empty;
            });
        }
        finally
        {
            SetState(s => s.IsBusy = false);
        }
    }

    void ClearInput()
    {
        SetState(s => s.UserInput = string.Empty);
    }

    async Task GetDescription()
    {
        SetState(s => s.IsBusy = true);
        try
        {
            var prompt = string.Empty;
            using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("DescribeThisImage.scriban-txt");
            using (StreamReader reader = new StreamReader(templateStream))
            {
                var template = Template.Parse(await reader.ReadToEndAsync());
                prompt = await template.RenderAsync();
            }

            var description = await _aiService.SendImage(State.ImageUrl.AbsoluteUri, prompt);
            SetState(s => s.Description = description);
            
            await _sceneImageService.SaveAsync(new SceneImage
            {
                Id = State.Id,
                Url = State.ImageUrl.AbsoluteUri,
                Description = State.Description
            });
        }
        finally
        {
            SetState(s => s.IsBusy = false);
        }
    }

    [RelayCommand]
    async Task LoadImage()
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
                s.Sentences = ImmutableList<Sentence>.Empty;
            });
            var sceneImage = new SceneImage { Url = result };
            SetState(s => s.Images = s.Images.Add(sceneImage));
            await _sceneImageService.SaveAsync(sceneImage);
            await GetDescription();
        }
    }

    async Task OnImageSelected(SceneImage image)
    {
        if (State.SelectionMode != SelectionMode.None)
        {             
            SetState(s => {
                if (s.SelectedImages.Contains(image))
                {
                    s.SelectedImages = s.SelectedImages.Remove(image);
                    image.IsSelected = false;
                }
                else
                {
                    s.SelectedImages = s.SelectedImages.Add(image);
                    image.IsSelected = true;
                }
            });
        }
        else
        {
            SetState(s => 
            {
                s.ImageUrl = new Uri(image.Url);
                s.Description = image.Description;
                s.Sentences = ImmutableList<Sentence>.Empty;
            });
            
            if(string.IsNullOrWhiteSpace(image.Description))
                await GetDescription();
        }
    }

    void ToggleSelection()
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
            s.SelectedImages = ImmutableList<SceneImage>.Empty;
        });
    }

    async Task DeleteImages()
    {
        if(State.SelectedImages.Count == 0)
            return;

        foreach(var img in State.SelectedImages)
        {
            await _sceneImageService.DeleteAsync(img);
            SetState(s => s.Images = s.Images.Remove(img));
        }
        SetState(s => s.SelectedImages = ImmutableList<SceneImage>.Empty);
    }    Task ShowError()
    {
        return Application.Current.MainPage.DisplayAlert(
            "Error",
            "Something went wrong. Check the server.",
            "OK"
        );
    }
}