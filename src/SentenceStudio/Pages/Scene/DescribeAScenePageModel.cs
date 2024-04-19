using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Maui.Core;
using SentenceStudio.Services;
using CommunityToolkit.Maui.Alerts;
using Scriban;
using SentenceStudio.Models;
using Sharpnado.Tasks;
using System.Collections.ObjectModel;


namespace SentenceStudio.Pages.Scene;

public partial class DescribeAScenePageModel : ObservableObject
{
    public LocalizationManager Localize => LocalizationManager.Instance;
    private AiService _aiService;
    private TeacherService _teacherService;
    private IPopupService _popupService;
    
    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private string _imageUrl = "https://fdczvxmwwjwpwbeeqcth.supabase.co/storage/v1/object/public/images/239cddf0-4406-4bb7-9326-23511fe938cd/6ed5384c-8025-4395-837c-dd4a73c0a0c1.png";
    
    [ObservableProperty]
    private string _userInput;

    [ObservableProperty]
    private bool _isBusy;
    
    public DescribeAScenePageModel(IServiceProvider service)
    {
        _aiService = service.GetRequiredService<AiService>();
        _teacherService = service.GetRequiredService<TeacherService>();
        _popupService = service.GetRequiredService<IPopupService>();
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

        var prompt = string.Empty;     
        using Stream templateStream = await FileSystem.OpenAppPackageFileAsync("DescribeThisImage.scriban-txt");
        using (StreamReader reader = new StreamReader(templateStream))
        {
            var template = Template.Parse(reader.ReadToEnd());
            prompt = await template.RenderAsync();
        }
        
        Description = await _aiService.SendImage(ImageUrl, prompt);
        TaskMonitor.Create(ShowDescription);
    }

    [RelayCommand]
    void ViewDescription()
    {
        TaskMonitor.Create(ShowDescription);
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
        
        try{
            string explanation = $"Original: {s.Answer}" + Environment.NewLine + Environment.NewLine;
            explanation += $"Recommended: {s.RecommendedSentence}" + Environment.NewLine + Environment.NewLine;
            explanation += $"Accuracy: {s.AccuracyExplanation}" + Environment.NewLine + Environment.NewLine;
            explanation += $"Fluency: {s.FluencyExplanation}" + Environment.NewLine + Environment.NewLine;
            explanation += $"Additional Notes: {s.GrammarNotes}" + Environment.NewLine + Environment.NewLine;
            
            await _popupService.ShowPopupAsync<ExplanationViewModel>(onPresenting: viewModel => {
                viewModel.Text = explanation;
            });
        }catch(Exception e){
            Debug.WriteLine(e.Message);
        }
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
}
