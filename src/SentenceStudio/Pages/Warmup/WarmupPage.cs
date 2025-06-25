using MauiReactor.Shapes;
using Plugin.Maui.Audio;
using System.Collections.ObjectModel;
using SentenceStudio.Pages.Controls;

namespace SentenceStudio.Pages.Warmup;

class WarmupPageState
{
    public ObservableCollection<ConversationChunk> Chunks { get; set; } = new();
    public string UserInput { get; set; }
    public bool IsBusy { get; set; }

    public bool IsExplanationShown { get; set; }

    public bool IsPhraseListShown { get; set; }

    public bool? PopupResult { get; set; }

    public string Explanation { get; set; }
}

partial class WarmupPage : Component<WarmupPageState>
{    [Inject] TeacherService _teacherService;
    [Inject] ConversationService _conversationService;
    [Inject] ElevenLabsSpeechService _speechService;
    LocalizationManager _localize => LocalizationManager.Instance;
    Conversation _conversation;
    
    private IAudioPlayer _audioPlayer;
    private FloatingAudioPlayer _floatingPlayer;
    private IDispatcherTimer _playbackTimer;

    string[] phrases = new[]
        {
            "이거 한국어로 뭐예요?",
            "더 자세히 설명해 주세요.",
            "잘 알겠어요.",
            "잘 이해했어요.",
            "다시 한 번 말해 주세요.",
            "한국어 조금밖에 안 해요.",
            "도와주셔서 감사합니다.",
            "한국어로 말해 주세요.",
            "한국어로 쓰세요.",
            "한국어로 번역해 주세요."
        };    public override VisualNode Render()
    {
        return ContentPage("Warmup",
            ToolbarItem($"{_localize["New Conversation"]}").OnClicked(StartNewConversation),
            Grid(rows: "*, Auto", "*",
                RenderMessageScroll(),
                RenderInput(),
                RenderExplanationPopup(),
                RenderPhrasesPopup(),
                CreateFloatingAudioPlayer()
            )
        ).OnAppearing(ResumeConversation);
    }
    
    VisualNode CreateFloatingAudioPlayer() =>
        _floatingPlayer = new FloatingAudioPlayer(
            _audioPlayer,
            onPlay: ResumeAudio,
            onPause: PauseAudio,
            onRewind: RewindAudio,
            onStop: StopAudio
        );

    VisualNode RenderMessageScroll() =>
        ScrollView(
            VStack(
                State.Chunks.Select(c => RenderChunk(c)).ToArray()
            )
            .Spacing(15)
        );

    VisualNode RenderExplanationPopup() =>
        new SfBottomSheet(
            Grid("*", "*",
                VStack(spacing: 10,
                    Label(State.Explanation),

                    Button("Close")
                        .OnClicked(() => SetState(s => {
                            s.IsExplanationShown = false;
                            s.PopupResult = false;
                        }))
                )
                .BackgroundColor(ApplicationTheme.LightBackground)
                .Padding(20)
            )
        )
        .GridRowSpan(2)       
        .IsOpen(State.IsExplanationShown);

    VisualNode RenderPhrasesPopup() =>
        new SfBottomSheet(
            Grid("*,Auto", "*",
                ScrollView(
                    VStack(
                        phrases.Select(text =>
                            Label()
                            .Text(text)
                            .OnTapped((sender, args) => {
                                SetState(s =>
                                {
                                    s.UserInput = (sender as Microsoft.Maui.Controls.Label)?.Text;
                                    s.IsPhraseListShown = false;
                                });
                            })
                        )
                    ).Spacing(20)
                )
                .GridRow(0),
                Button("Cancel")
                    .GridRow(1)
                    .OnClicked(() => SetState(s => s.IsPhraseListShown = false))
            )
            .BackgroundColor(ApplicationTheme.LightBackground)
            .Padding(15)
            .Margin(15)
            .HorizontalOptions(LayoutOptions.Fill)
            .MinimumWidthRequest(320)
        )
        .IsOpen(State.IsPhraseListShown);

    VisualNode RenderChunk(ConversationChunk chunk)
    {
        if (chunk.Author.Equals(ConversationParticipant.Bot.FirstName))
        {
            return Border(
                new SelectableLabel()
                    .Text(chunk.Text),
                     MenuFlyout(
                        MenuFlyoutItem("Play Audio").OnClicked(() => PlayAudio(chunk.Text))
                    )

            )
            .Margin(new Thickness(15, 5))
            .Padding(new Thickness(12, 4, 12, 8))
            .Background(ApplicationTheme.Primary)
            .Stroke(ApplicationTheme.Primary)
            .StrokeShape(new RoundRectangle().CornerRadius(10, 10, 2, 10))
            .HorizontalOptions(LayoutOptions.Start);
        }
        else
        {
            return Border(
                new SelectableLabel()
                    .Text(chunk.Text)
            )
            .Margin(new Thickness(15, 5))
            .Padding(new Thickness(12, 4, 12, 8))
            .Background((Color)Application.Current.Resources["Secondary"])
            .Stroke((Color)Application.Current.Resources["Secondary"])
            .StrokeShape(new RoundRectangle().CornerRadius(10, 0, 10, 2))
            .HorizontalOptions(LayoutOptions.End)
            .OnTapped(()=>{
                ShowExplanation(chunk);
            });
        }
    }

    async Task ShowExplanation(ConversationChunk s)
    {
        string explanation = $"Comprehension Score: {s.Comprehension}" + Environment.NewLine + Environment.NewLine;
        explanation += $"{s.ComprehensionNotes}" + Environment.NewLine + Environment.NewLine;
        
        try{
            SetState(s =>{
                s.Explanation = explanation;
                s.IsExplanationShown = true;
            });
        }catch(Exception e){
            Debug.WriteLine(e.Message);
        }
    }

    VisualNode RenderInput() =>
        Grid("", "* Auto",
            Border(
                Entry()
                    .Placeholder("그건 한국어로 어떻게 말해요?")
                    .FontSize((double)Application.Current.Resources["size200"])
                    .ReturnType(ReturnType.Send)
                    .Text(State.UserInput)
                    .OnTextChanged((s, e) => State.UserInput = e.NewTextValue)
                    .OnCompleted(async () =>
                    {
                        await SendMessage();
                    })
                    // .Bind(Entry.ReturnCommandProperty, nameof(WarmupPageModel.SendMessageCommand))
            )
            .Background(Colors.Transparent)
            .Stroke(ApplicationTheme.Gray300)
            .StrokeShape(new RoundRectangle().CornerRadius(6))
            .Padding(new Thickness(15, 0))
            .StrokeThickness(1)
            .VerticalOptions(LayoutOptions.End),
            Button()
                .BackgroundColor(ApplicationTheme.Gray300)
                .ImageSource(ApplicationTheme.IconAdd)
                .Text("add")
                .VCenter()
                .GridColumn(1)
                .OnPressed(async () =>
                {
                    SetState(s => s.IsPhraseListShown = true);
                })
        )
        .GridRow(1)
        .Margin(new Thickness(15))
        .ColumnSpacing(15)
        .VEnd();

    async Task SendMessage()
    {
        if (!string.IsNullOrWhiteSpace(State.UserInput))
        {
            var chunk = new ConversationChunk(
                _conversation.ID,
                DateTime.Now, 
                $"{ConversationParticipant.Me.FirstName} {ConversationParticipant.Me.LastName}", 
                State.UserInput
            );
            
            
            await _conversationService.SaveConversationChunk(chunk);
            
            SetState(s => {
                s.Chunks.Add(chunk);
                s.UserInput = string.Empty;
            });

            // send to the bot for a response
            await Task.Delay(2000);
            await GetReply();
        }
    }

    async Task ResumeConversation()
    {
        SetState(s => s.IsBusy = true);

        _conversation = await _conversationService.ResumeConversation();

        if (_conversation == null || !_conversation.Chunks.Any())
        {
            await StartConversation();
            return;
        }

        ObservableCollection<ConversationChunk> chunks = [];

        foreach (var chunk in _conversation.Chunks)
        {
            chunks.Add(chunk);
        }

        SetState(s =>{
            s.Chunks = chunks;
            s.IsBusy = false;
        });
    }

    /// <summary>
    /// Starts a new conversation thread, clearing the current one.
    /// </summary>
    async Task StartNewConversation()
    {
        // Show a confirmation dialog
        bool shouldStart = await Application.Current.MainPage.DisplayAlert(
            "Start New Conversation", 
            "This will clear the current conversation. Are you sure?",
            "Yes", "No");
            
        if (!shouldStart)
            return;
            
        // Clear the current conversation and chunks
        SetState(s => {
            s.Chunks.Clear();
            s.UserInput = string.Empty;
        });
        
        // Start a fresh conversation
        await StartConversation();
    }

    async Task StartConversation()
    {
        await Task.Delay(100);
        
        SetState(s => s.IsBusy = true);

        _conversation = new Conversation();
        await _conversationService.SaveConversation(_conversation);

        var chunk = new ConversationChunk(_conversation.ID, DateTime.Now, ConversationParticipant.Bot.FirstName, "...");

        SetState(s => s.Chunks.Add(chunk));

        await Task.Delay(1000);
        chunk.Text = "안녕하세요. 이름이 뭐예요?";
        await _conversationService.SaveConversationChunk(chunk);

        SetState(s => s.IsBusy = false);
    }

    async Task GetReply()
    {
        SetState(s => s.IsBusy = true);

        var chunk = new ConversationChunk(_conversation.ID, DateTime.Now, ConversationParticipant.Bot.FirstName, "...");
        SetState(s => s.Chunks.Add(chunk));

        Reply response = await _conversationService.ContinueConversation(State.Chunks.ToList());
        chunk.Text = response.Message;

        var previousChunk = State.Chunks[State.Chunks.Count - 2];
        previousChunk.Comprehension = response.Comprehension;
        previousChunk.ComprehensionNotes = response.ComprehensionNotes;

        await _conversationService.SaveConversationChunk(previousChunk);
        await _conversationService.SaveConversationChunk(chunk);

        SetState(s => s.IsBusy = false);

        // await PlayAudio(response.Message);
    }    async Task PlayAudio(string text)
    {
        try
        {
            // Stop any currently playing audio
            StopAudio();
            
            // Show the loading state immediately
            if (_floatingPlayer != null)
            {
                _floatingPlayer.SetTitle($"Loading: {(text.Length > 15 ? text.Substring(0, 15) + "..." : text)}");
                _floatingPlayer.ShowLoading();
            }
            
            // Use ElevenLabsSpeechService to generate audio with Korean voice
            var audioStream = await _speechService.TextToSpeechAsync(
                text,
                _speechService.DefaultVoiceId);

            if (audioStream != null)
            {
                // Create the audio player
                _audioPlayer = AudioManager.Current.CreatePlayer(audioStream);
                
                // Set up the floating player
                if (_floatingPlayer != null)
                {
                    _floatingPlayer.SetTitle($"Playing: {(text.Length > 15 ? text.Substring(0, 15) + "..." : text)}");
                    _floatingPlayer.SetReady(); // Switch from loading to ready state
                    _floatingPlayer.SetPlaying();
                }
                
                // Start playback
                _audioPlayer.Play();
                
                // Start tracking playback position
                StartPlaybackTimer();
                
                // Set up auto-hide when audio finishes
                _audioPlayer.PlaybackEnded += (s, e) => {
                    if (_floatingPlayer != null)
                    {
                        _floatingPlayer.Hide();
                    }
                };
            }
            else
            {
                // Hide the player if audio generation failed
                if (_floatingPlayer != null)
                {
                    _floatingPlayer.Hide();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error playing audio: {ex.Message}");
            
            // Hide the player on error
            if (_floatingPlayer != null)
            {
                _floatingPlayer.Hide();
            }
        }
    }
    
    /// <summary>
    /// Pauses the currently playing audio.
    /// </summary>
    private void PauseAudio()
    {
        if (_audioPlayer != null && _audioPlayer.IsPlaying)
        {
            _audioPlayer.Pause();
            _playbackTimer?.Stop();
            
            if (_floatingPlayer != null)
            {
                _floatingPlayer.SetPaused();
            }
        }
    }
    
    /// <summary>
    /// Resumes playback from the paused position.
    /// </summary>
    private void ResumeAudio()
    {
        if (_audioPlayer != null && !_audioPlayer.IsPlaying)
        {
            _audioPlayer.Play();
            StartPlaybackTimer();
            
            if (_floatingPlayer != null)
            {
                _floatingPlayer.SetPlaying();
            }
        }
    }
    
    /// <summary>
    /// Rewinds the audio to the beginning.
    /// </summary>
    private void RewindAudio()
    {
        if (_audioPlayer != null)
        {
            _audioPlayer.Seek(0);
            
            if (_floatingPlayer != null)
            {
                _floatingPlayer.UpdatePosition(0f);
            }
        }
    }
    
    /// <summary>
    /// Stops and disposes the audio playback.
    /// </summary>
    private void StopAudio()
    {
        if (_audioPlayer != null)
        {
            _playbackTimer?.Stop();
            
            if (_audioPlayer.IsPlaying)
            {
                _audioPlayer.Stop();
            }
            
            _audioPlayer.Dispose();
            _audioPlayer = null;
            
            if (_floatingPlayer != null)
            {
                _floatingPlayer.Hide();
            }
        }
    }
    
    /// <summary>
    /// Starts a timer to track audio playback progress.
    /// </summary>
    private void StartPlaybackTimer()
    {
        // Stop any existing timer
        _playbackTimer?.Stop();
        
        // Create a new timer that ticks 10 times per second
        _playbackTimer = Application.Current.Dispatcher.CreateTimer();
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(100);
        _playbackTimer.Tick += (s, e) => UpdatePlaybackPosition();
        _playbackTimer.Start();
    }
    
    /// <summary>
    /// Updates the playback position for the floating player.
    /// </summary>
    private void UpdatePlaybackPosition()
    {
        if (_audioPlayer == null || _floatingPlayer == null)
            return;
            
        // Only update if we have a valid duration
        if (_audioPlayer.Duration > 0)
        {
            // Calculate the position as a float between 0-1
            float position = (float)(_audioPlayer.CurrentPosition / _audioPlayer.Duration);
            _floatingPlayer.UpdatePosition(position);
        }
    }
}