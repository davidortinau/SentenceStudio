using SentenceStudio.Native.Adapters;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// The quiz, Shadowing and How Do You Say audio cache: a row written by an earlier
/// install stores an absolute path under that install's container, which iOS moves.
/// </summary>
public sealed class NativeAudioCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ss-audio-cache-" + Guid.NewGuid().ToString("N"));

    public NativeAudioCacheTests() => Directory.CreateDirectory(NativeAudioCache.DirectoryFor(_root));

    [Fact]
    public void StoredPathFromAnEarlierContainer_ResolvesByFileNameInTodaysCache()
    {
        var clip = Path.Combine(NativeAudioCache.DirectoryFor(_root), "word_abc.mp3");
        File.WriteAllBytes(clip, [1, 2, 3]);
        var stale = "/Users/x/Library/Developer/CoreSimulator/Devices/D/data/Containers/Data/Application/"
            + "OLD-CONTAINER/Library/AudioCache/word_abc.mp3";

        Assert.Equal(Path.GetFullPath(clip), NativeAudioCache.Resolve(_root, stale, 1024));
        // A current-install row still resolves to itself.
        Assert.Equal(Path.GetFullPath(clip), NativeAudioCache.Resolve(_root, clip, 1024));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/old/AudioCache/")]
    [InlineData("/old/AudioCache/missing.mp3")]
    [InlineData("/old/AudioCache/..")]
    public void MissingOrUnnamedClips_AreNotResolved(string? stored) =>
        Assert.Null(NativeAudioCache.Resolve(_root, stored, 1024));

    [Fact]
    public void EmptyOrOversizedClips_AndFilesOutsideTheCache_AreNotResolved()
    {
        var directory = NativeAudioCache.DirectoryFor(_root);
        File.WriteAllBytes(Path.Combine(directory, "empty.mp3"), []);
        File.WriteAllBytes(Path.Combine(directory, "big.mp3"), new byte[16]);
        File.WriteAllBytes(Path.Combine(_root, "outside.mp3"), [1]);

        Assert.Null(NativeAudioCache.Resolve(_root, "/old/AudioCache/empty.mp3", 8));
        Assert.Null(NativeAudioCache.Resolve(_root, "/old/AudioCache/big.mp3", 8));
        // Only the cache directory is searched, never the stored directory or AppData itself.
        Assert.Null(NativeAudioCache.Resolve(_root, Path.Combine(_root, "outside.mp3"), 8));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
