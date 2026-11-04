using Android.Provider;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Resource vocabulary file import (source Choose File, accept=".txt,.csv"):
/// the system Open Document picker returns one content URI, which is read off
/// the UI thread and handed to the shared importer for the form that asked.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private void ChooseResourceFile(AndroidResourcesCoordinator resources)
    {
        if (resources.BeginFilePick() is not { } formGeneration)
            return;
        if (!activity.LaunchVocabularyFilePicker(uri =>
            {
                if (!ReferenceEquals(_resources, resources))
                    return;
                if (uri is null)
                {
                    resources.CancelFilePick(formGeneration);
                    return;
                }
                var resolver = activity.ContentResolver;
                _ = Task.Run(() =>
                {
                    var file = resolver is null ? null : ReadVocabularyFile(resolver, uri);
                    Post(() =>
                    {
                        if (!ReferenceEquals(_resources, resources))
                            return;
                        if (file is { } picked)
                            resources.ImportFile(formGeneration, picked.Content, picked.Name);
                        else
                            resources.FilePickFailed(formGeneration, "NativeResources_FileReadError");
                    });
                });
            }))
            resources.FilePickFailed(formGeneration, "NativeResources_FileReadError");
    }

    /// <summary>
    /// Reads at most one byte past the 1 MB import limit (enough for the shared
    /// reader to report an oversize file) and the document's display name, which
    /// carries the .txt/.csv extension the importer checks. No UI is involved.
    /// </summary>
    private static (byte[] Content, string Name)? ReadVocabularyFile(
        global::Android.Content.ContentResolver resolver, global::Android.Net.Uri uri)
    {
        try
        {
            var name = PickedFileName(resolver, uri) ?? uri.LastPathSegment ?? string.Empty;
            using var input = resolver.OpenInputStream(uri);
            if (input is null)
                return null;
            const int limit = NativeResourcePresenter.MaxVocabularyFileBytes + 1;
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while (buffer.Length < limit
                && (read = input.Read(chunk, 0,
                    (int)Math.Min(chunk.Length, limit - buffer.Length))) > 0)
                buffer.Write(chunk, 0, read);
            return (buffer.ToArray(), name);
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or IOException)
        {
            global::Android.Util.Log.Warn("SentenceStudio.Native",
                $"Vocabulary file could not be read ({ex.GetType().Name}).");
            return null;
        }
    }

    private static string? PickedFileName(
        global::Android.Content.ContentResolver resolver, global::Android.Net.Uri uri)
    {
        using var cursor = resolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
        if (cursor is null || !cursor.MoveToFirst())
            return null;
        var column = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
        return column >= 0 && !cursor.IsNull(column) ? cursor.GetString(column) : null;
    }
}
