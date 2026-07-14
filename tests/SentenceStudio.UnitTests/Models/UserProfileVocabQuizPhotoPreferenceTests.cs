// Tests for the VocabQuizShowTextWithPhoto preference on UserProfile.
//
// Contract (approved by Captain):
//   - New bool UserProfile.VocabQuizShowTextWithPhoto defaults to false.
//   - Controls whether the target-language TERM TEXT is shown alongside
//     a photo prompt. Does NOT control photo visibility itself.
//   - Two users must retain independent values (multi-tenant).
//
// Does NOT test photo show/hide — photo behavior is UNCHANGED and out of scope.

using FluentAssertions;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Models;

public sealed class UserProfileVocabQuizPhotoPreferenceTests
{
    [Fact]
    public void NewUserProfile_VocabQuizShowTextWithPhoto_DefaultsToFalse()
    {
        var profile = new UserProfile();

        profile.VocabQuizShowTextWithPhoto.Should().BeFalse(
            "the approved contract specifies the default is false — term text is hidden when a photo is present");
    }

    [Fact]
    public void TwoProfiles_RetainIndependentVocabQuizShowTextWithPhotoValues()
    {
        var alice = new UserProfile
        {
            Id = "alice-id",
            Name = "Alice",
            VocabQuizShowTextWithPhoto = true
        };

        var bob = new UserProfile
        {
            Id = "bob-id",
            Name = "Bob",
            VocabQuizShowTextWithPhoto = false
        };

        alice.VocabQuizShowTextWithPhoto.Should().BeTrue("Alice opted in");
        bob.VocabQuizShowTextWithPhoto.Should().BeFalse("Bob kept the default");

        bob.VocabQuizShowTextWithPhoto = true;
        alice.VocabQuizShowTextWithPhoto.Should().BeTrue("Alice's value is independent of Bob's");
    }
}
