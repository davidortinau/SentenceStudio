using Android.Graphics;
using Android.Speech.Tts;
using Java.Util;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Android;

internal sealed class AndroidQuizMedia(
    MainActivity activity,
    Uri apiRoot,
    DeviceUserScopeProvider ownerScope,
    IPreferencesService preferences,
    IConnectivityService connectivity,
    ILogger<AndroidQuizMedia> logger) : INativeQuizMedia, IDisposable
{
    private const int MaxPhotoBytes = 6 * 1024 * 1024;
    private const int MaxBitmapEdge = 1600;
    private readonly SemaphoreSlim _speechGate = new(1, 1);
    private readonly HttpClient _images = new(new HttpClientHandler
    {
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private readonly object _photosGate = new();
    private readonly Dictionary<(string Owner, string Uri), Bitmap> _photos = new();
    private readonly Queue<(string Owner, string Uri)> _photoOrder = new();
    private TextToSpeech? _speech;
    private SpeechInitialization? _initialization;
    private readonly SpeechProgress _progress = new();
    private bool _disposed;

    public async Task<bool> CanPlayPromptAsync(
        string text, string language, CancellationToken cancellationToken)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text)
            || AndroidQuizMediaPolicy.LanguageTag(language) is null)
            return false;

        await _speechGate.WaitAsync(cancellationToken);
        try
        {
            return await SelectVoiceAsync(language, cancellationToken);
        }
        finally
        {
            _speechGate.Release();
        }
    }

    public async Task<bool> PlayPromptAsync(
        string text, string language, CancellationToken cancellationToken)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text)
            || AndroidQuizMediaPolicy.LanguageTag(language) is null)
            return false;

        await _speechGate.WaitAsync(cancellationToken);
        try
        {
            if (!await SelectVoiceAsync(language, cancellationToken)
                || _speech is null)
                return false;

            var utteranceId = Guid.NewGuid().ToString("N");
            var completed = _progress.Begin(utteranceId);
            if ((int)_speech.Speak(text, QueueMode.Flush, null, utteranceId) != 0)
                return false;
            try
            {
                return await completed.WaitAsync(
                    TimeSpan.FromSeconds(8), cancellationToken);
            }
            catch (TimeoutException)
            {
                _speech.Stop();
                return false;
            }
            catch (OperationCanceledException)
            {
                _speech.Stop();
                throw;
            }
        }
        finally
        {
            _progress.End();
            _speechGate.Release();
        }
    }

    public async Task<bool> CanShowPhotoAsync(
        string photoUri, CancellationToken cancellationToken)
    {
        if (_disposed || string.IsNullOrWhiteSpace(photoUri)
            || !TryGetCurrentOwner(out var owner))
            return false;
        if (TryGetPhoto(photoUri, out _))
            return true;

        try
        {
            var encoded = await ReadPhotoAsync(photoUri, cancellationToken);
            if (encoded is null)
                return false;
            var options = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeByteArray(encoded, 0, encoded.Length, options)?.Dispose();
            if (options.OutWidth <= 0 || options.OutHeight <= 0)
                return false;
            var sample = 1;
            while (Math.Max(options.OutWidth, options.OutHeight) / sample > MaxBitmapEdge)
                sample *= 2;
            options.InJustDecodeBounds = false;
            options.InSampleSize = sample;
            var bitmap = BitmapFactory.DecodeByteArray(encoded, 0, encoded.Length, options);
            if (bitmap is null)
                return false;

            lock (_photosGate)
            {
                if (_disposed || !TryGetCurrentOwner(out var currentOwner)
                    || currentOwner != owner)
                {
                    bitmap.Dispose();
                    return false;
                }
                var key = (owner, photoUri);
                if (_photos.TryAdd(key, bitmap))
                {
                    _photoOrder.Enqueue(key);
                    if (_photoOrder.Count > 6)
                        _photos.Remove(_photoOrder.Dequeue());
                }
                else
                    bitmap.Dispose();
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Quiz photo is unavailable ({FailureType}).", ex.GetType().Name);
            return false;
        }
    }

    internal bool TryGetPhoto(string? photoUri, out Bitmap? bitmap)
    {
        bitmap = null;
        if (string.IsNullOrWhiteSpace(photoUri)
            || !TryGetCurrentOwner(out var owner))
            return false;
        lock (_photosGate)
            return _photos.TryGetValue((owner, photoUri), out bitmap)
                && !bitmap.IsRecycled;
    }

    internal void ClearForSignedOut()
    {
        lock (_photosGate)
        {
            _photos.Clear();
            _photoOrder.Clear();
        }
        _speech?.Stop();
    }

    private bool TryGetCurrentOwner(out string owner)
    {
        if (ownerScope.TryGetUserProfileId(out owner)
            && preferences.Get(UserProfileRepository.ActiveProfileIdKey,
                string.Empty) == owner)
            return true;
        owner = string.Empty;
        return false;
    }

    private async Task<bool> SelectVoiceAsync(
        string language, CancellationToken cancellationToken)
    {
        if (_disposed || AndroidQuizMediaPolicy.LanguageTag(language) is not { } tag)
            return false;
        try
        {
            if (_speech is null)
            {
                _initialization = new SpeechInitialization();
                _speech = new TextToSpeech(activity, _initialization);
                _speech.SetOnUtteranceProgressListener(_progress);
            }
            if (!await _initialization!.Ready.WaitAsync(
                    TimeSpan.FromSeconds(8), cancellationToken))
                return false;
            var locale = Locale.ForLanguageTag(tag);
            if ((int)_speech.IsLanguageAvailable(locale) < 0)
                return false;
            var localVoice = _speech.Voices?.FirstOrDefault(voice =>
                !voice.IsNetworkConnectionRequired
                && voice.Locale?.Language == locale.Language);
            if (localVoice is not null)
                return (int)_speech.SetVoice(localVoice) == 0;
            return (int)_speech.SetLanguage(locale) >= 0
                && (_speech.Voice?.IsNetworkConnectionRequired != true
                    || connectivity.IsInternetAvailable);
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Quiz speech is unavailable ({FailureType}).", ex.GetType().Name);
            return false;
        }
    }

    private async Task<byte[]?> ReadPhotoAsync(
        string source, CancellationToken cancellationToken)
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
            if (!TryGetCurrentOwner(out var owner))
                return null;
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
                        new Uri(uri, location).AbsoluteUri,
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _speech?.Stop();
        _speech?.Shutdown();
        _speech?.Dispose();
        _initialization?.Dispose();
        _progress.Dispose();
        _images.Dispose();
        lock (_photosGate)
        {
            _photos.Clear();
            _photoOrder.Clear();
        }
    }

    private sealed class SpeechInitialization : Java.Lang.Object, TextToSpeech.IOnInitListener
    {
        private readonly TaskCompletionSource<bool> _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> Ready => _ready.Task;
        public void OnInit(OperationResult status) => _ready.TrySetResult((int)status == 0);
    }

    private sealed class SpeechProgress : UtteranceProgressListener
    {
        private readonly object _gate = new();
        private string? _utteranceId;
        private TaskCompletionSource<bool>? _completed;

        public Task<bool> Begin(string id)
        {
            lock (_gate)
            {
                _utteranceId = id;
                _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return _completed.Task;
            }
        }

        public void End()
        {
            lock (_gate)
            {
                _completed?.TrySetResult(false);
                _completed = null;
                _utteranceId = null;
            }
        }

        public override void OnStart(string? utteranceId) =>
            Finish(utteranceId, true);
        public override void OnDone(string? utteranceId) => Finish(utteranceId, true);
        public override void OnError(string? utteranceId) => Finish(utteranceId, false);
        public override void OnError(string? utteranceId, TextToSpeechError errorCode) =>
            Finish(utteranceId, false);
        public override void OnStop(string? utteranceId, bool interrupted) =>
            Finish(utteranceId, false);

        private void Finish(string? id, bool success)
        {
            lock (_gate)
            {
                if (_utteranceId == id)
                    _completed?.TrySetResult(success);
            }
        }
    }
}
