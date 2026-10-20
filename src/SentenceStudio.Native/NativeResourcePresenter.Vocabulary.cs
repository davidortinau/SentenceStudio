using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

public sealed partial class NativeResourcePresenter
{
    public const int MaxVocabularyFileBytes = 1_048_576;
    private long _vocabularySearchGeneration;
    private int _vocabularyMutation;

    private string? QuickAddLanguage =>
        _detail.Draft is { LanguageDirty: true } draft
            ? draft.Language : _detail.Resource?.Language;

    private bool HasUnsavedQuickAddLanguage =>
        _detail is { Resource: { } resource, Draft: { LanguageDirty: true } draft }
        && !string.Equals(draft.Language, resource.Language, StringComparison.Ordinal);

    public bool CanQuickAdd =>
        _detail.Phase is (NativeResourceDetailPhase.Ready
            or NativeResourceDetailPhase.SaveError)
        && _detail.Resource is { IsSmartResource: false }
        && QuickAddLanguage is { } language
        && !string.IsNullOrWhiteSpace(language)
        && !language.Equals("Other", StringComparison.OrdinalIgnoreCase);

    public async Task<NativeResourceVocabularySearchState> SearchVocabularyAsync(
        string? query, CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _vocabularySearchGeneration);
        if (!TryEditableResource(out var owner, out var resource, requireLanguage: true))
        {
            var blocked = new NativeResourceVocabularySearchState(
                NativeResourceVocabularySearchPhase.Error,
                ErrorKey: _detail.Phase == NativeResourceDetailPhase.Blocked
                    ? "ResourceEdit_NotFound"
                    : _detail.Resource?.IsSmartResource == true
                        ? "ResourceEdit_AutoUpdated"
                        : "ResourceEdit_SetLanguageFirst");
            _detail = _detail with { VocabularySearch = blocked };
            return blocked;
        }
        if (string.IsNullOrWhiteSpace(query))
        {
            var empty = new NativeResourceVocabularySearchState(
                NativeResourceVocabularySearchPhase.Idle);
            _detail = _detail with { VocabularySearch = empty };
            return empty;
        }
        var ownerStamp = userScope.CaptureStamp();
        var language = QuickAddLanguage!;
        var search = new NativeResourceVocabularySearchState(
            NativeResourceVocabularySearchPhase.Searching, Query: query);
        _detail = _detail with { VocabularySearch = search };
        try
        {
            await Task.Delay(250, cancellationToken);
            var results = await store.SearchVocabularyAsync(
                owner, resource.Id, language, query.Trim(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _vocabularySearchGeneration)
                || !IsCurrentOwnerStamp(owner, ownerStamp)
                || _detail.Resource?.Id != resource.Id
                || QuickAddLanguage != language)
                return _detail.VocabularySearch ?? search;
            var staged = _detail.Draft?.StagedVocabulary
                ?? ImmutableArray<NativeResourceVocabularyRow>.Empty;
            if (!staged.IsDefaultOrEmpty)
                results = results.Where(word => !staged.Any(candidate =>
                    candidate.Id == word.Id || string.Equals(candidate.TargetTerm,
                        word.TargetTerm, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
            var ready = new NativeResourceVocabularySearchState(
                NativeResourceVocabularySearchPhase.Ready, results.ToImmutableArray(), query);
            _detail = _detail with { VocabularySearch = ready };
            return ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (generation == Volatile.Read(ref _vocabularySearchGeneration)
                && _detail.Resource?.Id == resource.Id
                && QuickAddLanguage == language)
                _detail = _detail with {
                    VocabularySearch = new(NativeResourceVocabularySearchPhase.Idle)
                };
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Native resource vocabulary search failed.");
            var error = new NativeResourceVocabularySearchState(
                NativeResourceVocabularySearchPhase.Error, Query: query,
                ErrorKey: "FailedToLoadVocabularyData");
            if (generation == Volatile.Read(ref _vocabularySearchGeneration)
                && IsCurrentOwnerStamp(owner, ownerStamp)
                && _detail.Resource?.Id == resource.Id
                && QuickAddLanguage == language)
                _detail = _detail with { VocabularySearch = error };
            return error;
        }
    }

    public Task<NativeResourceMutationResult> AttachExistingVocabularyAsync(
        string wordId, CancellationToken cancellationToken = default)
    {
        if (_detail.VocabularySearch is not {
                Phase: NativeResourceVocabularySearchPhase.Ready } search
            || search.Results.IsDefaultOrEmpty
            || !search.Results.Any(word => word.Id == wordId))
            return Task.FromResult(NativeResourceMutationResult.Invalid);
        if (HasUnsavedQuickAddLanguage)
            return Task.FromResult(StageQuickAdd(
                search.Results.First(word => word.Id == wordId)));
        return MutateVocabularyAsync((owner, resource, ct) =>
            store.AttachVocabularyAsync(owner, resource.Id, wordId,
                resource.Language!, ct), cancellationToken);
    }

    public Task<NativeResourceMutationResult> CreateNewVocabularyAsync(
        string targetTerm, string nativeTerm,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetTerm))
            return Task.FromResult(NativeResourceMutationResult.Invalid);
        var row = new NativeResourceVocabularyRow(Guid.NewGuid().ToString(),
            targetTerm.Trim(), nativeTerm?.Trim() ?? string.Empty,
            VocabularyClassificationBackfillService.ClassifyHeuristic(targetTerm, null),
            Language: QuickAddLanguage);
        if (HasUnsavedQuickAddLanguage)
            return Task.FromResult(StageQuickAdd(row));
        return MutateVocabularyAsync((owner, resource, ct) =>
            store.CreateVocabularyAsync(owner, resource.Id, row,
                resource.Language!, ct), cancellationToken);
    }

    private NativeResourceMutationResult StageQuickAdd(NativeResourceVocabularyRow word)
    {
        if (!TryCurrentOwner(out var owner)
            || !IsCurrentOwnerStamp(owner, userScope.CaptureStamp()))
        {
            Block();
            return NativeResourceMutationResult.NotFoundOrForbidden;
        }
        if (_detail is not { Phase: NativeResourceDetailPhase.Ready,
                Resource: { IsSmartResource: false } resource,
                Draft: { } draft }
                || !HasUnsavedQuickAddLanguage || _detail.Reloading
                || Volatile.Read(ref _saving) != 0
                || Volatile.Read(ref _vocabularyMutation) != 0
                || word.Language != draft.Language)
                return NativeResourceMutationResult.Invalid;
        var staged = draft.StagedVocabulary.IsDefault
                ? ImmutableArray<NativeResourceVocabularyRow>.Empty
                : draft.StagedVocabulary;
        if (staged.Any(candidate => candidate.Id == word.Id
                    || string.Equals(candidate.TargetTerm.Trim(),
                        word.TargetTerm.Trim(), StringComparison.OrdinalIgnoreCase))
                || !resource.Vocabulary.IsDefaultOrEmpty
                    && resource.Vocabulary.Any(candidate =>
                        string.Equals(candidate.TargetTerm.Trim(),
                            word.TargetTerm.Trim(), StringComparison.OrdinalIgnoreCase)))
                return NativeResourceMutationResult.AlreadyPresent;
        _detail = _detail with {
                Draft = draft with { StagedVocabulary = staged.Add(word) },
                VocabularySearch = new(NativeResourceVocabularySearchPhase.Idle),
                ErrorKey = null
        };
        Interlocked.Increment(ref _vocabularySearchGeneration);
        return NativeResourceMutationResult.Success;
    }

    public Task<NativeResourceMutationResult> RemoveVocabularyAsync(
        string wordId, CancellationToken cancellationToken = default)
    {
        if (_detail.Resource is not { } resource
            || resource.Vocabulary.IsDefaultOrEmpty
            || !resource.Vocabulary.Any(word => word.Id == wordId))
            return Task.FromResult(NativeResourceMutationResult.NotFoundOrForbidden);
        return MutateVocabularyAsync((owner, current, ct) =>
            store.RemoveVocabularyAsync(owner, current.Id, wordId, ct),
            cancellationToken, requireLanguage: false);
    }

    public async Task<NativeResourceImportResult> ImportVocabularyFromTextAsync(
        string? text, string delimiter = "comma",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new(0, 0, NativeResourceMutationResult.Invalid,
                "NativeResources_SaveInvalid");
        var words = VocabularyWord.ParseVocabularyWords(text, delimiter)
            .Select(word => new NativeResourceVocabularyRow(
                word.Id, word.TargetLanguageTerm?.Trim() ?? string.Empty,
                word.NativeLanguageTerm?.Trim() ?? string.Empty,
                VocabularyClassificationBackfillService.ClassifyHeuristic(
                    word.TargetLanguageTerm ?? string.Empty, word.Tags),
                word.Tags))
            .ToArray();
        if (words.Length == 0)
            return new(0, 0, NativeResourceMutationResult.Invalid,
                "NativeResources_SaveInvalid");
        NativeResourceImportResult? import = null;
        var result = await MutateVocabularyAsync(async (owner, resource, ct) =>
        {
            import = await store.ImportVocabularyAsync(
                owner, resource.Id, resource.Language, words, ct);
            return import.Result;
        }, cancellationToken, requireLanguage: false,
            wroteRows: () => import?.Added > 0);
        var outcome = import is { } completed && result == NativeResourceMutationResult.Success
            ? completed
            : new NativeResourceImportResult(0, 0, result, _detail.ErrorKey);
        if (result == NativeResourceMutationResult.Success)
            _detail = _detail with { LastImport = outcome };
        return outcome;
    }

    public async Task<NativeResourceImportResult> ImportVocabularyFromFileAsync(
        Stream content, long length, string fileName, string delimiter = "comma",
        CancellationToken cancellationToken = default)
    {
        var draft = _detail.Draft;
        var id = _detail.Resource?.Id;
        var (text, errorKey) = await ReadVocabularyFileAsync(
            content, length, fileName, cancellationToken);
        if (errorKey is not null)
            return new(0, 0, NativeResourceMutationResult.Invalid, errorKey);
        if (!ReferenceEquals(draft, _detail.Draft) || _detail.Resource?.Id != id)
            return new(0, 0, NativeResourceMutationResult.NotFoundOrForbidden);
        return await ImportVocabularyFromTextAsync(text, delimiter, cancellationToken);
    }

    private async Task<NativeResourceMutationResult> MutateVocabularyAsync(
        Func<string, NativeResourceDetail, CancellationToken,
            Task<NativeResourceMutationResult>> action,
        CancellationToken cancellationToken, bool requireLanguage = true,
        Func<bool>? wroteRows = null)
    {
        if (!TryEditableResource(out var owner, out var resource, requireLanguage))
            return _detail.Phase == NativeResourceDetailPhase.Blocked
                ? NativeResourceMutationResult.NotFoundOrForbidden
                : _detail.Resource?.IsSmartResource == true
                    ? NativeResourceMutationResult.SmartReadOnly
                    : NativeResourceMutationResult.Invalid;
        if (Interlocked.CompareExchange(ref _vocabularyMutation, 1, 0) != 0)
            return NativeResourceMutationResult.Invalid;
        var stamp = userScope.CaptureStamp();
        var draft = _detail.Draft;
        _detail = _detail with { VocabularyBusy = true, ErrorKey = null };
        try
        {
            var result = await action(owner, resource, cancellationToken);
            if (result != NativeResourceMutationResult.Success)
            {
                if (IsCurrentOwnerStamp(owner, stamp)
                    && _detail.Resource?.Id == resource.Id)
                    _detail = _detail with { ErrorKey = MutationErrorKey(result) };
                return result;
            }

            var changed = wroteRows?.Invoke() ?? true;
            if (changed)
                pendingWrites.MarkPending(owner);
            if (!IsCurrentOwnerStamp(owner, stamp))
            {
                Block();
                return NativeResourceMutationResult.NotFoundOrForbidden;
            }
            if (changed)
            {
                _syncPhase = NativeResourceSyncPhase.Pending;
                _syncErrorKey = null;
            }
            var reloaded = await store.GetAsync(owner, resource.Id, cancellationToken);
            var rows = await store.ListAsync(owner, cancellationToken);
            if (!IsCurrentOwnerStamp(owner, stamp)
                || _detail.Resource?.Id != resource.Id)
                return NativeResourceMutationResult.NotFoundOrForbidden;
            if (reloaded is null)
                throw new InvalidOperationException("Vocabulary mutation saved but resource reload failed.");

            _all = rows;
            ReconcileFilters();
            ProjectList();
            var concurrentChange = draft is not null
                && !Equals(resource.OriginalContent, reloaded.OriginalContent);
            _detail = new(concurrentChange ? NativeResourceDetailPhase.Conflict
                    : NativeResourceDetailPhase.Ready,
                reloaded, draft, concurrentChange ? "NativeResources_Conflict" : null,
                Sync: _syncPhase, SyncErrorKey: _syncErrorKey);
            Interlocked.Increment(ref _vocabularySearchGeneration);
            return NativeResourceMutationResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsCurrentOwnerStamp(owner, stamp))
                _detail = _detail with { ErrorKey = "NativeResources_ReloadError" };
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Native resource vocabulary mutation failed.");
            if (IsCurrentOwnerStamp(owner, stamp)
                && _detail.Resource?.Id == resource.Id)
                _detail = _detail with { ErrorKey = "NativeResources_SaveError" };
            return NativeResourceMutationResult.Failed;
        }
        finally
        {
            if (IsCurrentOwnerStamp(owner, stamp)
                && _detail.Resource?.Id == resource.Id)
                _detail = _detail with { VocabularyBusy = false };
            Volatile.Write(ref _vocabularyMutation, 0);
        }
    }

    private bool TryEditableResource(
        out string owner, out NativeResourceDetail resource, bool requireLanguage)
    {
        resource = null!;
        if (!TryCurrentOwner(out owner)
            || !IsCurrentOwnerStamp(owner, userScope.CaptureStamp()))
        {
            Block();
            return false;
        }
        if (_detail.Resource is not { IsSmartResource: false } current
            || _detail.Phase is not (NativeResourceDetailPhase.Ready
                or NativeResourceDetailPhase.SaveError)
            || _detail.Reloading || Volatile.Read(ref _saving) != 0)
            return false;
        if (requireLanguage && (!CanQuickAdd
                || string.IsNullOrWhiteSpace(current.Language)))
            return false;
        resource = current;
        return true;
    }

    private static string MutationErrorKey(NativeResourceMutationResult result) => result switch
    {
        NativeResourceMutationResult.NotFoundOrForbidden => "ResourceEdit_NotFound",
        NativeResourceMutationResult.SmartReadOnly => "ResourceEdit_AutoUpdated",
        NativeResourceMutationResult.AlreadyPresent => "ResourceAdd_NoDuplicates",
        NativeResourceMutationResult.Invalid => "NativeResources_SaveInvalid",
        _ => "NativeResources_SaveError"
    };

    public NativeResourceImportResult StageVocabularyFromText(string? text, string delimiter = "comma")
    {
        if (!TryCurrentOwner(out var owner) || _owner != owner)
        {
            Block();
            return new(0, 0, NativeResourceMutationResult.NotFoundOrForbidden);
        }
        if (_detail is not { Resource: null, Draft: { } draft }
            || _detail.Phase is not (NativeResourceDetailPhase.Creating
                or NativeResourceDetailPhase.SaveError)
            || Volatile.Read(ref _saving) != 0)
            return new(0, 0, NativeResourceMutationResult.Invalid);
        if (string.IsNullOrWhiteSpace(text))
            return new(0, 0, NativeResourceMutationResult.Invalid,
                "ResourceAdd_NoVocabToImport");

        var stamp = userScope.CaptureStamp();
        var existing = draft.StagedVocabulary.IsDefault
            ? ImmutableArray<NativeResourceVocabularyRow>.Empty
            : draft.StagedVocabulary;
        var rows = existing.ToBuilder();
        var added = 0;
        var skipped = 0;
        foreach (var word in VocabularyWord.ParseVocabularyWords(text, delimiter))
        {
            var target = word.TargetLanguageTerm?.Trim();
            var native = word.NativeLanguageTerm?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(target)
                || rows.Any(row => string.Equals(row.TargetTerm.Trim(), target,
                    StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }

            rows.Add(new NativeResourceVocabularyRow(word.Id, target, native,
                VocabularyClassificationBackfillService.ClassifyHeuristic(target, word.Tags),
                word.Tags, draft.Language));
            added++;
        }

        if (!IsCurrentOwnerStamp(owner, stamp)
            || !ReferenceEquals(_detail.Draft, draft))
            return new(0, skipped + added, NativeResourceMutationResult.NotFoundOrForbidden);
        var result = new NativeResourceImportResult(added, skipped,
            NativeResourceMutationResult.Success);
        _detail = _detail with {
            Draft = draft with { StagedVocabulary = rows.ToImmutable() },
            LastImport = result,
            ErrorKey = null
        };
        return result;
    }

    public async Task<NativeResourceImportResult> StageVocabularyFromFileAsync(
        Stream content, long length, string fileName, string delimiter = "comma",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var draft = _detail.Draft;
        if (draft is null || _detail.Resource is not null
            || !TryCurrentOwner(out var owner) || _owner != owner)
            return new(0, 0, NativeResourceMutationResult.NotFoundOrForbidden);
        var stamp = userScope.CaptureStamp();
        var (text, errorKey) = await ReadVocabularyFileAsync(
            content, length, fileName, cancellationToken);
        if (errorKey is not null)
            return new(0, 0, NativeResourceMutationResult.Invalid, errorKey);
        if (!IsCurrentOwnerStamp(owner, stamp) || !ReferenceEquals(draft, _detail.Draft))
            return new(0, 0, NativeResourceMutationResult.NotFoundOrForbidden);
        return StageVocabularyFromText(text, delimiter);
    }

    private async Task<(string? Text, string? ErrorKey)> ReadVocabularyFileAsync(
        Stream content, long length, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var empty = _detail.Resource is null
            ? "ResourceAdd_FileEmpty" : "ResourceEdit_FileEmpty";
        var extension = Path.GetExtension(fileName);
        if (!string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
            return (null, "NativeResources_UnsupportedFile");
        if (length == 0)
            return (null, empty);
        if (length > MaxVocabularyFileBytes)
            return (null, "NativeResources_FileTooLarge");
        try
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (bytes.Length + read > MaxVocabularyFileBytes)
                    return (null, "NativeResources_FileTooLarge");
                bytes.Write(buffer, 0, read);
            }
            bytes.Position = 0;
            using var reader = new StreamReader(bytes, Encoding.UTF8, true, leaveOpen: true);
            var text = await reader.ReadToEndAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(text) ? (null, empty) : (text, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Native resource vocabulary file read failed ({FailureType}).",
                ex.GetType().Name);
            return (null, "NativeResources_FileReadError");
        }
    }

    public bool SetStagedLexicalUnitType(string wordId, LexicalUnitType lexicalUnitType)
    {
        if (!Enum.IsDefined(lexicalUnitType) || lexicalUnitType == LexicalUnitType.Unknown
            || !TryCurrentOwner(out var owner)
            || !IsCurrentOwnerStamp(owner, userScope.CaptureStamp())
            || _detail is not { Resource: null, Draft: { } draft }
            || Volatile.Read(ref _saving) != 0)
            return false;
        var rows = draft.StagedVocabulary;
        if (rows.IsDefault)
            return false;
        var index = FindStagedIndex(rows, wordId);
        if (index < 0)
            return false;
        _detail = _detail with {
            Draft = draft with {
                StagedVocabulary = rows.SetItem(index,
                    rows[index] with { LexicalUnitType = lexicalUnitType })
            }
        };
        return true;
    }

    public bool RemoveStagedVocabulary(string wordId)
    {
        if (!TryCurrentOwner(out var owner)
            || !IsCurrentOwnerStamp(owner, userScope.CaptureStamp())
            || _detail.Draft is not { } draft
            || _detail.Resource is { IsSmartResource: true }
            || _detail.Phase is not (NativeResourceDetailPhase.Creating
                or NativeResourceDetailPhase.Ready
                or NativeResourceDetailPhase.SaveError)
            || Volatile.Read(ref _saving) != 0)
            return false;
        var rows = draft.StagedVocabulary;
        if (rows.IsDefault)
            return false;
        var index = FindStagedIndex(rows, wordId);
        if (index < 0)
            return false;
        var clearError = _detail.LastSaveResult
            != ResourceMetadataUpdateResult.ValidationFailed
            && _detail.ErrorKey is ("NativeResources_SaveInvalid"
                or "NativeResources_SaveLanguageBeforeQuickAdd");
        _detail = _detail with {
            Draft = draft with { StagedVocabulary = rows.RemoveAt(index) },
            ErrorKey = clearError ? null : _detail.ErrorKey
        };
        return true;
    }

    private static int FindStagedIndex(
        ImmutableArray<NativeResourceVocabularyRow> rows, string wordId)
    {
        for (var index = 0; index < rows.Length; index++)
        {
            if (rows[index].Id == wordId)
                return index;
        }
        return -1;
    }
}
