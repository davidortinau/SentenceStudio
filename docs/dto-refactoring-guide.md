# DTO Refactoring for Translation and Clozure Activities

## Overview

This refactoring introduces specialized Data Transfer Objects (DTOs) for Translation and Clozure activities to improve AI response quality. Previously, both activities shared the same `Challenge` model, which led to confusion for the AI and suboptimal responses.

## What Changed

### 1. New DTO Models

#### ClozureDto (`/src/SentenceStudio.Shared/Models/ClozureDto.cs`)
- **Purpose**: Specialized for fill-in-the-blank (clozure) exercises
- **Key Properties**:
  - `SentenceText`: Korean sentence with `__` placeholder
  - `VocabularyWord`: Dictionary form from vocabulary list
  - `VocabularyWordAsUsed`: Conjugated form that fits in sentence
  - `VocabularyWordGuesses`: 5 comma-separated multiple-choice options
  - `RecommendedTranslation`: English translation

#### TranslationDto (`/src/SentenceStudio.Shared/Models/TranslationDto.cs`)
- **Purpose**: Specialized for translation exercises
- **Key Properties**:
  - `SentenceText`: Natural Korean sentence
  - `RecommendedTranslation`: Natural English translation
  - `TargetVocabulary`: List of vocabulary words used in sentence

### 2. Enhanced Templates

#### GetClozuresV2.scriban-txt
- **Clear JSON structure**: Forces AI to respond with exact JSON format
- **Specific requirements**: Detailed instructions for clozure exercise generation
- **Multiple choice guidance**: Explicit rules for generating 5 plausible options

#### GetTranslations.scriban-txt
- **Translation-focused**: Optimized for natural conversation sentences
- **Vocabulary tracking**: Records which words are used in each sentence
- **Flexible usage**: Allows 1-3 vocabulary words per sentence

### 3. Service Updates

#### ClozureService Updates
- **New method**: `GetSentences()` now uses `ClozureResponse` DTO
- **DTO-to-Challenge mapping**: Converts AI responses to app's Challenge model
- **Enhanced vocabulary linking**: Improved matching strategies for vocabulary words
- **Better error handling**: More robust fallback mechanisms

#### TranslationService Updates
- **New method**: `GetTranslationSentences()` for translation-specific sentence generation
- **DTO-to-Challenge mapping**: Converts `TranslationDto` objects to `Challenge` objects
- **Multi-vocabulary support**: Handles sentences with multiple vocabulary words
- **Vocabulary validation**: Ensures AI only uses words from provided vocabulary list

### 4. Page Updates

#### TranslationPage
- **Service switch**: Now uses `TranslationService` instead of `TeacherService`
- **Better vocabulary hints**: Provides relevant vocabulary words as hints
- **Improved loading**: More robust sentence loading and error handling

## Benefits

### 🎯 Improved AI Responses
- **Clear instructions**: DTOs provide explicit structure for AI responses
- **Activity-specific data**: Each activity gets data tailored to its needs
- **Reduced confusion**: AI no longer tries to fit all activities into one model

### 🔧 Better Maintainability
- **Separation of concerns**: Each activity has its own data contracts
- **Type safety**: Strong typing prevents data mismatches
- **Easier debugging**: Clear data flow from AI → DTO → Challenge

### 📈 Enhanced User Experience
- **More relevant exercises**: AI generates content optimized for each activity type
- **Better vocabulary tracking**: Improved linking between exercises and vocabulary words
- **Consistent behavior**: Each activity behaves predictably

## Usage Examples

### Clozure Activity
```csharp
var clozureService = serviceProvider.GetRequiredService<ClozureService>();
var challenges = await clozureService.GetSentences(resourceId, 5, skillId);
// Returns Challenge objects optimized for fill-in-the-blank exercises
```

### Translation Activity
```csharp
var translationService = serviceProvider.GetRequiredService<TranslationService>();
var challenges = await translationService.GetTranslationSentences(resourceId, 5, skillId);
// Returns Challenge objects optimized for translation practice
```

## Migration Notes

- **Backward compatibility**: Existing `Challenge` model remains unchanged
- **Service registration**: `TranslationService` already registered in DI container
- **Database**: No schema changes required - DTOs are for AI communication only
- **Old templates**: Original templates remain for reference but are not used

## Future Enhancements

1. **More DTOs**: Consider specialized DTOs for other activities (Shadowing, Conversation, etc.)
2. **Response validation**: Add JSON schema validation for AI responses
3. **Template versioning**: Version templates to track improvements over time
4. **Performance monitoring**: Track AI response quality metrics per activity type

## 🏴‍☠️ Captain's Notes

This refactoring follows the principle of "right tool for the right job" - each activity now gets AI responses specifically crafted for its needs. The AI will no longer be confused about whether to generate clozure blanks or translation pairs, leading to much better quality exercises for your language learners!
