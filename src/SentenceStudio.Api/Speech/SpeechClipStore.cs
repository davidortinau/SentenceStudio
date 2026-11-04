using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace SentenceStudio.Api.Speech;

/// <summary>Where generated clips are kept. Implementations throw on failure; the cache decides what a failure means.</summary>
public interface ISpeechClipStore
{
    /// <summary>The stored bytes, or null when nothing is stored under <paramref name="name"/>.</summary>
    Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="content"/>. When <paramref name="overwrite"/> is false the write only
    /// succeeds if nothing is stored yet; returns false when another writer got there first.
    /// </summary>
    Task<bool> WriteAsync(
        string name,
        byte[] content,
        string contentType,
        bool overwrite,
        CancellationToken cancellationToken);
}

/// <summary>Clips in one Azure Blob Storage container (Azurite locally).</summary>
/// <remarks>
/// Blobs carry only a content type. No metadata is written: the name is a content hash, and the
/// text, user and resource must not be recoverable from the blob. Writes can use their own client
/// (the API's has retries off); both clients point at the same container.
/// </remarks>
public sealed class AzureBlobSpeechClipStore(
    BlobContainerClient container,
    BlobContainerClient? writeContainer = null) : ISpeechClipStore
{
    private readonly BlobContainerClient _writeContainer = writeContainer ?? container;

    public async Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.GetBlobClient(name).DownloadContentAsync(cancellationToken);
            return response.Value.Content.ToArray();
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobNotFound)
        {
            // Only a missing blob is a miss. A missing container or a permission failure is a
            // real fault and propagates so it gets logged.
            return null;
        }
    }

    public async Task<bool> WriteAsync(
        string name,
        byte[] content,
        string contentType,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        };

        if (!overwrite)
        {
            // Write-if-absent: the first stored clip for a key is never replaced by a concurrent
            // writer, so readers always see one complete upload.
            options.Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
        }

        try
        {
            await _writeContainer.GetBlobClient(name).UploadAsync(BinaryData.FromBytes(content), options, cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (!overwrite
            && (ex.ErrorCode == BlobErrorCode.BlobAlreadyExists || ex.Status == 412))
        {
            return false;
        }
    }
}
