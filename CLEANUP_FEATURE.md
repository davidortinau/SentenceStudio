# Vocabulary Cleanup Feature

## Overview
Added cleanup functionality to the Vocabulary Management page to handle two common data quality issues:

## Features

### 1. Language Swap Cleanup
**Problem**: Some vocabulary words have English terms in the target language field and Korean terms in the native language field.

**Solution**: 
- Detects words where target term appears to be English (all ASCII) and native term appears to be Korean (contains Hangul characters)
- Swaps the NativeLanguageTerm ↔ TargetLanguageTerm
- Handles duplicates intelligently:
  - If swapping would create a duplicate, merges the associations and deletes the redundant word
  - If no duplicate exists, updates the word with swapped terms

**Detection Logic**:
- English: All ASCII characters (Latin alphabet)
- Korean: Contains Hangul characters (Unicode ranges U+AC00 to U+D7AF, U+1100 to U+11FF, U+3130 to U+318F)

### 2. Orphan Assignment
**Problem**: Some vocabulary words are not associated with any learning resource, making them hard to organize and review.

**Solution**:
- Finds all orphaned words (words without resource associations)
- Creates a "General Vocabulary" learning resource if it doesn't exist
- Bulk associates all orphaned words to this catch-all resource

## UI Implementation

### Toolbar Item
- Added "Cleanup" toolbar item (secondary order) with broom icon
- Opens a bottom sheet with cleanup options

### Bottom Sheet
- Clean, organized interface with two sections
- Each section has:
  - Bold title
  - Descriptive text explaining what the cleanup does
  - Action button
- Progress indicator shown while cleanup is running
- Buttons disabled during execution to prevent double-clicks

### User Feedback
- Toast notifications show results:
  - Language swap: Shows count of swapped and merged words
  - Orphan assignment: Shows count of assigned words
  - Special message if no orphans found
- Error alerts if something goes wrong

## Technical Details

### State Management
Added to `VocabularyManagementPageState`:
- `IsCleanupSheetOpen` - Controls bottom sheet visibility
- `IsCleanupRunning` - Prevents concurrent cleanup operations

### Methods
- `RenderCleanupSheet()` - Bottom sheet UI
- `RunLanguageSwapCleanup()` - Language swap logic
- `RunOrphanAssignment()` - Orphan assignment logic  
- `IsEnglish(string)` - Detects English text
- `IsKorean(string)` - Detects Korean text

### Repository Methods Used
- `GetAllVocabularyWordsWithResourcesAsync()` - Fetch all words for analysis
- `FindDuplicateVocabularyWordAsync()` - Check for duplicates
- `GetResourcesContainingWordAsync()` - Get word associations
- `AddVocabularyToResourceAsync()` - Transfer associations
- `DeleteVocabularyWordAsync()` - Remove duplicates
- `UpdateVocabularyWordAsync()` - Update swapped words
- `GetOrphanedVocabularyWordsAsync()` - Find unassociated words
- `SaveResourceAsync()` - Create general resource
- `BulkAssociateWordsWithResourceAsync()` - Bulk assignment

## Testing Recommendations
1. Test with words that have mixed language assignments
2. Test with existing duplicates
3. Test with no orphaned words
4. Test concurrent cleanup attempts (should be prevented)
5. Verify data integrity after cleanup operations

## Future Enhancements
- Add confirmation dialogs before running cleanup
- Show preview of affected words before executing
- Add undo functionality
- Expand language detection to support other languages
- Add cleanup history/audit log
