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

namespace SentenceStudio.Pages.HowDoYouSay;

public partial class HowDoYouSayPageModel : BaseViewModel
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

    [ObservableProperty]
    private string _phrase;

    [ObservableProperty] private Stream _stream;

    [ObservableProperty] 
    private ObservableCollection<StreamHistory> _streamHistory = new();
    

    public HowDoYouSayPageModel(IServiceProvider service)
    {
        _aiService = service.GetRequiredService<AiService>();
    }    

    [RelayCommand]
    async Task Submit()
    {
        IsBusy = true;
        var stream = await _aiService.TextToSpeechAsync(Phrase, "Nova");
        StreamHistory.Insert(0,new StreamHistory { Phrase = Phrase, Stream = stream });
        // await SaveStreamToFile(Stream);
        IsBusy = false;
    }

    

    // private async Task SaveStreamToFile(Stream stream)
    // {
    //     // Create an output filename
    //     string targetFile = System.IO.Path.Combine(FileSystem.Current.AppDataDirectory, $"Story_{_story.ID}.mp3");

    //     // Copy the file to the AppDataDirectory
    //     using FileStream outputStream = File.Create(targetFile);
    //     await stream.CopyToAsync(outputStream);
    // }

    private IAudioPlayer _player;

    [RelayCommand]
    async Task Play(StreamHistory stream)
    {
        Debug.WriteLine($"Playing {stream.Phrase}");
        var player = AudioManager.Current.CreatePlayer(stream.Stream);
        player.PlaybackEnded += (s, e) => stream.Stream.Position = 0;
        player.Play();
                
    }

    
}

public partial class StreamHistory : ObservableObject
{
    public string Phrase { get; set; }
    public Stream Stream { get; set; }

    
}
