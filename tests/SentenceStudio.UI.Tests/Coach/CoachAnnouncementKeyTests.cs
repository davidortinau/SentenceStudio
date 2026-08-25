using System.Globalization;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Every announcement key the state machine can emit must actually resolve to copy in both
/// shipped cultures. A typo here is silent: the live region would announce the raw key, or
/// nothing at all, and only a screen-reader user would notice.
/// </summary>
public class CoachAnnouncementKeyTests
{
    private static readonly CultureInfo[] Cultures = [new("en"), new("ko")];

    [Fact]
    public void EveryAnnouncementKeyResolvesInEveryCulture()
    {
        foreach (var state in Enum.GetValues<CoachUiState>())
        {
            var key = CoachStateMachine.AnnouncementKey(state);
            if (key is null)
            {
                continue;
            }

            foreach (var culture in Cultures)
            {
                var value = LocalizationManager.Instance.GetString(key, culture);

                // GetString returns the key itself when the resource is missing.
                value.Should().NotBe(key,
                    $"'{key}' (state {state}) has no copy in culture '{culture.Name}'");
                value.Should().NotBeNullOrWhiteSpace();
            }
        }
    }

    [Fact]
    public void AnnouncementKeysNeverCarryUnfilledPlaceholders()
    {
        // The live region renders the key directly, with no format arguments, so a placeholder
        // would be read aloud as a literal brace.
        foreach (var state in Enum.GetValues<CoachUiState>())
        {
            var key = CoachStateMachine.AnnouncementKey(state);
            if (key is null)
            {
                continue;
            }

            foreach (var culture in Cultures)
            {
                LocalizationManager.Instance.GetString(key, culture)
                    .Should().NotContain("{0}", $"'{key}' is announced without format arguments");
            }
        }
    }

    [Fact]
    public void OnlyTheSilentEntryStatesHaveNoAnnouncement()
    {
        var silent = Enum.GetValues<CoachUiState>()
            .Where(s => CoachStateMachine.AnnouncementKey(s) is null)
            .ToArray();

        silent.Should().BeEquivalentTo([CoachUiState.Opening, CoachUiState.Ready]);
    }
}
