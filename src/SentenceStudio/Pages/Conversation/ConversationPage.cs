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
using ConversationModel = SentenceStudio.Shared.Models.Conversation;

namespace SentenceStudio.Pages.Conversation;

class ConversationPageState
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

    // Target language for dynamic placeholders
    public string TargetLanguage { get; set; } = "Korean";
}

partial class ConversationPage : Component<ConversationPageState, ActivityProps>
{
    [Inject] TeacherService _teacherService;
    [Inject] IConversationAgentService _agentService;
    [Inject] ElevenLabsSpeechService _speechService;
    [Inject] SpeechVoicePreferences _speechVoicePreferences;
    [Inject] UserActivityRepository _userActivityRepository;
    [Inject] SentenceStudio.Services.Timer.IActivityTimerService _timerService;
    [Inject] IScenarioService _scenarioService;
    [Inject] ILogger<ConversationPage> _logger;
    [Inject] UserProfileRepository _userProfileRepository;
    LocalizationManager _localize => LocalizationManager.Instance;
    ConversationModel _conversation;

    private IAudioPlayer _audioPlayer;
    private FloatingAudioPlayer _floatingPlayer;
    private IDispatcherTimer _playbackTimer;
    private EventHandler _playbackEndedHandler;

    // Helper phrases will be generated dynamically based on target language
    string GetInputPlaceholder() => string.Format($"{_localize["ConversationInputPlaceholder"]}", State.TargetLanguage);

    // TODO: Make these phrases dynamic based on target language
    // For now, using generic English helper phrases
    string[] GetQuickPhrases() => new[]
    {
        "How do you say this?",
        "Can you explain that in more detail?",
        "I understand.",
        "Please say that again.",
        "I only speak a little.",
        "Thank you for helping.",
        "Please speak slower.",
        "What does that mean?",
        "Can you translate that?"
    };


    public override VisualNode Render()
    {
        // Hot reload diagnostic - this should log every time Render() is called
        System.Diagnostics.Debug.WriteLine($"🔄 ConversationPage.Render() called at {DateTime.Now:HH:mm:ss.fff}, ActiveScenario={State.ActiveScenario?.Name ?? "none"}");
        _logger.LogDebug("🔄 ConversationPage.Render() called at {Time}, ActiveScenario={Scenario}", DateTime.Now.ToString("HH:mm:ss.fff"), State.ActiveScenario?.Name ?? "none");

        return ContentPage($"{_localize["Conversation"]}",
            ToolbarItem($"{_localize["ChooseScenario"]}").OnClicked(ShowScenarioSelection),
            ToolbarItem(State.TargetLanguage).OnClicked(ShowLanguageSelection),
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
        var theme = BootstrapTheme.Current;
        System.Diagnostics.Debug.WriteLine($"🔍 RenderMessageScroll: IsBusy={State.IsBusy}, ChunksCount={State.Chunks?.Count ?? -1}");

        // Log each chunk being rendered
        foreach (var c in State.Chunks)
        {
            System.Diagnostics.Debug.WriteLine($"📝 Chunk: Author='{c.Author}', Text='{c.Text?.Substring(0, Math.Min(30, c.Text?.Length ?? 0))}...'");
        }

        // Loading state: no messages yet and busy (initial conversation load)
        if (State.IsBusy && (State.Chunks == null || State.Chunks.Count == 0))
        {
            return VStack(
                ActivityIndicator()
                    .IsRunning(true)
                    .Color(theme.Primary),
                Label($"{_localize["StartingConversation"]}")
                    .TextColor(theme.GetOnBackground())
                    .FontSize(16)
                    .HCenter()
            )
            .Spacing(12)
            .Center()
            .GridRow(0)
            .VFill();
        }

        // Build message list with optional typing indicator
        var items = State.Chunks.Select(c => RenderChunk(c)).ToList();

        // Typing indicator: busy and last message is from user (waiting for AI reply)
        if (State.IsBusy && State.Chunks.Count > 0
            && State.Chunks.Last().Role == ConversationRole.User)
        {
            items.Add(
                Border(
                    Label("...")
                        .FontSize(24)
                        .TextColor(Colors.White)
                )
                .Background(theme.Primary)
                .Padding(12, 8)
                .StrokeShape(new RoundRectangle().CornerRadius(12, 12, 12, 0))
                .HStart()
                .Margin(16, 4)
            );
        }

        return ScrollView(
            VStack(
                items.ToArray()
            )
            .Padding(0, 16, 0, 0)
            .Spacing(16)
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
            _logger.LogError(ex, "ConversationPage: Error showing explanation popup");
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
                ItemsSource = GetQuickPhrases(),
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
            _logger.LogError(ex, "ConversationPage: Error showing phrases popup");
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
                        Source = BootstrapIcons.Create(BootstrapIcons.CheckCircleFill, Colors.Green, 16),
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
                        Source = BootstrapIcons.Create(BootstrapIcons.Person, Colors.Gray, 12),
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

    async void ShowLanguageSelection()
    {
        try
        {
            var languages = SentenceStudio.Common.Constants.Languages;

            var popup = new ListActionPopup
            {
                Title = $"{_localize["SelectLanguage"]}",
                ShowActionButton = false,
                ItemsSource = languages,
                ItemDataTemplate = new Microsoft.Maui.Controls.DataTemplate(() =>
                {
                    var tapGesture = new Microsoft.Maui.Controls.TapGestureRecognizer();
                    tapGesture.SetBinding(Microsoft.Maui.Controls.TapGestureRecognizer.CommandParameterProperty, ".");
                    tapGesture.Tapped += async (s, e) =>
                    {
                        if (e is Microsoft.Maui.Controls.TappedEventArgs args && args.Parameter is string lang)
                        {
                            await IPopupService.Current.PopAsync();
                            ChangeLanguage(lang);
                        }
                    };

                    var layout = new Microsoft.Maui.Controls.HorizontalStackLayout { Spacing = 8, Padding = new Thickness(0, 8) };
                    layout.GestureRecognizers.Add(tapGesture);

                    var label = new Microsoft.Maui.Controls.Label
                    {
                        FontSize = 16,
                        TextColor = Colors.White,
                        VerticalOptions = LayoutOptions.Center
                    };
                    label.SetBinding(Microsoft.Maui.Controls.Label.TextProperty, ".");

                    var checkIcon = new Microsoft.Maui.Controls.Image
                    {
                        Source = BootstrapIcons.Create(BootstrapIcons.CheckCircleFill, Colors.Green, 16),
                        WidthRequest = 16,
                        HeightRequest = 16,
                        VerticalOptions = LayoutOptions.Center,
                        HorizontalOptions = LayoutOptions.EndAndExpand
                    };
                    checkIcon.SetBinding(Microsoft.Maui.Controls.Image.IsVisibleProperty,
                        new Microsoft.Maui.Controls.Binding(".",
                            converter: new IsActiveLanguageConverter(State.TargetLanguage)));

                    layout.Children.Add(label);
                    layout.Children.Add(checkIcon);

                    return layout;
                })
            };
            await IPopupService.Current.PushAsync(popup);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error showing language selection");
        }
    }

    async void ChangeLanguage(string language)
    {
        if (language == State.TargetLanguage) return;

        _logger.LogInformation("Changing language to: {Language}", language);

        Preferences.Default.Set("ConversationActivity_Language", language);
        SetState(s => s.TargetLanguage = language);

        // Restart the conversation with the new language
        await StartConversationWithScenario(State.ActiveScenario);
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
        var theme = BootstrapTheme.Current;
        // Use the Role property to determine message type - this is the authoritative source
        var isUserMessage = chunk.Role == ConversationRole.User;
        System.Diagnostics.Debug.WriteLine($"RenderChunk: Author='{chunk.Author}', Role={chunk.Role}, IsUser={isUserMessage}");

        if (!isUserMessage)
        {
            // Bot/System message (left-aligned, dark background)
            return Border(
                VStack(spacing: 4,
                    new SelectableLabel()
                        .Text(chunk.Text)
                        .TextColor(Colors.White)
                        .FontSize(State.FontSize),

                    ImageButton()
                        .Source(BootstrapIcons.Create(BootstrapIcons.VolumeUp, Colors.White, 16))
                        .Background(Colors.Transparent)
                        .OnClicked(() => PlayAudio(chunk.Text))
                        .WidthRequest(32)
                        .HeightRequest(32)
                        .HStart()
                )

            )
            .Margin(new Thickness(16, 4))
            .Padding(new Thickness(16, 4, 16, 8))
            .Background(theme.Primary)
            .Stroke(theme.Primary)
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
                    VStack(
                        new SelectableLabel()
                            .Text(chunk.Text)
                            .FontSize(State.FontSize),

                        // Inline grammar corrections display
                        hasCorrections ?
                            RenderGrammarCorrections(chunk.GrammarCorrections!)
                            : null,

                        // Comprehension score
                        hasComprehension ?
                            Label($"{_localize["Comprehension"]}: {(int)(chunk.Comprehension * 100)}%")
                                .FontSize(12)
                                .TextColor(theme.OnSurface.WithAlpha(0.6f))
                                .Margin(new Thickness(0, 4, 0, 0))
                            : null
                    )
                    .Spacing(0)
                )
                .Padding(new Thickness(16, 4, 16, 8))
                .Background(theme.Info)
                .Stroke(theme.Info)
                .StrokeShape(new RoundRectangle().CornerRadius(10, 0, 10, 2))
                .GridColumn(0),

                // Grammar/feedback indicator icon - only show if there's feedback
                hasFeedback ?
                    ImageButton()
                        .Source(BootstrapIcons.Create(hasCorrections ? BootstrapIcons.Spellcheck : BootstrapIcons.InfoCircle, theme.GetOnBackground(), 20))
                        .BackgroundColor(Colors.Transparent)
                        .Padding(4)
                        .GridColumn(1)
                        .VCenter()
                        .OnClicked(() => ShowExplanation(chunk))
                    : null
            )
            .Margin(new Thickness(16, 4))
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
            sb.AppendLine("Grammar Corrections:");
            sb.AppendLine();
            foreach (var correction in corrections)
            {
                sb.AppendLine($"✗ {correction.Original}");
                sb.AppendLine($"✓ {correction.Corrected}");
                if (!string.IsNullOrEmpty(correction.Explanation))
                {
                    sb.AppendLine($"Note: {correction.Explanation}");
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
            _logger.LogError(e, "ConversationPage: Error showing explanation");
        }
    }

    VisualNode RenderGrammarCorrections(List<GrammarCorrectionDto> corrections)
    {
        var theme = BootstrapTheme.Current;

        return VStack(
            // Separator line
            BoxView()
                .HeightRequest(1)
                .BackgroundColor(theme.OnSurface.WithAlpha(0.2f))
                .Margin(new Thickness(0, 8, 0, 4)),

            // Each correction: Original (strikethrough) -> Corrected (bold)
            corrections.Select(c =>
                (VisualNode)VStack(
                    HStack(
                        Label(c.Original)
                            .FontSize(12)
                            .TextColor(theme.Danger)
                            .TextDecorations(TextDecorations.Strikethrough),
                        Label(" → ")
                            .FontSize(12)
                            .TextColor(theme.OnSurface.WithAlpha(0.6f)),
                        Label(c.Corrected)
                            .FontSize(12)
                            .TextColor(theme.Success)
                            .FontAttributes(Microsoft.Maui.Controls.FontAttributes.Bold)
                    )
                    .Spacing(2),

                    !string.IsNullOrEmpty(c.Explanation) ?
                        Label(c.Explanation)
                            .FontSize(11)
                            .TextColor(theme.OnSurface.WithAlpha(0.5f))
                            .Margin(new Thickness(0, 2, 0, 0))
                        : null
                )
                .Spacing(0)
            ).ToArray()
        )
        .Spacing(4);
    }

    VisualNode RenderInput()
    {
        var theme = BootstrapTheme.Current;
        var isWaiting = State.IsBusy;
        return Grid("", "* Auto Auto Auto Auto",
            Border(
                Entry()
                    .Placeholder(GetInputPlaceholder())
                    .FontSize(20)
                    .ReturnType(ReturnType.Send)
                    .Text(State.UserInput)
                    .OnTextChanged((s, e) => State.UserInput = e.NewTextValue)
                    .OnCompleted(async () =>
                    {
                        await SendMessage();
                    })
                    .IsEnabled(!isWaiting)
            )
                .Background(Colors.Transparent)
                .Stroke(theme.GetOutline())
                .StrokeShape(new RoundRectangle().CornerRadius(6))
                .Padding(new Thickness(16, 0))
                .StrokeThickness(1)
                .VEnd(),
            Button($"{_localize["Send"]}")
                .Primary()
                .IsEnabled(!isWaiting && !string.IsNullOrWhiteSpace(State.UserInput))
                .OnClicked(async () => await SendMessage())
                .VCenter()
                .GridColumn(1),
            ImageButton()
                .Source(BootstrapIcons.Create(BootstrapIcons.DashLg, theme.GetOnBackground(), 20))
                .OnClicked(DecreaseFontSize)
                .IsEnabled(!isWaiting)
                .VCenter()
                .GridColumn(2),
            ImageButton()
                .Source(BootstrapIcons.Create(BootstrapIcons.PlusLg, theme.GetOnBackground(), 20))
                .OnClicked(IncreaseFontSize)
                .IsEnabled(!isWaiting)
                .VCenter()
                .GridColumn(3),
            ImageButton()
                .Source(BootstrapIcons.Create(BootstrapIcons.ChatLeftDots, theme.GetOnBackground(), 20))
                .VCenter()
                .GridColumn(4)
                .IsEnabled(!isWaiting)
                .OnClicked(ShowPhrasesPopup)
        )
        .GridRow(1)
        .Margin(16)
        .ColumnSpacing(16)
        .VEnd();
    }

    void IncreaseFontSize()
    {
        var newSize = Math.Min(State.FontSize + 2, 48.0);
        SetState(s => s.FontSize = newSize);
        Preferences.Set("ConversationActivity_FontSize", State.FontSize);
    }

    void DecreaseFontSize()
    {
        var newSize = Math.Max(State.FontSize - 2, 12.0);
        SetState(s => s.FontSize = newSize);
        Preferences.Set("ConversationActivity_FontSize", State.FontSize);
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
        var tcs = new TaskCompletionSource<bool>();
        var confirmPopup = new SimpleActionPopup
        {
            Title = $"{_localize["DeleteScenarioConfirm"]}",
            Text = State.ActiveScenario.Name,
            ActionButtonText = $"{_localize["Yes"]}",
            SecondaryActionButtonText = $"{_localize["No"]}",
            CloseWhenBackgroundIsClicked = false,
            ActionButtonCommand = new Command(async () =>
            {
                tcs.TrySetResult(true);
                await IPopupService.Current.PopAsync();
            }),
            SecondaryActionButtonCommand = new Command(async () =>
            {
                tcs.TrySetResult(false);
                await IPopupService.Current.PopAsync();
            })
        };
        await IPopupService.Current.PushAsync(confirmPopup);
        bool confirmed = await tcs.Task;

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
            _logger.LogDebug("ConversationPage: Starting activity timer for Conversation, PlanItemId: {PlanItemId}", Props.PlanItemId);
            _timerService.StartSession("Conversation", Props.PlanItemId);
        }

        SetState(s => s.IsBusy = true);

        // Load target language from Preferences (set by dashboard tile)
        var selectedLanguage = Preferences.Default.Get("ConversationActivity_Language", string.Empty);
        if (string.IsNullOrEmpty(selectedLanguage))
        {
            // Fallback to user profile target language
            try
            {
                var userProfile = await _userProfileRepository.GetAsync();
                selectedLanguage = userProfile?.TargetLanguage ?? "Korean";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading user profile for target language");
                selectedLanguage = "Korean";
            }
        }
        SetState(s => s.TargetLanguage = selectedLanguage);

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

        _conversation = await _agentService.ResumeConversationAsync(selectedLanguage);

        if (_conversation == null || _conversation.Chunks == null || !_conversation.Chunks.Any())
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
            _logger.LogDebug("ConversationPage: Pausing activity timer");
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
        var tcs = new TaskCompletionSource<bool>();
        var confirmPopup = new SimpleActionPopup
        {
            Title = "Start New Conversation",
            Text = "This will clear the current conversation. Are you sure?",
            ActionButtonText = "Yes",
            SecondaryActionButtonText = "No",
            CloseWhenBackgroundIsClicked = false,
            ActionButtonCommand = new Command(async () =>
            {
                tcs.TrySetResult(true);
                await IPopupService.Current.PopAsync();
            }),
            SecondaryActionButtonCommand = new Command(async () =>
            {
                tcs.TrySetResult(false);
                await IPopupService.Current.PopAsync();
            })
        };
        await IPopupService.Current.PushAsync(confirmPopup);
        bool shouldStart = await tcs.Task;

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

        _conversation = new ConversationModel
        {
            ScenarioId = scenario?.Id,
            Language = State.TargetLanguage
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
            Activity = SentenceStudio.Shared.Models.Activity.Conversation.ToString(),
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

            // Use ElevenLabsSpeechService with per-language voice preference
            var voiceId = _speechVoicePreferences.GetVoiceForLanguage(State.TargetLanguage);
            var audioStream = await _speechService.TextToSpeechAsync(
                text,
                voiceId);

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
            _logger.LogError(ex, "ConversationPage: Error playing audio");

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
            return type == ConversationType.Finite
                ? BootstrapIcons.Create(BootstrapIcons.ArrowRepeat, Colors.Gray, 16)
                : BootstrapIcons.Create(BootstrapIcons.ChatDots, Colors.Gray, 16);
        }
        return BootstrapIcons.Create(BootstrapIcons.ChatDots, Colors.Gray, 16);
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

/// <summary>
/// Converter to check if a language string matches the active language
/// </summary>
class IsActiveLanguageConverter : IValueConverter
{
    private readonly string _activeLanguage;

    public IsActiveLanguageConverter(string activeLanguage)
    {
        _activeLanguage = activeLanguage;
    }

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string lang)
        {
            return string.Equals(lang, _activeLanguage, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}