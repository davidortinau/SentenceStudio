using Plugin.Maui.Audio;
using MauiReactor.Shapes;
using SentenceStudio.Repositories;

namespace SentenceStudio.Pages.MinimalPairs;

/// <summary>
/// Props for MinimalPairSessionPage
/// </summary>
public class MinimalPairSessionPageProps
{
    public int[] PairIds { get; set; } = Array.Empty<int>();
    public string Mode { get; set; } = "Focus"; // "Focus" or "Mixed"
    public int? PlannedTrialCount { get; set; }
}

/// <summary>
/// Minimal Pair Practice Session Page
/// 
/// Responsibilities:
/// - Play prompt audio (one word from the pair)
/// - Show exactly two answer choices
/// - Provide immediate correctness feedback
/// - Track running correct/incorrect counts
/// - Allow replay of prompt
/// - Show session summary at end
/// 
/// Accessibility:
/// - Feedback does not rely on text color alone (uses icons + background)
/// - Audio player is properly disposed on unmount
/// 
/// Audio Strategy:
/// - Uses ElevenLabs + StreamHistoryRepository cache
/// - Plays from cache when available
/// - Shows error message if audio unavailable offline
/// </summary>
class MinimalPairSessionPageState
{
    public List<MinimalPair> Pairs { get; set; } = new();
    public int CurrentTrialIndex { get; set; }
    public int TotalTrials { get; set; } = 20; // Default
    public MinimalPair? CurrentPair { get; set; }
    public VocabularyWord? PromptWord { get; set; }
    public VocabularyWord? OtherWord { get; set; }
    public VocabularyWord? LeftWord { get; set; } // Store randomized positions
    public VocabularyWord? RightWord { get; set; }
    public bool IsPlayingAudio { get; set; }
    public int? SelectedWordId { get; set; } // Track which word was selected
    public bool HasCheckedAnswer { get; set; } // Whether user has pressed "Check Answer"
    public bool IsFirstTrial { get; set; } = true; // Track first trial for delayed audio
    public bool? AnswerWasCorrect { get; set; } // null = no answer yet, true = correct, false = incorrect
    public int CorrectCount { get; set; }
    public int IncorrectCount { get; set; }
    public bool IsDebouncing { get; set; } // Prevent double-tap
    public DateTime? SessionStartedAt { get; set; }
    public int? SessionId { get; set; }
    public bool ShowSummary { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}

partial class MinimalPairSessionPage : Component<MinimalPairSessionPageState, MinimalPairSessionPageProps>
{
    [Inject] ILogger<MinimalPairSessionPage> _logger;
    [Inject] MinimalPairRepository _pairRepo;
    [Inject] MinimalPairSessionRepository _sessionRepo;
    [Inject] StreamHistoryRepository _streamHistoryRepo;
    [Inject] ElevenLabsSpeechService _speechService;
    [Inject] IAudioManager _audioManager;
    [Inject] Services.SpeechVoicePreferences _voicePrefs;

    IAudioPlayer? _audioPlayer;
    readonly Random _random = new();

    LocalizationManager _localize => LocalizationManager.Instance;

    protected override void OnMounted()
    {
        _ = InitializeSessionAsync();
    }

    protected override void OnWillUnmount()
    {
        // T019: Dispose audio player
        _audioPlayer?.Stop();
        _audioPlayer?.Dispose();
        _audioPlayer = null;
    }

    private async Task InitializeSessionAsync()
    {
        try
        {
            // Load pairs
            var pairIds = Props?.PairIds ?? Array.Empty<int>();
            var pairs = new List<MinimalPair>();

            foreach (var id in pairIds)
            {
                var pair = await _pairRepo.GetPairByIdAsync(id);
                if (pair != null)
                {
                    pairs.Add(pair);
                }
            }

            if (pairs.Count == 0)
            {
                _logger.LogWarning("No pairs loaded for session");
                return;
            }

            // Start session in database
            var session = await _sessionRepo.StartSessionAsync(
                userId: 1, // Single-user app
                mode: Props?.Mode ?? "Focus",
                plannedTrialCount: Props?.PlannedTrialCount ?? 20
            );

            SetState(s =>
            {
                s.Pairs = pairs;
                s.TotalTrials = Props?.PlannedTrialCount ?? 20;
                s.SessionStartedAt = DateTime.UtcNow;
                s.SessionId = session.Id;
            });

            // Start first trial
            NextTrial();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize session");
        }
    }

    private void NextTrial()
    {
        // T015: Implement prompt selection
        if (State.CurrentTrialIndex >= State.TotalTrials)
        {
            _ = EndSessionAsync();
            return;
        }

        // Select a random pair (weighted by recent performance in future)
        var pair = State.Pairs[_random.Next(State.Pairs.Count)];

        // Randomly choose which word is the prompt
        var isAPrompt = _random.Next(2) == 0;
        var promptWord = isAPrompt ? pair.VocabularyWordA : pair.VocabularyWordB;
        var otherWord = isAPrompt ? pair.VocabularyWordB : pair.VocabularyWordA;

        // Randomly assign left/right positions (store to prevent re-randomization on audio replay)
        var leftWord = _random.Next(2) == 0 ? promptWord : otherWord;
        var rightWord = leftWord.Id == promptWord.Id ? otherWord : promptWord;

        SetState(s =>
        {
            s.CurrentPair = pair;
            s.PromptWord = promptWord;
            s.OtherWord = otherWord;
            s.LeftWord = leftWord;
            s.RightWord = rightWord;
            s.SelectedWordId = null;
            s.HasCheckedAnswer = false;
            s.AnswerWasCorrect = null;
            s.IsDebouncing = false;
            s.ErrorMessage = string.Empty;
        });

        // Auto-play audio (delayed on first trial)
        if (State.IsFirstTrial)
        {
            SetState(s => s.IsFirstTrial = false);
            _ = Task.Delay(500).ContinueWith(_ => PlayPromptAudioAsync());
        }
        else
        {
            _ = PlayPromptAudioAsync();
        }
    }

    private async Task PlayPromptAudioAsync()
    {
        // T016: Implement audio playback
        if (State.PromptWord == null) return;

        SetState(s => s.IsPlayingAudio = true);

        try
        {
            var text = State.PromptWord.TargetLanguageTerm;
            var voiceId = _voicePrefs.VoiceId;

            // Check cache first
            var cachedAudio = await _streamHistoryRepo.GetStreamHistoryByPhraseAndVoiceAsync(text, voiceId);

            Stream audioStream;
            if (cachedAudio != null && !string.IsNullOrEmpty(cachedAudio.AudioFilePath) && File.Exists(cachedAudio.AudioFilePath))
            {
                _logger.LogDebug("Using cached audio for: {Text}", text);
                audioStream = File.OpenRead(cachedAudio.AudioFilePath);
            }
            else
            {
                // T033: Show offline error if needed
                if (!Connectivity.Current.NetworkAccess.HasFlag(NetworkAccess.Internet))
                {
                    SetState(s =>
                    {
                        s.IsPlayingAudio = false;
                        s.ErrorMessage = $"{_localize["OfflineAudioUnavailable"]}";
                    });
                    return;
                }

                // Generate audio using ElevenLabs speech service
                _logger.LogDebug("Generating audio from ElevenLabs for: {Text} with voice: {VoiceId}", text, voiceId);
                audioStream = await _speechService.TextToSpeechAsync(
                    text: text,
                    voiceId: voiceId,
                    stability: 0.5f,
                    similarityBoost: 0.75f,
                    speed: 0.85f
                );

                // Save to cache for future use
                var audioCacheDir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "AudioCache");
                Directory.CreateDirectory(audioCacheDir);

                var fileName = $"word_{Guid.NewGuid()}.mp3";
                var filePath = System.IO.Path.Combine(audioCacheDir, fileName);

                // Save to file
                using (var fileStream = File.Create(filePath))
                {
                    await audioStream.CopyToAsync(fileStream);
                }

                // Create stream history entry for caching
                var streamHistory = new StreamHistory
                {
                    Phrase = text,
                    VoiceId = voiceId,
                    AudioFilePath = filePath,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                await _streamHistoryRepo.SaveStreamHistoryAsync(streamHistory);

                // Reopen file for playback
                audioStream = File.OpenRead(filePath);
            }

            // Play audio
            _audioPlayer?.Stop();
            _audioPlayer?.Dispose();
            _audioPlayer = _audioManager.CreatePlayer(audioStream);
            _audioPlayer.Play();

            SetState(s => s.IsPlayingAudio = false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to play audio");
            SetState(s =>
            {
                s.IsPlayingAudio = false;
                s.ErrorMessage = $"{_localize["AudioPlaybackError"]}";
            });
        }
    }

    private async Task<byte[]> ReadStreamToByteArray(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private void OnWordSelected(VocabularyWord selectedWord)
    {
        // Don't allow selection if already checked
        if (State.IsDebouncing || State.HasCheckedAnswer) return;

        SetState(s => s.SelectedWordId = selectedWord.Id);
    }

    private async Task OnCheckAnswerAsync()
    {
        // Don't allow check if no selection made or already checked
        if (!State.SelectedWordId.HasValue || State.HasCheckedAnswer) return;

        SetState(s => s.IsDebouncing = true);

        var isCorrect = State.SelectedWordId == State.PromptWord?.Id;

        // Show feedback via border colors
        SetState(s =>
        {
            s.HasCheckedAnswer = true;
            s.AnswerWasCorrect = isCorrect;
            if (isCorrect)
                s.CorrectCount++;
            else
                s.IncorrectCount++;
        });

        // Record attempt in database
        if (State.SessionId.HasValue && State.CurrentPair != null && State.PromptWord != null)
        {
            var selectedWord = State.LeftWord?.Id == State.SelectedWordId ? State.LeftWord : State.RightWord;
            if (selectedWord != null)
            {
                await _sessionRepo.RecordAttemptAsync(
                    userId: 1,
                    sessionId: State.SessionId.Value,
                    pairId: State.CurrentPair.Id,
                    promptWordId: State.PromptWord.Id,
                    selectedWordId: selectedWord.Id,
                    isCorrect: isCorrect
                );
            }
        }

        SetState(s => s.CurrentTrialIndex++);

        // Auto-advance after delay
        await Task.Delay(1500);
        NextTrial();
    }

    private void OnReplayAudio()
    {
        if (!State.IsPlayingAudio && !State.AnswerWasCorrect.HasValue)
        {
            _ = PlayPromptAudioAsync();
        }
    }

    private async Task EndSessionAsync()
    {
        if (State.SessionId.HasValue)
        {
            await _sessionRepo.EndSessionAsync(State.SessionId.Value);
        }

        SetState(s => s.ShowSummary = true);
    }

    public override VisualNode Render()
    {
        return ContentPage($"{_localize["MinimalPairsTitle"]}",
            State.ShowSummary
                ? RenderSummary()
                : RenderSession()
        );
    }

    private VisualNode RenderSession()
    {
        return Grid(rows: "Auto,*,Auto", columns: "*",
            // Progress and counters
            RenderHeader().GridRow(0),

            // Main content: answer choices
            RenderChoices().GridRow(1),

            // Replay button
            RenderReplayButton().GridRow(2)
        );
    }

    private VisualNode RenderHeader()
    {
        var theme = BootstrapTheme.Current;

        return VStack(spacing: 8,
            // Progress
            HStack(spacing: 8,
                Label($"{_localize["Trial"]} {State.CurrentTrialIndex + 1} / {State.TotalTrials}")
                    .Small()
                    .Muted()
            )
            .HCenter(),

            // Counters
            HStack(spacing: 16,
                Label($"✓ {State.CorrectCount}")
                    .H4()
                    .TextColor(theme.Success),

                Label($"✗ {State.IncorrectCount}")
                    .H4()
                    .TextColor(theme.Danger)
            )
            .HCenter(),

            string.IsNullOrEmpty(State.ErrorMessage)
                ? null
                : Label(State.ErrorMessage)
                    .Small()
                    .TextColor(theme.Warning)
                    .HCenter()
        )
        .Padding(16);
    }

    private VisualNode RenderChoices()
    {
        if (State.PromptWord == null || State.OtherWord == null || State.LeftWord == null || State.RightWord == null)
        {
            return Grid();
        }

        var theme = BootstrapTheme.Current;

        // Use stored positions (no re-randomization)
        return VStack(spacing: 16,
            HStack(spacing: 16,
                RenderAnswerTile(State.LeftWord),
                RenderAnswerTile(State.RightWord)
            )
            .HCenter(),

            Button($"{_localize["CheckAnswer"]}")
                .Background(new SolidColorBrush(theme.Primary))
                .TextColor(Colors.White)
                .BorderColor(theme.Primary)
                .BorderWidth(1)
                .CornerRadius(6)
                .OnClicked(async () => await OnCheckAnswerAsync())
                .IsEnabled(State.SelectedWordId.HasValue && !State.IsDebouncing && !State.HasCheckedAnswer)
                .HCenter()
        )
        .HCenter()
        .VCenter()
        .Padding(16);
    }

    private VisualNode RenderAnswerTile(VocabularyWord word)
    {
        var theme = BootstrapTheme.Current;
        var isSelected = State.SelectedWordId == word.Id;
        var isCorrectAnswer = word.Id == State.PromptWord?.Id;

        // Determine border color based on state
        Color borderColor;

        if (!State.HasCheckedAnswer && isSelected)
        {
            borderColor = theme.Primary;
        }
        else if (State.HasCheckedAnswer)
        {
            if (isSelected)
            {
                borderColor = State.AnswerWasCorrect == true ? theme.Success : theme.Danger;
            }
            else if (isCorrectAnswer && State.AnswerWasCorrect == false)
            {
                borderColor = theme.Success;
            }
            else
            {
                borderColor = theme.GetOutline();
            }
        }
        else
        {
            borderColor = theme.GetOutline();
        }

        return Border(
            Grid(
                Label(word.TargetLanguageTerm)
                    .H4()
                    .Center(),

                // Show checkmark on correct answer when showing feedback
                (State.HasCheckedAnswer && isCorrectAnswer)
                    ? Image()
                        .Source(BootstrapIcons.Create(BootstrapIcons.CheckCircleFill, theme.Success, 24))
                        .WidthRequest(24)
                        .HeightRequest(24)
                        .HEnd()
                        .VStart()
                        .Margin(4)
                    : null
            )
            .HeightRequest(DeviceInfo.Idiom == DeviceIdiom.Phone ? 120 : 150)
            .WidthRequest(DeviceInfo.Idiom == DeviceIdiom.Phone ? 120 : 150)
        )
        .BackgroundColor(theme.GetSurface())
        .Stroke(borderColor)
        .StrokeThickness(3)
        .StrokeShape(new RoundRectangle().CornerRadius(12))
        .Padding(16)
        .OnTapped(() => OnWordSelected(word))
        .OnTapped(() =>
        {
            OnWordSelected(word);
            _ = OnCheckAnswerAsync();
        }, 2)
        .IsEnabled(!State.IsDebouncing && !State.HasCheckedAnswer);
    }

    private VisualNode RenderReplayButton()
    {
        var theme = BootstrapTheme.Current;

        return ImageButton()
            .Source(BootstrapIcons.Create(BootstrapIcons.PlayFill, theme.GetOnBackground(), 24))
            .Background(Colors.Transparent)
            .WidthRequest(48)
            .HeightRequest(48)
            .OnClicked(() => OnReplayAudio())
            .IsEnabled(!State.IsPlayingAudio && !State.AnswerWasCorrect.HasValue)
            .HCenter()
            .Margin(0, 24, 0, 24);
    }

    private VisualNode RenderSummary()
    {
        var theme = BootstrapTheme.Current;
        var total = State.CorrectCount + State.IncorrectCount;
        var accuracy = total > 0 ? (double)State.CorrectCount / total * 100 : 0;
        var duration = State.SessionStartedAt.HasValue
            ? (DateTime.UtcNow - State.SessionStartedAt.Value).TotalMinutes
            : 0;

        return VStack(spacing: 24,
            Label($"{_localize["MinimalPairsSessionSummary"]}")
                .H3()
                .HCenter(),

            Border(
                VStack(spacing: 12,
                    Label($"{_localize["Correct"]}: {State.CorrectCount}")
                        .H4()
                        .TextColor(theme.Success),
                    Label($"{_localize["Incorrect"]}: {State.IncorrectCount}")
                        .H4()
                        .TextColor(theme.Danger),
                    Label($"{_localize["Accuracy"]}: {accuracy:F1}%")
                        .H4(),
                    Label($"{_localize["Duration"]}: {duration:F1} {_localize["Minutes"]}")
                        .H4()
                )
                .Padding(16)
            )
            .BackgroundColor(theme.GetSurface())
            .Stroke(theme.GetOutline())
            .StrokeThickness(1)
            .StrokeShape(new RoundRectangle().CornerRadius(12))
            .HCenter(),

            Button($"{_localize["Done"]}")
                .Background(new SolidColorBrush(theme.Primary))
                .TextColor(Colors.White)
                .BorderColor(theme.Primary)
                .BorderWidth(1)
                .CornerRadius(6)
                .OnClicked(async () => await MauiControls.Shell.Current.GoToAsync(".."))
        )
        .VCenter()
        .Padding(24);
    }
}
