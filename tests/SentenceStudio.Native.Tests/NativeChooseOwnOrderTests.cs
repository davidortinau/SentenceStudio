using System.Collections.Immutable;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Tests;

public sealed class NativeChooseOwnOrderTests
{
    private static readonly ImmutableArray<NativeChooseOwnOption> Listed =
    [
        new("daily", "Daily Review"),
        new("new", "New Words Practice"),
        new("phrases", "Phrases"),
        new("sentences", "Sentences"),
        new("travel", "German Travel")
    ];

    [Fact]
    public void SelectedFirst_LeadsWithTheChosenAndKeepsEachGroupInListedOrder()
    {
        // Chosen in the order the learner tapped them, not the listed order.
        var ordered = NativeChooseOwnOrder.SelectedFirst(Listed, ["travel", "new"]);

        Assert.Equal(["new", "travel", "daily", "phrases", "sentences"],
            ordered.Select(option => option.Id));
    }

    [Fact]
    public void SelectedFirst_WithNothingChosenKeepsTheListedOrder()
    {
        Assert.Equal(Listed, NativeChooseOwnOrder.SelectedFirst(Listed, []));
        Assert.Equal(Listed, NativeChooseOwnOrder.SelectedFirst(Listed, default));
    }

    [Fact]
    public void SelectedFirst_IgnoresAChosenIdThatIsNotListedAndAnUnloadedList()
    {
        var ordered = NativeChooseOwnOrder.SelectedFirst(Listed, ["gone", "phrases"]);

        Assert.Equal(["phrases", "daily", "new", "sentences", "travel"],
            ordered.Select(option => option.Id));
        Assert.Empty(NativeChooseOwnOrder.SelectedFirst(default, ["phrases"]));
    }
}
