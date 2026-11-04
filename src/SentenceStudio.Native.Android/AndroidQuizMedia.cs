using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using AudioAttributes = Android.Media.AudioAttributes;
using AudioContentType = Android.Media.AudioContentType;
using AudioUsageKind = Android.Media.AudioUsageKind;
using Bitmap = Android.Graphics.Bitmap;
using BitmapFactory = Android.Graphics.BitmapFactory;
using MediaPlayer = Android.Media.MediaPlayer;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Non-UI quiz media for the Compose quiz. Prompt and answer audio use the
/// source speech service with the learner's selected voice and the owner's
/// StreamHistory cache (the shared NativeShadowingSpeech path, as on iOS), and
/// play through MediaPlayer. Photos are owner-checked, size-bounded bytes that
/// decode to a Bitmap before the presenter is told a photo exists.
/// </summary>
internal sealed class AndroidQuizMedia(
    MainActivity activity,
    Uri apiRoot,
    DeviceUserScopeProvider ownerScope,
    IPreferencesService preferences,
    IConnectivityService connectivity,
    IFileSystemService files,
    SpeechVoicePreferences voices,
    IServiceScopeFactory scopes,
    ILoggerFactory loggers,
    IAuthService? auth,
    INativeQuizSpeechSource? speech,
    ILogger<AndroidQuizMedia> logger) : INativeQuizMedia, IDisposable
{
    private const int MaxPhotoBytes = 6 * 1024 * 1024;
    private const int MaxAudioBytes = 8 * 1024 * 1024;
    private const int MaxBitmapEdge = 1600;
    private const int MaxCachedPhotos = 3;
    private static readonly TimeSpan PlaybackGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxClipLength = TimeSpan.FromSeconds(30);
    private readonly HttpClient _images = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private readonly NativeActivityOwner _owner = new(ownerScope, preferences);
    private readonly object _photosGate = new();
    private readonly List<CachedPhoto> _photos = [];
    private readonly object _audioGate = new();
    private Playback? _playback;
    private long _audioVersion;
    private bool _disposed;

    public async Task<bool> CanPlayPromptAsync(
        string text, string language, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || string.IsNullOrWhiteSpace(text)
            || !TryGetCurrentOwner(out var owner))
            return false;
        var voice = voices.GetVoiceForLanguage(language);
        // SQLite runs synchronously; keep the owner's cache lookup off the UI thread.
        var cached = await Task.Run(async () =>
        {
            using var scope = scopes.CreateScope();
            return await scope.ServiceProvider
                .GetRequiredService<StreamHistoryRepository>()
                .GetStreamHistoryByPhraseAndVoiceAsync(text, voice);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_owner.IsActive(owner))
            return false;
        return cached?.UserProfileId == owner
                && NativeAudioCache.Resolve(files.AppDataDirectory,
                    cached.AudioFilePath, MaxAudioBytes) is not null
            || connectivity.IsInternetAvailable && speech is not null
                && auth?.IsSignedIn == true;
    }

    public async Task<bool> PlayPromptAsync(
        string text, string language, CancellationToken cancellationToken)
    {
        long requestedVersion;
        lock (_audioGate)
            requestedVersion = _audioVersion;
        if (_disposed || string.IsNullOrWhiteSpace(text)
            || !TryGetCurrentOwner(out var owner))
            return false;

        // The owner's cached clip, else synthesis with the selected voice (then cached);
        // its SQLite and file work stays off the UI thread. Playback starts on return.
        var clip = await Task.Run(async () =>
        {
            using var scope = scopes.CreateScope();
            var clips = new NativeShadowingSpeech(_owner, connectivity, files, voices,
                scope.ServiceProvider.GetRequiredService<StreamHistoryRepository>(),
                loggers.CreateLogger<NativeShadowingSpeech>(), speech, auth);
            return await clips.GetAsync(owner, text, language, cancellationToken);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (clip is not { Status: NativeSpeechClipStatus.Ready, Audio: { Length: > 0 } audio }
            || !_owner.IsActive(owner))
            return false;
        return await PlayClipAsync(audio, owner, requestedVersion, cancellationToken);
    }

    private async Task<bool> PlayClipAsync(
        byte[] audio, string owner, long requestedVersion,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(
            activity.CacheDir?.AbsolutePath ?? files.AppDataDirectory, "quiz-audio");
        Directory.CreateDirectory(directory);
        // Task-owned playback copy; the cached clip itself stays in AudioCache.
        var file = Path.Combine(directory, $"prompt_{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(file, audio, cancellationToken);

        Playback playback;
        TimeSpan limit;
        long startedVersion;
        lock (_audioGate)
        {
            if (_disposed || requestedVersion != _audioVersion
                || cancellationToken.IsCancellationRequested
                || !_owner.IsActive(owner))
            {
                DeleteQuietly(file);
                return false;
            }
            StopPlaybackCore();
            var player = new MediaPlayer();
            try
            {
                player.SetAudioAttributes(new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.Media)!
                    .SetContentType(AudioContentType.Speech)!
                    .Build()!);
                player.SetDataSource(file);
                player.Prepare();
                playback = new Playback(player, file);
                limit = TimeSpan.FromMilliseconds(Math.Clamp(player.Duration, 0,
                    (int)MaxClipLength.TotalMilliseconds)) + PlaybackGrace;
                _playback = playback;
                player.Start();
                startedVersion = _audioVersion;
            }
            catch (Exception ex) when (ex is Java.Lang.Exception or IOException)
            {
                logger.LogWarning("Quiz audio could not start ({FailureType}).",
                    ex.GetType().Name);
                if (_playback is not null && ReferenceEquals(_playback.Player, player))
                    StopPlaybackCore();
                else
                {
                    player.Release();
                    player.Dispose();
                    DeleteQuietly(file);
                }
                return false;
            }
        }

        using var cancellation = cancellationToken.Register(() =>
        {
            lock (_audioGate)
                if (ReferenceEquals(_playback, playback))
                    StopPlaybackCore();
        });
        try
        {
            var finished = await playback.Completed.WaitAsync(limit, cancellationToken);
            lock (_audioGate)
                return finished && startedVersion == _audioVersion
                    && ReferenceEquals(_playback, playback)
                    && !cancellationToken.IsCancellationRequested
                    && _owner.IsActive(owner);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Quiz audio did not finish in time.");
            return false;
        }
        finally
        {
            lock (_audioGate)
                if (ReferenceEquals(_playback, playback))
                    StopPlaybackCore();
        }
    }

    public void StopPlayback()
    {
        lock (_audioGate)
            StopPlaybackCore();
    }

    private void StopPlaybackCore()
    {
        _audioVersion++;
        var playback = _playback;
        _playback = null;
        playback?.Release();
    }

    public async Task<bool> CanShowPhotoAsync(
        string photoUri, CancellationToken cancellationToken) =>
        await LoadPhotoAsync(photoUri, cancellationToken) is not null;

    public async Task<NativeQuizPhotoAsset?> LoadPhotoAsync(
        string photoUri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || string.IsNullOrWhiteSpace(photoUri)
            || !TryGetCurrentOwner(out var owner))
            return null;
        lock (_photosGate)
            if (FindPhoto(owner, photoUri) is { } cached)
                return cached.Asset;

        try
        {
            var encoded = await ReadPhotoAsync(photoUri, owner, cancellationToken);
            if (encoded is not { Length: > 0 }
                || AndroidQuizMediaPolicy.DetectImageContentType(encoded) is not { } type)
                return null;
            var bitmap = await Task.Run(() => Decode(encoded), cancellationToken);
            if (bitmap is null)
                return null;

            lock (_photosGate)
            {
                if (_disposed || !_owner.IsActive(owner))
                {
                    bitmap.Dispose();
                    return null;
                }
                if (FindPhoto(owner, photoUri) is { } raced)
                {
                    bitmap.Dispose();
                    return raced.Asset;
                }
                var asset = new NativeQuizPhotoAsset(encoded, type);
                _photos.Add(new CachedPhoto(owner, photoUri, asset, bitmap));
                // Evicted bitmaps are left to the GC: a recycled bitmap could still
                // back a painter in the current composition.
                while (_photos.Count > MaxCachedPhotos)
                    _photos.RemoveAt(0);
                return asset;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            or FormatException or TaskCanceledException or Java.Lang.Exception)
        {
            logger.LogWarning("Quiz photo is unavailable ({FailureType}).", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>The decoded bitmap for a photo this adapter loaded for the active owner.</summary>
    internal bool TryGetPhoto(NativeQuizPhotoAsset? asset, out Bitmap? bitmap)
    {
        bitmap = null;
        if (asset is null || !TryGetCurrentOwner(out var owner))
            return false;
        lock (_photosGate)
        {
            foreach (var photo in _photos)
                if (photo.Owner == owner && ReferenceEquals(photo.Asset, asset)
                    && !photo.Bitmap.IsRecycled)
                {
                    bitmap = photo.Bitmap;
                    return true;
                }
        }
        return false;
    }

    internal void ClearForSignedOut()
    {
        lock (_photosGate)
            _photos.Clear();
        StopPlayback();
    }

    private CachedPhoto? FindPhoto(string owner, string uri)
    {
        for (var index = 0; index < _photos.Count; index++)
            if (_photos[index].Owner == owner && _photos[index].Uri == uri
                && !_photos[index].Bitmap.IsRecycled)
            {
                var found = _photos[index];
                _photos.RemoveAt(index);
                _photos.Add(found);
                return found;
            }
        return null;
    }

    private static Bitmap? Decode(byte[] encoded)
    {
        var options = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(encoded, 0, encoded.Length, options)?.Dispose();
        if (options.OutWidth <= 0 || options.OutHeight <= 0)
            return null;
        var sample = 1;
        while (Math.Max(options.OutWidth, options.OutHeight) / sample > MaxBitmapEdge)
            sample *= 2;
        options.InJustDecodeBounds = false;
        options.InSampleSize = sample;
        return BitmapFactory.DecodeByteArray(encoded, 0, encoded.Length, options);
    }

    private bool TryGetCurrentOwner(out string owner) => _owner.TryCurrent(out owner);

    private async Task<byte[]?> ReadPhotoAsync(
        string source, string owner, CancellationToken cancellationToken)
    {
        if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            var separator = source.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (separator < 0 || source.Length - separator > MaxPhotoBytes * 2)
                return null;
            var mime = source[11..separator].ToLowerInvariant();
            if (mime is not ("png" or "jpeg" or "webp" or "gif"))
                return null;
            var bytes = Convert.FromBase64String(source[(separator + 8)..]);
            return bytes.Length <= MaxPhotoBytes ? bytes : null;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var fileUri)
            && fileUri.IsFile)
        {
            var root = activity.FilesDir?.AbsolutePath;
            if (root is null
                || !AndroidQuizMediaPolicy.IsPrivatePhoto(
                    fileUri.LocalPath, root, owner))
                return null;
            var file = new FileInfo(fileUri.LocalPath);
            if (!file.Exists || file.LinkTarget is not null
                || file.Length > MaxPhotoBytes)
                return null;
            return await File.ReadAllBytesAsync(file.FullName, cancellationToken);
        }

#if DEBUG
        const bool allowEmulatorHttp = true;
#else
        const bool allowEmulatorHttp = false;
#endif
        if (!AndroidQuizMediaPolicy.TryResolveRemotePhoto(
                source, apiRoot, allowEmulatorHttp, out var uri))
            return null;
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            if (!AndroidQuizMediaPolicy.CanRequestWithoutOwnerToken(uri, apiRoot))
                return null;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _images.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects == 3 || response.Headers.Location is not { } location
                    || !AndroidQuizMediaPolicy.TryResolveRemotePhoto(
                        new Uri(request.RequestUri!, location).AbsoluteUri,
                        apiRoot, allowEmulatorHttp, out uri))
                    return null;
                continue;
            }
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength is > MaxPhotoBytes)
                return null;
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null
                && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mediaType, "application/octet-stream",
                    StringComparison.OrdinalIgnoreCase))
                return null;

            await using var network = await response.Content
                .ReadAsStreamAsync(cancellationToken);
            using var encoded = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await network.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (encoded.Length + read > MaxPhotoBytes)
                    return null;
                encoded.Write(buffer, 0, read);
            }
            return encoded.ToArray();
        }
        return null;
    }

    private static void DeleteQuietly(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        lock (_audioGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            StopPlaybackCore();
        }
        _images.Dispose();
        lock (_photosGate)
            _photos.Clear();
    }

    private sealed record CachedPhoto(
        string Owner, string Uri, NativeQuizPhotoAsset Asset, Bitmap Bitmap);

    /// <summary>One prepared MediaPlayer clip; completes true only when it plays to its end.</summary>
    private sealed class Playback
    {
        private readonly TaskCompletionSource<bool> _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _file;

        public Playback(MediaPlayer player, string file)
        {
            Player = player;
            _file = file;
            player.Completion += OnCompletion;
            player.Error += OnError;
        }

        public MediaPlayer Player { get; }
        public Task<bool> Completed => _completed.Task;

        private void OnCompletion(object? sender, EventArgs e) =>
            _completed.TrySetResult(true);

        private void OnError(object? sender, MediaPlayer.ErrorEventArgs e)
        {
            e.Handled = true;
            _completed.TrySetResult(false);
        }

        public void Release()
        {
            _completed.TrySetResult(false);
            Player.Completion -= OnCompletion;
            Player.Error -= OnError;
            try
            {
                if (Player.IsPlaying)
                    Player.Stop();
            }
            catch (Java.Lang.IllegalStateException)
            {
            }
            Player.Release();
            Player.Dispose();
            DeleteQuietly(_file);
        }
    }
}
