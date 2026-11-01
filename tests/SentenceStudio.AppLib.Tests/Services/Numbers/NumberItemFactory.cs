using SentenceStudio.Services.Numbers;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.AppLib.Tests.Services.Numbers;

/// <summary>
/// Builds grader fixtures for a specific value. The item generator draws values from
/// realistic per-category ranges (Counting 1-99, Money in everyday denominations), so
/// grader tests that need an exact value (5원, 100개, 150원) must not search generator seeds.
/// </summary>
internal static class NumberItemFactory
{
    public static NumberItem Counting(string counter, long value, string canonical) =>
        Build("Counting", counter, NumberSystem.Native, value, canonical);

    public static NumberItem Money(long value, string canonical) =>
        Build("Money", "원", NumberSystem.Sino, value, canonical);

    private static NumberItem Build(string context, string counter, NumberSystem system, long value, string canonical) =>
        new(
            Id: Guid.NewGuid(),
            ContextCode: context,
            SubModeCode: "ReadAndProduce",
            CounterId: counter,
            CounterText: counter,
            System: system,
            Bucket: "test",
            DigitValue: value,
            CanonicalAnswer: canonical,
            DisplayPrompt: $"{value} {counter}",
            AudioCue: "",
            Hints: new List<string>(),
            AcceptableAlternates: new List<string> { canonical.Replace(" ", "") },
            DigitDisplay: $"{value} {counter}");
}
