using System.Xml.Linq;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Shared.Sam;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Every sentence the write surface can show has to exist in both languages, be genuinely
/// translated, and be reachable from the code that names it.
/// </summary>
/// <remarks>
/// <para>
/// A missing key does not fail the build and does not throw at runtime: the resource manager
/// answers with the key name, so the learner is shown <c>Coach_WriteAccept</c> on a button. That
/// is the failure this file exists to catch, and it is worth catching for a surface whose whole
/// job is telling someone truthfully what is about to happen to their data.
/// </para>
/// <para>
/// The reachability half matters just as much. Every stage the card can be in, and every kind of
/// change the server can propose, resolves to a key through
/// <see cref="SamWritePresentation"/>; walking the enums proves that a member added later cannot
/// quietly resolve to nothing.
/// </para>
/// </remarks>
public class SamWriteResourceTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static Dictionary<string, string> Load(string fileName)
    {
        var path = Path.Combine(RepoRoot, "src", "SentenceStudio.Shared", "Resources", "Strings", fileName);

        return XDocument.Load(path).Root!
            .Elements("data")
            .Where(d => d.Attribute("name") is not null)
            .GroupBy(d => d.Attribute("name")!.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> English => Load("AppResources.resx");
    private static Dictionary<string, string> Korean => Load("AppResources.ko.resx");

    /// <summary>Every key the write surface can ask for.</summary>
    private static IEnumerable<string> WriteKeys()
    {
        foreach (var kind in Enum.GetValues<CoachWriteChangeKind>())
        {
            yield return SamWritePresentation.HeadingKey(kind);
        }

        foreach (var stage in Enum.GetValues<SamWriteStage>())
        {
            yield return SamWritePresentation.StateKey(stage);
        }

        yield return "Coach_WriteProposalLabel";
        yield return "Coach_WriteReceiptLabel";
        yield return "Coach_WriteAccept";
        yield return "Coach_WriteDecline";
        yield return "Coach_WriteReview";
        yield return "Coach_WriteConfirm";
        yield return "Coach_WriteCancelConfirm";
        yield return "Coach_WriteUndo";
        yield return "Coach_WriteRefreshAction";
        yield return "Coach_WriteNothingChangedYet";
        yield return "Coach_WriteProtectedNotice";
        yield return "Coach_WriteIrreversibleNotice";
        yield return "Coach_WriteReversibleNotice";
        yield return "Coach_WriteUndoWindow";
        yield return "Coach_WriteConfirmExpiresAt";
        yield return "Coach_WriteExpiredHint";
        yield return "Coach_WriteInDoubtHint";
        yield return "Coach_WriteUnreadableHint";
        yield return "Coach_WriteSupersededHint";
        yield return "Coach_WriteBusy";
        yield return "Coach_WriteConfirmTitle";
        yield return "Coach_WriteConfirmIntro";
        yield return "Coach_WriteUnavailable";
        yield return "Coach_WriteRefused";
        yield return "Coach_WriteLimited";
        yield return "Coach_WriteNetworkFailed";
        yield return "Coach_WriteConfirmExpired";
        yield return "Coach_WriteApplied";
        yield return "Coach_WriteDeclined";
        yield return "Coach_WriteUndone";
        yield return "Coach_WriteExpired";
        yield return "Coach_WriteFailed";
        yield return "Coach_WriteInDoubt";
        yield return "Coach_WriteConfirmAnnounce";
    }

    public static TheoryData<string> Keys()
    {
        var data = new TheoryData<string>();
        foreach (var key in WriteKeys().Distinct(StringComparer.Ordinal))
        {
            data.Add(key);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void The_key_exists_in_english(string key) =>
        English.Should().ContainKey(key, "a missing key renders as the key name on the learner's screen");

    [Theory]
    [MemberData(nameof(Keys))]
    public void The_key_exists_in_korean(string key) =>
        Korean.Should().ContainKey(key);

    [Theory]
    [MemberData(nameof(Keys))]
    public void The_korean_string_is_genuinely_translated(string key) =>
        Korean[key].Should().NotBe(English[key], $"{key} must be translated, not copied");

    /// <summary>
    /// A format placeholder that exists in one language and not the other throws at render time,
    /// and it throws in the language nobody on the team is reading.
    /// </summary>
    [Theory]
    [MemberData(nameof(Keys))]
    public void The_two_languages_take_the_same_arguments(string key)
    {
        Placeholders(Korean[key]).Should().BeEquivalentTo(
            Placeholders(English[key]),
            $"{key} is formatted with the same arguments whatever the language");
    }

    private static IEnumerable<string> Placeholders(string value) =>
        System.Text.RegularExpressions.Regex
            .Matches(value, "\\{\\d+\\}")
            .Select(m => m.Value)
            .OrderBy(v => v, StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(Keys))]
    public void No_write_string_uses_an_emoji(string key)
    {
        foreach (var value in new[] { English[key], Korean[key] })
        {
            value.EnumerateRunes()
                .Where(r => r.Value is >= 0x1F300 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF)
                .Should().BeEmpty($"{key} must use an icon or plain text, never an emoji");
        }
    }

    /// <summary>
    /// The copy is about the change, not about who proposed it.
    /// </summary>
    /// <remarks>
    /// Naming Sam on an approval control would make a refusal read as the person being unwilling,
    /// which is the same reason the rate-limit string is already forbidden from naming them.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Keys))]
    public void No_write_string_blames_the_coach(string key)
    {
        English[key].Should().NotContain("Sam");
        Korean[key].Should().NotContain("쌤");
    }

    [Fact]
    public void Every_change_kind_resolves_to_its_own_heading()
    {
        var headings = Enum.GetValues<CoachWriteChangeKind>()
            .ToDictionary(kind => kind, SamWritePresentation.HeadingKey);

        headings[CoachWriteChangeKind.Unknown].Should().Be("Coach_WriteKindUnknown");

        headings.Where(pair => pair.Key != CoachWriteChangeKind.Unknown)
            .Select(pair => pair.Value)
            .Should().OnlyHaveUniqueItems("a kind that shares another's heading is mislabelled");
    }

    /// <summary>
    /// A change kind added to the contract without a heading falls back to the neutral copy rather
    /// than printing an internal identifier at the learner.
    /// </summary>
    [Fact]
    public void An_unrecognised_change_kind_falls_back_to_neutral_copy()
    {
        SamWritePresentation.HeadingKey((CoachWriteChangeKind)9999)
            .Should().Be("Coach_WriteKindUnknown");
    }

    [Fact]
    public void An_unrecognised_stage_falls_back_to_the_unavailable_label()
    {
        SamWritePresentation.StateKey((SamWriteStage)9999)
            .Should().Be("Coach_WriteStateUnreadable");
    }
}
