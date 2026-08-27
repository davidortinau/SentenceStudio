using System.Globalization;
using System.Xml.Linq;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The coach is a person named Sam (ko: 쌤). These pin the identity so it cannot quietly
/// regress to the role noun "coach", and so the name is never hardcoded in one language.
/// </summary>
/// <remarks>
/// Session and history management keep the functional noun on purpose — "End coach session",
/// "Delete coach history" describe a feature, not something Sam does. That split is asserted
/// here too, so a future well-meaning find-and-replace does not erase it.
/// </remarks>
public class CoachIdentityResourceTests
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
        var doc = XDocument.Load(path);

        return doc.Root!
            .Elements("data")
            .Where(d => d.Attribute("name") is not null)
            .GroupBy(d => d.Attribute("name")!.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> English => Load("AppResources.resx");
    private static Dictionary<string, string> Korean => Load("AppResources.ko.resx");

    /// <summary>Keys where the learner is addressed by, or about, the coach as a person.</summary>
    private static readonly string[] IdentityKeys =
    [
        "Coach_Title",
        "Coach_TitleShort",
        "Coach_RoleCoach",
        "Coach_CoachTab",
        "Coach_Open",
        "Coach_ComposerLabel",
        "Coach_ConversationLabel",
        "Coach_Incomplete",
        "Coach_AnnounceClarification",
        "Coach_SettingsSection",
        "Coach_SettingsDescription",
        "Coach_SettingsUnavailable"
    ];

    public static TheoryData<string> PersonalIdentityKeys()
    {
        var data = new TheoryData<string>();
        foreach (var key in IdentityKeys)
        {
            data.Add(key);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PersonalIdentityKeys))]
    public void TheEnglishStringUsesTheName(string key)
    {
        English.Should().ContainKey(key);
        English[key].Should().Contain("Sam", "the coach is named, not described by role");
    }

    [Theory]
    [MemberData(nameof(PersonalIdentityKeys))]
    public void TheKoreanStringUsesTheLocalizedName(string key)
    {
        Korean.Should().ContainKey(key);
        Korean[key].Should().Contain("쌤", "the name is localized, not transliterated or left as 코치");
    }

    [Theory]
    [MemberData(nameof(PersonalIdentityKeys))]
    public void TheKoreanStringDropsTheRoleNoun(string key)
    {
        // 학습 코치 쌤 introduces the persona in Settings and is the one allowed pairing.
        if (key is "Coach_SettingsSection")
        {
            return;
        }

        Korean[key].Should().NotContain("코치",
            "once the coach has a name, the role noun is redundant in conversation");
    }

    [Fact]
    public void TheChatSpeakerLabelsAreJustTheTwoPeople()
    {
        English["Coach_RoleCoach"].Should().Be("Sam");
        English["Coach_RoleYou"].Should().Be("You");
        Korean["Coach_RoleCoach"].Should().Be("쌤");
        Korean["Coach_RoleYou"].Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SessionAndHistoryManagementKeepTheFunctionalNoun()
    {
        // These name the feature, not the person. "Sam history" would be worse copy.
        English["Coach_EndSession"].Should().Contain("coach session");
        English["Coach_SettingsDelete"].Should().Contain("coach history");
        English["Coach_DeleteDialogTitle"].Should().Contain("coach history");
    }

    [Fact]
    public void TheRateLimitDoesNotBlameSam()
    {
        // A quota is a system limit. Attributing it to Sam makes the person look unwilling.
        English["Coach_Limited"].Should().NotContain("Sam");
        Korean["Coach_Limited"].Should().NotContain("쌤");
    }

    [Fact]
    public void TheNameIsNotHardcodedInTheComponents()
    {
        var ui = Path.Combine(RepoRoot, "src", "SentenceStudio.UI");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ui, "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            // The name must arrive through resources so Korean gets 쌤, never "Sam".
            foreach (var line in text.Split('\n'))
            {
                if (line.Contains("\"Sam\"", StringComparison.Ordinal)
                    || line.Contains(">Sam<", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty("the persona name is localized, never written into markup");
    }

    [Fact]
    public void EveryIdentityKeyIsTranslatedDifferentlyFromEnglish()
    {
        foreach (var key in IdentityKeys)
        {
            Korean[key].Should().NotBe(English[key],
                $"{key} must be genuinely translated, not copied");
        }
    }

    [Fact]
    public void IdentityKeysCarryATranslatorNoteAboutTheName()
    {
        var path = Path.Combine(RepoRoot, "src", "SentenceStudio.Shared", "Resources", "Strings", "AppResources.resx");
        var doc = XDocument.Load(path);

        var missing = new List<string>();

        foreach (var key in new[] { "Coach_Title", "Coach_TitleShort", "Coach_RoleCoach", "Coach_Open" })
        {
            var comment = doc.Root!
                .Elements("data")
                .FirstOrDefault(d => d.Attribute("name")?.Value == key)?
                .Element("comment")?.Value ?? string.Empty;

            if (!comment.Contains("Sam", StringComparison.OrdinalIgnoreCase))
            {
                missing.Add(key);
            }
        }

        missing.Should().BeEmpty("a translator seeing only \"Sam\" needs to know it is a name");
    }

    [Fact]
    public void NoIdentityStringUsesAnEmoji()
    {
        foreach (var key in IdentityKeys)
        {
            foreach (var value in new[] { English[key], Korean[key] })
            {
                var runes = value.EnumerateRunes()
                    .Where(r => r.Value is >= 0x1F300 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF)
                    .ToList();

                runes.Should().BeEmpty(
                    $"{key} must read as plain text in {CultureInfo.InvariantCulture.Name} and every other culture");
            }
        }
    }

    // ================================================================ the parameterized sentences

    /// <summary>
    /// Keys that carry the persona's name as a placeholder rather than as literal text.
    /// </summary>
    /// <remarks>
    /// Added 2026-08-20 with the change that made the name follow the language being studied. The
    /// name and the sentence around it come from two different cultures now — English chrome with a
    /// Korean target language reads "Ask 쌤" — so any string that mentions the coach by name inside
    /// a sentence has to take it as an argument instead of baking it in.
    /// </remarks>
    private static readonly string[] NamePlaceholderKeys =
    [
        "Coach_OpenNamed",
        "Coach_ConversationLabelNamed",
        "Coach_ComposerLabelNamed"
    ];

    public static TheoryData<string> ParameterizedIdentityKeys()
    {
        var data = new TheoryData<string>();
        foreach (var key in NamePlaceholderKeys)
        {
            data.Add(key);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ParameterizedIdentityKeys))]
    public void TheSentenceTakesTheNameAsAnArgumentInBothLanguages(string key)
    {
        English.Should().ContainKey(key);
        Korean.Should().ContainKey(key);

        English[key].Should().Contain("{0}",
            "the name comes from the study language, so it cannot be part of the sentence");
        Korean[key].Should().Contain("{0}");
    }

    [Theory]
    [MemberData(nameof(ParameterizedIdentityKeys))]
    public void TheSentenceNeverHardcodesAPersonaName(string key)
    {
        foreach (var value in new[] { English[key], Korean[key] })
        {
            value.Should().NotContain("Sam",
                $"{key} is used for every market; a name in it would be wrong in most of them");
            value.Should().NotContain("쌤");
        }
    }

    [Theory]
    [MemberData(nameof(ParameterizedIdentityKeys))]
    public void TheKoreanSentenceIsTranslatedAndKeepsTheRoleNounOut(string key)
    {
        Korean[key].Should().NotBe(English[key], $"{key} must be genuinely translated, not copied");
        Korean[key].Should().NotContain("코치",
            "once the coach has a name, the role noun is redundant in conversation");
    }

    /// <summary>
    /// The name itself is read from <c>Coach_RoleCoach</c> in the study language's culture, so that
    /// key has to exist and be a bare name in every culture that ships one.
    /// </summary>
    [Fact]
    public void ThePersonaNameKeyIsABareNameInEveryShippedCulture()
    {
        foreach (var (culture, resources) in new[]
                 {
                     ("en", English),
                     ("ko", Korean)
                 })
        {
            resources.Should().ContainKey("Coach_RoleCoach");

            var name = resources["Coach_RoleCoach"];
            name.Should().NotBeNullOrWhiteSpace();
            name.Should().NotContain(" ",
                $"the {culture} persona name is projected as a speaker label and a heading, so it "
                + "has to be a name and not a phrase");
            name.Should().NotContain("{0}", "the name is the leaf, never a format string");
        }
    }
}
