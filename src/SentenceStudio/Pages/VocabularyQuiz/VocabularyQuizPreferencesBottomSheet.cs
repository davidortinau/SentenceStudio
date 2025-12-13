using Microsoft.Extensions.Logging;
using MauiReactor;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.VocabularyQuiz;

class VocabularyQuizPreferencesBottomSheetProps
{
    public Action OnPreferencesSaved { get; set; }
    public Action OnClose { get; set; }
}

class VocabularyQuizPreferencesBottomSheetState
{
    public string DisplayDirection { get; set; }
    public bool AutoPlayVocabAudio { get; set; }
    public bool IsSaving { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}

partial class VocabularyQuizPreferencesBottomSheet : Component<VocabularyQuizPreferencesBottomSheetState, VocabularyQuizPreferencesBottomSheetProps>
{
    [Inject] ILogger<VocabularyQuizPreferencesBottomSheet> _logger;
    [Inject] VocabularyQuizPreferences _preferences;
    
    LocalizationManager _localize => LocalizationManager.Instance;
    
    protected override void OnMounted()
    {
        base.OnMounted();
        
        // Load current preferences into state
        SetState(s =>
        {
            s.DisplayDirection = _preferences.DisplayDirection;
            s.AutoPlayVocabAudio = _preferences.AutoPlayVocabAudio;
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
                    .OnClicked(() => Props.OnClose?.Invoke())
                    .HeightRequest(32)
                    .WidthRequest(32)
                    .HEnd()
            ).Padding(MyTheme.LayoutSpacing),
            
            // Content
            ScrollView(
                VStack(spacing: MyTheme.SectionSpacing,
                    RenderDisplayDirectionSection(),
                    RenderAudioPlaybackSection(),
                    
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
        VStack(spacing: MyTheme.ComponentSpacing,
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
    
    VisualNode RenderAudioPlaybackSection() =>
        VStack(spacing: MyTheme.ComponentSpacing,
            Label($"{_localize["AudioPlayback"]}")
                .ThemeKey(MyTheme.SubHeadline),
                
            CheckBox()
                .IsChecked(State.AutoPlayVocabAudio)
                .OnCheckedChanged((s, e) => SetState(s => s.AutoPlayVocabAudio = e.Value)),
                
            Label($"{_localize["AutoPlayVocabAudio"]}")
                .ThemeKey(MyTheme.Body1)
        );
    
    async Task SavePreferencesAsync()
    {
        SetState(s => { s.IsSaving = true; s.ErrorMessage = string.Empty; });
        
        try
        {
            // Update preferences via service
            _preferences.DisplayDirection = State.DisplayDirection;
            _preferences.AutoPlayVocabAudio = State.AutoPlayVocabAudio;
            
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
