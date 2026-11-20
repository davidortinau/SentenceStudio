using System.Collections.Immutable;

namespace SentenceStudio.Native;

public static class NativeChooseOwnOrder
{
    /// <summary>
    /// The options with the chosen ones first, each group in the order it was listed. A
    /// selector takes this order when it opens or its list is reloaded and keeps it while the
    /// learner switches rows, so a row does not move under the finger (issue 244).
    /// </summary>
    public static ImmutableArray<NativeChooseOwnOption> SelectedFirst(
        ImmutableArray<NativeChooseOwnOption> options, ImmutableArray<string> selectedIds)
    {
        if (options.IsDefaultOrEmpty)
            return [];
        if (selectedIds.IsDefaultOrEmpty)
            return options;
        var chosen = selectedIds.ToHashSet(StringComparer.Ordinal);
        return [
            .. options.Where(option => chosen.Contains(option.Id)),
            .. options.Where(option => !chosen.Contains(option.Id))
        ];
    }
}
