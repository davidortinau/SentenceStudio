using MauiReactor.Shapes;
using Plugin.Maui.Audio;
using System.Collections.ObjectModel;

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
{
    [Inject] TeacherService _teacherService;
    [Inject] ConversationService _conversationService;
    [Inject] AiService _aiService;

    Conversation _conversation;

    CommunityToolkit.Maui.Views.Popup? _popup, _phrasesPopup;

    // Action<string>? _onItemTapped;
    // Action? _onCloseClicked;

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
        return ContentPage("Warmup",
            Grid(rows: "*, Auto","*",
                RenderMessageScroll(),
                RenderInput(),
                RenderExplanationPopup(),
                RenderPhrasesPopup()
            )
        ).OnAppearing(ResumeConversation);
    }

    VisualNode RenderMessageScroll() =>
        ScrollView(
            VStack(
                State.Chunks.Select(c => RenderChunk(c)).ToArray()
            )
            .Spacing(15)
        );

    VisualNode RenderExplanationPopup() =>
        new PopupHost(r => _popup = r)
        {
            VStack(spacing: 10,

                Label(State.Explanation),

                Button("Close", ()=> _popup?.Close(false))
            )
            .BackgroundColor(ApplicationTheme.LightBackground)
        }
        .OnClosed(result => SetState(s =>
        {
            s.IsExplanationShown = false;
            s.PopupResult = (bool?)result;
        }))
        .GridRowSpan(2)       
        .IsShown(State.IsExplanationShown);

    VisualNode RenderPhrasesPopup() =>
        new PopupHost(r => _phrasesPopup = r)
        {
            Grid("*,Auto","",
            ScrollView(
                VStack(spacing: 20,
                    phrases.Select(text =>
                        Label()
                        .Text(text)
                        .OnTapped((sender, args) => {
                            SetState(s=> s.UserInput = (sender as Microsoft.Maui.Controls.Label)?.Text);
                            _phrasesPopup?.Close(true);
                        })
                    )
                )
            ),
            Button()
                .Text("Cancel")
                .GridRow(1)
                // .OnClicked(() => _onCloseClicked?.Invoke())
            )
            .BackgroundColor(ApplicationTheme.LightBackground)
            .Padding(15)
            .Margin(15)
            .HorizontalOptions(LayoutOptions.Fill)
            .MinimumWidthRequest(320)
        }.IsShown(State.IsPhraseListShown);

    VisualNode RenderChunk(ConversationChunk chunk)
    {
        if (chunk.Author.Equals(ConversationParticipant.Bot.FirstName))
        {
            return Border(
                new SelectableLabel()
                    .Text(chunk.Text)
                    
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

    async Task StartConversation()
    {
        await Task.Delay(100);
        State.IsBusy = true;

        _conversation = new Conversation();
        await _conversationService.SaveConversation(_conversation);

        var chunk = new ConversationChunk(_conversation.ID, DateTime.Now, ConversationParticipant.Bot.FirstName, "...");
        State.Chunks.Add(chunk);

        await Task.Delay(1000);
        chunk.Text = "안녕하세요. 이름이 뭐예요?";
        await _conversationService.SaveConversationChunk(chunk);

        State.IsBusy = false;
    }

    async Task GetReply()
    {
        State.IsBusy = true;

        var chunk = new ConversationChunk(_conversation.ID, DateTime.Now, ConversationParticipant.Bot.FirstName, "...");
        State.Chunks.Add(chunk);

        Reply response = await _conversationService.ContinueConveration(State.Chunks.ToList());
        chunk.Text = response.Message;

        var previousChunk = State.Chunks[State.Chunks.Count - 2];
        previousChunk.Comprehension = response.Comprehension;
        previousChunk.ComprehensionNotes = response.ComprehensionNotes;

        await _conversationService.SaveConversationChunk(previousChunk);
        await _conversationService.SaveConversationChunk(chunk);

        State.IsBusy = false;

        await PlayAudio(response.Message);
    }

    async Task PlayAudio(string text)
    {
        var myStream = await _aiService.TextToSpeechAsync(text, "Nova");
        var audioPlayer = AudioManager.Current.CreatePlayer(myStream);
        audioPlayer.Play();
    }
}