using Microsoft.Extensions.Logging;
using MauiReactor;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.VocabularyQuiz;

class VocabularyQuizPreferencesProps
{
    public VocabularyQuizPreferences Preferences { get; set; }
    public Action OnPreferencesSaved { get; set; }
    public Action OnClose { get; set; }
}

class VocabularyQuizPreferencesState
{
    public string DisplayDirection { get; set; }
    public bool AutoPlayVocabAudio { get; set; }
    public bool AutoPlaySampleAudio { get; set; }
    public bool ShowMnemonicImage { get; set; }
    public bool IsSaving { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}

partial class VocabularyQuizPreferencesBottomSheet : Component<VocabularyQuizPreferencesState, VocabularyQuizPreferencesProps>
{
    [Inject] ILogger<VocabularyQuizPreferencesBottomSheet> _logger;
    
    LocalizationManager _localize => LocalizationManager.Instance;
    
    protected override void OnMounted()
    {
        base.OnMounted();
        
        // Load current preferences into state
        SetState(s =>
        {
            s.DisplayDirection = Props.Preferences.DisplayDirection;
            s.AutoPlayVocabAudio = Props.Preferences.AutoPlayVocabAudio;
            s.AutoPlaySampleAudio = Props.Preferences.AutoPlaySampleAudio;
            s.ShowMnemonicImage = Props.Preferences.ShowMnemonicImage;
        });
    }
    
    public override VisualNode Render()
    {
        return VStack(spacing: MyTheme.SectionSpacing,
            // Header
            HStack(spacing: MyTheme.MicroSpacing,
                Label($"{_localize["VocabQuizPreferences"]}")
                    .ThemeKey(MyTheme.Title2)
                    .HStart()
                    .VCenter(),
                    
                ImageButton()
                    .Source(MyTheme.IconClose)
                    .OnClicked(Props.OnClose)
                    .HeightRequest(32)
                    .WidthRequest(32)
                    .HEnd()
            ).Padding(MyTheme.LayoutSpacing),
            
            // Content
            ScrollView(
                VStack(spacing: MyTheme.SectionSpacing,
                    RenderDisplayDirectionSection(),
                    
                    // Error message
                    !string.IsNullOrEmpty(State.ErrorMessage) ?
                        Label(State.ErrorMessage)
                            .TextColor(MyTheme.Error)
                            .HStart() : null,
                    
                    // Save button
                    Button($"{_localize["SavePreferences"]}")
                        .ThemeKey(MyTheme.PrimaryButton)
                        .OnClicked(SavePreferencesAsync)
                        .IsEnabled(!State.IsSaving)
                ).Padding(MyTheme.LayoutSpacing)
            )
        );
    }
    
    VisualNode RenderDisplayDirectionSection() =>
        VStack(spacing: MyTheme.SmallSpacing,
            Label($"{_localize["DisplayDirection"]}")
                .ThemeKey(MyTheme.SubHeadline),
                
            RadioButton()
                .Content($"{_localize["ShowTargetLanguage"]}")
                .IsChecked(State.DisplayDirection == "TargetToNative")
                .OnCheckedChanged((s, e) => 
                {
                    if (e.Value) SetState(s => s.DisplayDirection = "TargetToNative");
                }),
                
            RadioButton()
                .Content($"{_localize["ShowNativeLanguage"]}")
                .IsChecked(State.DisplayDirection == "NativeToTarget")
                .OnCheckedChanged((s, e) => 
                {
                    if (e.Value) SetState(s => s.DisplayDirection = "NativeToTarget");
                })
        );
    
    async Task SavePreferencesAsync()
    {
        SetState(s => { s.IsSaving = true; s.ErrorMessage = string.Empty; });
        
        try
        {
            // Update preferences
            Props.Preferences.DisplayDirection = State.DisplayDirection;
            
            _logger.LogInformation("✅ Vocabulary quiz preferences saved");
            Props.OnPreferencesSaved?.Invoke();
            Props.OnClose?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Exception saving vocabulary quiz preferences");
            SetState(s => s.ErrorMessage = $"{_localize["PreferencesSaveFailed"]}");
        }
        finally
        {
            SetState(s => s.IsSaving = false);
        }
    }
}
