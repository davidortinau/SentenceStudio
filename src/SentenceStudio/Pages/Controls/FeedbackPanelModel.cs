using CommunityToolkit.Mvvm.Input;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.Controls;

public partial class FeedbackPanelModel : ObservableObject
{
    [ObservableProperty]
    private string _feedback;

    [ObservableProperty]
    private string _userInput;

    [RelayCommand]
    void Ask()
    {
        // the idea here is that I can ask for clarification or a question related to the feedback.
        // how might I provide that context?
    }
}