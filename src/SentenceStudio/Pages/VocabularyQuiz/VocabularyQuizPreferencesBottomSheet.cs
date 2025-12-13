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
    public string SelectedVoiceId { get; set; }
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
            s.SelectedVoiceId = _preferences.VoiceId;
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
                .ThemeKey(MyTheme.Body1),
            
            // Voice selection picker
            Label($"{_localize["VoiceSelection"]}")
                .ThemeKey(MyTheme.Body1)
                .Margin(0, MyTheme.ComponentSpacing, 0, 0),
                
            Picker()
                .Title($"{_localize["SelectVoice"]}")
                .ItemsSource(GetVoiceOptions())
                .SelectedIndex(GetVoiceIndexFromId(State.SelectedVoiceId))
                .OnSelectedIndexChanged((index) =>
                {
                    if (index >= 0 && index < GetVoiceOptions().Length)
                    {
                        var selectedDisplay = GetVoiceOptions()[index];
                        var voiceId = GetVoiceIdFromDisplayName(selectedDisplay);
                        SetState(s => s.SelectedVoiceId = voiceId);
                        _logger.LogInformation("🎤 Voice selected: {DisplayName} ({VoiceId})", selectedDisplay, voiceId);
                    }
                })
                .IsEnabled(State.AutoPlayVocabAudio)
        );
    
    async Task SavePreferencesAsync()
    {
        SetState(s => { s.IsSaving = true; s.ErrorMessage = string.Empty; });
        
        try
        {
            // Update preferences via service
            _preferences.DisplayDirection = State.DisplayDirection;
            _preferences.AutoPlayVocabAudio = State.AutoPlayVocabAudio;
            _preferences.VoiceId = State.SelectedVoiceId;
            
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
    
    // Helper methods for voice selection
    string[] GetVoiceOptions()
    {
        return new[]
        {
            "Ji-Young (Korean, Female)",
            "Yuna (Korean, Female)",
            "Jennie (Korean, Female)",
            "Jina (Korean, Female)",
            "Hyun-Bin (Korean, Male)",
            "Do-Hyeon (Korean, Male)",
            "Yohan Koo (Korean, Male)"
        };
    }
    
    int GetVoiceIndexFromId(string voiceId)
    {
        var displayName = GetVoiceDisplayName(voiceId);
        var options = GetVoiceOptions();
        return Array.IndexOf(options, displayName);
    }
    
    string GetVoiceDisplayName(string voiceId)
    {
        return voiceId switch
        {
            var id when id == Voices.JiYoung => "Ji-Young (Korean, Female)",
            var id when id == Voices.Yuna => "Yuna (Korean, Female)",
            var id when id == Voices.Jennie => "Jennie (Korean, Female)",
            var id when id == Voices.Jina => "Jina (Korean, Female)",
            var id when id == Voices.HyunBin => "Hyun-Bin (Korean, Male)",
            var id when id == Voices.DoHyeon => "Do-Hyeon (Korean, Male)",
            var id when id == Voices.YohanKoo => "Yohan Koo (Korean, Male)",
            _ => "Ji-Young (Korean, Female)" // Default
        };
    }
    
    string GetVoiceIdFromDisplayName(string displayName)
    {
        return displayName switch
        {
            "Ji-Young (Korean, Female)" => Voices.JiYoung,
            "Yuna (Korean, Female)" => Voices.Yuna,
            "Jennie (Korean, Female)" => Voices.Jennie,
            "Jina (Korean, Female)" => Voices.Jina,
            "Hyun-Bin (Korean, Male)" => Voices.HyunBin,
            "Do-Hyeon (Korean, Male)" => Voices.DoHyeon,
            "Yohan Koo (Korean, Male)" => Voices.YohanKoo,
            _ => Voices.JiYoung // Default
        };
    }
}
