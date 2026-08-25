using System.Collections;
using FluentAssertions;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;

namespace SentenceStudio.UnitTests.Resources;

/// <summary>
/// Contract tests for the Learning Coach resource strings.
/// </summary>
/// <remarks>
/// These live here rather than in the UI test project because the resources belong to
/// SentenceStudio.Shared, and this project is the one CI runs. They read the compiled resource
/// sets through the ResourceManager rather than the .resx files on disk, so they validate what
/// actually ships (including the satellite assembly) and do not depend on the working directory.
/// </remarks>
public class CoachResourceStringTests
{
    private const string KeyPrefix = "Coach_";

    private static readonly CultureInfo Neutral = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Korean = new("ko");

    private static readonly Regex PlaceholderPattern = new(@"\{(\d+)\}", RegexOptions.Compiled);

    /// <summary>
    /// AppResources is internal to SentenceStudio.Shared, so the ResourceManager is reached by
    /// reflection rather than by making the type public just for a test.
    /// </summary>
    private static ResourceManager ResourceManager
    {
        get
        {
            var type = typeof(LocalizationManager).Assembly
                .GetType("SentenceStudio.Resources.Strings.AppResources", throwOnError: true)!;

            var property = type.GetProperty("ResourceManager",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

            return (ResourceManager)property.GetValue(null)!;
        }
    }

    private static Dictionary<string, string> CoachStringsFor(CultureInfo culture)
    {
        // tryParents: false so a key present only in the neutral file does NOT masquerade as a
        // translated one. That is exactly the gap a parity test has to catch.
        var set = ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        set.Should().NotBeNull($"the resource set for '{culture.Name}' must ship");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in set!)
        {
            if (entry.Key is string key && key.StartsWith(KeyPrefix, StringComparison.Ordinal)
                && entry.Value is string value)
            {
                result[key] = value;
            }
        }

        return result;
    }

    [Fact]
    public void CoachStringsExistInBothCultures()
    {
        var english = CoachStringsFor(Neutral);
        var korean = CoachStringsFor(Korean);

        english.Should().NotBeEmpty("the coach ships localized copy");

        var missingKorean = english.Keys.Except(korean.Keys).OrderBy(k => k, StringComparer.Ordinal);
        var orphanedKorean = korean.Keys.Except(english.Keys).OrderBy(k => k, StringComparer.Ordinal);

        missingKorean.Should().BeEmpty("every coach string must be translated, not silently fall back to English");
        orphanedKorean.Should().BeEmpty("a Korean-only key is a leftover from a renamed or removed string");
    }

    [Fact]
    public void CoachStringsAreNeverEmpty()
    {
        foreach (var culture in new[] { Neutral, Korean })
        {
            foreach (var (key, value) in CoachStringsFor(culture))
            {
                value.Should().NotBeNullOrWhiteSpace(
                    $"'{key}' would render as blank UI in culture '{culture.Name}'");
            }
        }
    }

    [Fact]
    public void CoachPlaceholdersMatchAcrossCultures()
    {
        var english = CoachStringsFor(Neutral);
        var korean = CoachStringsFor(Korean);

        foreach (var (key, englishValue) in english)
        {
            if (!korean.TryGetValue(key, out var koreanValue))
            {
                continue; // covered by the parity test
            }

            var englishTokens = Placeholders(englishValue);
            var koreanTokens = Placeholders(koreanValue);

            // Korean word order legitimately reorders arguments, so compare the SET of indices,
            // not their order. A missing or extra index is a FormatException waiting to happen.
            koreanTokens.Should().BeEquivalentTo(englishTokens,
                $"'{key}' must use the same format arguments in both cultures");
        }
    }

    [Fact]
    public void CoachPlaceholderIndicesAreContiguousFromZero()
    {
        foreach (var culture in new[] { Neutral, Korean })
        {
            foreach (var (key, value) in CoachStringsFor(culture))
            {
                var indices = Placeholders(value).OrderBy(i => i).ToArray();
                if (indices.Length == 0)
                {
                    continue;
                }

                indices.Should().BeEquivalentTo(Enumerable.Range(0, indices.Length),
                    $"'{key}' in culture '{culture.Name}' skips a format argument, which throws at runtime");
            }
        }
    }

    [Fact]
    public void CoachAccessibleTextDoesNotUseParentheticalPluralHacks()
    {
        // "update(s)" is unreadable in a screen reader and untranslatable into Korean, which has
        // no plural marker at all. Count-carrying strings must be phrased without it.
        foreach (var culture in new[] { Neutral, Korean })
        {
            var offenders = CoachStringsFor(culture)
                .Where(kvp => kvp.Value.Contains("(s)", StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key)
                .OrderBy(k => k, StringComparer.Ordinal);

            offenders.Should().BeEmpty($"culture '{culture.Name}' still contains a (s) plural hack");
        }
    }

    [Fact]
    public void TodaysPlanIsCapitalizedConsistentlyInEnglish()
    {
        // "Today's Plan" is the product name of the feature the coach edits, so it is capitalized
        // wherever it appears in learner-facing copy.
        var offenders = CoachStringsFor(Neutral)
            .Where(kvp => kvp.Value.Contains("today's plan", StringComparison.Ordinal))
            .Select(kvp => kvp.Key)
            .OrderBy(k => k, StringComparer.Ordinal);

        offenders.Should().BeEmpty("Today's Plan must be capitalized consistently");
    }

    [Fact]
    public void DateAndTimeFormatStringsAreValidPatternsInBothCultures()
    {
        var sampleDate = new DateTime(2026, 8, 14, 15, 45, 0, DateTimeKind.Local);

        foreach (var culture in new[] { Neutral, Korean })
        {
            var strings = CoachStringsFor(culture);

            var dateFormat = strings["Coach_EvidenceDateFormat"];
            var timeFormat = strings["Coach_RevisionTimeFormat"];

            // A translator who replaces the PATTERN with a literal date would break every
            // evidence window; formatting must produce something that varies with the input.
            var formattedDate = sampleDate.ToString(dateFormat, culture);
            var formattedTime = sampleDate.ToString(timeFormat, culture);

            formattedDate.Should().NotBeNullOrWhiteSpace();
            formattedTime.Should().NotBeNullOrWhiteSpace();

            sampleDate.AddMonths(1).ToString(dateFormat, culture)
                .Should().NotBe(formattedDate, $"'Coach_EvidenceDateFormat' in '{culture.Name}' must be a pattern, not a literal");
            sampleDate.AddHours(2).ToString(timeFormat, culture)
                .Should().NotBe(formattedTime, $"'Coach_RevisionTimeFormat' in '{culture.Name}' must be a pattern, not a literal");
        }
    }

    [Fact]
    public void DestructiveConfirmationCopyStatesWhatIsPreserved()
    {
        var english = CoachStringsFor(Neutral);

        // The learner must be told, at the moment of confirming, that the plan and progress
        // survive. Without this the dialog reads like it might undo their work.
        english["Coach_DeleteDialogBody"].Should().Contain("Today's Plan");
        english["Coach_DeleteDialogBody"].Should().Contain("not changed");

        // The static helper text under the button is a statement, not a question — the question
        // belongs in the dialog, where it has actions attached.
        english["Coach_DeleteConfirm"].Should().NotContain("?");
        english["Coach_SettingsDescription"].Should().NotContain("?");
    }

    [Fact]
    public void UnsupportedMessageCopyIsAboutTheAppVersionNotAboutLostContent()
    {
        var english = CoachStringsFor(Neutral);
        var korean = CoachStringsFor(Korean);

        // Two different facts with two different remedies. An unreadable message is gone; an
        // unsupported one arrived intact and this build cannot present it. Telling a learner their
        // message was lost when it was not is its own defect, so the two strings must not collapse
        // into each other.
        english.Should().ContainKey("Coach_UnsupportedMessage");
        korean.Should().ContainKey("Coach_UnsupportedMessage");
        english["Coach_UnsupportedMessage"].Should().NotBe(english["Coach_UnreadableMessage"]);

        // It names the remedy the learner actually has.
        english["Coach_UnsupportedMessage"].Should().Contain("app");

        // It is a statement, not a prompt: the placeholder carries no controls, so a question
        // would be asking something the learner cannot answer from there.
        english["Coach_UnsupportedMessage"].Should().NotContain("?");
    }

    private static int[] Placeholders(string value) => PlaceholderPattern
        .Matches(value)
        .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
        .Distinct()
        .ToArray();
}
