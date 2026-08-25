using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The coach persona is spelled 쌤, and the resource file is where that goes wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Twice in two sessions, Korean coach copy shipped with 쌀 (U+C300,
/// "rice") in place of 쌤 (U+C324). Both times it was written by hand from a codepoint rather than
/// copied from correct copy, both times it read as plausible Korean to a non-reader, and the second
/// time a blind global fix also rewrote 쌍 ("pair") in six unrelated minimal-pair strings. Neither
/// mistake was catchable by any existing test: resource parity passed, every render assertion
/// passed, and the Korean localization tests assert on substrings that did not include the
/// persona.
/// </para>
/// <para>
/// <b>Why it is scoped, and scoped this narrowly.</b> 쌀 and 쌍 are ordinary Korean words. This app
/// teaches Korean vocabulary, so a future lesson string, minimal pair or example sentence may
/// legitimately contain either — twenty non-coach keys already contain 쌍 today. A global ban would
/// forbid real vocabulary to catch a UI typo. So the rule runs only over <c>Coach_*</c>, which is
/// persona and interface copy: by design it carries no learner terms, glosses or examples (W3
/// content-embargo, W7 no-content-on-the-card, W9 no term/gloss/answer), so a rice or a pair
/// appearing there is a misspelling of the persona and nothing else.
/// </para>
/// <para>
/// <b>What is deliberately not asserted.</b> Not "the English names Sam, so the Korean must contain
/// 쌤". Korean drops the subject freely, and two coach strings do exactly that today —
/// <c>Coach_Limitation_ExceedsSafeChangeScope</c> renders "한 번에 바꾸기에는 범위가 너무 커요."
/// for "This is more than Sam will change in one step." That is good Korean, not a defect, and a
/// rule demanding the subject would either force stilted copy or be suppressed until it meant
/// nothing. The defect being guarded is a wrong character, so the guard is about characters.
/// </para>
/// </remarks>
public class CoachPersonaCopyGuardTests
{
    private const char Persona = '\uC324';   // 쌤 — the coach
    private const char Rice = '\uC300';      // 쌀 — the twice-shipped typo
    private const char Pair = '\uC30D';      // 쌍 — the collateral of fixing it blindly

    /// <summary>
    /// Syllables that share the 쌍시옷 onset with 쌤 and have been mistaken for it in practice.
    /// </summary>
    /// <remarks>
    /// Evidence-driven, not speculative: these are the two characters that have actually been
    /// written where 쌤 was meant. Add to this list when a third one gets shipped, not before —
    /// a guard that forbids syllables nobody has ever confused only invites suppression.
    /// </remarks>
    private static readonly (char Char, string Gloss)[] Confusables =
    [
        (Rice, "rice"),
        (Pair, "pair")
    ];

    /// <summary>The keys whose persona spelling the W9 refusal depends on, named exactly.</summary>
    private static readonly string[] WithheldFamily =
    [
        "Coach_Limitation_UnverifiedClaimWithheld",
        "Coach_Limitation_UnverifiedClaimWithheldNoEvidence",
        "Coach_Announce_ClaimWithheld",
        "Coach_Announce_ClaimWithheldNoEvidence"
    ];

    [Fact]
    public void No_coach_copy_spells_the_persona_as_rice_or_a_pair()
    {
        var korean = Load("AppResources.ko.resx");
        var coach = korean.Where(entry => entry.Key.StartsWith("Coach_", StringComparison.Ordinal)).ToList();

        coach.Should().HaveCountGreaterThan(300,
            "the scan must be running over the real coach family, not an empty filter");

        var offenders = coach
            .SelectMany(entry => Confusables
                .Where(c => entry.Value.Contains(c.Char))
                .Select(c => $"{entry.Key}: '{c.Char}' ({c.Gloss}) — did you mean '{Persona}'? — {entry.Value}"))
            .ToList();

        offenders.Should().BeEmpty(
            "coach copy carries no learner vocabulary, so a rice or a pair in it is the persona "
            + "spelled wrong. Copy the character from existing coach copy rather than typing a "
            + "codepoint: 쌤 is U+C324, and U+C300 and U+C30D are its near neighbours");
    }

    [Fact]
    public void The_withheld_family_spells_the_persona_correctly()
    {
        var korean = Load("AppResources.ko.resx");

        foreach (var key in WithheldFamily)
        {
            korean.Should().ContainKey(key);
            var value = korean[key];

            value.Should().Contain(
                Persona.ToString(),
                $"{key} names the coach, and these four render in the same two slots — a learner "
                + "hearing one variant name the coach and the other not is reading an inconsistency");

            foreach (var (character, gloss) in Confusables)
            {
                value.Should().NotContain(character.ToString(), $"{key} must not say '{gloss}'");
            }
        }
    }

    [Fact]
    public void Every_coach_string_is_translated()
    {
        var english = Load("AppResources.resx");
        var korean = Load("AppResources.ko.resx");

        var coachKeys = english.Keys.Where(k => k.StartsWith("Coach_", StringComparison.Ordinal)).ToList();
        coachKeys.Should().HaveCountGreaterThan(300, "the parity check must have something to check");

        coachKeys.Where(key => !korean.ContainsKey(key)).Should().BeEmpty(
            "an untranslated coach string falls back to English mid-sentence for a Korean learner");

        coachKeys.Where(key => string.IsNullOrWhiteSpace(korean[key])).Should().BeEmpty(
            "an empty translation renders as nothing at all, which is worse than the fallback");
    }

    /// <summary>
    /// A coach key declared twice would resolve unpredictably, and the loser would be invisible.
    /// </summary>
    /// <remarks>
    /// Coach-scoped on purpose. Nine keys elsewhere in the file are declared twice today, several
    /// with different values — <c>Mastered</c> is both "Mastered" and "Mastered!", and
    /// <c>ScenarioCreated</c> exists with and without a <c>{0}</c> placeholder. That is a real
    /// defect and is reported, but it belongs to the areas that own those strings; widening this
    /// test to the whole file would make coach copy hostage to it.
    /// </remarks>
    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void No_coach_key_is_declared_twice(string fileName)
    {
        var duplicates = AllDeclaredKeys(fileName)
            .Where(key => key.StartsWith("Coach_", StringComparison.Ordinal))
            .GroupBy(key => key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} x{group.Count()}")
            .ToList();

        duplicates.Should().BeEmpty(
            $"a coach key declared twice in {fileName} resolves to whichever the loader saw last, "
            + "and the other value is unreachable and untestable");
    }

    /// <summary>
    /// The scope, proven against real data rather than asserted in a comment.
    /// </summary>
    [Fact]
    public void The_guard_leaves_real_korean_vocabulary_alone()
    {
        var korean = Load("AppResources.ko.resx");

        // Twenty-odd strings outside the coach family legitimately say 쌍 — minimal pairs are a
        // feature of this app. The rule above must not see them.
        var legitimatePairs = korean
            .Where(e => !e.Key.StartsWith("Coach_", StringComparison.Ordinal) && e.Value.Contains(Pair))
            .Select(e => e.Key)
            .ToList();

        legitimatePairs.Should().HaveCountGreaterThan(10,
            "if these ever disappear this test is no longer proving the scope is needed");
        legitimatePairs.Should().Contain("MinimalPairsTitle");

        // And the rule genuinely discriminates. Run it over a purpose-built set rather than the
        // real file, so this proves scope rather than re-proving that the file is currently clean —
        // otherwise a real typo would fail this test too and the scope claim would be untested
        // exactly when it mattered.
        var outsideTheFamily = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Vocab_ExampleRice"] = $"{Rice}\uC744 \uC0AC\uC138\uC694",   // 쌀을 사세요 — "buy rice"
            ["MinimalPair_Invented"] = $"\uC0C8 \uB300\uB9BD{Pair}",       // 새 대립쌍 — "a new minimal pair"
            ["Lesson_Grain"] = $"{Rice}\uACFC \uBCF4\uB9AC"                // 쌀과 보리 — "rice and barley"
        };

        Scan(outsideTheFamily).Should().BeEmpty(
            "a lesson about rice, or one more minimal pair, must not be a build failure");

        var insideTheFamily = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Coach_Invented"] = $"{Rice}\uC774 \uB2F5\uBCC0\uD588\uC5B4\uC694",  // 쌀이 답변했어요
            ["Coach_AlsoInvented"] = $"\uB300\uB9BD{Pair}"                            // 대립쌍
        };

        Scan(insideTheFamily).Should().HaveCount(2,
            "the same characters inside the coach family are the bug this guard exists for");
    }

    private static List<string> Scan(IReadOnlyDictionary<string, string> resources) =>
        resources
            .Where(entry => entry.Key.StartsWith("Coach_", StringComparison.Ordinal))
            .SelectMany(entry => Confusables
                .Where(c => entry.Value.Contains(c.Char))
                .Select(c => $"{entry.Key}: '{c.Char}' ({c.Gloss})"))
            .ToList();

    private static Dictionary<string, string> Load(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.Shared")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the resource files must be locatable from the test output directory");

        var path = Path.Combine(
            directory!.FullName, "src", "SentenceStudio.Shared", "Resources", "Strings", fileName);

        File.Exists(path).Should().BeTrue($"{fileName} must exist");

        // Last-wins rather than ToDictionary. The file has nine duplicate keys today, none of
        // them in the coach family, and throwing on them would make this guard depend on unrelated
        // resource hygiene it is not here to police.
        var loaded = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var data in XDocument.Load(path).Root!.Elements("data"))
        {
            if (data.Attribute("name") is { } name)
            {
                loaded[name.Value] = data.Element("value")?.Value ?? string.Empty;
            }
        }

        return loaded;
    }

    /// <summary>Every declared key in the file, duplicates included.</summary>
    private static List<string> AllDeclaredKeys(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.Shared")))
        {
            directory = directory.Parent;
        }

        var path = Path.Combine(
            directory!.FullName, "src", "SentenceStudio.Shared", "Resources", "Strings", fileName);

        return XDocument.Load(path).Root!.Elements("data")
            .Select(d => d.Attribute("name")?.Value)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToList();
    }
}
