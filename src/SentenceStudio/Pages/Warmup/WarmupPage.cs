using MauiReactor.Shapes;
using Plugin.Maui.Audio;
using System.Collections.ObjectModel;
using SentenceStudio.Pages.Controls;
using SentenceStudio.Pages.Dashboard;
using Microsoft.Maui.Dispatching;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;
using SentenceStudio.Services.Agents;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;

namespace SentenceStudio.Pages.Warmup;

class WarmupPageState
{
    public ObservableCollection<ConversationChunk> Chunks { get; set; } = new();
    public string UserInput { get; set; }
    public bool IsBusy { get; set; }

    public string Explanation { get; set; }

    public double FontSize { get; set; } = 18.0;

    // Scenario support
    public ConversationScenario? ActiveScenario { get; set; }
    public List<ConversationScenario> AvailableScenarios { get; set; } = new();
    public bool IsConversationComplete { get; set; }

    // Scenario creation support
    public ScenarioCreationState? CreationState { get; set; }
    public bool IsCreatingScenario => CreationState != null;

    // Grammar corrections for the currently selected message
    public List<GrammarCorrectionDto> SelectedGrammarCorrections { get; set; } = new();
}

partial class WarmupPage : Component<WarmupPageState, ActivityProps>
{
    [Inject] TeacherService _teacherService;
    [Inject] IConversationAgentService _agentService;
    [Inject] ElevenLabsSpeechService _speechService;
    [Inject] UserActivityRepository _userActivityRepository;
    [Inject] SentenceStudio.Services.Timer.IActivityTimerService _timerService;
    [Inject] IScenarioService _scenarioService;
    [Inject] ILogger<WarmupPage> _logger;
    LocalizationManager _localize => LocalizationManager.Instance;
    Conversation _conversation;

    private IAudioPlayer _audioPlayer;
    private FloatingAudioPlayer _floatingPlayer;
    private IDispatcherTimer _playbackTimer;
    private EventHandler _playbackEndedHandler;

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
        };

    public override VisualNode Render()
    {
        // Hot reload diagnostic - this should log every time Render() is called
        System.Diagnostics.Debug.WriteLine($"🔄 WarmupPage.Render() called at {DateTime.Now:HH:mm:ss.fff}, ActiveScenario={State.ActiveScenario?.Name ?? "none"}");
        _logger.LogDebug("🔄 WarmupPage.Render() called at {Time}, ActiveScenario={Scenario}", DateTime.Now.ToString("HH:mm:ss.fff"), State.ActiveScenario?.Name ?? "none");

        return ContentPage($"{_localize["Warmup"]}",
            ToolbarItem($"{_localize["ChooseScenario"]}").OnClicked(ShowScenarioSelection),
            Grid(rows: "*, Auto", "*",
                RenderMessageScroll(),
                RenderInput(),
                CreateFloatingAudioPlayer()
            )
        )
        .Set(MauiControls.Shell.TitleViewProperty, Props?.FromTodaysPlan == true ? new Components.ActivityTimerBar() : null)
        .OnAppearing(ResumeConversation);
    }

    VisualNode CreateFloatingAudioPlayer() =>
        _floatingPlayer = new FloatingAudioPlayer(
            _audioPlayer,
            onPlay: ResumeAudio,
            onPause: PauseAudio,
            onRewind: RewindAudio,
            onStop: StopAudio
        );

    VisualNode RenderMessageScroll()
    {
        System.Diagnostics.Debug.WriteLine($"🔍 RenderMessageScroll: IsBusy={State.IsBusy}, ChunksCount={State.Chunks?.Count ?? -1}");

        // Log each chunk being rendered
        foreach (var c in State.Chunks)
        {
            System.Diagnostics.Debug.WriteLine($"📝 Chunk: Author='{c.Author}', Text='{c.Text?.Substring(0, Math.Min(30, c.Text?.Length ?? 0))}...'");
        }

        return ScrollView(
            VStack(
                State.Chunks.Select(c => RenderChunk(c)).ToArray()
            )
            .Spacing(MyTheme.LayoutSpacing)
        )
        .GridRow(0)
        .VFill();
    }

    async void ShowExplanationPopup()
    {
        try
        {
            var popup = new ListActionPopup
            {
                Title = $"{_localize["Explanation"]}",
                ShowActionButton = false,
                ItemsSource = new List<string> { State.Explanation },
                ItemDataTemplate = new Microsoft.Maui.Controls.DataTemplate(() =>
                {
                    var tapGesture = new Microsoft.Maui.Controls.TapGestureRecognizer();
                    tapGesture.Tapped += async (s, e) =>
                    {
                        await IPopupService.Current.PopAsync();
                    };

                    var label = new Microsoft.Maui.Controls.Label
                    {
                        TextColor = Colors.White,
                        FontSize = 16,
                        LineBreakMode = LineBreakMode.WordWrap
                    };
                    label.SetBinding(Microsoft.Maui.Controls.Label.TextProperty, ".");
                    label.GestureRecognizers.Add(tapGesture);
                    return label;
                })
            };
            await IPopupService.Current.PushAsync(popup);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WarmupPage: Error showing explanation popup");
        }
    }

    async void ShowPhrasesPopup()
    {
        try
        {
            var popup = new ListActionPopup
            {
                Title = $"{_localize["QuickPhrases"]}",
                ShowActionButton = false,
                ItemsSource = phrases,
                ItemDataTemplate = new Microsoft.Maui.Controls.DataTemplate(() =>
                {
                    var tapGesture = new Microsoft.Maui.Controls.TapGestureRecognizer();
                    tapGesture.SetBinding(Microsoft.Maui.Controls.TapGestureRecognizer.CommandParameterProperty, ".");
                    tapGesture.Tapped += async (s, e) =>
                    {
                        if (e is Microsoft.Maui.Controls.TappedEventArgs args && args.Parameter is string phrase)
                        {
                            await IPopupService.Current.PopAsync();
                            SetState(s => s.UserInput = phrase);
                        }
                    };

                    var label = new Microsoft.Maui.Controls.Label
                    {
                        TextColor = Colors.White,
                        FontSize = 16,
                        Padding = new Thickness(0, 8)
                    };
                    label.SetBinding(Microsoft.Maui.Controls.Label.TextProperty, ".");
                    label.GestureRecognizers.Add(tapGesture);
                    return label;
                })
            };
            await IPopupService.Current.PushAsync(popup);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WarmupPage: Error showing phrases popup");
        }
    }

    async void ShowScenarioSelection()
    {
        try
        {
            var scenarios = await _scenarioService.GetAllScenariosAsync();
            SetState(s => s.AvailableScenarios = scenarios);

            var popup = new ListActionPopup
            {
                Title = $"{_localize["ChooseScenario"]}",
                ShowActionButton = false,
                ItemsSource = scenarios,
                ItemDataTemplate = new Microsoft.Maui.Controls.DataTemplate(() =>
                {
                    var tapGesture = new Microsoft.Maui.Controls.TapGestureRecognizer();
                    tapGesture.SetBinding(Microsoft.Maui.Controls.TapGestureRecognizer.CommandParameterProperty, ".");
                    tapGesture.Tapped += async (s, e) =>
                    {
                        if (e is Microsoft.Maui.Controls.TappedEventArgs args && args.Parameter is ConversationScenario scenario)
                        {
                            await IPopupService.Current.PopAsync();
                            SelectScenario(scenario);
                        }
                    };

                    var layout = new Microsoft.Maui.Controls.VerticalStackLayout
                    {
                        Spacing = 4,
                        Padding = new Thickness(0, 8)
                    };
                    layout.GestureRecognizers.Add(tapGesture);

                    // Header row with icon, name, and active checkmark
                    var headerLayout = new Microsoft.Maui.Controls.HorizontalStackLayout { Spacing = 8 };

                    // Type icon (Repeat for Finite, Chat for OpenEnded)
                    var typeIcon = new Microsoft.Maui.Controls.Image
                    {
                        WidthRequest = 16,
                        HeightRequest = 16,
                        VerticalOptions = LayoutOptions.Center
                    };
                    typeIcon.SetBinding(Microsoft.Maui.Controls.Image.SourceProperty,
                        new Microsoft.Maui.Controls.Binding("ConversationType",
                            converter: new ConversationTypeToIconConverter()));

                    // Scenario name
                    var nameLabel = new Microsoft.Maui.Controls.Label
                    {
                        FontSize = 16,
                        FontAttributes = FontAttributes.Bold,
                        VerticalOptions = LayoutOptions.Center,
                        TextColor = Colors.White
                    };
                    nameLabel.SetBinding(Microsoft.Maui.Controls.Label.TextProperty, "DisplayName");

                    // Active checkmark icon
                    var checkIcon = new Microsoft.Maui.Controls.Image
                    {
                        Source = MyTheme.IconCheckmarkCircleFilledCorrect,
                        WidthRequest = 16,
                        HeightRequest = 16,
                        VerticalOptions = LayoutOptions.Center,
                        HorizontalOptions = LayoutOptions.EndAndExpand
                    };
                    checkIcon.SetBinding(Microsoft.Maui.Controls.Image.IsVisibleProperty,
                        new Microsoft.Maui.Controls.Binding("Id",
                            converter: new IsActiveScenarioConverter(State.ActiveScenario?.Id)));

                    headerLayout.Children.Add(typeIcon);
                    headerLayout.Children.Add(nameLabel);
                    headerLayout.Children.Add(checkIcon);

                    // Persona row with icon and name
                    var personaLayout = new Microsoft.Maui.Controls.HorizontalStackLayout { Spacing = 4 };
                    var personaIcon = new Microsoft.Maui.Controls.Image
                    {
                        Source = MyTheme.IconPerson,
                        WidthRequest = 12,
                        HeightRequest = 12,
                        VerticalOptions = LayoutOptions.Center
                    };
                    var personaLabel = new Microsoft.Maui.Controls.Label
                    {
                        FontSize = 12,
                        TextColor = Colors.Gray
                    };
                    personaLabel.SetBinding(Microsoft.Maui.Controls.Label.TextProperty, "PersonaName");
                    personaLayout.Children.Add(personaIcon);
                    personaLayout.Children.Add(personaLabel);

                    // Description label
                    var descLabel = new Microsoft.Maui.Controls.Label
                    {
                        FontSize = 14,
                        TextColor = Colors.LightGray,
                        MaxLines = 2
                    };
                    descLabel.SetBinding(Microsoft.Maui.Controls.Label.TextProperty, "SituationDescription");

                    layout.Children.Add(headerLayout);
                    layout.Children.Add(personaLayout);
                    layout.Children.Add(descLabel);

                    return layout;
                })
            };

            await IPopupService.Current.PushAsync(popup);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading scenarios");
        }
    }

    async void SelectScenario(ConversationScenario scenario)
    {
        _logger.LogInformation("Selected scenario: {Name}", scenario.Name);

        SetState(s =>
        {
            s.ActiveScenario = scenario;
            s.IsConversationComplete = false;
        });

        // Start a new conversation with the selected scenario
        await StartConversationWithScenario(scenario);
    }

    VisualNode RenderChunk(ConversationChunk chunk)
    {
        // Use the Role property to determine message type - this is the authoritative source
        var isUserMessage = chunk.Role == ConversationRole.User;
        System.Diagnostics.Debug.WriteLine($"RenderChunk: Author='{chunk.Author}', Role={chunk.Role}, IsUser={isUserMessage}");

        if (!isUserMessage)
        {
            // Bot/System message (left-aligned, dark background)
            return Border(
                new SelectableLabel()
                    .Text(chunk.Text)
                    .TextColor(Colors.White)
                    .FontSize(State.FontSize),
                     MenuFlyout(
                        MenuFlyoutItem("Play Audio").OnClicked(() => PlayAudio(chunk.Text))
                    )

            )
            .Margin(new Thickness(MyTheme.LayoutSpacing, MyTheme.MicroSpacing))
            .Padding(new Thickness(MyTheme.CardPadding, MyTheme.MicroSpacing, MyTheme.CardPadding, MyTheme.ComponentSpacing))
            .Background(MyTheme.HighlightDarkest)
            .Stroke(MyTheme.HighlightDarkest)
            .StrokeShape(new RoundRectangle().CornerRadius(10, 10, 2, 10))
            .HStart();
        }
        else
        {
            // User message (right-aligned, highlight background)
            // Check if this message has grammar corrections or feedback
            var hasCorrections = chunk.GrammarCorrections?.Any() == true;
            var hasComprehension = chunk.Comprehension > 0;
            var hasFeedback = hasCorrections || hasComprehension;

            return Grid("Auto", "*, Auto",
                Border(
                    new SelectableLabel()
                        .Text(chunk.Text)
                        .FontSize(State.FontSize)
                )
                .Padding(new Thickness(MyTheme.CardPadding, MyTheme.MicroSpacing, MyTheme.CardPadding, MyTheme.ComponentSpacing))
                .Background(MyTheme.HighlightMedium)
                .Stroke(MyTheme.HighlightMedium)
                .StrokeShape(new RoundRectangle().CornerRadius(10, 0, 10, 2))
                .GridColumn(0),

                // Grammar/feedback indicator icon - only show if there's feedback
                hasFeedback ?
                    ImageButton()
                        .Source(hasCorrections ? MyTheme.IconGrammarCheck : MyTheme.IconMeta)
                        .BackgroundColor(Colors.Transparent)
                        .Padding(4)
                        .GridColumn(1)
                        .VCenter()
                        .OnClicked(() => ShowExplanation(chunk))
                    : null
            )
            .Margin(new Thickness(MyTheme.LayoutSpacing, MyTheme.MicroSpacing))
            .HEnd()
            .OnTapped(() => ShowExplanation(chunk));
        }
    }

    async Task ShowExplanation(ConversationChunk s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Comprehension Score: {s.Comprehension:P0}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(s.ComprehensionNotes))
        {
            sb.AppendLine(s.ComprehensionNotes);
            sb.AppendLine();
        }

        // Add grammar corrections if available
        var corrections = s.GrammarCorrections;
        if (corrections?.Any() == true)
        {
            sb.AppendLine("📝 Grammar Corrections:");
            sb.AppendLine();
            foreach (var correction in corrections)
            {
                sb.AppendLine($"❌ {correction.Original}");
                sb.AppendLine($"✅ {correction.Corrected}");
                if (!string.IsNullOrEmpty(correction.Explanation))
                {
                    sb.AppendLine($"💡 {correction.Explanation}");
                }
                sb.AppendLine();
            }
        }

        try
        {
            SetState(state => state.Explanation = sb.ToString());
            ShowExplanationPopup();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "WarmupPage: Error showing explanation");
        }
    }

    VisualNode RenderInput() =>
        Grid("", "* Auto Auto Auto",
            Border(
                Entry()
                    .Placeholder("그건 한국어로 어떻게 말해요?")
                    .FontSize(MyTheme.Size200)
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
                .Stroke(MyTheme.Gray300)
                .StrokeShape(new RoundRectangle().CornerRadius(6))
                .Padding(new Thickness(MyTheme.LayoutSpacing, 0))
                .StrokeThickness(1)
                .VEnd(),
            ImageButton()
                .Source(MyTheme.IconFontDecrease)
                .OnClicked(DecreaseFontSize)
                .VCenter()
                .GridColumn(1),
            ImageButton()
                .Source(MyTheme.IconFontIncrease)
                .OnClicked(IncreaseFontSize)
                .VCenter()
                .GridColumn(2),
            ImageButton()
                .Source(MyTheme.IconAdd)
                .VCenter()
                .GridColumn(3)
                .OnClicked(ShowPhrasesPopup)
        )
        .GridRow(1)
        .Margin(MyTheme.LayoutPadding)
        .ColumnSpacing(MyTheme.LayoutSpacing)
        .VEnd();

    void IncreaseFontSize()
    {
        var newSize = Math.Min(State.FontSize + 2, 48.0);
        SetState(s => s.FontSize = newSize);
        Preferences.Set("WarmupActivity_FontSize", State.FontSize);
    }

    void DecreaseFontSize()
    {
        var newSize = Math.Max(State.FontSize - 2, 12.0);
        SetState(s => s.FontSize = newSize);
        Preferences.Set("WarmupActivity_FontSize", State.FontSize);
    }

    async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(State.UserInput))
            return;

        var userMessage = State.UserInput;

        // Check for scenario creation/management intents
        if (State.IsCreatingScenario)
        {
            await HandleScenarioCreationInput(userMessage);
            return;
        }

        // Check for scenario management intents
        var intent = _scenarioService.DetectScenarioIntent(userMessage);

        switch (intent)
        {
            case ScenarioIntent.CreateScenario:
                await StartScenarioCreation();
                return;

            case ScenarioIntent.EditScenario:
                await StartScenarioEditing();
                return;

            case ScenarioIntent.DeleteScenario:
                await HandleDeleteScenario();
                return;
        }

        // Normal conversation flow
        var chunk = new ConversationChunk(
            _conversation.Id,
            DateTime.Now,
            $"{ConversationParticipant.Me.FirstName} {ConversationParticipant.Me.LastName}",
            userMessage,
            ConversationRole.User
        );

        await _agentService.SaveConversationChunkAsync(chunk);

        SetState(s =>
        {
            s.Chunks.Add(chunk);
            s.UserInput = string.Empty;
        });

        // send to the bot for a response
        await Task.Delay(2000);
        await GetReply();
    }

    async Task StartScenarioCreation()
    {
        _logger.LogInformation("Starting scenario creation flow");

        var creationState = new ScenarioCreationState
        {
            CurrentStep = ScenarioCreationStep.AskName
        };

        SetState(s =>
        {
            s.CreationState = creationState;
            s.UserInput = string.Empty;
        });

        // Add system message asking for scenario name
        var question = await _scenarioService.GetNextClarificationQuestionAsync(creationState);
        AddSystemMessage(question ?? $"{_localize["ScenarioAskName"]}");
    }

    async Task HandleScenarioCreationInput(string userMessage)
    {
        if (State.CreationState == null)
            return;

        // Display user's response
        AddUserMessage(userMessage);
        SetState(s => s.UserInput = string.Empty);

        // Check for cancellation
        if (userMessage.ToLowerInvariant().Contains("cancel") || userMessage.Contains("취소"))
        {
            AddSystemMessage($"{_localize["ScenarioCancelled"]}");
            SetState(s => s.CreationState = null);
            return;
        }

        // Check for confirmation at the confirm step
        if (State.CreationState.CurrentStep == ScenarioCreationStep.Confirm)
        {
            var lower = userMessage.ToLowerInvariant();
            if (lower.Contains("yes") || lower.Contains("confirm") || lower.Contains("네") || lower.Contains("확인"))
            {
                await FinalizeScenarioCreation();
                return;
            }
            else if (lower.Contains("no") || lower.Contains("restart") || lower.Contains("아니") || lower.Contains("다시"))
            {
                // Restart creation
                await StartScenarioCreation();
                return;
            }
        }

        // Parse response and update state
        var updatedState = await _scenarioService.ParseCreationResponseAsync(userMessage, State.CreationState);
        SetState(s => s.CreationState = updatedState);

        // Get next question or finalize
        var nextQuestion = await _scenarioService.GetNextClarificationQuestionAsync(updatedState);
        if (nextQuestion != null)
        {
            AddSystemMessage(nextQuestion);
        }
        else if (updatedState.IsComplete)
        {
            await FinalizeScenarioCreation();
        }
    }

    async Task FinalizeScenarioCreation()
    {
        if (State.CreationState == null)
            return;

        try
        {
            var scenario = await _scenarioService.FinalizeScenarioCreationAsync(State.CreationState);

            AddSystemMessage(string.Format($"{_localize["ScenarioCreated"]}", scenario.Name));

            // Refresh available scenarios
            var scenarios = await _scenarioService.GetAllScenariosAsync();
            SetState(s =>
            {
                s.CreationState = null;
                s.AvailableScenarios = scenarios;
                s.ActiveScenario = scenario;
            });

            // Optionally start a conversation with the new scenario
            await StartConversationWithScenario(scenario);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finalizing scenario creation");
            AddSystemMessage($"Error creating scenario: {ex.Message}");
            SetState(s => s.CreationState = null);
        }
    }

    async Task StartScenarioEditing()
    {
        if (State.ActiveScenario == null)
        {
            AddSystemMessage($"{_localize["NoActiveScenarioToEdit"]}");
            return;
        }

        if (State.ActiveScenario.IsPredefined)
        {
            AddSystemMessage($"{_localize["CannotEditPredefined"]}");
            return;
        }

        _logger.LogInformation("Starting scenario editing for: {Name}", State.ActiveScenario.Name);

        // Initialize creation state with existing values for editing
        var editState = new ScenarioCreationState
        {
            CurrentStep = ScenarioCreationStep.AskName,
            IsEditing = true,
            EditingScenarioId = State.ActiveScenario.Id,
            Name = State.ActiveScenario.Name,
            PersonaName = State.ActiveScenario.PersonaName,
            PersonaDescription = State.ActiveScenario.PersonaDescription,
            SituationDescription = State.ActiveScenario.SituationDescription,
            ConversationType = State.ActiveScenario.ConversationType,
            QuestionBank = State.ActiveScenario.QuestionBank
        };

        SetState(s =>
        {
            s.CreationState = editState;
            s.UserInput = string.Empty;
        });

        // Show current values and ask what to change
        AddSystemMessage(string.Format($"{_localize["EditingScenario"]}", State.ActiveScenario.Name));
        var question = await _scenarioService.GetNextClarificationQuestionAsync(editState);
        AddSystemMessage($"{_localize["EditScenarioPrompt"]} {question}");
    }

    async Task HandleDeleteScenario()
    {
        if (State.ActiveScenario == null)
        {
            AddSystemMessage($"{_localize["NoActiveScenarioToDelete"]}");
            return;
        }

        if (State.ActiveScenario.IsPredefined)
        {
            AddSystemMessage($"{_localize["CannotDeletePredefined"]}");
            return;
        }

        // Confirm deletion
        bool confirmed = await Application.Current.MainPage.DisplayAlert(
            $"{_localize["DeleteScenarioConfirm"]}",
            State.ActiveScenario.Name,
            $"{_localize["Yes"]}",
            $"{_localize["No"]}");

        if (!confirmed)
            return;

        try
        {
            await _scenarioService.DeleteScenarioAsync(State.ActiveScenario.Id);

            AddSystemMessage($"{_localize["ScenarioDeleted"]}");

            // Refresh scenarios and switch to default
            var scenarios = await _scenarioService.GetAllScenariosAsync();
            var defaultScenario = scenarios.FirstOrDefault(s => s.Name == "First Meeting") ?? scenarios.FirstOrDefault();

            SetState(s =>
            {
                s.AvailableScenarios = scenarios;
                s.ActiveScenario = defaultScenario;
            });

            if (defaultScenario != null)
            {
                await StartConversationWithScenario(defaultScenario);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting scenario");
            AddSystemMessage($"Error deleting scenario: {ex.Message}");
        }
    }

    void AddSystemMessage(string message)
    {
        var chunk = new ConversationChunk(
            _conversation?.Id ?? 0,
            DateTime.Now,
            "System",
            message,
            ConversationRole.Assistant
        );
        SetState(s => s.Chunks.Add(chunk));
    }

    void AddUserMessage(string message)
    {
        var chunk = new ConversationChunk(
            _conversation?.Id ?? 0,
            DateTime.Now,
            $"{ConversationParticipant.Me.FirstName} {ConversationParticipant.Me.LastName}",
            message,
            ConversationRole.User
        );
        SetState(s => s.Chunks.Add(chunk));
    }

    async Task ResumeConversation()
    {
        // Start activity timer if launched from Today's Plan (only once)
        if (Props?.FromTodaysPlan == true && !_timerService.IsActive)
        {
            _logger.LogDebug("WarmupPage: Starting activity timer for Warmup, PlanItemId: {PlanItemId}", Props.PlanItemId);
            _timerService.StartSession("Warmup", Props.PlanItemId);
        }

        SetState(s => s.IsBusy = true);

        // Load available scenarios
        try
        {
            var scenarios = await _scenarioService.GetAllScenariosAsync();
            SetState(s => s.AvailableScenarios = scenarios);

            // Set default scenario if none active
            if (State.ActiveScenario == null && scenarios.Any())
            {
                var defaultScenario = scenarios.FirstOrDefault(s => s.Name == "First Meeting") ?? scenarios.First();
                SetState(s => s.ActiveScenario = defaultScenario);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading scenarios");
        }

        _conversation = await _agentService.ResumeConversationAsync();

        if (_conversation == null || !_conversation.Chunks.Any())
        {
            await StartConversation();
            return;
        }

        // Load memory state for the conversation
        if (_conversation.Id > 0)
        {
            await _agentService.LoadMemoryStateAsync(_conversation.Id);
        }

        // Load the scenario if conversation has one
        if (_conversation.ScenarioId.HasValue && _conversation.Scenario != null)
        {
            SetState(s => s.ActiveScenario = _conversation.Scenario);
        }

        ObservableCollection<ConversationChunk> chunks = [];

        foreach (var chunk in _conversation.Chunks)
        {
            chunks.Add(chunk);
        }

        SetState(s =>
        {
            s.Chunks = chunks;
            s.IsBusy = false;
        });
    }

    protected override void OnWillUnmount()
    {
        base.OnWillUnmount();

        // Pause timer when leaving activity
        if (Props?.FromTodaysPlan == true && _timerService.IsActive)
        {
            _logger.LogDebug("WarmupPage: Pausing activity timer");
            _timerService.Pause();
        }

        // Clean up audio player and event handler
        CleanupAudioPlayer();
    }

    private void CleanupAudioPlayer()
    {
        if (_audioPlayer != null && _playbackEndedHandler != null)
        {
            _audioPlayer.PlaybackEnded -= _playbackEndedHandler;
            _playbackEndedHandler = null;
        }
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
        SetState(s =>
        {
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

        // Use active scenario or default
        var scenario = State.ActiveScenario;
        if (scenario == null)
        {
            var scenarios = await _scenarioService.GetAllScenariosAsync();
            scenario = scenarios.FirstOrDefault(s => s.Name == "First Meeting") ?? scenarios.FirstOrDefault();
            SetState(s => s.ActiveScenario = scenario);
        }

        await StartConversationWithScenario(scenario);
    }

    async Task StartConversationWithScenario(ConversationScenario? scenario)
    {
        SetState(s =>
        {
            s.IsBusy = true;
            s.Chunks.Clear();
            s.IsConversationComplete = false;
        });

        _conversation = new Conversation
        {
            ScenarioId = scenario?.Id
        };
        await _agentService.SaveConversationAsync(_conversation);

        var personaName = scenario?.PersonaName ?? ConversationParticipant.Bot.FirstName;
        var chunk = new ConversationChunk(_conversation.Id, DateTime.Now, personaName, "...", ConversationRole.Assistant);

        SetState(s => s.Chunks.Add(chunk));

        await Task.Delay(1000);

        // Get opening line based on scenario
        chunk.Text = await _agentService.StartConversationAsync(scenario);
        await _agentService.SaveConversationChunkAsync(chunk);

        SetState(s => s.IsBusy = false);
    }

    async Task GetReply()
    {
        SetState(s => s.IsBusy = true);

        var personaName = State.ActiveScenario?.PersonaName ?? ConversationParticipant.Bot.FirstName;

        // Capture the conversation history BEFORE adding the placeholder
        var conversationHistory = State.Chunks.ToList();

        // Get the user's last message for grading
        var userMessage = conversationHistory.LastOrDefault(c => c.Role == ConversationRole.User)?.Text ?? string.Empty;

        // Add placeholder chunk to show loading state
        var chunk = new ConversationChunk(_conversation.Id, DateTime.Now, personaName, "...", ConversationRole.Assistant);
        SetState(s => s.Chunks.Add(chunk));

        // Get response from agent service (runs conversation + grading in parallel)
        var response = await _agentService.ContinueConversationAsync(userMessage, conversationHistory, State.ActiveScenario);

        // Save memory state after each turn
        if (_conversation?.Id > 0)
        {
            await _agentService.SaveMemoryStateAsync(_conversation.Id);
        }

        chunk.Text = response.Message;

        // Update comprehension and grammar corrections on the user's previous message (which is now second-to-last)
        var previousChunk = State.Chunks[State.Chunks.Count - 2];
        previousChunk.Comprehension = response.Comprehension;
        previousChunk.ComprehensionNotes = response.ComprehensionNotes;
        previousChunk.GrammarCorrections = response.GrammarCorrections;

        // Track user activity
        await _userActivityRepository.SaveAsync(new UserActivity
        {
            Activity = SentenceStudio.Shared.Models.Activity.Warmup.ToString(),
            Input = previousChunk.Text,
            Accuracy = response.Comprehension,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        });

        await _agentService.SaveConversationChunkAsync(previousChunk);
        await _agentService.SaveConversationChunkAsync(chunk);

        SetState(s => s.IsBusy = false);

        // await PlayAudio(response.Message);
    }
    async Task PlayAudio(string text)
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

                // Set up auto-hide when audio finishes (clean up old handler first)
                CleanupAudioPlayer();
                _playbackEndedHandler = (s, e) =>
                {
                    if (_floatingPlayer != null)
                    {
                        _floatingPlayer.Hide();
                    }
                };
                _audioPlayer.PlaybackEnded += _playbackEndedHandler;
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
            _logger.LogError(ex, "WarmupPage: Error playing audio");

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

/// <summary>
/// Converter to show icon based on conversation type (IconRepeatSmall for Finite, IconChatSmall for OpenEnded)
/// </summary>
class ConversationTypeToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is ConversationType type)
        {
            return type == ConversationType.Finite ? MyTheme.IconRepeatSmall : MyTheme.IconChatSmall;
        }
        return MyTheme.IconChatSmall;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converter to check if a scenario ID matches the active scenario
/// </summary>
class IsActiveScenarioConverter : IValueConverter
{
    private readonly int? _activeScenarioId;

    public IsActiveScenarioConverter(int? activeScenarioId)
    {
        _activeScenarioId = activeScenarioId;
    }

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is int id && _activeScenarioId.HasValue)
        {
            return id == _activeScenarioId.Value;
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}