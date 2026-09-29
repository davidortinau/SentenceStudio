using SentenceStudio.Native.Android;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidReplacementGuardTests
{
    [Theory]
    [InlineData("sstudio.db3")]
    [InlineData("sstudio.db3-wal")]
    [InlineData("sstudio.db3-shm")]
    public void ExistingLegacyDatabaseOrSidecarBlocksProductionReplacement(string filename)
    {
        var scratch = Path.Combine(Path.GetTempPath(),
            "sentence-studio-android-replacement-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var file = Path.Combine(scratch, filename);
            File.WriteAllText(file, "existing data");
            Assert.Throws<InvalidOperationException>(
                () => AndroidReplacementGuard.RefuseLegacyDatabase(scratch));
            Assert.Equal("existing data", File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void FreshSandboxPermitsNativeDatabaseWithoutCreatingFiles()
    {
        var scratch = Path.Combine(Path.GetTempPath(),
            "sentence-studio-android-replacement-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            AndroidReplacementGuard.RefuseLegacyDatabase(scratch);
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }
}
