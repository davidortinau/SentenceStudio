# API Contracts: Vocabulary Quiz Preferences

**Feature**: 001-vocab-quiz-preferences  
**Date**: 2025-12-13  
**Phase**: 1 - Design & Contracts

## Overview

This feature extends existing repositories and services rather than adding new REST/GraphQL APIs. All interactions are internal to the MAUI application via service interfaces.

## Service Contracts

### 1. UserProfileRepository (Extended)

**File**: `src/SentenceStudio.Shared/Services/UserProfileRepository.cs`

**Extended Methods** (no signature changes, behavior extended):

#### GetCurrentUserProfileAsync()

**Description**: Loads current user profile including new preference fields

**Returns**: `Task<UserProfile>`

**Contract**:
```csharp
/// <summary>
/// Gets the current user profile with all preferences including vocabulary quiz settings.
/// </summary>
/// <returns>UserProfile with VocabQuiz preferences populated</returns>
Task<UserProfile> GetCurrentUserProfileAsync();
```

**Behavior**:
- Returns UserProfile with all fields including new preference columns
- New fields have default values for existing users (via migration)
- Never returns null (creates default profile if none exists)

**Example Response**:
```csharp
new UserProfile
{
    Id = 1,
    Name = "Student",
    NativeLanguage = "English",
    TargetLanguage = "Korean",
    VocabQuizDisplayDirection = "TargetToNative", // New field
    VocabQuizAutoPlayVocabAudio = true,           // New field
    VocabQuizAutoPlaySampleAudio = false,         // New field
    VocabQuizShowMnemonicImage = true,            // New field
    // ... other existing fields
}
```

#### UpdateUserProfileAsync(UserProfile profile)

**Description**: Saves user profile changes including preference updates

**Parameters**:
- `profile`: UserProfile with modified preference fields

**Returns**: `Task<bool>` (true if successful)

**Contract**:
```csharp
/// <summary>
/// Updates user profile including vocabulary quiz preferences.
/// </summary>
/// <param name="profile">Profile with updated preference values</param>
/// <returns>True if save successful, false otherwise</returns>
Task<bool> UpdateUserProfileAsync(UserProfile profile);
```

**Validation**:
- VocabQuizDisplayDirection must be "TargetToNative" or "NativeToTarget"
- Boolean fields accept any bool value
- Logs warning if invalid DisplayDirection, defaults to "TargetToNative"

**Error Handling**:
- Database errors logged but not thrown (returns false)
- Invalid data logged with warning, corrected to defaults

### 2. Audio Playback Interface (Informal Contract)

**Description**: Vocabulary quiz page uses existing Plugin.Maui.Audio interfaces

**Key Interface**: `IAudioManager` (from Plugin.Maui.Audio)

**Pattern**:
```csharp
// Existing pattern - no changes to contract
IAudioManager audioManager; // Injected

// Create player from stream
IAudioPlayer player = audioManager.CreatePlayer(audioStream);

// Subscribe to completion event
player.PlaybackEnded += OnAudioEnded;

// Start playback
player.Play();

// Cleanup
player.Stop();
player.Dispose();
```

**Extension for Preferences**:
```csharp
// Check preferences before playing
if (userProfile.VocabQuizAutoPlayVocabAudio && 
    !string.IsNullOrEmpty(word.AudioPronunciationUri))
{
    await PlayVocabularyAudioAsync(word.AudioPronunciationUri);
}

// Chain to sample sentence on completion
void OnVocabularyAudioEnded(object sender, EventArgs e)
{
    if (userProfile.VocabQuizAutoPlaySampleAudio && selectedSentence != null)
    {
        await PlaySampleAudioAsync(selectedSentence.AudioUri);
    }
}
```

### 3. ExampleSentenceRepository (No Changes)

**File**: `src/SentenceStudio.Shared/Services/ExampleSentenceRepository.cs`

**Used Method**: `GetExampleSentencesByVocabularyWordIdAsync(int wordId)`

**Contract** (existing, no changes):
```csharp
/// <summary>
/// Gets all example sentences for a vocabulary word.
/// </summary>
/// <param name="wordId">The vocabulary word ID</param>
/// <returns>List of example sentences (may be empty)</returns>
Task<List<ExampleSentence>> GetExampleSentencesByVocabularyWordIdAsync(int wordId);
```

**Selection Logic** (applied in VocabularyQuizPage):
```csharp
var allSentences = await _exampleRepo.GetExampleSentencesByVocabularyWordIdAsync(wordId);

var selectedSentence = allSentences
    .Where(s => !string.IsNullOrEmpty(s.AudioUri))  // Must have audio
    .OrderByDescending(s => s.IsCore)               // Core sentences first
    .ThenBy(s => s.CreatedAt)                       // Oldest first (stable)
    .FirstOrDefault();
```

## Component Contracts

### VocabularyQuizPreferencesBottomSheet Component

**File**: `src/SentenceStudio/Pages/VocabularyQuiz/VocabularyQuizPreferencesBottomSheet.cs` (new)

**Props**:
```csharp
class VocabularyQuizPreferencesProps
{
    /// <summary>
    /// Current user profile with preference values to display/edit
    /// </summary>
    public UserProfile UserProfile { get; set; }
    
    /// <summary>
    /// Callback invoked when preferences are saved
    /// </summary>
    public Action<UserProfile> OnPreferencesSaved { get; set; }
    
    /// <summary>
    /// Callback to close the bottom sheet
    /// </summary>
    public Action OnClose { get; set; }
}
```

**State**:
```csharp
class VocabularyQuizPreferencesState
{
    /// <summary>
    /// Working copy of display direction (mutated by UI)
    /// </summary>
    public string DisplayDirection { get; set; }
    
    /// <summary>
    /// Working copy of auto-play vocabulary audio flag
    /// </summary>
    public bool AutoPlayVocabAudio { get; set; }
    
    /// <summary>
    /// Working copy of auto-play sample audio flag
    /// </summary>
    public bool AutoPlaySampleAudio { get; set; }
    
    /// <summary>
    /// Working copy of show mnemonic image flag
    /// </summary>
    public bool ShowMnemonicImage { get; set; }
    
    /// <summary>
    /// True while saving to database
    /// </summary>
    public bool IsSaving { get; set; }
}
```

**Component Interface**:
```csharp
partial class VocabularyQuizPreferencesBottomSheet : Component<VocabularyQuizPreferencesState, VocabularyQuizPreferencesProps>
{
    /// <summary>
    /// Render the preferences UI with radio buttons, checkboxes, and save button
    /// </summary>
    public override VisualNode Render();
    
    /// <summary>
    /// Saves modified preferences to UserProfile via repository
    /// </summary>
    private async Task SavePreferencesAsync();
    
    /// <summary>
    /// Validates display direction is valid enum value
    /// </summary>
    private bool IsValidDisplayDirection(string direction);
}
```

**Behavior Contract**:
1. Loads Props.UserProfile values into State on mount
2. User modifies State via UI controls
3. Save button triggers SavePreferencesAsync()
4. Repository updates UserProfile
5. Calls Props.OnPreferencesSaved(updatedProfile)
6. Calls Props.OnClose() to dismiss bottom sheet

### VocabularyQuizPage (Extended)

**File**: `src/SentenceStudio/Pages/VocabularyQuiz/VocabularyQuizPage.cs`

**New State Fields**:
```csharp
class VocabularyQuizPageState
{
    // ... existing state fields ...
    
    /// <summary>
    /// Current user preferences (loaded from UserProfile)
    /// </summary>
    public UserProfile UserPreferences { get; set; }
    
    /// <summary>
    /// True when preferences bottom sheet is visible
    /// </summary>
    public bool ShowPreferencesSheet { get; set; }
    
    /// <summary>
    /// Current vocabulary audio player instance (for stopping/cleanup)
    /// </summary>
    public IAudioPlayer VocabularyAudioPlayer { get; set; }
    
    /// <summary>
    /// Current sample sentence audio player instance
    /// </summary>
    public IAudioPlayer SampleAudioPlayer { get; set; }
}
```

**New Methods**:
```csharp
/// <summary>
/// Loads user preferences from UserProfileRepository
/// Called in OnMounted lifecycle
/// </summary>
private async Task LoadUserPreferencesAsync();

/// <summary>
/// Opens preferences bottom sheet overlay
/// </summary>
private void OpenPreferences();

/// <summary>
/// Closes preferences bottom sheet, reloads preferences
/// </summary>
private void ClosePreferences();

/// <summary>
/// Plays vocabulary word audio if preferences enabled and URI exists
/// Chains to sample sentence audio on completion
/// </summary>
private async Task PlayVocabularyAudioAsync(VocabularyWord word);

/// <summary>
/// Plays sample sentence audio if preferences enabled
/// </summary>
private async Task PlaySampleAudioAsync(ExampleSentence sentence);

/// <summary>
/// Handles vocabulary audio playback completion
/// Checks if sample audio should play next
/// </summary>
private void OnVocabularyAudioEnded(object sender, EventArgs e);

/// <summary>
/// Stops all audio playback (called when navigating away or skipping)
/// </summary>
private void StopAllAudio();

/// <summary>
/// Determines question/answer format based on display direction preference
/// </summary>
private (string question, string correctAnswer) GetQuestionFormat(VocabularyWord word);

/// <summary>
/// Selects sample sentence for auto-play (IsCore first, oldest CreatedAt)
/// </summary>
private ExampleSentence SelectSampleSentence(List<ExampleSentence> sentences);
```

## Event Flows

### User Opens Preferences

```
User clicks preferences icon in VocabularyQuizPage
    │
    ▼
VocabularyQuizPage.OpenPreferences()
    │
    ▼
SetState(s => s.ShowPreferencesSheet = true)
    │
    ▼
VocabularyQuizPreferencesBottomSheet rendered
    │
    ▼
BottomSheet loads Props.UserProfile into State
    │
    ▼
User sees current preference values
```

### User Saves Preferences

```
User clicks "Save Preferences" button
    │
    ▼
VocabularyQuizPreferencesBottomSheet.SavePreferencesAsync()
    │
    ▼
Update UserProfile object with State values
    │
    ▼
UserProfileRepository.UpdateUserProfileAsync(profile)
    │
    ▼
Database saves changes
    │
    ▼
Props.OnPreferencesSaved(updatedProfile) callback
    │
    ▼
VocabularyQuizPage.ClosePreferences()
    │
    ▼
SetState(s => { 
    s.ShowPreferencesSheet = false; 
    s.UserPreferences = updatedProfile;  // Reload
})
    │
    ▼
Next question uses new preferences
```

### Audio Auto-Play Sequence

```
VocabularyQuizPage.ShowNextQuestion()
    │
    ▼
Load next VocabularyWord from State.VocabularyItems
    │
    ▼
Check State.UserPreferences.VocabQuizAutoPlayVocabAudio
    │
    ├─ false ───> [Skip audio, show question immediately]
    │
    └─ true ────▼
        Check word.AudioPronunciationUri exists
        │
        ├─ null/empty ───> [Log warning, skip audio]
        │
        └─ valid URI ────▼
            PlayVocabularyAudioAsync(word)
            │
            ▼
        Create IAudioPlayer from audio stream
        Subscribe to PlaybackEnded event
        Start playback
            │
            ▼
        [Audio plays for 2-3 seconds]
            │
            ▼
        OnVocabularyAudioEnded event fires
            │
            ▼
        Check State.UserPreferences.VocabQuizAutoPlaySampleAudio
        │
        ├─ false ───> [Done, show question]
        │
        └─ true ────▼
            SelectSampleSentence(word.ExampleSentences)
            │
            ├─ null ───> [No sentence found, skip]
            │
            └─ sentence ────▼
                PlaySampleAudioAsync(sentence)
                │
                ▼
            Create IAudioPlayer from sentence audio
            Start playback
                │
                ▼
            [Audio plays for 3-5 seconds]
                │
                ▼
            [Done, show question]
```

## Data Transfer Objects

### UserProfile (Extended)

```csharp
public class UserProfile
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string NativeLanguage { get; set; } = "English";
    public string TargetLanguage { get; set; } = "Korean";
    public string? DisplayLanguage { get; set; }
    public string? Email { get; set; }
    public string? OpenAI_APIKey { get; set; }
    public int PreferredSessionMinutes { get; set; } = 20;
    public string? TargetCEFRLevel { get; set; }
    public DateTime CreatedAt { get; set; }
    
    // === NEW VOCABULARY QUIZ PREFERENCES ===
    
    /// <summary>
    /// Display direction for vocabulary quiz questions
    /// "TargetToNative" = show Korean, answer English
    /// "NativeToTarget" = show English, answer Korean
    /// </summary>
    public string VocabQuizDisplayDirection { get; set; } = "TargetToNative";
    
    /// <summary>
    /// Auto-play target language vocabulary word audio
    /// </summary>
    public bool VocabQuizAutoPlayVocabAudio { get; set; } = true;
    
    /// <summary>
    /// Auto-play sample sentence audio after vocabulary audio
    /// Only plays if VocabQuizAutoPlayVocabAudio is also true
    /// </summary>
    public bool VocabQuizAutoPlaySampleAudio { get; set; } = false;
    
    /// <summary>
    /// Show mnemonic image on correct answer confirmation
    /// </summary>
    public bool VocabQuizShowMnemonicImage { get; set; } = true;
    
    [NotMapped]
    public string DisplayCulture => DisplayLanguage switch
    {
        "English" => "en",
        "Korean" => "ko",
        _ => "en"
    };
}
```

## Error Responses

### Invalid Display Direction

**Scenario**: User somehow submits invalid VocabQuizDisplayDirection value

**Handling**:
```csharp
if (!IsValidDisplayDirection(profile.VocabQuizDisplayDirection))
{
    _logger.LogWarning(
        "⚠️ Invalid VocabQuizDisplayDirection: {Direction}. Defaulting to TargetToNative.", 
        profile.VocabQuizDisplayDirection
    );
    profile.VocabQuizDisplayDirection = "TargetToNative";
}
```

**User Impact**: Preferences default to safe value, no crash, logged for debugging

### Missing Audio URI

**Scenario**: Auto-play enabled but word.AudioPronunciationUri is null/empty

**Handling**:
```csharp
if (string.IsNullOrEmpty(word.AudioPronunciationUri))
{
    _logger.LogWarning(
        "⚠️ Auto-play enabled but missing audio for word: {Word} (ID: {Id})", 
        word.TargetLanguageTerm, 
        word.Id
    );
    return; // Skip audio, continue quiz
}
```

**User Impact**: Quiz continues without audio, no error shown to user

### Audio Playback Failure

**Scenario**: IAudioPlayer throws exception during playback

**Handling**:
```csharp
try
{
    _vocabularyAudioPlayer.Play();
}
catch (Exception ex)
{
    _logger.LogError(ex, 
        "❌ Failed to play vocabulary audio for word: {Word}", 
        word.TargetLanguageTerm
    );
    // Continue quiz without audio
}
```

**User Impact**: Quiz continues, user can manually retry audio if needed

### Database Save Failure

**Scenario**: UpdateUserProfileAsync fails due to database error

**Handling**:
```csharp
var success = await _userProfileRepo.UpdateUserProfileAsync(profile);
if (!success)
{
    _logger.LogError("❌ Failed to save vocabulary quiz preferences");
    // Show error message to user
    SetState(s => s.ErrorMessage = _localize["PreferencesSaveFailed"]);
    return;
}
```

**User Impact**: Error message shown, preferences not saved, user can retry

## Backward Compatibility

### Existing Users (Before Migration)

**Scenario**: User upgrades to new version, database migration adds preference columns

**Handling**:
- Migration sets default values: DisplayDirection="TargetToNative", AutoPlayVocabAudio=true, AutoPlaySampleAudio=false, ShowMnemonicImage=true
- Existing quiz behavior preserved (default to showing target language, no auto-play)
- No user action required

### Missing Preferences (Edge Case)

**Scenario**: UserProfile loaded before migration runs

**Handling**:
```csharp
// Null-coalescing ensures defaults if migration incomplete
var displayDirection = userProfile.VocabQuizDisplayDirection ?? "TargetToNative";
var autoPlayVocab = userProfile.VocabQuizAutoPlayVocabAudio ?? true;
```

## Summary

All API contracts defined:
- ✅ UserProfileRepository extended (no new methods, behavior enhanced)
- ✅ VocabularyQuizPreferencesBottomSheet component contract
- ✅ VocabularyQuizPage extensions documented
- ✅ Event flows mapped for preferences and audio playback
- ✅ Error handling patterns defined
- ✅ Backward compatibility ensured

Ready to proceed to quickstart guide.
