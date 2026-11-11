using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Features.Vocabulary;

/// <summary>A Reference Sentences text field: the sentence or its translation.</summary>
internal enum VocabularyExampleField { Target, Native }

/// <summary>
/// The text keys of the Reference Sentences labels, shared by the heads so they cannot drift.
/// </summary>
internal static class VocabularyExampleText
{
    /// <summary>Source RegisterLabel.</summary>
    internal static string RegisterKey(SpeechRegister register) => register switch
    {
        SpeechRegister.FormalPolite => "Register_FormalPolite",
        SpeechRegister.InformalPolite => "Register_InformalPolite",
        SpeechRegister.Casual => "Register_Casual",
        SpeechRegister.PlainWritten => "Register_PlainWritten",
        _ => "Register_Unspecified"
    };

    /// <summary>Source RenderSourceBadge: none for a sentence of unknown origin.</summary>
    internal static string? SourceKey(ExampleSentenceSource source) => source switch
    {
        ExampleSentenceSource.AiGenerated => "RefSentences_SourceAi",
        ExampleSentenceSource.UserAuthored => "RefSentences_SourceUser",
        ExampleSentenceSource.FromReading => "RefSentences_SourceReading",
        _ => null
    };

    /// <summary>The word's encoding label (Basic, Good or Strong) as a text key.</summary>
    internal static string EncodingKey(string label) => label switch
    {
        "Strong" => "Vocabulary_Strong",
        "Good" => "Vocabulary_Good",
        _ => "Vocabulary_Basic"
    };
}

/// <summary>An AI suggestion the learner has not kept yet (source suggestion tray); its text can be edited first.</summary>
internal sealed record VocabularyExampleSuggestion(
    string Key, string TargetSentence, string NativeSentence,
    SpeechRegister Register, int? DifficultyLevel);

/// <summary>
/// The word editor's Reference Sentences (source ReferenceSentencesSection): the owner's saved
/// sentences (core first), the AI suggestions, the add row, the sentence being edited, and the
/// last result to show.
/// </summary>
internal sealed record VocabularyExamplesState(
    IReadOnlyList<NativeVocabularyExample> Sentences,
    IReadOnlyList<VocabularyExampleSuggestion> Suggestions,
    string NewTarget, string NewNative, SpeechRegister NewRegister,
    string? EditingId, string EditTarget, string EditNative, SpeechRegister EditRegister,
    bool Generating, bool Writing, string? Message, bool MessageIsError)
{
    /// <summary>No sentences, nothing typed, nothing running (a head drawing before the word loads).</summary>
    internal static VocabularyExamplesState Empty { get; } = new([], [], string.Empty,
        string.Empty, SpeechRegister.Unspecified, null, string.Empty, string.Empty,
        SpeechRegister.Unspecified, false, false, null, false);

    /// <summary>Source: Add needs a target sentence.</summary>
    internal bool CanAdd => !Writing && !string.IsNullOrWhiteSpace(NewTarget);

    /// <summary>Source: the edit's Save needs a target sentence.</summary>
    internal bool CanSaveEdit => !Writing && EditingId is not null
        && !string.IsNullOrWhiteSpace(EditTarget);

    internal static bool CanKeep(bool writing, VocabularyExampleSuggestion suggestion) =>
        !writing && !string.IsNullOrWhiteSpace(suggestion.TargetSentence);
}

internal partial class VocabularyBrowser
{
    /// <summary>Source GenerateAsync asks for four suggestions.</summary>
    private const int ExampleSuggestionCount = 4;

    private readonly List<VocabularyExampleSuggestion> _exampleSuggestions = [];
    private string _newExampleTarget = string.Empty;
    private string _newExampleNative = string.Empty;
    private SpeechRegister _newExampleRegister;
    private string? _editingExampleId;
    private string _editExampleTarget = string.Empty;
    private string _editExampleNative = string.Empty;
    private SpeechRegister _editExampleRegister;
    private bool _generatingExamples;
    private bool _writingExample;
    private string? _exampleMessage;
    private bool _exampleMessageIsError;
    /// <summary>
    /// A sentence changed since the list last read its words: the strength badges count them,
    /// so the list reads them again when the editor closes. Kept while the editor moves from
    /// one word to another; a list reload clears it.
    /// </summary>
    private bool _examplesChanged;
    private long _exampleSuggestionKeys;

    private VocabularyExamplesState? ExamplesState => _detail is not { } detail ? null
        : new(detail.Examples, _exampleSuggestions.ToArray(),
            _newExampleTarget, _newExampleNative, _newExampleRegister,
            _editingExampleId, _editExampleTarget, _editExampleNative, _editExampleRegister,
            _generatingExamples, _writingExample, _exampleMessage, _exampleMessageIsError);

    /// <summary>
    /// The add row's sentence or translation, as typed. Refused while a sentence is being
    /// written: the fields are read-only then, and the write may clear them.
    /// </summary>
    internal void ChangeNewExample(VocabularyExampleField field, string value)
    {
        if (_writingExample || !CanChangeExamples(out _, out _))
            return;
        if (field == VocabularyExampleField.Native)
            _newExampleNative = value ?? string.Empty;
        else
            _newExampleTarget = value ?? string.Empty;
        Notify(VocabularyChanges.ExampleText);
    }

    internal void SetNewExampleRegister(SpeechRegister register)
    {
        if (_writingExample || !CanChangeExamples(out _, out _) || !Enum.IsDefined(register))
            return;
        _newExampleRegister = register;
        Notify(VocabularyChanges.ExampleText);
    }

    /// <summary>The sentence being edited: its text or translation (not while it is being written).</summary>
    internal void ChangeEditedExample(VocabularyExampleField field, string value)
    {
        if (_editingExampleId is null || _writingExample || !CanChangeExamples(out _, out _))
            return;
        if (field == VocabularyExampleField.Native)
            _editExampleNative = value ?? string.Empty;
        else
            _editExampleTarget = value ?? string.Empty;
        Notify(VocabularyChanges.ExampleText);
    }

    internal void SetEditedExampleRegister(SpeechRegister register)
    {
        if (_editingExampleId is null || _writingExample || !CanChangeExamples(out _, out _)
            || !Enum.IsDefined(register))
            return;
        _editExampleRegister = register;
        Notify(VocabularyChanges.ExampleText);
    }

    /// <summary>A suggestion's text or translation, edited before Keep (not while a sentence is being written).</summary>
    internal void ChangeSuggestion(string key, VocabularyExampleField field, string value)
    {
        var index = _exampleSuggestions.FindIndex(suggestion => suggestion.Key == key);
        if (index < 0 || _writingExample || !CanChangeExamples(out _, out _))
            return;
        var suggestion = _exampleSuggestions[index];
        _exampleSuggestions[index] = field == VocabularyExampleField.Native
            ? suggestion with { NativeSentence = value ?? string.Empty }
            : suggestion with { TargetSentence = value ?? string.Empty };
        Notify(VocabularyChanges.ExampleText);
    }

    /// <summary>Source AddManualAsync: the first of the owner's sentences on the word becomes its core example.</summary>
    internal Task AddExampleAsync()
    {
        if (string.IsNullOrWhiteSpace(_newExampleTarget))
            return Task.CompletedTask;
        var draft = new NativeExampleSentenceDraft(
            _newExampleTarget, _newExampleNative, _newExampleRegister);
        if (RefuseTooLong(draft))
            return Task.CompletedTask;
        return WriteExampleAsync(
            (owner, wordId) => _store.AddOwnedExampleAsync(owner, wordId, draft,
                ExampleSentenceSource.UserAuthored, coreWhenFirst: true, _lifetimeToken),
            () =>
            {
                _newExampleTarget = string.Empty;
                _newExampleNative = string.Empty;
                _newExampleRegister = SpeechRegister.Unspecified;
            },
            "RefSentences_Saved");
    }

    /// <summary>Source KeepAsync: the suggestion is saved as a curated AI sentence.</summary>
    internal Task KeepSuggestionAsync(string key)
    {
        if (_exampleSuggestions.Find(suggestion => suggestion.Key == key) is not { } kept
            || !VocabularyExamplesState.CanKeep(_writingExample, kept))
            return Task.CompletedTask;
        var draft = new NativeExampleSentenceDraft(kept.TargetSentence, kept.NativeSentence,
            kept.Register, kept.DifficultyLevel);
        if (RefuseTooLong(draft))
            return Task.CompletedTask;
        return WriteExampleAsync(
            (owner, wordId) => _store.AddOwnedExampleAsync(owner, wordId, draft,
                ExampleSentenceSource.AiGenerated, coreWhenFirst: false, _lifetimeToken),
            () => _exampleSuggestions.RemoveAll(suggestion => suggestion.Key == key),
            "RefSentences_Kept");
    }

    internal void DiscardSuggestion(string key)
    {
        if (!CanChangeExamples(out _, out _)
            || _exampleSuggestions.RemoveAll(suggestion => suggestion.Key == key) == 0)
            return;
        _exampleMessage = null;
        Notify(VocabularyChanges.Examples);
    }

    internal void StartEditExample(string id)
    {
        if (_writingExample || !CanChangeExamples(out _, out _)
            || _detail?.Examples.FirstOrDefault(example => example.Id == id) is not { } sentence)
            return;
        _editingExampleId = sentence.Id;
        _editExampleTarget = sentence.TargetSentence;
        _editExampleNative = sentence.NativeSentence ?? string.Empty;
        _editExampleRegister = sentence.Register;
        _exampleMessage = null;
        Notify(VocabularyChanges.Examples);
    }

    internal void CancelEditExample()
    {
        if (_editingExampleId is null || _writingExample)
            return;
        _editingExampleId = null;
        Notify(VocabularyChanges.Examples);
    }

    /// <summary>Source SaveEditAsync.</summary>
    internal Task SaveEditedExampleAsync()
    {
        if (_editingExampleId is not { } id || string.IsNullOrWhiteSpace(_editExampleTarget))
            return Task.CompletedTask;
        var draft = new NativeExampleSentenceDraft(
            _editExampleTarget, _editExampleNative, _editExampleRegister);
        if (RefuseTooLong(draft))
            return Task.CompletedTask;
        return WriteExampleAsync(
            (owner, wordId) => _store.EditOwnedExampleAsync(owner, wordId, id, draft,
                _lifetimeToken),
            () => _editingExampleId = null,
            "RefSentences_Saved");
    }

    /// <summary>Source SetCoreAsync: a core sentence stops being core; any other becomes the word's one core example.</summary>
    internal Task ToggleExampleCoreAsync(string id)
    {
        if (_detail?.Examples.FirstOrDefault(example => example.Id == id) is not { } sentence)
            return Task.CompletedTask;
        return WriteExampleAsync(
            (owner, wordId) => _store.SetOwnedExampleCoreAsync(owner, wordId, id,
                !sentence.IsCore, _lifetimeToken),
            static () => { }, savedKey: null);
    }

    /// <summary>Source ToggleFlagAsync.</summary>
    internal Task ToggleExampleFlagAsync(string id)
    {
        if (_detail?.Examples.FirstOrDefault(example => example.Id == id) is not { } sentence)
            return Task.CompletedTask;
        return WriteExampleAsync(
            (owner, wordId) => _store.SetOwnedExampleFlagAsync(owner, wordId, id,
                !sentence.IsFlagged, _lifetimeToken),
            static () => { }, savedKey: null);
    }

    /// <summary>Source DeleteAsync (no confirmation, as the source).</summary>
    internal Task DeleteExampleAsync(string id) =>
        WriteExampleAsync(
            (owner, wordId) => _store.DeleteOwnedExampleAsync(owner, wordId, id, _lifetimeToken),
            () =>
            {
                if (_editingExampleId == id)
                    _editingExampleId = null;
            },
            "RefSentences_Deleted");

    /// <summary>
    /// Source GenerateAsync: four suggestions for the stored word, in its language (Korean when
    /// it has none) with English translations. None back, or a failure, says so.
    /// </summary>
    internal async Task GenerateExamplesAsync()
    {
        if (_generatingExamples || !CanChangeExamples(out var owner, out var wordId))
            return;
        var generation = Volatile.Read(ref _formGeneration);
        var language = _draft?.Language;
        var targetLanguage = string.IsNullOrWhiteSpace(language) ? "Korean" : language.Trim();
        _generatingExamples = true;
        _exampleMessage = null;
        Notify(VocabularyChanges.Examples);
        IReadOnlyList<NativeExampleSentenceDraft> drafts = [];
        var failed = false;
        try
        {
            drafts = await OffUiAsync(() => _store.SuggestOwnedExamplesAsync(
                owner, wordId, targetLanguage, "English", ExampleSuggestionCount,
                _lifetimeToken));
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native example sentence suggestions failed.");
            failed = true;
        }
        if (!IsSameOpenWord(generation, wordId))
            return;
        _generatingExamples = false;
        if (!TryOwner(out _))
            return;
        if (failed || drafts.Count == 0)
            ShowExampleMessage("RefSentences_GenerateFailed", error: true);
        else
            _exampleSuggestions.AddRange(drafts.Select(draft => new VocabularyExampleSuggestion(
                $"s{Interlocked.Increment(ref _exampleSuggestionKeys)}",
                draft.TargetSentence, draft.NativeSentence ?? string.Empty,
                draft.Register, draft.DifficultyLevel)));
        Notify(VocabularyChanges.Examples);
    }

    /// <summary>
    /// One sentence write at a time, off the UI thread. A write that returns after the editor
    /// moved to another word (or closed) changes nothing on screen.
    /// </summary>
    private async Task WriteExampleAsync(
        Func<string, string, Task<NativeExampleSentenceResult>> write,
        Action saved, string? savedKey)
    {
        if (_writingExample || !CanChangeExamples(out var owner, out var wordId))
            return;
        var generation = Volatile.Read(ref _formGeneration);
        _writingExample = true;
        _exampleMessage = null;
        Notify(VocabularyChanges.Examples);
        NativeExampleSentenceResult? result = null;
        try
        {
            result = await OffUiAsync(() => write(owner, wordId));
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native example sentence write failed.");
        }
        if (!IsSameOpenWord(generation, wordId))
            return;
        _writingExample = false;
        if (!TryOwner(out _))
            return;
        if (result is { Saved: true } && _detail is { } detail)
        {
            _detail = detail with
            {
                Examples = result.Examples,
                Word = detail.Word with { EncodingLabel = result.EncodingLabel }
            };
            _examplesChanged = true;
            saved();
            if (result.Status == NativeExampleSentenceStatus.SavedSyncUnconfirmed)
                ShowExampleMessage("NativeVocabulary_SavedSyncUnconfirmed", error: true);
            else if (savedKey is not null)
                ShowExampleMessage(savedKey, error: false);
        }
        else
            ShowExampleMessage("RefSentences_SaveFailed", error: true);
        Notify(VocabularyChanges.Examples);
    }

    /// <summary>The editor shows the owner's word and nothing holds it (its Save, Delete or a handoff).</summary>
    private bool CanChangeExamples(out string owner, out string wordId)
    {
        owner = string.Empty;
        wordId = _detail?.Word.Id ?? string.Empty;
        return _formOpen && !_formLoading && _detail is not null && !_saving
            && !_cleanup.Deleting && !_handingOff && TryOwner(out owner);
    }

    private bool IsSameOpenWord(long generation, string wordId) =>
        !_disposed && generation == Volatile.Read(ref _formGeneration)
        && _formOpen && _detail?.Word.Id == wordId;

    /// <summary>
    /// A sentence or translation longer than its column is not sent (the store would refuse
    /// it); the card says how long it may be, so a retry is not attempted in vain.
    /// </summary>
    private bool RefuseTooLong(NativeExampleSentenceDraft draft)
    {
        if (draft.Fits || !CanChangeExamples(out _, out _))
            return false;
        ShowExampleMessage("RefSentences_TooLong", error: true);
        Notify(VocabularyChanges.Examples);
        return true;
    }

    private void ShowExampleMessage(string key, bool error)
    {
        _exampleMessage = _text(key);
        _exampleMessageIsError = error;
    }

    /// <summary>A word opened, or the editor closed: nothing of the last word's sentences stays.</summary>
    private void ResetExamples()
    {
        _exampleSuggestions.Clear();
        _newExampleTarget = string.Empty;
        _newExampleNative = string.Empty;
        _newExampleRegister = SpeechRegister.Unspecified;
        _editingExampleId = null;
        _editExampleTarget = string.Empty;
        _editExampleNative = string.Empty;
        _editExampleRegister = SpeechRegister.Unspecified;
        _generatingExamples = false;
        _writingExample = false;
        _exampleMessage = null;
        _exampleMessageIsError = false;
    }
}
