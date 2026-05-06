---
confidence: high
last_validated: 2026-05-10
status: production-proven
pattern: TTS audio playback for Blazor activities
---

# Activity Audio Playback (Blazor + .NET MAUI)

## When to Use

Any Blazor activity page (Quiz, NumberDrill, Shadowing, Pronunciation drill, etc.) that needs to play text-to-speech audio for prompts, answers, or feedback.

## Required Service Injections

```razor
@using Plugin.Maui.Audio
@inject IAudioManager AudioManager
@inject ElevenLabsSpeechService SpeechService
@inject StreamHistoryRepository StreamHistoryRepo
@inject SentenceStudio.Services.SpeechVoicePreferences VoicePrefs
@inject SentenceStudio.Abstractions.IConnectivityService Connectivity
@inject SentenceStudio.Abstractions.IFileSystemService FileSystemService
@inject ToastService Toast
@inject IJSRuntime JS
@inject ILogger<YourPage> Logger
```

## Component State

```csharp
private bool isPlayingAudio;
private IAudioPlayer? audioPlayer;
```

## Cache-First Playback Pattern

The canonical implementation follows this flow:

```csharp
private async Task PlayAudioAsync(string text, string languageCode)
{
    if (isPlayingAudio || string.IsNullOrWhiteSpace(text))
        return;
        
    isPlayingAudio = true;

    try
    {
        var voiceId = VoicePrefs.GetVoiceForLanguage(languageCode);
        var cached = await StreamHistoryRepo.GetStreamHistoryByPhraseAndVoiceAsync(text, voiceId);
        Stream audioStream;

        // 1. Check cache first (offline-friendly)
        if (cached != null && !string.IsNullOrEmpty(cached.AudioFilePath) && File.Exists(cached.AudioFilePath))
        {
            audioStream = File.OpenRead(cached.AudioFilePath);
        }
        // 2. Generate TTS if online
        else if (Connectivity.IsInternetAvailable)
        {
            audioStream = await SpeechService.TextToSpeechAsync(text, voiceId, speed: 1.0f);

            // Cache to disk for future offline use
            var cacheDir = Path.Combine(FileSystemService.AppDataDirectory, "AudioCache");
            Directory.CreateDirectory(cacheDir);
            var filePath = Path.Combine(cacheDir, $"tts_{Guid.NewGuid()}.mp3");
            using (var fs = File.Create(filePath)) { await audioStream.CopyToAsync(fs); }

            await StreamHistoryRepo.SaveStreamHistoryAsync(new StreamHistory
            {
                Phrase = text,
                VoiceId = voiceId,
                AudioFilePath = filePath,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });

            audioStream = File.OpenRead(filePath);
        }
        // 3. Offline fallback
        else
        {
            Toast.ShowWarning("Audio unavailable offline.");
            return;
        }

        // 4. Play audio (dual path: native or WebApp)
        audioPlayer?.Stop();
        audioPlayer?.Dispose();
        audioPlayer = AudioManager.CreatePlayer(audioStream);
        if (audioPlayer != null)
        {
            // Native playback (iOS/Android/Mac Catalyst)
            audioPlayer.Play();
        }
        else
        {
            // Server-side Blazor (WebApp) fallback via JS interop
            audioStream.Position = 0;
            using var ms = new MemoryStream();
            await audioStream.CopyToAsync(ms);
            var base64 = Convert.ToBase64String(ms.ToArray());
            await JS.InvokeVoidAsync("audioInterop.playFromBase64", base64, "audio/mpeg");
        }
    }
    catch (Exception ex)
    {
        Logger.LogError(ex, "Failed to play audio for '{Text}'", text);
        Toast.ShowError("Failed to play audio.");
    }
    finally
    {
        isPlayingAudio = false;
    }
}
```

## Component Disposal

Always dispose the audio player on component teardown:

```csharp
public async ValueTask DisposeAsync()
{
    audioPlayer?.Stop();
    audioPlayer?.Dispose();
    
    // ... other disposal
}
```

## UI Pattern

Play button with disabled state during playback:

```razor
<button type="button" 
        class="btn btn-lg btn-ss-primary" 
        @onclick="PlayAudioAsync" 
        disabled="@isPlayingAudio">
    <i class="bi @(isPlayingAudio ? "bi-volume-mute" : "bi-volume-up") me-2"></i>
    @(isPlayingAudio ? "Playing..." : "Play")
</button>
```

## Key Principles

1. **Cache-first**: Always check `StreamHistoryRepo` before calling ElevenLabs API (reduces latency, enables offline mode)
2. **Dual playback path**: Native (`Plugin.Maui.Audio`) for MAUI apps, JS interop for WebApp
3. **Graceful degradation**: Toast warning if offline and not cached
4. **Cleanup discipline**: Dispose player on component teardown to prevent resource leaks
5. **User feedback**: Disable button + change icon during playback

## JS Interop Requirement (WebApp Only)

The WebApp fallback requires `audioInterop.playFromBase64()` to be available in `wwwroot/js/audioInterop.js`. This is already present in SentenceStudio.WebApp — do NOT duplicate it per activity.

```javascript
// Already exists in src/SentenceStudio.WebApp/wwwroot/js/audioInterop.js
window.audioInterop = {
    playFromBase64: function (base64Data, mimeType) {
        // ... implementation
    }
};
```

## When NOT to Use This Pattern

- **Pre-generated audio files**: If audio files are bundled with the app (not TTS), use `AudioManager.CreatePlayer(await FileSystem.OpenAppPackageFileAsync("audio.mp3"))`
- **Specialized caching**: If a feature has a dedicated audio cache service (e.g., `NumberAudioCache` for number drills), prefer that over `StreamHistoryRepo`

## Production Examples

- **VocabQuiz.razor**: `PlayAudioTextAsync()` method (lines 1446-1510)
- **NumberDrill.razor**: `PlayAudioAsync()` method (after 2026-05-10 fix)

## Common Mistakes

1. **Forgetting dual playback path**: Native MAUI apps need `AudioManager.CreatePlayer()`, WebApp needs JS interop fallback
2. **Not caching**: Every TTS call costs API credits and latency — always cache to disk
3. **Missing disposal**: Audio players hold system resources — always dispose in `DisposeAsync()`
4. **Hardcoding voice ID**: Use `VoicePrefs.GetVoiceForLanguage()` so user preferences are respected
5. **Leaking debug UI**: Remove placeholder text like "(TTS placeholder: ...)" before shipping

## Related Skills

- `blazor-hybrid-firstrender-jsinit`: JS interop timing for Blazor Hybrid
- `maui-azure-monitor`: Logging TTS failures to Application Insights
- `project-conventions`: Service injection patterns
