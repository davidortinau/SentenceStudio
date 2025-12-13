# Research: Vocabulary Quiz Preferences

**Feature**: 001-vocab-quiz-preferences  
**Date**: 2025-12-13  
**Phase**: 0 - Outline & Research

## Research Questions

### 1. Where should preferences be stored in the data model?

**Question**: Should vocabulary quiz preferences be stored in UserProfile table, or create a new VocabularyQuizPreferences table?

**Research Findings**:
- Existing UserProfile model (UserProfile.cs) stores user-level preferences like PreferredSessionMinutes, TargetCEFRLevel
- Constitution principle I states "Users MUST be able to import custom vocabulary" - preferences are user-centric
- VocabularyQuizPage already accesses UserProfile via UserProfileRepository
- Migrations exist for adding user preferences (20251119155946_AddUserPreferencesToProfile.cs)

**Decision**: Store preferences as additional columns in UserProfile table

**Rationale**:
- Consistent with existing preference storage pattern (PreferredSessionMinutes, TargetCEFRLevel already in UserProfile)
- Single-user app scope (no multi-user complexity needed)
- Simpler data access (VocabularyQuizPage already loads UserProfile)
- Fewer database tables to maintain
- Easier to extend with additional quiz preferences in future

**Alternatives Considered**:
- **Separate VocabularyQuizPreferences table**: Rejected because it adds unnecessary complexity for single-user preferences. Would require additional repository, joins, and synchronization logic. Only beneficial if we need to support multiple preference sets per user (e.g., different profiles for different learning contexts), which is not in scope.
- **JSON blob in UserProfile**: Rejected because it makes querying/indexing harder and loses type safety at database level. SQLite columns provide better tooling support and migrations.

### 2. How to handle audio playback chaining (vocabulary → sample sentence)?

**Question**: What's the best pattern for playing vocabulary audio, then automatically playing sample sentence audio?

**Research Findings**:
- EditVocabularyWordPage.cs shows existing audio playback pattern using Plugin.Maui.Audio (IAudioPlayer)
- Pattern: Create IAudioPlayer → Subscribe to PlaybackEnded event → Play audio stream → Cleanup on completion
- ElevenLabsSpeechService provides text-to-speech via ElevenLabs API with caching via StreamHistoryRepository
- ExampleSentence model has AudioUri field for pre-generated audio
- VocabularyWord model has AudioPronunciationUri field for word audio

**Decision**: Use event-driven chaining with PlaybackEnded handler

**Rationale**:
- Matches existing app patterns (see EditVocabularyWordPage PlayWordAudioAsync method)
- Allows graceful cancellation when user navigates away
- Supports existing audio caching mechanism
- Plugin.Maui.Audio provides PlaybackEnded event specifically for sequencing

**Implementation Pattern**:
```csharp
// 1. Play vocabulary audio
_audioPlayer = audioManager.CreatePlayer(vocabAudioStream);
_audioPlayer.PlaybackEnded += OnVocabularyAudioEnded;
_audioPlayer.Play();

// 2. On completion, check preferences and play sample sentence
void OnVocabularyAudioEnded(object sender, EventArgs e)
{
    if (preferences.AutoPlaySampleSentenceAudio && selectedSentence != null)
    {
        // Stop previous player
        _audioPlayer?.PlaybackEnded -= OnVocabularyAudioEnded;
        _audioPlayer?.Dispose();
        
        // Play sample sentence audio
        _audioPlayer = audioManager.CreatePlayer(sampleSentenceStream);
        _audioPlayer.Play();
    }
}
```

**Alternatives Considered**:
- **Task.Delay polling**: Rejected because it's inefficient (wastes CPU cycles), less precise timing, and doesn't match app conventions
- **await audio completion**: Plugin.Maui.Audio doesn't support async/await pattern directly. Would require custom TaskCompletionSource wrapper, adding unnecessary complexity.
- **Queue-based system**: Rejected as over-engineering for 2-item sequence. Queue makes sense for 5+ items, but vocab+sentence is simple enough for event handler.

### 3. How to display preferences UI?

**Question**: Should preferences be shown as a separate page, settings sheet, or in-page controls?

**Research Findings**:
- App uses Syncfusion.Maui.Popup.SfBottomSheet for overlays (seen in VocabularyQuizPage implementation patterns)
- VocabularyQuizPage already has complex state management and multiple sections
- Constitution requires MauiReactor MVU patterns with semantic alignment methods
- FR-001 states "accessible from the vocabulary quiz page or app settings"

**Decision**: Use SfBottomSheet overlay on VocabularyQuizPage, with toolbar icon to trigger

**Rationale**:
- Consistent with app patterns (bottom sheets used for transient UI)
- Doesn't require navigation away from quiz (preserves quiz state)
- Quick access during quiz session (user can adjust mid-session per FR-020)
- Follows MAUI/mobile conventions for contextual settings
- Can be reused from app settings page if needed later

**UI Layout**:
```
┌─────────────────────────────────────┐
│  Vocabulary Quiz Preferences    [x] │
├─────────────────────────────────────┤
│                                     │
│  Display Direction                  │
│  ⚪ Show target language (요청)       │
│  ⚫ Show native language (answer)    │ 
│                                     │
│  ─────────────────────────────────  │
│                                     │
│  Audio Playback                     │
│  ☑ Auto-play vocabulary audio       │
│  ☑ Auto-play sample sentence        │
│      (requires vocabulary audio)    │
│                                     │
│  ─────────────────────────────────  │
│                                     │
│  Confirmation Display               │
│  ☑ Show mnemonic image              │
│                                     │
│  ─────────────────────────────────  │
│                                     │
│       [Save Preferences]            │
│                                     │
└─────────────────────────────────────┘
```

**Alternatives Considered**:
- **Separate SettingsPage**: Rejected because it requires navigation away from quiz, losing quiz context. User would have to abandon current quiz to change preferences, poor UX.
- **In-page toggles**: Rejected because VocabularyQuizPage is already complex with quiz state, progress tracking, answer validation. Adding 4+ preference controls would clutter the UI.
- **Modal dialog**: Rejected because bottom sheets are standard in app, provide better UX on mobile (swipe to dismiss), and match app design patterns.

### 4. How to select sample sentence when multiple exist?

**Question**: When a VocabularyWord has multiple ExampleSentences, which one should auto-play?

**Research Findings**:
- ExampleSentence model has IsCore boolean field (indicates importance)
- ExampleSentenceRepository provides data access
- VocabularyWord has List<ExampleSentence> navigation property
- FR-011 requires "select one sample sentence when multiple available"

**Decision**: Prioritize IsCore=true sentences, then select first by CreatedAt (oldest first)

**Rationale**:
- IsCore flag indicates pedagogically important examples
- CreatedAt ordering provides deterministic, stable selection (same sentence every time unless data changes)
- Oldest-first assumes core sentences are added first during curriculum creation
- Simple logic, no randomization (random would be inconsistent for learning)

**Selection Logic**:
```csharp
var selectedSentence = word.ExampleSentences
    .Where(s => !string.IsNullOrEmpty(s.AudioUri)) // Must have audio
    .OrderByDescending(s => s.IsCore)              // Core first
    .ThenBy(s => s.CreatedAt)                      // Oldest first
    .FirstOrDefault();
```

**Alternatives Considered**:
- **Random selection**: Rejected because it creates inconsistent learning experience. User expects same sentence each time they review a word.
- **Most recent sentence (newest first)**: Rejected because older sentences are typically more fundamental/core to the vocabulary word's meaning.
- **User selection**: Rejected as out of scope for auto-play feature. User can manually tap sentences if they want to choose. Auto-play needs default behavior.

### 5. How to handle missing audio/images gracefully?

**Question**: What happens if AudioPronunciationUri, ExampleSentence.AudioUri, or MnemonicImageUri is null/empty/invalid?

**Research Findings**:
- FR-012 requires "handle missing audio files gracefully without disrupting quiz flow"
- FR-013 requires "handle missing mnemonic images gracefully without broken placeholders"
- VocabularyWord and ExampleSentence models use nullable string properties for URIs
- EditVocabularyWordPage checks `if (!string.IsNullOrWhiteSpace(word))` before audio playback

**Decision**: Check for null/empty before attempting playback/display, log warning, continue quiz flow

**Rationale**:
- Defensive programming prevents crashes
- Logging helps identify missing content for content creators
- User experience is uninterrupted (quiz continues even if audio/image missing)
- Matches existing app patterns (see EditVocabularyWordPage audio checks)

**Implementation Pattern**:
```csharp
// Audio playback
if (!string.IsNullOrEmpty(word.AudioPronunciationUri) && preferences.AutoPlayVocabularyAudio)
{
    await PlayAudioAsync(word.AudioPronunciationUri);
}
else if (preferences.AutoPlayVocabularyAudio)
{
    _logger.LogWarning("⚠️ Missing audio for vocabulary word: {Word}", word.TargetLanguageTerm);
}

// Mnemonic image display
if (!string.IsNullOrEmpty(word.MnemonicImageUri) && preferences.ShowMnemonicImage)
{
    // Display image
}
// No else needed - just don't show image if missing
```

**Alternatives Considered**:
- **Show error message to user**: Rejected because it disrupts learning flow and user can't fix it. Better to log for developers.
- **Generate audio on-the-fly**: Rejected because ElevenLabs API calls take time (500ms+ per FR-012 budget), would block quiz flow. Better to pre-generate audio.
- **Placeholder image**: Rejected per FR-013 requirement - no broken placeholders. Empty space is cleaner than "image not found" icon.

## Technology Decisions

### Audio Playback
- **Plugin.Maui.Audio**: Already integrated, cross-platform, supports IAudioPlayer and IAudioManager
- **ElevenLabs API**: Already integrated via ElevenLabsSpeechService for TTS generation
- **StreamHistoryRepository**: Already provides audio caching to minimize API calls

### Data Storage
- **SQLite**: UserProfile table extension with new preference columns
- **Entity Framework Core**: Existing migration framework for schema changes
- **No new repositories needed**: UserProfileRepository already exists

### UI Components
- **MauiReactor**: MVU pattern with fluent methods
- **Syncfusion.Maui.Popup.SfBottomSheet**: Preference overlay UI
- **MyTheme**: Centralized styling via `.ThemeKey()` and theme constants
- **LocalizationManager**: `$"{_localize["Key"]}"` pattern for all strings

### Logging
- **ILogger<T>**: Production logging for preference changes, audio events, errors
- **Structured logging**: Use log levels (Debug, Information, Warning, Error)

## Best Practices

### Cross-Platform Audio
- Test on all platforms: iOS (CoreAudio), Android (MediaPlayer), macOS (AVFoundation), Windows (MediaPlayer)
- Handle platform-specific audio session interruptions (phone calls, other apps)
- Respect system audio ducking (lower volume when other audio plays)
- Stop audio when navigating away from page (OnWillUnmount lifecycle)

### Preferences Persistence
- Load preferences once in OnMounted lifecycle method
- Save preferences immediately on change (no explicit "Save" button for individual settings)
- Use optimistic UI updates (show change immediately, persist in background)
- Handle migration from null preferences (new users) with sensible defaults

### Performance
- Lazy-load audio streams (don't preload all sample sentences)
- Use existing AudioCacheManager to avoid redundant ElevenLabs API calls
- Debounce rapid preference changes (if user toggles repeatedly)
- Dispose IAudioPlayer instances properly to prevent memory leaks

### Accessibility
- Provide text labels for all icons/buttons
- Ensure preferences can be changed via keyboard (desktop)
- Don't rely on color alone for preference state (use checkboxes, radio buttons)
- Test screen reader compatibility (especially on iOS VoiceOver)

## Dependencies

### Existing Services
- ✅ UserProfileRepository (data access)
- ✅ ElevenLabsSpeechService (audio generation)
- ✅ StreamHistoryRepository (audio caching)
- ✅ ExampleSentenceRepository (sample sentences)
- ✅ Plugin.Maui.Audio (playback)
- ✅ LocalizationManager (strings)

### New Components
- VocabularyQuizPreferencesBottomSheet (MauiReactor component)
- UserProfile migration (add 4 new columns)
- Localization keys (10-15 new strings)

### No New External Dependencies
- All required packages already in project
- No new NuGet packages needed
- No new API integrations required

## Risks & Mitigations

### Risk: Audio playback fails on specific platform
- **Mitigation**: Test on all 4 platforms before merge. Add platform-specific error handling if needed.
- **Fallback**: Log error, disable auto-play for that session, show notification to user

### Risk: Preferences changes mid-quiz cause state inconsistency
- **Mitigation**: FR-020 specifies changes take effect on NEXT question, not current. Reload preferences before ShowNextQuestion().
- **Fallback**: If state corruption occurs, reset quiz to last checkpoint

### Risk: Mnemonic images are too large, slow to load
- **Mitigation**: Set 1-second loading timeout per SC-005. Show ActivityIndicator while loading.
- **Fallback**: Hide image if loading exceeds 1 second, log performance warning

### Risk: User doesn't understand display direction options
- **Mitigation**: Use clear labels with examples: "Show target language (한국어 → English)" vs "Show native language (English → 한국어)"
- **Fallback**: Provide in-app help text or tooltip explaining difference

## Open Questions

None - all research questions resolved with decisions above.

## Summary

All technical unknowns have been researched and resolved:
1. ✅ Preferences stored in UserProfile table
2. ✅ Audio chaining via PlaybackEnded event handler
3. ✅ Preferences UI via SfBottomSheet overlay
4. ✅ Sample sentence selection: IsCore first, then oldest
5. ✅ Graceful handling of missing audio/images

Ready to proceed to Phase 1: Design & Contracts.
