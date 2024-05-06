using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Mvvm.Input;
using SentenceStudio.Models;
using SentenceStudio.Pages.Controls;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.Lesson;

public partial class WarmupPageModel : BaseViewModel
{
    private TeacherService _teacherService;

    private ConversationService _conversationService;

    private IPopupService _popupService;
    
    [ObservableProperty]
    private string _userInput;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty] 
    private ObservableCollection<ConversationChunk> _chunks;

    private Conversation _conversation;

    public WarmupPageModel(IServiceProvider service)
    {
        Chunks = new ObservableCollection<ConversationChunk>();
        _teacherService = service.GetRequiredService<TeacherService>();
        _conversationService = service.GetRequiredService<ConversationService>();
        _popupService = service.GetRequiredService<IPopupService>();
        TaskMonitor.Create(ResumeConversation);
        
    }

    private async Task ResumeConversation()
    {
        _conversation = await _conversationService.ResumeConversation();

        if (_conversation == null || !_conversation.Chunks.Any())
        {
            await StartConversation();
            return;
        }

        foreach (var chunk in _conversation.Chunks)
        {
            Chunks.Add(chunk);
        }
    }

    public async Task StartConversation()
    {
        await Task.Delay(100);
        IsBusy = true; 

        _conversation = new Conversation();
        await _conversationService.SaveConversation(_conversation);

        var chunk = new ConversationChunk(_conversation.ID, DateTime.Now, ConversationParticipant.Bot.FirstName, "...");
        Chunks.Add(chunk);

        await Task.Delay(1000);
        
        // var response = await _conversationService.StartConversation();
        // chunk.Text = response;
        chunk.Text = "안녕하세요. 이름이 워예요?";

        await _conversationService.SaveConversationChunk(chunk);

        IsBusy = false;
            
    }

    public async Task GetReply()
    {
        IsBusy = true; 

        var chunk = new ConversationChunk(_conversation.ID, DateTime.Now, ConversationParticipant.Bot.FirstName, "...");
        Chunks.Add(chunk);

        // start the convo...pick a random scenario? Saying hello, introducing yourself to a group, ordering a coffee, etc.
        Reply response = await _conversationService.ContinueConveration(Chunks.ToList());
        chunk.Text = response.Message;

        var previousChunk = Chunks[Chunks.Count - 2];
        previousChunk.Comprehension = response.Comprehension;
        previousChunk.ComprehensionNotes = response.ComprehensionNotes;

        await _conversationService.SaveConversationChunk(previousChunk);
        await _conversationService.SaveConversationChunk(chunk);
        
        IsBusy = false;            
    }

    [RelayCommand]
    public async Task SendMessage()
    {
        
        if (!string.IsNullOrWhiteSpace(UserInput))
        {
            var chunk = new ConversationChunk(
                _conversation.ID,
                DateTime.Now, 
                $"{ConversationParticipant.Me.FirstName} {ConversationParticipant.Me.LastName}", 
                UserInput
            );
            
            Chunks.Add(chunk);
            await _conversationService.SaveConversationChunk(chunk);
            
            UserInput = string.Empty;

            // send to the bot for a response
            await Task.Delay(2000);
            await GetReply();
        }
    }

    [RelayCommand]
    async Task NewConversation()
    {
        Chunks.Clear();
        await StartConversation();
    }

    [RelayCommand]
    async Task GetPhrase()
    {
        
        var result = await _popupService.ShowPopupAsync<PhraseClipboardViewModel>(CancellationToken.None);
        if(result is string phrase)
        {
            UserInput = phrase;
        }
    }

    [RelayCommand]
    async Task ShowExplanation(ConversationChunk s)
    {
        string explanation = $"Comprehension Score: {s.Comprehension}" + Environment.NewLine + Environment.NewLine;
        explanation += $"{s.ComprehensionNotes}" + Environment.NewLine + Environment.NewLine;
        
        try{
            await _popupService.ShowPopupAsync<ExplanationViewModel>(onPresenting: viewModel => {
                viewModel.Text = explanation;
                });
        }catch(Exception e){
            Debug.WriteLine(e.Message);
        }
    }
}
