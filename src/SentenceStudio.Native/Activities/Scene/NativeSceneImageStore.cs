using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// The source SceneImageService over the native owner's database, plus image
/// bytes. SceneImage has no owner column and is not in the CoreSync set
/// (SharedSyncRegistration), so nothing here is uploaded and no row can block
/// owner-scoped sync; the native ApplicationDbContext opens only the active
/// owner's SQLite file (NativeOwnerStores), which is what keeps one account's
/// gallery out of another's. Every call also names the owner and refuses an
/// absent or inactive one.
/// <para>
/// URL images (the source's only add path) are fetched without credentials
/// through <see cref="NativeRemotePhotoLoader"/> (HTTPS only, bounded). Photos
/// chosen in the system picker (Captain, 2026-11-01) are stored as files under
/// <c>{AppData}/SceneImages/{owner}/</c> and referenced by a relative
/// <c>native-scene-image:</c> URL, so they survive the app container moving
/// and resolve only inside the owner's own folder. The API has no image
/// upload endpoint and the table is local-only, so nothing is uploaded.
/// </para>
/// </summary>
public sealed partial class NativeSceneImageStore : INativeSceneImageStore, IDisposable
{
    public const string LocalScheme = "native-scene-image:";
    public const int MaxImageBytes = 8 * 1024 * 1024;
    internal const string Folder = "SceneImages";

    private readonly IServiceScopeFactory _scopes;
    private readonly NativeActivityOwner _owner;
    private readonly IFileSystemService _files;
    private readonly ILogger<NativeSceneImageStore> _logger;
    private readonly NativeRemotePhotoLoader _remote;

    public NativeSceneImageStore(IServiceScopeFactory scopes, NativeActivityOwner owner,
        IFileSystemService files, ILogger<NativeSceneImageStore> logger)
        : this(scopes, owner, files, logger,
            new NativeRemotePhotoLoader(MaxImageBytes, apiRoot: null, TimeSpan.FromSeconds(20)))
    {
    }

    internal NativeSceneImageStore(IServiceScopeFactory scopes, NativeActivityOwner owner,
        IFileSystemService files, ILogger<NativeSceneImageStore> logger,
        NativeRemotePhotoLoader remote)
    {
        _scopes = scopes;
        _owner = owner;
        _files = files;
        _logger = logger;
        _remote = remote;
    }

    public async Task<IReadOnlyList<SceneImage>> ListAsync(string owner,
        CancellationToken cancellationToken)
    {
        if (!_owner.IsActive(owner))
            return [];
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.SceneImages.AsNoTracking().OrderBy(image => image.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Source GetRandomAsync: a uniformly random row, or null when the gallery is empty.</summary>
    public async Task<SceneImage?> GetRandomAsync(string owner, CancellationToken cancellationToken)
    {
        var images = await ListAsync(owner, cancellationToken);
        return images.Count == 0 ? null : images[Random.Shared.Next(images.Count)];
    }

    /// <summary>Source AddImage (new SceneImage { Url }); only absolute HTTPS URLs can be shown natively.</summary>
    public async Task<SceneImage?> AddUrlAsync(string owner, string url,
        CancellationToken cancellationToken)
    {
        var trimmed = url?.Trim() ?? string.Empty;
        if (!IsSupportedRemoteUrl(trimmed))
            return null;
        return await SaveAsync(owner, new SceneImage { Url = trimmed }, cancellationToken);
    }

    public async Task<SceneImage?> AddPhotoAsync(string owner, NativeScenePickedPhoto photo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(photo);
        if (!_owner.IsActive(owner) || photo.Data is not { Length: > 0 and <= MaxImageBytes } data
            || DetectExtension(data) is not { } extension
            || OwnerDirectory(owner) is not { } directory)
            return null;
        Directory.CreateDirectory(directory);
        var name = $"{Guid.NewGuid():N}.{extension}";
        var path = Path.Combine(directory, name);
        await File.WriteAllBytesAsync(path, data, cancellationToken);
        try
        {
            var saved = await SaveAsync(owner, new SceneImage { Url = LocalScheme + name },
                cancellationToken);
            if (saved is null)
                TryDeleteFile(path);
            return saved;
        }
        catch
        {
            TryDeleteFile(path);
            throw;
        }
    }

    public async Task<int> DeleteAsync(string owner, IReadOnlyCollection<int> ids,
        CancellationToken cancellationToken)
    {
        if (!_owner.IsActive(owner) || ids.Count == 0)
            return 0;
        List<SceneImage> rows;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            rows = await db.SceneImages.Where(image => ids.Contains(image.Id))
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
                return 0;
            if (!_owner.IsActive(owner))
                return 0;
            db.SceneImages.RemoveRange(rows);
            await db.SaveChangesAsync(cancellationToken);
        }
        // The rows are gone; a file that cannot be removed is logged, not reported as a failed delete.
        foreach (var row in rows)
            if (LocalPath(owner, row.Url) is { } path)
                TryDeleteFile(path);
        return rows.Count;
    }

    public async Task<byte[]?> LoadBytesAsync(string owner, SceneImage image,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!_owner.IsActive(owner) || string.IsNullOrWhiteSpace(image.Url))
            return null;
        byte[]? bytes;
        if (image.Url.StartsWith(LocalScheme, StringComparison.Ordinal))
        {
            if (LocalPath(owner, image.Url) is not { } path || !File.Exists(path)
                || new FileInfo(path).Length > MaxImageBytes)
                return null;
            bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        else
        {
            if (!Uri.TryCreate(image.Url, UriKind.Absolute, out var uri))
                return null;
            bytes = await _remote.LoadAsync(uri, cancellationToken);
        }
        return bytes is { Length: > 0 } && DetectExtension(bytes) is not null
            && _owner.IsActive(owner) ? bytes : null;
    }

    public static bool IsSupportedRemoteUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && NativeRemotePhotoLoader.CanRequest(uri, apiRoot: null);

    /// <summary>Image signatures the SwiftUI and Compose image views decode.</summary>
    internal static string? DetectExtension(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> png = [137, 80, 78, 71, 13, 10, 26, 10];
        if (data.Length >= png.Length && data[..png.Length].SequenceEqual(png))
            return "png";
        if (data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff)
            return "jpg";
        if (data.Length >= 6 && data[..3].SequenceEqual("GIF"u8) && data[3] == '8')
            return "gif";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8)
            && data[8..12].SequenceEqual("WEBP"u8))
            return "webp";
        return null;
    }

    private async Task<SceneImage?> SaveAsync(string owner, SceneImage image,
        CancellationToken cancellationToken)
    {
        if (!_owner.IsActive(owner))
            return null;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SceneImages.Add(image);
        await db.SaveChangesAsync(cancellationToken);
        return new SceneImage { Id = image.Id, Url = image.Url, Description = image.Description };
    }

    /// <summary>Owner IDs are server GUIDs; anything else has no folder and fails closed.</summary>
    private string? OwnerDirectory(string owner) =>
        Guid.TryParse(owner, out var id)
            ? Path.Combine(_files.AppDataDirectory, Folder, id.ToString("N"))
            : null;

    /// <summary>A local URL resolves only to a well-formed file name inside the owner's folder.</summary>
    internal string? LocalPath(string owner, string? url)
    {
        if (url is null || !url.StartsWith(LocalScheme, StringComparison.Ordinal)
            || OwnerDirectory(owner) is not { } directory)
            return null;
        var name = url[LocalScheme.Length..];
        return LocalName().IsMatch(name) ? Path.Combine(directory, name) : null;
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "A scene photo file could not be removed.");
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}\\.(png|jpg|gif|webp)$")]
    private static partial Regex LocalName();

    public void Dispose() => _remote.Dispose();
}
