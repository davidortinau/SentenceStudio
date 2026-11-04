using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Services.Numbers;

/// <summary>
/// Display order of the Number Drill sub-modes. <see cref="NumberSubMode"/> has
/// no sort column and the query has no ORDER BY, so the store's row order is
/// undefined; this keeps the seed order (lib/content/numbers/ko.json): Phase,
/// then the seed position, then the code.
/// </summary>
public static class NumberSubModeOrder
{
    public static int SeedPosition(string? code) => code switch
    {
        "ListenAndType" => 0,
        "ReadAndProduce" => 1,
        "TapTheCounter" => 2,
        "Disambiguate" => 3,
        "ListenAndPlace" => 4,
        _ => 5,
    };

    public static List<NumberSubMode> InSeedOrder(IEnumerable<NumberSubMode> modes) =>
        modes
            .OrderBy(mode => mode.Phase)
            .ThenBy(mode => SeedPosition(mode.Code))
            .ThenBy(mode => mode.Code, StringComparer.Ordinal)
            .ToList();
}
