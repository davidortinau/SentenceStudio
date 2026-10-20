using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

internal enum ResourceQuickAddKey { ArrowDown, ArrowUp, Enter, Escape }

internal static class ResourceQuickAddNavigation
{
    internal static int Move(int selected, int count, ResourceQuickAddKey key)
    {
        if (count <= 0)
            return -1;
        return key switch
        {
            ResourceQuickAddKey.ArrowDown => (selected + 1 + count) % count,
            ResourceQuickAddKey.ArrowUp => (selected - 1 + count) % count,
            _ => selected
        };
    }
}

internal sealed class ResourceRequiredPicker
{
    private readonly string? _current;

    internal ResourceRequiredPicker(string? current, IReadOnlyList<string> catalog)
    {
        _current = current;
        var options = catalog.ToArray();
        if (!string.IsNullOrWhiteSpace(current)
            && !options.Contains(current, StringComparer.OrdinalIgnoreCase))
            options = [current, .. options];
        Options = options;
        SelectedIndex = string.IsNullOrWhiteSpace(current) ? 0
            : Array.FindIndex(options, value =>
                string.Equals(value, current, StringComparison.OrdinalIgnoreCase));
    }

    internal IReadOnlyList<string> Options { get; }
    internal int SelectedIndex { get; }

    internal void Select(int index, Action<string> changed)
    {
        var value = Options[index];
        if (!string.Equals(value, _current, StringComparison.OrdinalIgnoreCase))
            changed(value);
    }
}
