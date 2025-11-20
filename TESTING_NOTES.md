# Testing Notes for Reading Page UX Fixes

## Summary of Changes

This PR fixes three UX bugs in the Reading Page:

### 1. Font Size Increments (✅ FIXED)
**Issue**: Font size increase/decrease buttons used ±2 increments, which was too small.
**Fix**: Changed to ±4 increments for faster adjustment.
**File**: `src/SentenceStudio/Pages/Reading/ReadingPage.cs` (lines 886-910)

**How to Test**:
1. Open any reading activity
2. Tap the A+ button in the toolbar
3. Notice the font size increases noticeably (by 4 points instead of 2)
4. Tap A- button
5. Notice the font size decreases noticeably
6. Verify you can quickly adjust to your preferred size

### 2. Next/Previous Buttons Audio Navigation (✅ FIXED)
**Issue**: When audio is playing and you tap next/previous sentence buttons, the visual highlighting updates but the audio doesn't follow.
**Fix**: Changed the logic so that when audio IS playing, the audio manager controls both the audio position AND the visual highlighting via events. When audio is NOT playing, only the visual highlighting is updated.
**Files**: 
- `src/SentenceStudio/Pages/Reading/ReadingPage.cs` (lines 824-874)
- `src/SentenceStudio/Services/TimestampedAudioManager.cs` (lines 328-366)

**How to Test**:
1. Open any reading activity
2. Wait for audio to load
3. Tap the Play button (▶️) to start audio playback
4. While audio is playing, tap the Next Sentence button (⏭️)
5. **Expected**: Audio should jump to the next sentence and continue playing from there, with highlighting following
6. Tap Previous Sentence button (⏮️) while audio is still playing
7. **Expected**: Audio should jump back to previous sentence and continue playing
8. Now pause the audio (⏸️)
9. Tap Next/Previous buttons
10. **Expected**: Only the visual highlighting should change (no audio plays)
11. Tap Play (▶️) again
12. **Expected**: Audio should resume from the currently highlighted sentence

### 3. Double-Tap Sentence to Play (✅ IMPROVED)
**Issue**: Double-tapping a sentence should move the audio to that sentence and play it, but it may not have been working consistently.
**Fix**: Improved the `PlayFromSentenceAsync` method in TimestampedAudioManager to ensure it always starts playback after seeking. Added better logging to track the playback state.
**File**: `src/SentenceStudio/Services/TimestampedAudioManager.cs` (lines 328-366)

**How to Test**:
1. Open any reading activity
2. Wait for audio to load
3. Scroll down to a sentence further in the text
4. Double-tap on that sentence
5. **Expected**: 
   - You should see a toast "🏴‍☠️ Jumping to that sentence, Captain!" (first time only)
   - Audio should jump to that sentence
   - Audio should START playing from that sentence
   - Visual highlighting should follow the audio
6. Try double-tapping on different sentences throughout the text
7. **Expected**: Each time, audio should jump and play from that sentence

## Key Behavioral Changes

### Before:
- Font changes were too slow (±2 steps)
- Next/Previous buttons during playback would update UI immediately, then try to update audio, causing potential race conditions
- Audio might not consistently start after double-tap or seek operations

### After:
- Font changes are faster (±4 steps)
- When audio is playing, Next/Previous buttons delegate control to the audio manager, which updates both audio position and UI state via events (eliminates race conditions)
- When audio is NOT playing, Next/Previous buttons only update visual highlighting
- Audio playback is guaranteed to start/continue after seek operations

## Technical Details

### Race Condition Fix
The original code had this sequence:
```csharp
SetState(s => s.CurrentSentenceIndex = newIndex);  // Update UI first
await _audioManager.NextSentenceAsync();            // Then try to update audio
```

This could cause issues because:
1. UI updates immediately to newIndex
2. Audio manager seeks to newIndex
3. Audio manager's progress event fires and tries to update UI back to old position
4. Race condition!

The new code:
```csharp
if (State.IsAudioPlaying)
{
    await _audioManager.NextSentenceAsync();  // Audio manager controls everything
    // Audio manager will fire SentenceChanged event which updates UI
}
else
{
    SetState(s => s.CurrentSentenceIndex = newIndex);  // Just update UI
}
```

This ensures a single source of truth: when audio is playing, the audio manager is in control.

### Audio Playback Guarantee
Added explicit tracking of playback state before and after seek operations:
```csharp
bool wasPlaying = IsPlaying;
_player.Seek(sentenceInfo.StartTime);
if (!IsPlaying)
{
    Play();  // Ensure playback starts if it stopped during seek
}
```

This handles cases where some audio players may pause during seek operations.

## Logging for Debugging

All methods now include detailed logging:
- `PreviousSentence/NextSentence`: Logs whether audio is playing and which path is taken
- `PlayFromSentenceAsync`: Logs playback state before/after seek
- `OnProgressTimerElapsed`: Already had extensive logging for sentence tracking

To view logs on macOS:
```bash
log show --predicate 'process == "SentenceStudio"' --last 5m --style compact | grep -E "(PreviousSentence|NextSentence|PlayFromSentence)"
```
