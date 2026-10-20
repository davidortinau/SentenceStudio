using System.Net;
using AVFoundation;
using Foundation;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Platform;

/// <summary>Non-UI iOS playback and bounded photo bytes for the SwiftUI quiz.</summary>
public sealed class NativeQuizMedia(
    DeviceUserScopeProvider scope,
    IPreferencesService preferences,
    IConnectivityService connectivity,
    IFileSystemService files,
    SpeechVoicePreferences voices,
    StreamHistoryRepository history,
    IAuthService? auth,
    INativeQuizSpeechSource? speech,
    Uri? apiRoot) : INativeQuizMedia, IDisposable
{
    private const int MaxAudioBytes = 8 * 1024 * 1024;
    private const int MaxPhotoBytes = 8 * 1024 * 1024;
    private readonly object _audioGate = new();
    private readonly SemaphoreSlim _photoGate = new(1, 1);
    private readonly HttpClient _photos = new(new HttpClientHandler
    {
        AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(20) };
    private AVAudioPlayer? _player;
    private NSData? _audioData;
    private TaskCompletionSource<bool>? _playbackCompletion;
    private EventHandler<AVStatusEventArgs>? _finishedPlaying;
    private EventHandler<AVErrorEventArgs>? _decoderError;
    private NSObject? _interruptionObserver;
    private long _audioVersion;
    private string? _photoOwner;
    private string? _photoUri;
    private NativeQuizPhotoAsset? _photoAsset;

    private bool TryOwner(out string owner)
    {
        owner = string.Empty;
        if (!scope.TryGetUserProfileId(out var active)
            || string.IsNullOrWhiteSpace(active)
            || preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty) != active)
            return false;
        owner = active;
        return true;
    }

    public async Task<bool> CanPlayPromptAsync(
        string text, string language, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text) || !TryOwner(out var owner))
            return false;
        var voice = voices.GetVoiceForLanguage(language);
        var cached = await history.GetStreamHistoryByPhraseAndVoiceAsync(text, voice);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryOwner(out var current) || current != owner)
            return false;
        return IsCachedAudio(cached?.AudioFilePath)
            || connectivity.IsInternetAvailable && speech is not null
                && auth?.IsSignedIn == true;
    }

    public async Task<bool> PlayPromptAsync(
        string text, string language, CancellationToken cancellationToken)
    {
        long audioVersion;
        lock (_audioGate)
            audioVersion = _audioVersion;
        if (!await CanPlayPromptAsync(text, language, cancellationToken)
            || !TryOwner(out var owner))
            return false;
        var voice = voices.GetVoiceForLanguage(language);
        var cached = await history.GetStreamHistoryByPhraseAndVoiceAsync(text, voice);
        byte[]? bytes = null;
        if (IsCachedAudio(cached?.AudioFilePath))
        {
            using var stream = File.OpenRead(cached!.AudioFilePath!);
            bytes = await ReadLimitedAsync(stream, MaxAudioBytes, cancellationToken);
        }
        else if (connectivity.IsInternetAvailable && speech is not null
            && auth?.IsSignedIn == true)
        {
            using var stream = await speech.SynthesizeAsync(
                owner, text, voice, cancellationToken);
            if (stream is not null)
            {
                bytes = await ReadLimitedAsync(stream, MaxAudioBytes, cancellationToken);
                if (bytes is { Length: > 0 } && TryOwner(out var current) && current == owner)
                {
                    try
                    {
                        var directory = Path.Combine(files.AppDataDirectory, "AudioCache");
                        Directory.CreateDirectory(directory);
                        var path = Path.Combine(directory, $"word_{Guid.NewGuid():N}.mp3");
                        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                        await history.SaveStreamHistoryAsync(new StreamHistory
                        {
                            UserProfileId = owner,
                            Phrase = text,
                            VoiceId = voice,
                            AudioFilePath = path,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        });
                    }
                    catch (IOException)
                    {
                        // The just-synthesized sound can still play without a cache write.
                    }
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes is not { Length: > 0 } || !TryOwner(out var active) || active != owner)
            return false;

        AVAudioPlayer player;
        Task<bool> completion;
        long startedVersion;
        lock (_audioGate)
        {
            if (audioVersion != _audioVersion || cancellationToken.IsCancellationRequested
                || !TryOwner(out var beforePlay) || beforePlay != owner)
                return false;
            StopPlaybackCore();
            _audioData = NSData.FromArray(bytes);
            try
            {
                _player = AVAudioPlayer.FromData(_audioData, out var error);
                if (_player is null || error is not null)
                {
                    StopPlaybackCore();
                    return false;
                }
                player = _player;
                var playbackCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _playbackCompletion = playbackCompletion;
                _finishedPlaying = (_, status) => SignalPlayback(player, status.Status);
                _decoderError = (_, _) => SignalPlayback(player, false);
                player.FinishedPlaying += _finishedPlaying;
                player.DecoderError += _decoderError;
                _interruptionObserver = AVAudioSession.Notifications.ObserveInterruption(
                    (_, interruption) =>
                    {
                        if (interruption.InterruptionType == AVAudioSessionInterruptionType.Began)
                            SignalPlayback(player, false);
                    });
                if (!player.Play())
                {
                    StopPlaybackCore();
                    return false;
                }
                startedVersion = _audioVersion;
                completion = playbackCompletion.Task;
            }
            catch
            {
                StopPlaybackCore();
                throw;
            }
        }

        using var cancellation = cancellationToken.Register(() =>
        {
            lock (_audioGate)
                if (ReferenceEquals(_player, player))
                    StopPlaybackCore();
        });
        try
        {
            var finished = await completion.WaitAsync(cancellationToken);
            lock (_audioGate)
                return finished && _audioVersion == startedVersion
                    && ReferenceEquals(_player, player)
                    && !cancellationToken.IsCancellationRequested
                    && TryOwner(out var current) && current == owner;
        }
        finally
        {
            lock (_audioGate)
                if (ReferenceEquals(_player, player))
                    StopPlaybackCore();
        }
    }

    private void SignalPlayback(AVAudioPlayer player, bool completed)
    {
        lock (_audioGate)
            if (ReferenceEquals(_player, player))
                _playbackCompletion?.TrySetResult(completed);
    }

    public void StopPlayback()
    {
        lock (_audioGate)
            StopPlaybackCore();
    }

    private void StopPlaybackCore()
    {
        _audioVersion++;
        var player = _player;
        var data = _audioData;
        var completion = _playbackCompletion;
        var finishedPlaying = _finishedPlaying;
        var decoderError = _decoderError;
        var interruptionObserver = _interruptionObserver;
        _player = null;
        _audioData = null;
        _playbackCompletion = null;
        _finishedPlaying = null;
        _decoderError = null;
        _interruptionObserver = null;

        if (player is not null)
        {
            if (finishedPlaying is not null)
                player.FinishedPlaying -= finishedPlaying;
            if (decoderError is not null)
                player.DecoderError -= decoderError;
        }
        interruptionObserver?.Dispose();
        player?.Stop();
        player?.Dispose();
        data?.Dispose();
        completion?.TrySetResult(false);
    }

    private bool IsCachedAudio(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var directory = Path.GetFullPath(
            Path.Combine(files.AppDataDirectory, "AudioCache")) + Path.DirectorySeparatorChar;
        try
        {
            return Path.GetFullPath(path).StartsWith(directory, StringComparison.Ordinal)
                && File.Exists(path)
                && new FileInfo(path).Length is > 0 and <= MaxAudioBytes;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<bool> CanShowPhotoAsync(
        string photoUri, CancellationToken cancellationToken) =>
        await LoadPhotoAsync(photoUri, cancellationToken) is not null;

    public async Task<NativeQuizPhotoAsset?> LoadPhotoAsync(
        string photoUri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryOwner(out var owner) || string.IsNullOrWhiteSpace(photoUri))
            return null;
        await _photoGate.WaitAsync(cancellationToken);
        try
        {
            if (_photoOwner == owner && _photoUri == photoUri)
                return _photoAsset;
            _photoOwner = null;
            _photoAsset = null;
            _photoUri = null;
            byte[]? bytes;
            if (photoUri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var separator = photoUri.IndexOf(',');
                if (separator < 0 || !photoUri[..separator].EndsWith(
                    ";base64", StringComparison.OrdinalIgnoreCase)
                    || photoUri.Length - separator > MaxPhotoBytes * 2)
                    return null;
                try { bytes = Convert.FromBase64String(photoUri[(separator + 1)..]); }
                catch (FormatException) { return null; }
            }
            else
            {
                if (!Uri.TryCreate(photoUri, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps
                        && !(uri.Scheme == Uri.UriSchemeHttp
                            && apiRoot is not null && apiRoot.IsBaseOf(uri)))
                    return null;

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await _photos.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode != HttpStatusCode.OK
                    || response.Content.Headers.ContentLength > MaxPhotoBytes)
                    return null;
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                bytes = await ReadLimitedAsync(stream, MaxPhotoBytes, cancellationToken);
            }
            if (!TryOwner(out var active) || active != owner
                || bytes is not { Length: > 0 }
                || DetectImageContentType(bytes) is not { } type)
                return null;
            _photoOwner = owner;
            _photoUri = photoUri;
            _photoAsset = new NativeQuizPhotoAsset(bytes, type);
            return _photoAsset;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            _photoGate.Release();
        }
    }

    private static string? DetectImageContentType(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (data.Length >= pngSignature.Length
            && data[..pngSignature.Length].SequenceEqual(pngSignature))
            return "image/png";
        if (data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8
            && data[2] == 0xff) return "image/jpeg";
        if (data.Length >= 6 && data[..3].SequenceEqual("GIF"u8)
            && data[3] == '8') return "image/gif";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8)
            && data[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    private static async Task<byte[]?> ReadLimitedAsync(
        Stream stream, int maximum, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maximum)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public void Dispose()
    {
        StopPlayback();
        _photos.Dispose();
        _photoGate.Dispose();
    }
}
