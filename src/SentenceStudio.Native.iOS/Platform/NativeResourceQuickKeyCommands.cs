namespace SentenceStudio.Native.iOS.Platform;

internal static class NativeResourceQuickKeyCommands
{
    internal static TCommand[] Merge<TCommand, TChord>(
        TCommand[]? inherited, TCommand[]? active,
        Func<TCommand, TChord> chord) where TChord : notnull
    {
        if (active is not { Length: > 0 })
            return inherited ?? [];

        var activeChords = active.Select(chord).ToHashSet();
        var seen = new HashSet<TChord>();
        var combined = new List<TCommand>((inherited?.Length ?? 0) + active.Length);
        foreach (var command in inherited ?? [])
        {
            var key = chord(command);
            if (!activeChords.Contains(key) && seen.Add(key))
                combined.Add(command);
        }
        foreach (var command in active)
            if (seen.Add(chord(command)))
                combined.Add(command);
        return [.. combined];
    }
}
