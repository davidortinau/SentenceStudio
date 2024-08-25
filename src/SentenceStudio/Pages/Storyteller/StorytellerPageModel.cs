using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Media;
using SentenceStudio.Data;
using Plugin.Maui.Audio;

namespace SentenceStudio.Pages.Storyteller;

public partial class StorytellerPageModel : BaseViewModel, IQueryAttributable
{
    ///* <summary>
    /// * Store stories and save the audio
    /// * Add a media player with a scrub bar and a pause button
    /// * Possible to visually indicate progress while reading on the words?
    /// * Hover or some gesture to get a definition.
    /// * Move questions to a bottom drawer or similar, with a button to start the quiz, and a button to grade the quiz. 
    /// * Necessary or useful to have AI score the quiz?
    /// </summary>    

    private AiService _aiService;
    private readonly StorytellerService _storytellerService;
    private readonly UserActivityRepository _userActivityRepository;

    [ObservableProperty]
    private string _body;

    [ObservableProperty]
    private ObservableCollection<Question> _questions;
    
    public int ListID { get; set; }
    public int SkillProfileID { get; set; }
    
    public StorytellerPageModel(IServiceProvider service)
    {
        _aiService = service.GetRequiredService<AiService>();
        _storytellerService = service.GetRequiredService<StorytellerService>();
        _userActivityRepository = service.GetRequiredService<UserActivityRepository>();
    }    

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        ListID = (int)query["listID"];
        SkillProfileID = query.ContainsKey("skillProfileID") ? (int)query["skillProfileID"] : 1;

        TaskMonitor.Create(LoadStoryAsync);
    }

    private async Task LoadStoryAsync()
    {
        IsBusy = true;
        
        var story = await _storytellerService.TellAStory(ListID, 10, SkillProfileID);
                
        Body = story.Body;

        Questions = new ObservableCollection<Question>(story.Questions);

        IsBusy = false;        
    }

    private Stream _myStream;

    private IAudioPlayer _audioPlayer;

    [RelayCommand]
    async Task Play()
    {
        if(_myStream is null)
        {
            _myStream = await _aiService.TextToSpeechAsync(Body, "Nova");
        }   

        if(_audioPlayer is null)
        {
            _audioPlayer = AudioManager.Current.CreatePlayer(_myStream);
        }     
            
        _audioPlayer.Play();
    }
}