using System.Globalization;
using System.Security.Claims;
using SentenceStudio.Contracts;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The coach's name follows the language the learner is studying, not the language the interface
/// is drawn in.
/// </summary>
/// <remarks>
/// <para>
/// Reported by Captain on 2026-08-20: a learner studying Korean with an English interface was
/// introduced to "Sam". Their teacher is 쌤. The name was being read from the UI culture, which
/// meant it changed whenever the reader changed the interface language — a thing that does not
/// happen to a person — and was wrong for the majority case, an English-speaking learner studying
/// Korean.
/// </para>
/// <para>
/// These pin both halves: the language a name is chosen from, and the fact that changing the
/// interface language does not rename anybody.
/// </para>
/// </remarks>
public class CoachPersonaTests
{
    private sealed class FixedLanguageSource(string? language, int throwCount = 0)
        : ICoachPersonaLanguageSource
    {
        private int _remainingThrows = throwCount;

        public int Calls { get; private set; }

        public Task<string?> GetStudyLanguageAsync(CancellationToken cancellationToken = default)
        {
            Calls++;

            if (_remainingThrows > 0)
            {
                _remainingThrows--;
                throw new InvalidOperationException("profile unavailable");
            }

            return Task.FromResult(language);
        }
    }

    /// <summary>
    /// A real boundary over fakes, so the persona is driven by the same event the shipped code
    /// subscribes to rather than by a hand-raised one.
    /// </summary>
    private static CoachAccountBoundary NewBoundary()
    {
        var client = new FakeCoachApiClient();
        var directory = new CoachConversationDirectory(client);

        return new CoachAccountBoundary(
            new CoachWorkspaceState(client, directory),
            directory,
            new CoachFeatureFlags(client),
            new CoachMemoryDirectory(client));
    }

    private static ClaimsPrincipal Learner(string profileId, string email) =>
        new(new ClaimsIdentity(
            [
                new Claim(AuthClaimTypes.UserProfileId, profileId),
                new Claim("sub", profileId),
                new Claim(ClaimTypes.Email, email)
            ],
            authenticationType: "jwt"));

    // ================================================================ the language map

    [Theory]
    [InlineData("Korean")]
    [InlineData("korean")]
    [InlineData("ko")]
    [InlineData("ko-KR")]
    [InlineData("ko_KR")]
    [InlineData("kor")]
    [InlineData("한국어")]
    public void AKoreanStudyLanguageNamesTheCoachInKorean(string studyLanguage)
    {
        CoachPersonaLanguages.ResolvePersonaCulture(studyLanguage)
            .TwoLetterISOLanguageName.Should().Be("ko");
    }

    [Theory]
    [InlineData("English")]
    [InlineData("Spanish")]
    [InlineData("German")]
    [InlineData("es-MX")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EveryOtherStudyLanguageFallsBackToTheDefaultMarket(string? studyLanguage)
    {
        CoachPersonaLanguages.ResolvePersonaCulture(studyLanguage)
            .TwoLetterISOLanguageName.Should().Be("en",
                "a language with no persona of its own gets the default name, not the reader's");

        CoachPersonaLanguages.HasDedicatedPersona(studyLanguage).Should().BeFalse();
    }

    [Fact]
    public void AMultiLanguageProfileIsNamedAfterItsPrimaryLanguage()
    {
        // UserProfile.TargetLanguagesList is comma-separated and its first entry is primary.
        CoachPersonaLanguages.ResolvePersonaCulture("Korean,German,Spanish")
            .TwoLetterISOLanguageName.Should().Be("ko");

        CoachPersonaLanguages.ResolvePersonaCulture("German,Korean")
            .TwoLetterISOLanguageName.Should().Be("en");
    }

    // ================================================================ the resolved name

    [Fact]
    public async Task AKoreanLearnerIsIntroducedTo쌤()
    {
        var persona = new CoachPersona(new FixedLanguageSource("Korean"));

        await persona.RefreshAsync();

        persona.DisplayName.Should().Be("쌤");
    }

    [Fact]
    public async Task AnEnglishLearnerIsIntroducedToSam()
    {
        var persona = new CoachPersona(new FixedLanguageSource("English"));

        await persona.RefreshAsync();

        persona.DisplayName.Should().Be("Sam");
    }

    /// <summary>The reported case: English interface, Korean study language.</summary>
    [Fact]
    public async Task AnEnglishInterfaceStudyingKoreanStillNames쌤()
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("en");

        try
        {
            var persona = new CoachPersona(new FixedLanguageSource("Korean"));
            await persona.RefreshAsync();

            persona.DisplayName.Should().Be("쌤",
                "the coach belongs to the language being learned, not to the chrome");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>And the mirror image, so the rule is a rule and not a Korean special case.</summary>
    [Fact]
    public async Task AKoreanInterfaceStudyingEnglishStillNamesSam()
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("ko");

        try
        {
            var persona = new CoachPersona(new FixedLanguageSource("English"));
            await persona.RefreshAsync();

            persona.DisplayName.Should().Be("Sam");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void BeforeAnyProfileIsReadTheDefaultNameIsUsedRatherThanABlank()
    {
        var persona = new CoachPersona();

        persona.DisplayName.Should().Be("Sam");
        persona.HasResolved.Should().BeFalse();
    }

    // ================================================================ change notification

    [Fact]
    public async Task ChangingTheStudyLanguageRenamesTheCoachAndNotifies()
    {
        var persona = new CoachPersona(new FixedLanguageSource("English"));
        await persona.RefreshAsync();

        var notifications = 0;
        persona.Changed += () => notifications++;

        persona.ApplyStudyLanguage("Korean");

        persona.DisplayName.Should().Be("쌤");
        notifications.Should().Be(1);
    }

    [Fact]
    public async Task ReapplyingTheSameLanguageDoesNotNotify()
    {
        var persona = new CoachPersona(new FixedLanguageSource("Korean"));
        await persona.RefreshAsync();

        var notifications = 0;
        persona.Changed += () => notifications++;

        persona.ApplyStudyLanguage("Korean");
        persona.ApplyStudyLanguage("ko-KR");

        notifications.Should().Be(0, "a re-render for a name that did not change is noise");
    }

    [Fact]
    public void AProfileReadThatFailsKeepsTheNameItAlreadyHad()
    {
        var persona = new CoachPersona(new FixedLanguageSource("Korean"));
        persona.ApplyStudyLanguage("Korean");

        var failing = new CoachPersona(new FixedLanguageSource("Korean", throwCount: 1));

        var act = async () => await failing.RefreshAsync();

        act.Should().NotThrowAsync();
        failing.DisplayName.Should().Be("Sam", "an unread profile is not evidence of a rename");
        persona.DisplayName.Should().Be("쌤");
    }

    // ================================================================ account changes

    [Fact]
    public async Task SigningInAsSomebodyElseReReadsTheStudyLanguage()
    {
        var source = new FixedLanguageSource("Korean");
        var boundary = NewBoundary();
        boundary.Apply(Learner("profile-a", "a@example.test"));
        var persona = new CoachPersona(source, boundary);

        await persona.RefreshAsync();
        persona.DisplayName.Should().Be("쌤");
        var callsBefore = source.Calls;

        boundary.Apply(Learner("profile-b", "b@example.test"));

        // The re-read is fired and not awaited, so give the continuation a turn to land.
        await Task.Delay(50);

        source.Calls.Should().BeGreaterThan(callsBefore,
            "the next learner's study language is theirs, not the previous learner's");
    }

    [Fact]
    public async Task SigningOutDropsTheNameBackToTheDefault()
    {
        var source = new FixedLanguageSource("Korean");
        var boundary = NewBoundary();
        boundary.Apply(Learner("profile-a", "a@example.test"));
        var persona = new CoachPersona(source, boundary);

        await persona.RefreshAsync();
        persona.DisplayName.Should().Be("쌤");

        boundary.Apply(principal: null);
        await Task.Delay(50);

        persona.DisplayName.Should().Be("Sam",
            "a signed-out shell names nobody's teacher");
        persona.StudyLanguage.Should().BeNull();
    }

    [Fact]
    public async Task DisposingStopsListeningToTheBoundary()
    {
        var source = new FixedLanguageSource("Korean");
        var boundary = NewBoundary();
        boundary.Apply(Learner("profile-a", "a@example.test"));
        var persona = new CoachPersona(source, boundary);

        await persona.RefreshAsync();
        persona.Dispose();
        var callsBefore = source.Calls;

        boundary.Apply(Learner("profile-c", "c@example.test"));
        await Task.Delay(50);

        source.Calls.Should().Be(callsBefore);
    }
}
