using CommunityToolkit.Maui.Core;
using MauiReactor;
using MauiReactor.Shapes;
using Plugin.Maui.Audio;
using SentenceStudio.Models;
using SentenceStudio.Pages.Controls;
using SentenceStudio.Services;
using System.Collections.ObjectModel;

namespace SentenceStudio.Pages.Warmup;

class WarmupPageState
{
    public ObservableCollection<ConversationChunk> Chunks { get; set; } = new();
    public string UserInput { get; set; }
    public bool IsBusy { get; set; }

    public bool IsPopupShown { get; set; }

    public bool? PopupResult { get; set; }

    public string Explanation { get; set; }
}

partial class WarmupPage : Component<WarmupPageState>
{
    [Inject] TeacherService _teacherService;
    [Inject] ConversationService _conversationService;
    [Inject] AiService _aiService;

    private Conversation _conversation;

    private CommunityToolkit.Maui.Views.Popup? _popup;

    public override VisualNode Render()
    {
        return ContentPage("Warmup",
            Grid(rows: "*, Auto","*",
                ScrollView(
                    VStack(
                        State.Chunks.Select(c => RenderChunk(c)).ToArray()
                    )
                    .Spacing(15)
                ),
                Input(),
                new PopupHost(r => _popup = r)
                {
                    VStack(spacing: 10,
                    
                        Label("Hi!"),

                        HStack(spacing: 10,
                        
                            Button("OK", ()=> _popup?.Close(true)),

                            Button("Cancel", ()=> _popup?.Close(false))
                        )
                    )
                }
                .GridRowSpan(2)
                .IsShown(State.IsPopupShown)
                .OnClosed(result => SetState(s =>
                {
                    s.IsPopupShown = false;
                    s.PopupResult = (bool?)result;
                }))
            )
        ).OnAppearing(ResumeConversation);
    }

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
            .Background((Color)Application.Current.Resources["Primary"])
            .Stroke((Color)Application.Current.Resources["Primary"])
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

    private async Task ShowExplanation(ConversationChunk s)
    {
        string explanation = $"Comprehension Score: {s.Comprehension}" + Environment.NewLine + Environment.NewLine;
        explanation += $"{s.ComprehensionNotes}" + Environment.NewLine + Environment.NewLine;
        
        try{
            SetState(s =>{
                s.Explanation = explanation;
                s.IsPopupShown = true;
            });
        }catch(Exception e){
            Debug.WriteLine(e.Message);
        }
    }

    VisualNode Input() =>
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
            .Stroke((Color)Application.Current.Resources["Gray300"])
            .StrokeShape(new RoundRectangle().CornerRadius(6))
            .Padding(new Thickness(15, 0))
            .StrokeThickness(1)
            .VerticalOptions(LayoutOptions.End),
            Button()
                .BackgroundColor(Colors.Transparent)
                // .ImageSource(SegoeFluentIcons.Add)
                // .IconSize(18)
                // .AppThemeBinding(Button.TextColorProperty, (Color)Application.Current.Resources["DarkOnLightBackground"], (Color)Application.Current.Resources["LightOnDarkBackground"])
                .VCenter()
                // .BindCommand(nameof(WarmupPageModel.GetPhraseCommand))
                .GridColumn(1)
        )
        .GridRow(1)
        .Margin(new Thickness(15))
        .ColumnSpacing(15)
        .VEnd();

    private async Task SendMessage()
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

    private async Task ResumeConversation()
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

    private async Task StartConversation()
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

    private async Task GetReply()
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

    private async Task PlayAudio(string text)
    {
        var myStream = await _aiService.TextToSpeechAsync(text, "Nova");
        var audioPlayer = AudioManager.Current.CreatePlayer(myStream);
        audioPlayer.Play();
    }
}