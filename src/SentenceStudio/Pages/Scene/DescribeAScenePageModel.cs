using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Maui.Core;
using SentenceStudio.Services;
using CommunityToolkit.Maui.Alerts;
using Scriban;
using SentenceStudio.Models;
using Sharpnado.Tasks;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.ComponentModel;
using CommunityToolkit.Maui.Views;


namespace SentenceStudio.Pages.Scene;

public partial class DescribeAScenePageModel : BaseViewModel
{
    public LocalizationManager Localize => LocalizationManager.Instance;
    private AiService _aiService;
    private TeacherService _teacherService;
    private IPopupService _popupService;

    private SceneImageService _sceneImageService;
    
    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private string _imageUrl = "https://fdczvxmwwjwpwbeeqcth.supabase.co/storage/v1/object/public/images/239cddf0-4406-4bb7-9326-23511fe938cd/6ed5384c-8025-4395-837c-dd4a73c0a0c1.png";
    
    [ObservableProperty]
    private string _userInput;    
    
    public DescribeAScenePageModel(IServiceProvider service)
    {
        _aiService = service.GetRequiredService<AiService>();
        _teacherService = service.GetRequiredService<TeacherService>();
        _popupService = service.GetRequiredService<IPopupService>();
        _sceneImageService = service.GetRequiredService<SceneImageService>();
        TaskMonitor.Create(LoadScene);
    }

    private async Task LoadScene()
    {
        var image = await _sceneImageService.GetRandomAsync();
        if(image is null)
            return;

        ImageUrl = image.Url;
        Description = image.Description;
    }

    private async Task ShowError()
    {
        ToastDuration duration = ToastDuration.Long;
        double fontSize = 14;
        var toast = Toast.Make("Something went wrong. Check the server.", duration, fontSize);
        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        await toast.Show(cancellationTokenSource.Token);
    }

    static Page MainPage => Shell.Current;

    [RelayCommand]
    async Task GetDescription()
    {
        
        if(string.IsNullOrWhiteSpace(ImageUrl))
            return;

        Description = string.Empty;

        var sceneImage = await _sceneImageService.GetAsync(ImageUrl);
        if (sceneImage != null && !string.IsNullOrEmpty(sceneImage.Description))
        {
            Description = sceneImage.Description;
            return;
        }

        // IsBusy = true;

        var prompt = string.Empty;     
        using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("DescribeThisImage.scriban-txt");
        using (StreamReader reader = new StreamReader(templateStream))
        {
            var template = Template.Parse(reader.ReadToEnd());
            prompt = await template.RenderAsync();
        }
        
        Description = await _aiService.SendImage(ImageUrl, prompt);
        if(sceneImage is null)
        {
            sceneImage = new SceneImage { Url = ImageUrl };
        }
        sceneImage.Description = Description;
        await _sceneImageService.SaveAsync(sceneImage);
    }

    [RelayCommand]
    void ViewDescription()
    {
        TaskMonitor.Create(ShowDescription);
    }

    [ObservableProperty]
    private SelectionMode _selectionMode;

    [ObservableProperty]
    private ObservableCollection<SceneImage> _selectedImages = new ObservableCollection<SceneImage>();

    [RelayCommand]
    void LongPress(SceneImage obj)
    {
        Debug.WriteLine("LongPressed");
        if(SelectionMode == SelectionMode.None)
        {
            SelectionMode = SelectionMode.Multiple;
            SelectedImages.Add(obj);
        }
    }

    private bool CanViewDescription()
    {
        
        return !string.IsNullOrWhiteSpace(Description);
        
    }

    private async Task ShowDescription()
    {
        try{
            await _popupService.ShowPopupAsync<ExplanationViewModel>(onPresenting: viewModel => {
                viewModel.Text = Description;
            });
        }catch(Exception e){
            Debug.WriteLine(e.Message);
        }
    }

    [RelayCommand]
    async Task GradeMyDescription()
    {
        if(string.IsNullOrWhiteSpace(UserInput))
            return;

        if(Sentences is null)
            Sentences = new ObservableCollection<Sentence>();

        var s = new Sentence{
            Answer = UserInput
        };
        Sentences.Add(s);

        UserInput = string.Empty;

        var timeout = TimeSpan.FromSeconds(10);

        var stopwatch = Stopwatch.StartNew();

        while (string.IsNullOrEmpty(Description) && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(100); // Wait for 100 milliseconds before checking again
        }

        if (string.IsNullOrEmpty(Description))
        {
            ToastDuration duration = ToastDuration.Long;
            double fontSize = 14;
            var toast = Toast.Make("Description is still empty.", duration, fontSize);
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
            await toast.Show(cancellationTokenSource.Token);
            return;
        }
        
        var grade = await _teacherService.GradeDescription(s.Answer, Description);
        if(grade is null){
            _ = ShowError();
        }

        s.Accuracy = grade.Accuracy;
        s.Fluency = grade.Fluency;
        s.FluencyExplanation = grade.FluencyExplanation;
        s.AccuracyExplanation = grade.AccuracyExplanation;
        s.RecommendedSentence = grade.GrammarNotes.RecommendedTranslation;
        s.GrammarNotes = grade.GrammarNotes.Explanation;
        
    }

    [ObservableProperty]
    private ObservableCollection<Sentence> _sentences;

    [RelayCommand]
    async Task TranslateInput()
    {
        if(string.IsNullOrWhiteSpace(UserInput))
            return;

        
        var translation = await _teacherService.Translate(UserInput);
        ToastDuration duration = ToastDuration.Long;
        double fontSize = 14;
        var toast = Toast.Make(translation, duration, fontSize);
        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        await toast.Show(cancellationTokenSource.Token);
    }
    
    [RelayCommand]
    void ClearInput()
    {
        UserInput = string.Empty;
    }

    [RelayCommand]
    async Task ShowExplanation(Sentence s)
    {
        string explanation = $"Original: {s.Answer}" + Environment.NewLine + Environment.NewLine;
        explanation += $"Recommended: {s.RecommendedSentence}" + Environment.NewLine + Environment.NewLine;
        explanation += $"Accuracy: {s.AccuracyExplanation}" + Environment.NewLine + Environment.NewLine;
        explanation += $"Fluency: {s.FluencyExplanation}" + Environment.NewLine + Environment.NewLine;
        explanation += $"Additional Notes: {s.GrammarNotes}" + Environment.NewLine + Environment.NewLine;

        try{
            await _popupService.ShowPopupAsync<ExplanationViewModel>(onPresenting: viewModel => {
                viewModel.Text = explanation;
                });
        }catch(Exception e){
            Debug.WriteLine(e.Message);
        }
    }

    [RelayCommand]
    async Task LoadImage()
    {
        // open an alert to access a string input
        await Shell.Current.CurrentPage.DisplayPromptAsync("Enter Image URL", "Please enter the URL of the image you would like to describe.", "OK", "Cancel", "https://example.com/something.jpg").ContinueWith(async (task) =>
        {
            var result = await task;
            if (result != null)
            {
                ImageUrl = result;
                var sceneImage = new SceneImage { Url = ImageUrl };
                Images.Add(sceneImage);
                await _sceneImageService.SaveAsync(sceneImage);
                Sentences.Clear();
                await GetDescription();
            }
        });
    }

    [ObservableProperty]
    private ObservableCollection<SceneImage> _images;

    [RelayCommand]
    async Task ManageImages()
    {
        var imgs = await _sceneImageService.ListAsync();
        Images = new ObservableCollection<SceneImage>(imgs);
        if(DeviceInfo.Idiom == DeviceIdiom.Phone){
            try{
                var gallery = new ImageGalleryBottomSheet(this);        
                await gallery.ShowAsync(Shell.Current.CurrentPage.Window);
            }catch(Exception e){
                Debug.WriteLine(e.Message);
            }
        }else{
            try{
                var gallery = new ImageGalleryPopup(this);        
                await Shell.Current.CurrentPage.ShowPopupAsync(gallery);
            }catch(Exception e){
                Debug.WriteLine(e.Message);
            }
        }
    }

    [RelayCommand]
    async Task SelectImage(SceneImage image)
    {
        if (SelectionMode != SelectionMode.None)
        {             
            if (SelectedImages.Contains(image)){
                SelectedImages.Remove(image);
                image.IsSelected = false;
            }
            else{
                SelectedImages.Add(image);
                image.IsSelected = true;
            }

            Debug.WriteLine($"Added or removed {image.Url} to SelectedImages: {SelectedImages.Count}");
        }
        else
        {
            Debug.WriteLine($"Swap for {image.Url}");
            ImageUrl = image.Url;
            if(string.IsNullOrWhiteSpace(image.Description))
                await GetDescription();
            
            Description = image.Description;
            Sentences?.Clear();
        }

        
    }

    [ObservableProperty]
    private bool _isDeleteVisible;

    [RelayCommand]
    async Task DeleteImages()
    {
        if(SelectedImages.Count == 0)
            return;

        foreach(var img in SelectedImages)
        {
            await _sceneImageService.DeleteAsync(img);
            Images.Remove(img);
        }
        SelectedImages.Clear();
    }

    [ObservableProperty]
    private bool _isSelecting;

    [RelayCommand]
    async Task ToggleSelection()
    {
        if(SelectionMode == SelectionMode.None){
            SelectionMode = SelectionMode.Multiple;
            IsDeleteVisible = true;
            IsSelecting = true;
        }
        else{
            SelectionMode = SelectionMode.None;
            IsDeleteVisible = false;
            IsSelecting = false;
        }
        
        foreach(var img in SelectedImages)
        {
            img.IsSelected = false;
        }
        SelectedImages.Clear();
    }
}
