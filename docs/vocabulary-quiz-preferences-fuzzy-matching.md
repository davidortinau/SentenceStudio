# Vocabulary Quiz Preferences & Fuzzy Answer Matching

## Overview

Enhanced the VocabularyQuizPage with configurable user preferences and intelligent fuzzy answer matching to improve the quiz learning experience.

## Features Implemented

### 1. User Preferences (Saved via .NET MAUI Preferences API)

Users can now configure their quiz experience through a bottom sheet preferences panel:

#### Display Direction
- **Target → Native**: Show target language word, answer in native language (default)
- **Native → Target**: Show native language word, answer in target language

#### Audio Playback
- **Auto-play word audio**: Automatically play target language word audio when question appears
- **Auto-play sentence audio**: Automatically play a sample sentence after the word (if available)
- **Voice selection**: Choose from available ElevenLabs voices for audio generation

#### Mnemonic Display
- **Show mnemonic on correct answer**: Display the mnemonic image when user answers correctly

### 2. Enhanced Fuzzy Answer Matching

The `FuzzyMatcher` service now includes multiple intelligent matching strategies:

#### Matching Rules (Applied in Order)

1. **Exact Match**: Direct comparison after normalization
   - Example: "take" = "take" ✓

2. **Parenthetical Content Removal**: Matches core terms
   - Example: "take" = "take (a photo)" ✓
   - Example: "ding" = "ding~ (a sound)" ✓

3. **Substring Matching**: Partial matches accepted
   - Example: "cloudy" = "get cloudy" ✓
   - Example: "choose" = "to choose" ✓

4. **"to" Prefix Handling**: Infinitive verb variations
   - Example: "choose" = "to choose" ✓
   - Example: "to take" = "take" ✓

5. **Typo Tolerance** (85% similarity threshold using Levenshtein distance):
   - Example: "celcius" = "celsius" ✓
   - Example: "recieve" = "receive" ✓

#### Normalization Process

The matcher normalizes text before comparison:
- Unicode normalization (NFC for Korean support)
- Removes parenthetical content: `(anything)`
- Removes tilde descriptors: `~ (description)`
- Removes punctuation for comparison
- Case-insensitive matching
- Handles "to" prefix for infinitive verbs

## Technical Implementation

### Files Modified

1. **VocabularyQuizPage.cs**
   - Added preferences injection
   - Added preferences bottom sheet UI
   - Integrated audio auto-play logic
   - Added mnemonic image display on correct answers
   - Added manual audio playback buttons

2. **FuzzyMatcher.cs** (Enhanced)
   - Added substring matching logic
   - Added Levenshtein distance calculation
   - Added typo tolerance threshold (85%)
   - Enhanced normalization rules

3. **VocabularyQuizPreferences.cs**
   - Added `DisplayDirection` property (enum)
   - Added `AutoPlayWordAudio` property (bool)
   - Added `AutoPlaySentenceAudio` property (bool)
   - Added `ShowMnemonicOnCorrect` property (bool)
   - Added `VoiceId` property (string)
   - Uses .NET MAUI Preferences API for persistence

### Audio Integration

Uses the existing ElevenLabs integration:
- Generates audio via `ElevenLabsSpeechService`
- Plays audio via `IAudioManager` (Plugin.Maui.Audio)
- Caches generated audio to avoid redundant API calls
- Proper audio player disposal after playback

### Preferences Storage

All preferences stored using .NET MAUI `Preferences` API:
```csharp
Preferences.Set("VocabQuiz_DisplayDirection", value);
Preferences.Get("VocabQuiz_AutoPlayWordAudio", true);
```

No database migrations required - preferences are device-local and per-user.

## User Experience Improvements

### Before
- Fixed quiz direction (target → native only)
- No audio playback
- No typo tolerance
- Strict exact matching required
- Manual answer validation only

### After
- Configurable quiz direction
- Automatic audio playback (word + optional sentence)
- Manual replay buttons for audio
- Intelligent fuzzy matching with typo tolerance
- Mnemonic image display on correct answers
- Personalized voice selection
- More forgiving answer acceptance

## Testing Recommendations

Test the following scenarios:

1. **Preferences Persistence**
   - Change preferences
   - Close app
   - Reopen app
   - Verify preferences retained

2. **Audio Playback**
   - Enable auto-play word audio
   - Enable auto-play sentence audio
   - Test manual replay buttons
   - Verify voice selection works

3. **Fuzzy Matching**
   - Test parenthetical removal: "take" for "take (a photo)"
   - Test substring: "cloudy" for "get cloudy"
   - Test typos: "celcius" for "celsius"
   - Test infinitive: "choose" for "to choose"

4. **Display Direction**
   - Test Target → Native mode
   - Test Native → Target mode
   - Verify question/answer swap correctly

5. **Mnemonic Display**
   - Enable mnemonic on correct answer
   - Answer correctly
   - Verify mnemonic image appears

## Future Enhancements

Potential improvements:
- Adjustable typo tolerance threshold (user preference)
- Additional fuzzy matching rules (phonetic similarity)
- Audio playback speed control
- Multiple sentence audio options
- Custom voice per language pair
- Hint system using fuzzy matching confidence scores

## Related Documentation

- [ElevenLabs API Documentation](https://context7.com/elevenlabs/elevenlabs-docs/llms.txt)
- [.NET MAUI Preferences API](https://learn.microsoft.com/en-us/dotnet/maui/platform-integration/storage/preferences)
- [Levenshtein Distance Algorithm](https://en.wikipedia.org/wiki/Levenshtein_distance)
