namespace SentenceStudio.Native.Android;

internal static class AndroidReplacementGuard
{
    public static void RefuseLegacyDatabase(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        var database = Path.Combine(appDataDirectory, "sstudio.db3");
        if (File.Exists(database) || File.Exists($"{database}-wal")
            || File.Exists($"{database}-shm"))
            throw new InvalidOperationException(
                "A previous SentenceStudio database exists; native replacement requires a verified backup and migration. Existing data was not changed.");
    }
}
