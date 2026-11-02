using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Describe a Scene (source UI/Pages/Scene.razor, activities.md 7). A random
/// image from the owner's gallery, a one-line description graded by the
/// source GradeMyDescription prompt, a newest-first list of graded cards with
/// More/Less details, a new-scene button, and the gallery overlay (switch
/// image, add by URL or system photo picker, multi-select delete). Every
/// grade writes a SceneDescription UserActivity and scores the owner's whole
/// vocabulary through ExtractAndScoreVocabularyAsync (weight 1.5), as the
/// source. Unlike the source, every read and write is bound to the launch
/// owner, a failed grade is an error (the source recorded a 0% grade), and AI
/// and image downloads run outside the presenter gate so Back always stops
/// and saves the timer at once.
/// </summary>
public sealed class NativeScenePresenter(
    NativeActivityOwner owner,
    NativeActivityTimer timer,
    LearningResourceRepository resources,
    INativeSceneImageStore images,
    INativeSceneAi ai,
    IServiceScopeFactory scopes,
    NativePendingWrites pendingWrites,
    ILogger<NativeScenePresenter> logger) : IDisposable
{
    public const string ScoringActivity = "SceneDescription";
    public const float DifficultyWeight = 1.5f;
    /// <summary>Gallery thumbnails kept in memory; the current image is always kept.</summary>
    public const int ImageCacheLimit = 24;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NativeActivityExit _exit = new();
    private readonly NativeActivityLoad _imageLoad = new();
    private readonly NativeActivityLoad _galleryLoad = new();
    private readonly List<VocabularyWord> _vocabulary = [];
    private readonly List<NativeSceneResult> _results = [];
    private readonly Dictionary<int, byte[]?> _bytes = [];
    private readonly LinkedList<int> _bytesOrder = new();
    private readonly List<SceneImage> _gallery = [];
    private readonly HashSet<int> _selected = [];
    private NativeActivityLaunch? _launch;
    private NativeSceneState _state = new(NativePhase.Idle);
    private ImmutableArray<NativeMessage> _notices = [];
    private SceneImage? _current;
    private bool _currentFailed;
    private string _draft = string.Empty;
    private string _newUrl = string.Empty;
    private int _expanded = -1;
    private int _nextResult;
    private bool _grading;
    private bool _galleryOpen;
    private bool _galleryLoading;
    private bool _selecting;
    private bool _saving;
    private long _generation;
    private bool _exited;
    private bool _disposed;

    /// <summary>The Back/Exit fence (tests force continuation ordering through it).</summary>
    internal NativeActivityExit ExitFence => _exit;

    public NativeSceneState State =>
        _launch is not null && !owner.IsActive(_launch.UserId)
            && _state.Destination != NativeDestination.TodaysPlan
            ? new(NativePhase.Blocked, ErrorKey: NativeSceneText.Unavailable)
            : _state;

    public event Action<NativeSceneState>? StateChanged;

    public async Task<NativeSceneState> OpenAsync(
        NativeActivityLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_launch is not null)
                return State;
            _launch = launch;
            Publish(new(NativePhase.Loading));
            if (launch.Route != NativeActivityCatalog.Scene
                || !owner.IsActive(launch.UserId)
                || !await timer.StartAsync(launch, cancellationToken))
                return Refuse();
            var userId = launch.UserId;
            try
            {
                // Source loads the whole vocabulary for scoring; native loads only the owner's.
                _vocabulary.AddRange(await resources.GetAllVocabularyWordsWithResourcesAsync(userId));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Describe a Scene could not load vocabulary; grades will not score words.");
            }
            if (!Active())
                return Refuse();
            if (!await PickRandomAsync(userId, cancellationToken))
                return Refuse();
        }
        finally
        {
            _gate.Release();
        }
        return await LoadCurrentImageAsync();
    }

    /// <summary>Typing updates the draft without publishing, so a re-render never interrupts IME composition.</summary>
    public void SetDraft(string? value)
    {
        _draft = value ?? string.Empty;
        _state = _state with { Draft = _draft };
    }

    /// <summary>Gallery URL field; like the draft it does not publish.</summary>
    public void SetNewImageUrl(string? value)
    {
        _newUrl = value ?? string.Empty;
        if (_state.Gallery is { } gallery)
            _state = _state with { Gallery = gallery with { NewImageUrl = _newUrl } };
    }

    /// <summary>Source LoadNewScene (refresh button): another random image; the graded list stays.</summary>
    public async Task<NativeSceneState> NewSceneAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (!_state.CanChangeScene)
                return State;
            _notices = [];
            if (!await PickRandomAsync(_launch!.UserId, cancellationToken))
                return Refuse();
        }
        finally
        {
            _gate.Release();
        }
        return await LoadCurrentImageAsync();
    }

    /// <summary>
    /// Source SubmitDescription. The input clears at once (as the source); the
    /// AI call runs outside the gate, and an exit or owner change while it runs
    /// discards the grade without writing anything.
    /// </summary>
    public async Task<NativeSceneState> GradeAsync(CancellationToken cancellationToken = default)
    {
        string input;
        string userId;
        string sceneDescription;
        long generation;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_state.Phase != NativePhase.Ready || _current is null || _grading
                || string.IsNullOrWhiteSpace(_draft))
                return State;
            input = _draft.Trim();
            _draft = string.Empty;
            _grading = true;
            _notices = [];
            userId = _launch!.UserId;
            sceneDescription = _current.Description ?? string.Empty;
            generation = _generation;
            PublishPage();
        }
        finally
        {
            _gate.Release();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        GradeResponse? grade = null;
        Exception? failure = null;
        try
        {
            var (target, native) = await LanguagesAsync(userId);
            grade = await ai.GradeAsync(userId, input, sceneDescription, target, native, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        var proceed = await _exit.EnterAsync(_gate);
        try
        {
            _grading = false;
            if (!proceed || Exited)
                return State;
            if (!Active())
                return Refuse();
            if (generation != _generation || linked.IsCancellationRequested)
                return PublishPage();
            if (failure is not null || grade is null)
            {
                if (failure is not null)
                    logger.LogError(failure, "Describe a Scene could not grade a description.");
                else
                    logger.LogWarning("Describe a Scene grading returned no verdict.");
                _notices = [new(NativeMessageKind.Error, NativeSceneText.GradeFailed)];
                return PublishPage();
            }
            return await ApplyGradeAsync(userId, input, grade);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Source ToggleExpand: one card's details open at a time.</summary>
    public async Task<NativeSceneState> ToggleDetailsAsync(int index,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            _expanded = _expanded == index ? -1 : index;
            // A message stays until the learner's next action (Captain, 2026-11-02).
            _notices = [];
            return PublishPage();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeSceneState> DismissNoticesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            _notices = [];
            return PublishPage();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Source OpenGallery: list every image, then load thumbnails in the background.</summary>
    public async Task<NativeSceneState> OpenGalleryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_state.Phase is not (NativePhase.Ready or NativePhase.Empty))
                return State;
            _galleryOpen = true;
            _galleryLoading = true;
            _selecting = false;
            _selected.Clear();
            _notices = [];
            PublishPage();
            if (!await ReloadGalleryAsync(cancellationToken))
                return Refuse();
            _galleryLoading = false;
            PublishPage();
        }
        finally
        {
            _gate.Release();
        }
        return await LoadThumbnailsAsync();
    }

    /// <summary>Source CloseGallery: also leaves select mode and clears the URL field.</summary>
    public async Task<NativeSceneState> CloseGalleryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            CloseGallery();
            _notices = [];
            return PublishPage();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NativeSceneState> ToggleSelectModeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (!_galleryOpen)
                return State;
            _selecting = !_selecting;
            if (!_selecting)
                _selected.Clear();
            _notices = [];
            return PublishPage();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Source OnGalleryItemClick: in select mode toggles the check; otherwise
    /// makes the image current, clears the graded list and closes the gallery.
    /// While a grade is in flight the scene does not change.
    /// </summary>
    public async Task<NativeSceneState> TapGalleryItemAsync(int id,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (!_galleryOpen || _gallery.FirstOrDefault(image => image.Id == id) is not { } image)
                return State;
            if (_selecting)
            {
                if (!_selected.Add(id))
                    _selected.Remove(id);
                _notices = [];
                return PublishPage();
            }
            if (_grading)
                return State;
            SetCurrent(image);
            _results.Clear();
            _expanded = -1;
            CloseGallery();
            _notices = [];
            PublishPage();
        }
        finally
        {
            _gate.Release();
        }
        return await LoadCurrentImageAsync();
    }

    /// <summary>Source AddImage: save the pasted URL, refresh the gallery.</summary>
    public async Task<NativeSceneState> AddImageUrlAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (!_galleryOpen || _saving || string.IsNullOrWhiteSpace(_newUrl))
                return State;
            var url = _newUrl.Trim();
            _newUrl = string.Empty;
            if (!NativeSceneImageStore.IsSupportedRemoteUrl(url))
            {
                _notices = [new(NativeMessageKind.Error, NativeSceneText.InvalidUrl)];
                return PublishPage();
            }
            return await SaveImageAsync(token => images.AddUrlAsync(_launch!.UserId, url, token),
                null, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A photo chosen in the system picker (Captain-approved add path).</summary>
    public async Task<NativeSceneState> AddPhotoAsync(NativeScenePickedPhoto photo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (!_galleryOpen || _saving)
                return State;
            if (photo.Data.Length > NativeSceneImageStore.MaxImageBytes)
            {
                _notices = [new(NativeMessageKind.Error, NativeSceneText.PhotoTooLarge)];
                return PublishPage();
            }
            return await SaveImageAsync(token => images.AddPhotoAsync(_launch!.UserId, photo, token),
                photo.Data, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The picker reported a failure (the bridge's "loadFailed" or "tooLarge").</summary>
    public async Task<NativeSceneState> PhotoPickFailedAsync(string? code,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            _notices = [new(NativeMessageKind.Error, code == "tooLarge"
                ? NativeSceneText.PhotoTooLarge : NativeSceneText.PhotoFailed)];
            return PublishPage();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Source DeleteSelectedImages: delete the checked images, leave select
    /// mode; deleting the current image empties the scene.
    /// </summary>
    public async Task<NativeSceneState> DeleteSelectedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Exited)
                return State;
            if (!Active())
                return Refuse();
            if (!_galleryOpen || !_selecting || _selected.Count == 0 || _saving)
                return State;
            var ids = _selected.ToList();
            var userId = _launch!.UserId;
            try
            {
                var deleted = await images.DeleteAsync(userId, ids, cancellationToken);
                if (!Active())
                    return Refuse();
                if (!await ReloadGalleryAsync(cancellationToken))
                    return Refuse();
                _selected.Clear();
                _selecting = false;
                foreach (var id in ids)
                    Forget(id);
                if (_current is not null && ids.Contains(_current.Id))
                {
                    _imageLoad.Cancel();
                    _current = null;
                    _currentFailed = false;
                }
                _notices = [new(NativeMessageKind.Success, NativeSceneText.Deleted,
                    deleted.ToString(CultureInfo.InvariantCulture))];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Describe a Scene could not delete gallery images.");
                _notices = [new(NativeMessageKind.Error, NativeSceneText.DeleteFailed)];
            }
            return PublishPage();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Back / Go Back: stop and save the timer, then return to the Dashboard.</summary>
    public async Task<NativeSceneState> ExitAsync(CancellationToken cancellationToken = default)
    {
        // Cancel an in-flight grade and image downloads before waiting for the gate.
        var exited = false;
        _exit.Begin();
        try
        {
            _lifetime.Cancel();
            _imageLoad.Cancel();
            _galleryLoad.Cancel();
            await _exit.AfterCancelAsync();
            await _gate.WaitAsync(cancellationToken);
            try
            {
                _generation++;
                _exited = exited = true;
                string? errorKey = null;
                try
                {
                    if (await timer.StopAsync(cancellationToken) == NativeActivityStop.Failed)
                        errorKey = NativeText.SaveFailed;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Describe a Scene could not save its activity minutes.");
                    errorKey = NativeText.SaveFailed;
                }
                return Publish(new(NativePhase.Ready, NativeDestination.TodaysPlan,
                    ErrorKey: errorKey,
                    HasPendingSync: _launch is not null && pendingWrites.IsPending(_launch.UserId)));
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _exit.End(exited);
        }
    }

    private async Task<NativeSceneState> ApplyGradeAsync(string userId, string input, GradeResponse grade)
    {
        _results.Insert(0, new NativeSceneResult(
            _nextResult++, input, grade.Accuracy, grade.Fluency,
            grade.AccuracyExplanation, grade.FluencyExplanation,
            grade.GrammarNotes?.RecommendedTranslation ?? grade.RecommendedTranslation,
            grade.GrammarNotes?.Explanation));

        var saveFailed = false;
        using (var scope = scopes.CreateScope())
        {
            try
            {
                var activities = scope.ServiceProvider.GetRequiredService<UserActivityRepository>();
                var saved = await activities.SaveAsync(NativeUserActivity.Create(
                    userId, Activity.SceneDescription, input, grade.Accuracy, grade.Fluency));
                if (saved < 0)
                {
                    logger.LogError("Describe a Scene could not save its UserActivity row.");
                    saveFailed = true;
                }
                else
                    pendingWrites.MarkPending(userId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Describe a Scene could not save its UserActivity row.");
                saveFailed = true;
            }

            if (_vocabulary.Count > 0 && owner.IsActive(userId))
            {
                try
                {
                    var progress = scope.ServiceProvider.GetRequiredService<IVocabularyProgressService>();
                    var results = await progress.ExtractAndScoreVocabularyAsync(
                        grade.VocabularyAnalysis, [.. _vocabulary], userId,
                        ScoringActivity, DifficultyWeight);
                    if (results.Count > 0)
                        pendingWrites.MarkPending(userId);
                    scope.ServiceProvider.GetService<ProgressCacheService>()?.InvalidateVocabSummary();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Describe a Scene could not score vocabulary for a grade.");
                    saveFailed = true;
                }
            }
        }

        if (!owner.IsActive(userId))
            return Refuse();
        var percent = ((int)grade.Accuracy).ToString(CultureInfo.InvariantCulture);
        var label = grade.Accuracy >= 80 ? NativeSceneText.GradeExcellent
            : grade.Accuracy >= 50 ? NativeSceneText.GradeGood
            : NativeSceneText.GradeKeepPracticing;
        _notices = saveFailed
            ? [new(NativeMessageKind.Success, label, percent),
                new(NativeMessageKind.Error, NativeText.SaveFailed)]
            : [new(NativeMessageKind.Success, label, percent)];
        return PublishPage();
    }

    /// <summary>Picks a random image under the gate. False when the owner changed.</summary>
    private async Task<bool> PickRandomAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            var scene = await images.GetRandomAsync(userId, cancellationToken);
            if (!Active())
                return false;
            if (scene is null)
                _notices = [new(NativeMessageKind.Warning, NativeSceneText.NoImagesFound)];
            else
                SetCurrent(scene);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Describe a Scene could not load a scene.");
            _notices = [new(NativeMessageKind.Error, NativeSceneText.LoadFailed)];
        }
        PublishPage();
        return true;
    }

    private void SetCurrent(SceneImage image)
    {
        _generation++;
        _current = image;
        _currentFailed = false;
    }

    /// <summary>Downloads (or reads) the current image outside the gate; applies only while still current.</summary>
    private async Task<NativeSceneState> LoadCurrentImageAsync()
    {
        SceneImage image;
        string userId;
        NativeActivityLoadTicket ticket;
        // Opening releases the gate before this; a Back in between settles first.
        var entered = await _exit.EnterAsync(_gate);
        try
        {
            if (!entered || Exited || !Active() || _current is null)
                return State;
            if (_bytes.TryGetValue(_current.Id, out var cached) && cached is not null)
                return PublishPage();
            image = _current;
            userId = _launch!.UserId;
            ticket = _imageLoad.Begin(_lifetime.Token);
        }
        finally
        {
            _gate.Release();
        }

        byte[]? bytes = null;
        try
        {
            bytes = await images.LoadBytesAsync(userId, image, ticket.Token);
        }
        catch (OperationCanceledException) when (ticket.Token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Describe a Scene could not load the scene image.");
        }

        var proceed = await _exit.EnterAsync(_gate);
        try
        {
            if (!proceed || !_imageLoad.IsCurrent(ticket) || Exited)
                return State;
            if (!Active())
                return Refuse();
            if (_current?.Id != image.Id)
                return State;
            Remember(image.Id, bytes);
            _currentFailed = bytes is null;
            return PublishPage();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Loads missing gallery thumbnails one at a time, publishing as each arrives.</summary>
    private async Task<NativeSceneState> LoadThumbnailsAsync()
    {
        NativeActivityLoadTicket ticket;
        List<SceneImage> pending;
        string userId;
        var entered = await _exit.EnterAsync(_gate);
        try
        {
            if (!entered || Exited || !Active() || !_galleryOpen)

                return State;
            pending = _gallery.Where(image => !_bytes.ContainsKey(image.Id)).ToList();
            if (pending.Count == 0)
                return State;
            userId = _launch!.UserId;
            ticket = _galleryLoad.Begin(_lifetime.Token);
        }
        finally
        {
            _gate.Release();
        }

        foreach (var image in pending)
        {
            byte[]? bytes = null;
            try
            {
                bytes = await images.LoadBytesAsync(userId, image, ticket.Token);
            }
            catch (OperationCanceledException) when (ticket.Token.IsCancellationRequested)
            {
                return await _exit.StateAsync(_gate, () => State);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Describe a Scene could not load a gallery image.");
            }
            var proceed = await _exit.EnterAsync(_gate);
            try
            {
                if (!proceed || !_galleryLoad.IsCurrent(ticket) || Exited || !_galleryOpen)
                    return State;
                if (!Active())
                    return Refuse();
                Remember(image.Id, bytes);
                PublishPage();
            }
            finally
            {
                _gate.Release();
            }
        }
        return State;
    }

    private async Task<NativeSceneState> SaveImageAsync(
        Func<CancellationToken, Task<SceneImage?>> save, byte[]? knownBytes,
        CancellationToken cancellationToken)
    {
        _saving = true;
        PublishPage();
        try
        {
            var saved = await save(cancellationToken);
            if (!Active())
                return Refuse();
            if (saved is null)
                _notices = [new(NativeMessageKind.Error, NativeSceneText.ImageSaveFailed)];
            else
            {
                if (knownBytes is not null)
                    Remember(saved.Id, knownBytes);
                if (!await ReloadGalleryAsync(cancellationToken))
                    return Refuse();
                _notices = [new(NativeMessageKind.Success, NativeSceneText.ImageAdded)];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Describe a Scene could not save a gallery image.");
            _notices = [new(NativeMessageKind.Error, NativeSceneText.ImageSaveFailed)];
        }
        finally
        {
            _saving = false;
        }
        var state = PublishPage();
        // New URL images need their thumbnail; it loads in the background.
        if (_gallery.Any(image => !_bytes.ContainsKey(image.Id)))
            _ = ContinueThumbnailsAsync();
        return state;
    }

    private async Task ContinueThumbnailsAsync()
    {
        try
        {
            await Task.Yield();
            await LoadThumbnailsAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Describe a Scene thumbnail loading stopped.");
        }
    }

    /// <summary>Source re-lists the gallery after every change. False when the owner changed.</summary>
    private async Task<bool> ReloadGalleryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var listed = await images.ListAsync(_launch!.UserId, cancellationToken);
            if (!Active())
                return false;
            _gallery.Clear();
            _gallery.AddRange(listed);
            _selected.IntersectWith(_gallery.Select(image => image.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Describe a Scene could not load the gallery.");
            _notices = [new(NativeMessageKind.Error, NativeSceneText.GalleryLoadFailed)];
        }
        return true;
    }

    private void CloseGallery()
    {
        _galleryLoad.Cancel();
        _galleryOpen = false;
        _galleryLoading = false;
        _selecting = false;
        _selected.Clear();
        _newUrl = string.Empty;
    }

    private void Remember(int id, byte[]? bytes)
    {
        if (_bytes.ContainsKey(id))
            _bytesOrder.Remove(id);
        _bytes[id] = bytes;
        _bytesOrder.AddLast(id);
        // Evict the oldest entries, never the current image.
        var node = _bytesOrder.First;
        while (_bytes.Count > ImageCacheLimit && node is not null)
        {
            var next = node.Next;
            if (node.Value != _current?.Id && node.Value != id)
            {
                _bytes.Remove(node.Value);
                _bytesOrder.Remove(node);
            }
            node = next;
        }
    }

    private void Forget(int id)
    {
        if (_bytes.Remove(id))
            _bytesOrder.Remove(id);
    }

    private async Task<(string Target, string Native)> LanguagesAsync(string userId)
    {
        using var scope = scopes.CreateScope();
        var profile = await scope.ServiceProvider.GetRequiredService<UserProfileRepository>()
            .GetByIdAsync(userId);
        return (string.IsNullOrWhiteSpace(profile?.TargetLanguage) ? "Korean" : profile.TargetLanguage,
            string.IsNullOrWhiteSpace(profile?.NativeLanguage) ? "English" : profile.NativeLanguage);
    }

    private bool Exited => _exited || _exit.HasBegun || _state.Destination != NativeDestination.Activity;

    private bool Active() => _launch is not null && owner.IsActive(_launch.UserId);

    private NativeSceneState Snapshot()
    {
        NativeSceneImage? image = null;
        if (_current is { } current)
        {
            var bytes = _bytes.GetValueOrDefault(current.Id);
            image = new(current.Id,
                bytes is not null ? NativeSceneImageLoad.Loaded
                : _currentFailed ? NativeSceneImageLoad.Failed : NativeSceneImageLoad.Loading,
                bytes);
        }
        NativeSceneGallery? gallery = null;
        if (_galleryOpen)
            gallery = new(_galleryLoading,
                [.. _gallery.Select(item =>
                {
                    var known = _bytes.TryGetValue(item.Id, out var bytes);
                    return new NativeSceneGalleryItem(item.Id,
                        bytes is not null ? NativeSceneImageLoad.Loaded
                        : known ? NativeSceneImageLoad.Failed : NativeSceneImageLoad.Loading,
                        bytes, item.Id == _current?.Id, _selected.Contains(item.Id));
                })],
                _selecting, _selected.Count, _newUrl, _saving);
        return new(
            _current is null ? NativePhase.Empty : NativePhase.Ready,
            Image: image,
            Results: [.. _results],
            ExpandedIndex: _expanded,
            Draft: _draft,
            IsGrading: _grading,
            Gallery: gallery,
            Notices: _notices,
            HasPendingSync: _launch is not null && pendingWrites.IsPending(_launch.UserId));
    }

    private NativeSceneState PublishPage() =>
        Exited ? State : Publish(Snapshot());

    private NativeSceneState Refuse()
    {
        _generation++;
        _imageLoad.Cancel();
        _galleryLoad.Cancel();
        if (_exited)
            return State;
        return Publish(new(NativePhase.Blocked, ErrorKey: NativeSceneText.Unavailable));
    }

    private NativeSceneState Publish(NativeSceneState state)
    {
        _state = state;
        StateChanged?.Invoke(State);
        return State;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _exit.Complete();
        _lifetime.Cancel();
        _imageLoad.Dispose();

        _galleryLoad.Dispose();
        timer.Dispose();
    }
}
