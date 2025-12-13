# Quickstart Guide: Vocabulary Quiz Preferences

**Feature**: 001-vocab-quiz-preferences  
**Target Audience**: Developers implementing this feature  
**Estimated Time**: 3-4 hours

## Prerequisites

- ✅ .NET 10 SDK with MAUI workloads installed
- ✅ SentenceStudio repository cloned and building
- ✅ Database with existing UserProfile table
- ✅ Plugin.Maui.Audio integrated (already in project)
- ✅ Basic understanding of MauiReactor MVU pattern
- ✅ Read: [research.md](research.md), [data-model.md](data-model.md), [contracts/service-contracts.md](contracts/service-contracts.md)

## Implementation Checklist

### Phase 1: Database Schema (30 minutes)

**Task 1.1: Create Migration**

```bash
cd src/SentenceStudio.Shared
dotnet ef migrations add AddVocabularyQuizPreferences --context ApplicationDbContext
```

**Task 1.2: Verify Migration File**

Open `Migrations/[timestamp]_AddVocabularyQuizPreferences.cs` and verify:

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.AddColumn<string>(
        name: "VocabQuizDisplayDirection",
        table: "UserProfiles",
        type: "TEXT",
        nullable: false,
        defaultValue: "TargetToNative");

    migrationBuilder.AddColumn<bool>(
        name: "VocabQuizAutoPlayVocabAudio",
        table: "UserProfiles",
        type: "INTEGER",
        nullable: false,
        defaultValue: true);

    migrationBuilder.AddColumn<bool>(
        name: "VocabQuizAutoPlaySampleAudio",
        table: "UserProfiles",
        type: "INTEGER",
        nullable: false,
        defaultValue: false);

    migrationBuilder.AddColumn<bool>(
        name: "VocabQuizShowMnemonicImage",
        table: "UserProfiles",
        type: "INTEGER",
        nullable: false,
        defaultValue: true);
}
```

**Task 1.3: Update UserProfile Model**

Edit `src/SentenceStudio.Shared/Models/UserProfile.cs`:

```csharp
[Table("UserProfiles")]
public class UserProfile
{
    // ... existing properties ...
    
    public string VocabQuizDisplayDirection { get; set; } = "TargetToNative";
    public bool VocabQuizAutoPlayVocabAudio { get; set; } = true;
    public bool VocabQuizAutoPlaySampleAudio { get; set; } = false;
    public bool VocabQuizShowMnemonicImage { get; set; } = true;
}
```

**Task 1.4: Apply Migration**

```bash
cd src/SentenceStudio
dotnet build -t:Run -f net10.0-maccatalyst
# Migration auto-applies on app start
```

**Verification**: Use SQLite browser to check UserProfiles table has new columns.

---

### Phase 2: Localization Strings (20 minutes)

**Task 2.1: Add English Strings**

Edit `src/SentenceStudio/Resources/Strings/Resources.resx`:

```xml
<data name="VocabQuizPreferences" xml:space="preserve">
  <value>Vocabulary Quiz Preferences</value>
</data>
<data name="DisplayDirection" xml:space="preserve">
  <value>Display Direction</value>
</data>
<data name="ShowTargetLanguage" xml:space="preserve">
  <value>Show target language (한국어 → English)</value>
</data>
<data name="ShowNativeLanguage" xml:space="preserve">
  <value>Show native language (English → 한국어)</value>
</data>
<data name="AudioPlayback" xml:space="preserve">
  <value>Audio Playback</value>
</data>
<data name="AutoPlayVocabAudio" xml:space="preserve">
  <value>Auto-play vocabulary audio</value>
</data>
<data name="AutoPlaySampleAudio" xml:space="preserve">
  <value>Auto-play sample sentence</value>
</data>
<data name="RequiresVocabAudio" xml:space="preserve">
  <value>(requires vocabulary audio)</value>
</data>
<data name="ConfirmationDisplay" xml:space="preserve">
  <value>Confirmation Display</value>
</data>
<data name="ShowMnemonicImage" xml:space="preserve">
  <value>Show mnemonic image</value>
</data>
<data name="SavePreferences" xml:space="preserve">
  <value>Save Preferences</value>
</data>
<data name="PreferencesSaveFailed" xml:space="preserve">
  <value>Failed to save preferences. Please try again.</value>
</data>
```

**Task 2.2: Add Korean Translations**

Edit `src/SentenceStudio/Resources/Strings/Resources.ko.resx` (use same keys, Korean values).

**Verification**: Run app, change language to Korean, verify strings appear in Korean.

---

### Phase 3: Preferences Bottom Sheet UI (60 minutes)

**Task 3.1: Create Component File**

Create `src/SentenceStudio/Pages/VocabularyQuiz/VocabularyQuizPreferencesBottomSheet.cs`:

```csharp
using Microsoft.Extensions.Logging;
using MauiReactor;
using Plugin.Maui.Audio;

namespace SentenceStudio.Pages.VocabularyQuiz;

class VocabularyQuizPreferencesProps
{
    public UserProfile UserProfile { get; set; }
    public Action<UserProfile> OnPreferencesSaved { get; set; }
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
    [Inject] UserProfileRepository _userProfileRepo;
    [Inject] ILogger<VocabularyQuizPreferencesBottomSheet> _logger;
    
    LocalizationManager _localize => LocalizationManager.Instance;
    
    protected override void OnMounted()
    {
        base.OnMounted();
        
        // Load current preferences into state
        SetState(s =>
        {
            s.DisplayDirection = Props.UserProfile.VocabQuizDisplayDirection;
            s.AutoPlayVocabAudio = Props.UserProfile.VocabQuizAutoPlayVocabAudio;
            s.AutoPlaySampleAudio = Props.UserProfile.VocabQuizAutoPlaySampleAudio;
            s.ShowMnemonicImage = Props.UserProfile.VocabQuizShowMnemonicImage;
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
                    RenderAudioPlaybackSection(),
                    RenderConfirmationSection(),
                    
                    // Error message
                    !string.IsNullOrEmpty(State.ErrorMessage) ?
                        Label(State.ErrorMessage)
                            .TextColor(MyTheme.Error)
                            .HStart() : null,
                    
                    // Save button
                    Button($"{_localize["SavePreferences"]}")
                        .ThemeKey(MyTheme.Primary)
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
    
    VisualNode RenderAudioPlaybackSection() =>
        VStack(spacing: MyTheme.SmallSpacing,
            Label($"{_localize["AudioPlayback"]}")
                .ThemeKey(MyTheme.SubHeadline),
                
            CheckBox()
                .IsChecked(State.AutoPlayVocabAudio)
                .OnCheckedChanged((s, e) => SetState(s => s.AutoPlayVocabAudio = e.Value)),
            Label($"{_localize["AutoPlayVocabAudio"]}")
                .ThemeKey(MyTheme.Body1),
                
            CheckBox()
                .IsChecked(State.AutoPlaySampleAudio)
                .IsEnabled(State.AutoPlayVocabAudio)
                .OnCheckedChanged((s, e) => SetState(s => s.AutoPlaySampleAudio = e.Value)),
            Label($"{_localize["AutoPlaySampleAudio"]} {_localize["RequiresVocabAudio"]}")
                .ThemeKey(MyTheme.Body1)
                .Opacity(State.AutoPlayVocabAudio ? 1.0 : 0.5)
        );
    
    VisualNode RenderConfirmationSection() =>
        VStack(spacing: MyTheme.SmallSpacing,
            Label($"{_localize["ConfirmationDisplay"]}")
                .ThemeKey(MyTheme.SubHeadline),
                
            CheckBox()
                .IsChecked(State.ShowMnemonicImage)
                .OnCheckedChanged((s, e) => SetState(s => s.ShowMnemonicImage = e.Value)),
            Label($"{_localize["ShowMnemonicImage"]}")
                .ThemeKey(MyTheme.Body1)
        );
    
    async Task SavePreferencesAsync()
    {
        SetState(s => { s.IsSaving = true; s.ErrorMessage = string.Empty; });
        
        try
        {
            // Update profile object
            Props.UserProfile.VocabQuizDisplayDirection = State.DisplayDirection;
            Props.UserProfile.VocabQuizAutoPlayVocabAudio = State.AutoPlayVocabAudio;
            Props.UserProfile.VocabQuizAutoPlaySampleAudio = State.AutoPlaySampleAudio;
            Props.UserProfile.VocabQuizShowMnemonicImage = State.ShowMnemonicImage;
            
            // Save to database
            var success = await _userProfileRepo.UpdateUserProfileAsync(Props.UserProfile);
            
            if (success)
            {
                _logger.LogInformation("✅ Vocabulary quiz preferences saved");
                Props.OnPreferencesSaved?.Invoke(Props.UserProfile);
                Props.OnClose?.Invoke();
            }
            else
            {
                SetState(s => s.ErrorMessage = $"{_localize["PreferencesSaveFailed"]}");
                _logger.LogError("❌ Failed to save vocabulary quiz preferences");
            }
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
```

**Verification**: Compile and check for syntax errors.

---

### Phase 4: Integrate into VocabularyQuizPage (90 minutes)

**Task 4.1: Add State Fields**

Edit `src/SentenceStudio/Pages/VocabularyQuiz/VocabularyQuizPage.cs`:

In `VocabularyQuizPageState` class:

```csharp
class VocabularyQuizPageState
{
    // ... existing fields ...
    
    public UserProfile UserPreferences { get; set; }
    public bool ShowPreferencesSheet { get; set; }
    public IAudioPlayer VocabularyAudioPlayer { get; set; }
    public IAudioPlayer SampleAudioPlayer { get; set; }
}
```

**Task 4.2: Add Toolbar Icon**

In `VocabularyQuizPage.Render()`, add preferences icon to toolbar:

```csharp
.ToolbarItems(
    new ToolbarItem()
    {
        IconImageSource = MyTheme.IconSettings,
        Command = new Command(OpenPreferences)
    }
)
```

**Task 4.3: Add Preferences Sheet to Page**

In `Render()` method's main Grid, add bottom sheet overlay:

```csharp
Grid(rows: "*", columns: "*",
    // ... existing quiz content ...
    
    // Preferences bottom sheet overlay
    State.ShowPreferencesSheet && State.UserPreferences != null ?
        new Syncfusion.Maui.Popup.SfBottomSheet()
        {
            Content = new VocabularyQuizPreferencesBottomSheet()
            {
                Props = new VocabularyQuizPreferencesProps
                {
                    UserProfile = State.UserPreferences,
                    OnPreferencesSaved = OnPreferencesSaved,
                    OnClose = ClosePreferences
                }
            }
        } : null
)
```

**Task 4.4: Implement Preference Methods**

Add these methods to `VocabularyQuizPage` class:

```csharp
async Task LoadUserPreferencesAsync()
{
    var profile = await _userProfileRepo.GetCurrentUserProfileAsync();
    SetState(s => s.UserPreferences = profile);
    _logger.LogInformation("📋 Loaded vocabulary quiz preferences: DisplayDirection={Direction}, AutoPlayVocab={AutoVocab}, AutoPlaySample={AutoSample}, ShowMnemonic={ShowMnemonic}", 
        profile.VocabQuizDisplayDirection,
        profile.VocabQuizAutoPlayVocabAudio,
        profile.VocabQuizAutoPlaySampleAudio,
        profile.VocabQuizShowMnemonicImage);
}

void OpenPreferences()
{
    _logger.LogInformation("⚙️ Opening vocabulary quiz preferences");
    SetState(s => s.ShowPreferencesSheet = true);
}

void ClosePreferences()
{
    _logger.LogInformation("⚙️ Closing vocabulary quiz preferences");
    SetState(s => s.ShowPreferencesSheet = false);
}

void OnPreferencesSaved(UserProfile updatedProfile)
{
    _logger.LogInformation("✅ Preferences saved, reloading");
    SetState(s => s.UserPreferences = updatedProfile);
}
```

**Task 4.5: Load Preferences in OnMounted**

Modify `OnMounted()` lifecycle method:

```csharp
protected override void OnMounted()
{
    _logger.LogDebug("🚀 VocabularyQuizPage.OnMounted() START");
    base.OnMounted();
    
    // Load preferences
    Task.Run(async () => await LoadUserPreferencesAsync());
    
    // ... existing OnMounted logic ...
}
```

**Task 4.6: Implement Audio Playback**

Add audio playback methods:

```csharp
[Inject] IAudioManager _audioManager;

async Task PlayVocabularyAudioAsync(VocabularyWord word)
{
    if (!State.UserPreferences?.VocabQuizAutoPlayVocabAudio ?? false)
        return;
    
    if (string.IsNullOrEmpty(word.AudioPronunciationUri))
    {
        _logger.LogWarning("⚠️ Auto-play enabled but missing audio for word: {Word} (ID: {Id})", 
            word.TargetLanguageTerm, word.Id);
        return;
    }
    
    try
    {
        // Stop existing player
        State.VocabularyAudioPlayer?.Stop();
        State.VocabularyAudioPlayer?.Dispose();
        
        // Load audio stream (check cache first via StreamHistoryRepository)
        var cachedAudio = await _historyRepo.GetStreamHistoryByPhraseAndVoiceAsync(
            word.TargetLanguageTerm, Voices.JiYoung);
        
        Stream audioStream;
        if (cachedAudio != null && File.Exists(cachedAudio.AudioFilePath))
        {
            audioStream = File.OpenRead(cachedAudio.AudioFilePath);
        }
        else
        {
            // Generate via ElevenLabs
            var audioBytes = await _speechService.GenerateSpeechAsync(
                word.TargetLanguageTerm, Voices.JiYoung);
            audioStream = new MemoryStream(audioBytes);
        }
        
        // Create player and subscribe to completion
        State.VocabularyAudioPlayer = _audioManager.CreatePlayer(audioStream);
        State.VocabularyAudioPlayer.PlaybackEnded += OnVocabularyAudioEnded;
        State.VocabularyAudioPlayer.Play();
        
        _logger.LogInformation("🎧 Playing vocabulary audio for: {Word}", word.TargetLanguageTerm);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "❌ Failed to play vocabulary audio for word: {Word}", 
            word.TargetLanguageTerm);
    }
}

void OnVocabularyAudioEnded(object sender, EventArgs e)
{
    _logger.LogInformation("✅ Vocabulary audio ended");
    
    // Cleanup
    State.VocabularyAudioPlayer.PlaybackEnded -= OnVocabularyAudioEnded;
    
    // Check if should play sample sentence
    if (State.UserPreferences?.VocabQuizAutoPlaySampleAudio ?? false)
    {
        var currentWord = State.VocabularyItems[State.CurrentTurn - 1];
        _ = PlaySampleAudioAsync(currentWord);
    }
}

async Task PlaySampleAudioAsync(VocabularyWord word)
{
    // Load example sentences
    var sentences = await _exampleRepo.GetExampleSentencesByVocabularyWordIdAsync(word.Id);
    
    // Select sentence (IsCore first, oldest CreatedAt)
    var selectedSentence = sentences
        .Where(s => !string.IsNullOrEmpty(s.AudioUri))
        .OrderByDescending(s => s.IsCore)
        .ThenBy(s => s.CreatedAt)
        .FirstOrDefault();
    
    if (selectedSentence == null)
    {
        _logger.LogInformation("ℹ️ No sample sentence with audio found for word: {Word}", 
            word.TargetLanguageTerm);
        return;
    }
    
    try
    {
        // Stop existing player
        State.SampleAudioPlayer?.Stop();
        State.SampleAudioPlayer?.Dispose();
        
        // Load audio (assume URI is file path or URL)
        Stream audioStream;
        if (File.Exists(selectedSentence.AudioUri))
        {
            audioStream = File.OpenRead(selectedSentence.AudioUri);
        }
        else
        {
            // Generate via ElevenLabs if needed
            var audioBytes = await _speechService.GenerateSpeechAsync(
                selectedSentence.TargetSentence, Voices.JiYoung);
            audioStream = new MemoryStream(audioBytes);
        }
        
        State.SampleAudioPlayer = _audioManager.CreatePlayer(audioStream);
        State.SampleAudioPlayer.Play();
        
        _logger.LogInformation("🎧 Playing sample sentence audio: {Sentence}", 
            selectedSentence.TargetSentence);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "❌ Failed to play sample sentence audio");
    }
}

void StopAllAudio()
{
    State.VocabularyAudioPlayer?.Stop();
    State.VocabularyAudioPlayer?.Dispose();
    State.SampleAudioPlayer?.Stop();
    State.SampleAudioPlayer?.Dispose();
}
```

**Task 4.7: Call Audio on Question Display**

Modify `ShowNextQuestion()` or question display logic to trigger audio:

```csharp
async Task ShowNextQuestion()
{
    // Stop any playing audio from previous question
    StopAllAudio();
    
    // ... existing question loading logic ...
    
    // Trigger audio playback based on preferences
    var currentWord = State.VocabularyItems[State.CurrentTurn - 1];
    await PlayVocabularyAudioAsync(currentWord);
    
    // ... rest of question display logic ...
}
```

**Task 4.8: Cleanup Audio on Unmount**

Modify `OnWillUnmount()`:

```csharp
protected override void OnWillUnmount()
{
    _logger.LogDebug("🛑 VocabularyQuizPage.OnWillUnmount() START");
    
    // Stop and dispose audio players
    StopAllAudio();
    
    base.OnWillUnmount();
    
    // ... existing unmount logic ...
}
```

**Task 4.9: Apply Display Direction**

Modify question/answer display logic to use `State.UserPreferences.VocabQuizDisplayDirection`:

```csharp
string GetQuestionText(VocabularyWord word)
{
    if (State.UserPreferences?.VocabQuizDisplayDirection == "TargetToNative")
    {
        // Show target language, answer in native
        return word.TargetLanguageTerm;
    }
    else
    {
        // Show native language, answer in target
        return word.NativeLanguageTerm;
    }
}

string GetCorrectAnswer(VocabularyWord word)
{
    if (State.UserPreferences?.VocabQuizDisplayDirection == "TargetToNative")
    {
        return word.NativeLanguageTerm;
    }
    else
    {
        return word.TargetLanguageTerm;
    }
}
```

**Task 4.10: Apply Mnemonic Image Display**

Modify answer confirmation screen to conditionally show mnemonic:

```csharp
VisualNode RenderAnswerConfirmation()
{
    var currentWord = State.VocabularyItems[State.CurrentTurn - 1];
    
    return VStack(spacing: MyTheme.SmallSpacing,
        Label("✅ Correct!")
            .ThemeKey(MyTheme.Title2)
            .TextColor(MyTheme.Success),
            
        // Show mnemonic image if preference enabled and URI exists
        (State.UserPreferences?.VocabQuizShowMnemonicImage ?? false) 
            && !string.IsNullOrEmpty(currentWord.MnemonicImageUri) ?
            Image()
                .Source(currentWord.MnemonicImageUri)
                .HeightRequest(200)
                .Aspect(Aspect.AspectFit) : null,
                
        // ... rest of confirmation UI ...
    );
}
```

---

### Phase 5: Testing (60 minutes)

**Test Case 1: Display Direction**

1. Open VocabularyQuizPage
2. Click preferences icon
3. Select "Show native language"
4. Save preferences
5. Start quiz
6. **Expected**: Questions show English word, answer field expects Korean
7. Navigate back, reopen preferences
8. **Expected**: "Show native language" still selected

**Test Case 2: Auto-play Vocabulary Audio**

1. Open preferences
2. Enable "Auto-play vocabulary audio"
3. Save and start quiz
4. **Expected**: Korean audio plays automatically when question appears
5. Disable preference
6. **Expected**: Next question has no auto-play audio

**Test Case 3: Auto-play Sample Sentence**

1. Enable "Auto-play vocabulary audio"
2. Enable "Auto-play sample sentence"
3. Start quiz
4. **Expected**: Vocabulary audio plays, then sample sentence audio plays after
5. Disable "Auto-play vocabulary audio"
6. **Expected**: Sample sentence checkbox becomes disabled
7. **Expected**: No audio plays

**Test Case 4: Mnemonic Image**

1. Enable "Show mnemonic image"
2. Answer question correctly
3. **Expected**: Mnemonic image appears in confirmation (if word has image)
4. Disable preference
5. Answer next question correctly
6. **Expected**: No mnemonic image shown

**Test Case 5: Missing Audio/Image**

1. Test with vocabulary word that has no AudioPronunciationUri
2. **Expected**: Quiz continues without error, warning logged
3. Test with word that has no MnemonicImageUri
4. **Expected**: Confirmation shows without image, no placeholder

**Test Case 6: Cross-Platform**

1. Build for iOS: `dotnet build -f net10.0-ios`
2. Build for Android: `dotnet build -f net10.0-android`
3. Build for macOS: `dotnet build -f net10.0-maccatalyst`
4. Build for Windows: `dotnet build -f net10.0-windows10.0.19041.0`
5. **Expected**: All features work on all platforms

---

## Troubleshooting

### Audio doesn't play

**Check**:
- Verify word has non-null AudioPronunciationUri
- Check StreamHistoryRepository has cached audio
- Check ElevenLabs API key is configured
- Check device audio permissions (iOS/Android)
- Check logs for "❌ Failed to play" errors

### Preferences don't persist

**Check**:
- Verify database migration applied (check UserProfiles table schema)
- Check UserProfileRepository.UpdateUserProfileAsync returns true
- Check logs for "❌ Failed to save preferences" errors
- Verify UserProfile.Id is not 0 (profile must exist first)

### Bottom sheet doesn't appear

**Check**:
- Verify SfBottomSheet is in correct Grid layer (not behind other content)
- Check State.ShowPreferencesSheet is true when icon clicked
- Check State.UserPreferences is not null
- Check Syncfusion.Maui.Popup package is referenced

### Sample sentence never plays

**Check**:
- Verify vocabulary audio is enabled (dependency)
- Verify vocabulary audio completes (OnVocabularyAudioEnded fires)
- Check example sentences have non-null AudioUri
- Check ExampleSentenceRepository returns data

---

## Performance Checklist

- [ ] Audio streams disposed after playback (no memory leaks)
- [ ] Database saves are async (don't block UI)
- [ ] Preferences loaded once per page mount (not per question)
- [ ] Audio caching via StreamHistoryRepository used
- [ ] Mnemonic images respect 1-second loading timeout (SC-005)

---

## Documentation Checklist

- [ ] Update CHANGELOG.md with new feature
- [ ] Add preferences section to user guide (docs/)
- [ ] Update VocabularyQuizPage header comment with preference info
- [ ] Add XML comments to new public methods
- [ ] Ensure all localization keys documented

---

## Next Steps After Implementation

1. **Code Review**: Submit PR, reference spec in description
2. **Testing**: Run all test cases on 4 platforms
3. **User Feedback**: Deploy to internal testing group
4. **Documentation**: Update user-facing guides
5. **Monitoring**: Watch logs for audio playback failures

---

## Estimated Timeline

| Phase | Task | Time |
|-------|------|------|
| 1 | Database schema | 30 min |
| 2 | Localization strings | 20 min |
| 3 | Preferences UI component | 60 min |
| 4 | VocabularyQuizPage integration | 90 min |
| 5 | Testing & verification | 60 min |
| **Total** | | **~4 hours** |

---

## Success Criteria Verification

After implementation, verify all success criteria from [spec.md](spec.md):

- [ ] **SC-001**: Configure 4 preferences in < 30 seconds
- [ ] **SC-002**: Preferences persist across app restarts (100% reliability)
- [ ] **SC-003**: Audio starts < 500ms after word display
- [ ] **SC-004**: Display direction change reflected in < 2 seconds
- [ ] **SC-005**: Mnemonic images load/display < 1 second
- [ ] **SC-006**: 90% first-attempt configuration success (user testing)
- [ ] **SC-007**: Audio works on iOS, Android, macOS, Windows

---

## References

- [Feature Specification](spec.md)
- [Research Findings](research.md)
- [Data Model](data-model.md)
- [Service Contracts](contracts/service-contracts.md)
- [MauiReactor Documentation](https://adospace.github.io/reactorui-maui/)
- [Plugin.Maui.Audio Docs](https://github.com/jfversluis/Plugin.Maui.Audio)
- [ElevenLabs API Docs](https://elevenlabs.io/docs)

---

**Ready to implement!** Follow checklist sequentially, test after each phase, commit frequently. 🚀
