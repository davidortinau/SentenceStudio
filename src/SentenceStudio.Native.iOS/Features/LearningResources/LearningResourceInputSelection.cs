using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

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
