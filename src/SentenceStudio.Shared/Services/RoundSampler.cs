using System;
using System.Collections.Generic;

namespace SentenceStudio.Shared.Services;

/// <summary>
/// Rotating-window sampler for per-round word selection in the Vocab Quiz (F1).
/// Draws up to <paramref name="take"/> items from a need-ordered pool starting at a
/// persistent cursor, wrapping around. This guarantees every item is shown once before
/// any repeat (no front-bias starvation — fixes "same 10 every round / never see all 20")
/// while preserving the pool's existing ordering (SRS overdue priority), unlike a naive
/// random shuffle which would drop priority and risk coverage gaps.
/// </summary>
public static class RoundSampler
{
    /// <summary>
    /// Returns up to <paramref name="take"/> items starting at <paramref name="cursor"/>
    /// (wrapping around the pool) plus the advanced cursor for the next round.
    /// </summary>
    public static (List<T> Window, int NextCursor) NextWindow<T>(IReadOnlyList<T> pool, int cursor, int take)
    {
        var count = pool?.Count ?? 0;
        var window = new List<T>(count == 0 ? 0 : Math.Min(take, count));
        if (count == 0)
            return (window, 0);
        if (cursor < 0 || cursor >= count)
            cursor = 0;

        var n = Math.Min(take, count);
        for (int k = 0; k < n; k++)
            window.Add(pool![(cursor + k) % count]);

        return (window, (cursor + n) % count);
    }
}
