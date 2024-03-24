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

    private ConversationService _conversationService;
    
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
        Chunks = new ObservableCollection<ConversationChunk>();
        _teacherService = service.GetRequiredService<TeacherService>();
        _conversationService = service.GetRequiredService<ConversationService>();
        TaskMonitor.Create(StartConversation);
        
    }

    public async Task StartConversation()
    {
        await Task.Delay(100);
        IsBusy = true; 
        
        // start the convo...pick a random scenario? Saying hello, introducing yourself to a group, ordering a coffee, etc.
        var response = await _conversationService.StartConversation();
        var chunk = new ConversationChunk(DateTime.Now, ConversationParticipant.Bot, response);
        Chunks.Add(chunk);

        IsBusy = false;
            
    }

    public async Task GetReply()
    {
        IsBusy = true; 
        
        // start the convo...pick a random scenario? Saying hello, introducing yourself to a group, ordering a coffee, etc.
        var response = await _conversationService.ContinueConveration(Chunks.ToList());
        var chunk = new ConversationChunk(DateTime.Now, ConversationParticipant.Bot, response);
        // Chunks.Insert(0, chunk);
        Chunks.Add(chunk);

        IsBusy = false;
            
    }

    [RelayCommand]
    public async Task SendMessage()
    {
        if (!string.IsNullOrWhiteSpace(UserInput))
        {
            var chunk = new ConversationChunk(DateTime.Now, ConversationParticipant.Me, UserInput);
            // Chunks.Insert(0, chunk);
            Chunks.Add(chunk);
            
            UserInput = string.Empty;

            // send to the bot for a response
            await Task.Delay(2000);
            await GetReply();
        }
    }
}
