namespace SentenceStudio.Native;

public sealed partial class NativeResourcePresenter
{
    /// <summary>
    /// The open form holds edits that are not saved: a field whose value differs from the
    /// loaded resource (typing a character and deleting it is no edit), staged words, or, on a
    /// new resource, any text entered. Every head asks this before leaving the form for
    /// another resource.
    /// </summary>
    public bool HasUnsavedEdits
    {
        get
        {
            if (_detail.Draft is not { } draft)
                return false;
            if (!draft.StagedVocabulary.IsDefaultOrEmpty)
                return true;
            if (_detail.Resource is not { } resource)
                return HasText(draft.Title) || HasText(draft.Description) || HasText(draft.Tags)
                    || HasText(draft.MediaUrl) || HasText(draft.Transcript)
                    || HasText(draft.Translation);
            return (draft.TitleDirty && Differs(draft.Title, resource.Title))
                || (draft.DescriptionDirty && Differs(draft.Description, resource.Description))
                || (draft.TagsDirty && Differs(draft.Tags, resource.Tags))
                || (draft.MediaTypeDirty && Differs(draft.MediaType, resource.MediaType))
                || (draft.LanguageDirty && Differs(draft.Language, resource.Language))
                || (draft.MediaUrlDirty && Differs(draft.MediaUrl, resource.MediaUrl))
                || (draft.TranscriptDirty && Differs(draft.Transcript, resource.Transcript))
                || (draft.TranslationDirty && Differs(draft.Translation, resource.Translation));

            static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);
            static bool Differs(string? draftValue, string? saved) =>
                !string.Equals(draftValue ?? string.Empty, saved ?? string.Empty,
                    StringComparison.Ordinal);
        }
    }
}
