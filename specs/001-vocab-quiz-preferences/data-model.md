# Data Model: Vocabulary Quiz Preferences

**Feature**: 001-vocab-quiz-preferences  
**Date**: 2025-12-13  
**Phase**: 1 - Design & Contracts

## Entity Changes

### UserProfile (Modified)

**Location**: `src/SentenceStudio.Shared/Models/UserProfile.cs`

**New Fields**:

| Field Name | Type | Description | Default Value | Constraints |
|------------|------|-------------|---------------|-------------|
| VocabQuizDisplayDirection | string | Display direction: "TargetToNative" or "NativeToTarget" | "TargetToNative" | Not null, max 20 chars |
| VocabQuizAutoPlayVocabAudio | bool | Auto-play vocabulary word audio | true | Not null |
| VocabQuizAutoPlaySampleAudio | bool | Auto-play sample sentence audio | false | Not null |
| VocabQuizShowMnemonicImage | bool | Show mnemonic image on correct answer | true | Not null |

**Rationale**:
- **VocabQuizDisplayDirection**: String enum for clarity. "TargetToNative" shows Korean word, asks for English. "NativeToTarget" shows English, asks for Korean.
- **Defaults**: TargetToNative (recognition before production), vocabulary audio enabled (pronunciation critical), sample audio disabled (optional enhancement), mnemonic enabled (visual learners benefit).
- **Naming**: Prefixed with `VocabQuiz` to distinguish from future activity preferences (reading, shadowing, etc.)

**Entity Diagram**:

```
┌──────────────────────────────────────────────┐
│              UserProfile                      │
├──────────────────────────────────────────────┤
│ Id: int (PK)                                 │
│ Name: string?                                │
│ NativeLanguage: string = "English"           │
│ TargetLanguage: string = "Korean"            │
│ DisplayLanguage: string?                     │
│ Email: string?                               │
│ OpenAI_APIKey: string?                       │
│ PreferredSessionMinutes: int = 20            │
│ TargetCEFRLevel: string?                     │
│ CreatedAt: DateTime                          │
│ ─────────────────────────────────────────── │
│ VocabQuizDisplayDirection: string = "..." ← NEW │
│ VocabQuizAutoPlayVocabAudio: bool = true  ← NEW │
│ VocabQuizAutoPlaySampleAudio: bool = false← NEW │
│ VocabQuizShowMnemonicImage: bool = true   ← NEW │
└──────────────────────────────────────────────┘
```

**Migration**:
- Migration file: `20251213_AddVocabularyQuizPreferences.cs`
- Add columns with default values
- Existing UserProfile records get defaults automatically
- No data loss, backward compatible

### VocabularyWord (No Changes)

**Location**: `src/SentenceStudio.Shared/Models/VocabularyWord.cs`

**Relevant Existing Fields**:
- `AudioPronunciationUri: string?` - Used for auto-play vocabulary audio
- `MnemonicImageUri: string?` - Used for displaying mnemonic image
- `ExampleSentences: List<ExampleSentence>` - Navigation to sample sentences

**No changes needed** - existing schema already supports all features.

### ExampleSentence (No Changes)

**Location**: `src/SentenceStudio.Shared/Models/ExampleSentence.cs`

**Relevant Existing Fields**:
- `AudioUri: string?` - Used for auto-play sample sentence audio
- `IsCore: bool` - Used for prioritizing which sentence to auto-play
- `TargetSentence: string` - The sentence text
- `CreatedAt: DateTime` - Used for deterministic ordering

**No changes needed** - existing schema already supports sample sentence auto-play.

## Validation Rules

### UserProfile.VocabQuizDisplayDirection

**Valid Values**:
- `"TargetToNative"` - Show target language (Korean), answer in native language (English)
- `"NativeToTarget"` - Show native language (English), answer in target language (Korean)

**Validation Logic**:
```csharp
public bool IsValidDisplayDirection(string direction)
{
    return direction == "TargetToNative" || direction == "NativeToTarget";
}
```

**Error Handling**:
- Invalid values default to "TargetToNative"
- Log warning if invalid value detected
- UI enforces valid values via radio buttons (no free text entry)

### Audio Auto-Play Dependencies

**Business Rule**: Sample audio only plays if vocabulary audio is enabled

**Validation Logic**:
```csharp
public bool ShouldPlaySampleAudio(UserProfile profile)
{
    // Sample audio requires vocabulary audio to be enabled
    return profile.VocabQuizAutoPlayVocabAudio 
        && profile.VocabQuizAutoPlaySampleAudio;
}
```

**UI Behavior**:
- Sample audio checkbox disabled when vocabulary audio unchecked
- Tooltip explains dependency: "Requires vocabulary audio"

## State Transitions

### Preference Change Flow

```
┌───────────────────────────────────────────────────────────────┐
│  User Opens Preferences                                        │
└───────────────┬───────────────────────────────────────────────┘
                │
                ▼
┌───────────────────────────────────────────────────────────────┐
│  Load Current Preferences from UserProfile                     │
│  - VocabQuizDisplayDirection                                   │
│  - VocabQuizAutoPlayVocabAudio                                 │
│  - VocabQuizAutoPlaySampleAudio                                │
│  - VocabQuizShowMnemonicImage                                  │
└───────────────┬───────────────────────────────────────────────┘
                │
                ▼
┌───────────────────────────────────────────────────────────────┐
│  User Modifies Preference(s)                                   │
│  - Toggle checkbox                                             │
│  - Select radio button                                         │
└───────────────┬───────────────────────────────────────────────┘
                │
                ▼
┌───────────────────────────────────────────────────────────────┐
│  Save Preferences to Database (UserProfileRepository)          │
│  - Immediate save (no "Save" button)                           │
│  - Optimistic UI update                                        │
└───────────────┬───────────────────────────────────────────────┘
                │
                ▼
┌───────────────────────────────────────────────────────────────┐
│  Close Preferences Sheet                                       │
└───────────────┬───────────────────────────────────────────────┘
                │
                ▼
┌───────────────────────────────────────────────────────────────┐
│  Continue Quiz with New Preferences                            │
│  - Applied starting on NEXT question (not current)             │
│  - FR-020: No retroactive effects                              │
└───────────────────────────────────────────────────────────────┘
```

### Audio Playback Flow

```
┌───────────────────────────────────────────────────────────────┐
│  Question Displayed                                            │
└───────────────┬───────────────────────────────────────────────┘
                │
                ▼
┌───────────────────────────────────────────────────────────────┐
│  Check Preference: VocabQuizAutoPlayVocabAudio?                │
└───────────────┬───────────────────────────────────────────────┘
                │
        ┌───────┴───────┐
        │               │
      YES              NO
        │               │
        ▼               └────────────────┐
┌──────────────────────┐                │
│  Check for Audio URI │                │
│  word.AudioPronunc-  │                │
│  iationUri exists?   │                │
└────────┬─────────────┘                │
         │                               │
   ┌─────┴─────┐                        │
  YES          NO                        │
   │            │                        │
   ▼            └──> [Log Warning]       │
┌──────────────────────────┐            │
│  Play Vocabulary Audio   │            │
│  - Create IAudioPlayer   │            │
│  - Subscribe PlaybackEnd │            │
│  - Start playback        │            │
└────────┬─────────────────┘            │
         │                               │
         ▼                               │
┌──────────────────────────────────┐    │
│  Audio Playback Ended Event      │    │
└────────┬─────────────────────────┘    │
         │                               │
         ▼                               │
┌──────────────────────────────────┐    │
│  Check: VocabQuizAutoPlay-       │    │
│  SampleAudio?                    │    │
└────────┬─────────────────────────┘    │
         │                               │
   ┌─────┴─────┐                        │
  YES          NO                        │
   │            │                        │
   ▼            └────────────────────────┤
┌──────────────────────────┐            │
│  Select Sample Sentence  │            │
│  - IsCore = true first   │            │
│  - Order by CreatedAt    │            │
│  - Check AudioUri exists │            │
└────────┬─────────────────┘            │
         │                               │
   ┌─────┴─────┐                        │
  Found       None                       │
   │            │                        │
   ▼            └──> [Skip]              │
┌──────────────────────────┐            │
│  Play Sample Audio       │            │
│  - Create IAudioPlayer   │            │
│  - Start playback        │            │
└────────┬─────────────────┘            │
         │                               │
         └───────────────────────────────┘
                        │
                        ▼
         ┌──────────────────────────────┐
         │  Wait for User Answer        │
         └──────────────────────────────┘
```

### Mnemonic Image Display Flow

```
┌───────────────────────────────────────────────────────────────┐
│  User Submits Correct Answer                                   │
└───────────────┬───────────────────────────────────────────────┘
                │
                ▼
┌───────────────────────────────────────────────────────────────┐
│  Check Preference: VocabQuizShowMnemonicImage?                 │
└───────────────┬───────────────────────────────────────────────┘
                │
        ┌───────┴───────┐
        │               │
      YES              NO
        │               │
        ▼               └──> [Show Confirmation Without Image]
┌──────────────────────┐
│  Check word.Mnemonic-│
│  ImageUri exists?    │
└────────┬─────────────┘
         │
   ┌─────┴─────┐
  YES          NO
   │            │
   ▼            └──> [Show Confirmation Without Image]
┌──────────────────────────┐
│  Display Confirmation    │
│  - Checkmark icon ✅     │
│  - Correct answer text   │
│  - Mnemonic image        │
│  - (1 second timeout)    │
└────────┬─────────────────┘
         │
         ▼
┌──────────────────────────────────┐
│  Auto-advance to Next Question   │
│  - Clear image from memory       │
│  - Load next question            │
└──────────────────────────────────┘
```

## Relationships

### Existing (No Changes)

```
UserProfile (1) ─────── (∞) VocabularyProgress
UserProfile (1) ─────── (∞) VocabularyAttempt
UserProfile (1) ─────── (∞) DailyPlanCompletion

VocabularyWord (1) ──── (∞) ExampleSentence
VocabularyWord (∞) ──── (∞) LearningResource (via ResourceVocabularyMapping)
VocabularyWord (1) ──── (∞) VocabularyProgress
VocabularyWord (1) ──── (∞) VocabularyAttempt
```

### Usage in Application Flow

```
VocabularyQuizPage
    │
    ├──> UserProfileRepository.GetCurrentUserProfileAsync()
    │    └──> Returns UserProfile with preferences
    │
    ├──> Reads preferences from UserProfile
    │    ├── VocabQuizDisplayDirection → determines question format
    │    ├── VocabQuizAutoPlayVocabAudio → triggers vocabulary audio
    │    ├── VocabQuizAutoPlaySampleAudio → triggers sample sentence audio
    │    └── VocabQuizShowMnemonicImage → shows/hides mnemonic on confirmation
    │
    └──> VocabularyQuizPreferencesBottomSheet
         └──> Updates UserProfile via UserProfileRepository
```

## Database Schema

### Migration: 20251213_AddVocabularyQuizPreferences

**Up Migration**:
```sql
ALTER TABLE UserProfiles 
ADD COLUMN VocabQuizDisplayDirection TEXT NOT NULL DEFAULT 'TargetToNative';

ALTER TABLE UserProfiles 
ADD COLUMN VocabQuizAutoPlayVocabAudio INTEGER NOT NULL DEFAULT 1;

ALTER TABLE UserProfiles 
ADD COLUMN VocabQuizAutoPlaySampleAudio INTEGER NOT NULL DEFAULT 0;

ALTER TABLE UserProfiles 
ADD COLUMN VocabQuizShowMnemonicImage INTEGER NOT NULL DEFAULT 1;
```

**Down Migration**:
```sql
ALTER TABLE UserProfiles DROP COLUMN VocabQuizDisplayDirection;
ALTER TABLE UserProfiles DROP COLUMN VocabQuizAutoPlayVocabAudio;
ALTER TABLE UserProfiles DROP COLUMN VocabQuizAutoPlaySampleAudio;
ALTER TABLE UserProfiles DROP COLUMN VocabQuizShowMnemonicImage;
```

**Notes**:
- SQLite uses INTEGER for boolean (0 = false, 1 = true)
- Default values ensure backward compatibility
- No data migration needed (all new fields)

## Index Strategy

**No new indexes required** - preferences are always loaded as part of UserProfile by primary key lookup. No filtering/searching on preference fields.

## Performance Considerations

### Load Time
- **Impact**: +4 columns to UserProfile query
- **Estimated Cost**: <1ms (columns are simple primitives, no joins)
- **Acceptable**: Well under 100ms UI response budget

### Save Time
- **Impact**: Update 1-4 columns on UserProfile row
- **Estimated Cost**: 5-10ms per save
- **Mitigation**: Use optimistic UI updates (show change immediately, save async)

### Memory
- **Impact**: +24 bytes per UserProfile instance (1 string + 3 bools)
- **Single-user app**: Only 1 UserProfile loaded at a time
- **Acceptable**: Negligible memory impact

## Summary

All data model design completed:
- ✅ UserProfile extended with 4 new preference fields
- ✅ Validation rules defined for display direction and audio dependencies
- ✅ State transitions documented for preference changes and audio playback
- ✅ Database migration planned with backward compatibility
- ✅ No new entities required - extends existing schema
- ✅ Performance impact minimal (<10ms save, <1ms load)

Ready to proceed to contracts and quickstart.
