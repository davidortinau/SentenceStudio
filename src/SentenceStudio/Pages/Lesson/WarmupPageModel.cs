using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using SentenceStudio.Models;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.Lesson;

public partial class WarmupPageModel : ObservableObject
{
    private TeacherService _teacherService;
    
    [ObservableProperty]
    private string _userInput;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private ObservableCollection<ConversationParticipant> _participants;

    [ObservableProperty] 
    private ObservableCollection<ConversationChunk> _chunks;

    public WarmupPageModel(IServiceProvider service)
    {
        _teacherService = service.GetRequiredService<TeacherService>();
        TaskMonitor.Create(StartConversation);
    }

    public async Task StartConversation()
    {
        await Task.Delay(100);
        IsBusy = true; 
        
        // start the convo...pick a random scenario? Saying hello, introducing yourself to a group, ordering a coffee, etc.

        IsBusy = false;
            
    }

    [RelayCommand]
    public async Task SendMessage()
    {
        if (!string.IsNullOrWhiteSpace(UserInput))
        {
            var chunk = new ConversationChunk(DateTime.Now, ConversationParticipant.Me, UserInput);
            Chunks.Add(chunk);
            UserInput = string.Empty;

            // send to the bot for a response
        }
    }
}
