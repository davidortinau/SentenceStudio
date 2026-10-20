using SentenceStudio.Native.Android;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidRegistrationPolicyTests
{
    [Theory]
    [InlineData("", "learner@example.test", "Secure123", "Secure123")]
    [InlineData("Learner", "", "Secure123", "Secure123")]
    [InlineData("Learner", "learner@example.test", "short1A", "short1A")]
    [InlineData("Learner", "learner@example.test", "alllowercase123", "alllowercase123")]
    [InlineData("Learner", "learner@example.test", "ALLUPPERCASE", "ALLUPPERCASE")]
    [InlineData("Learner", "learner@example.test", "Secure123", "Wrong123")]
    public void InvalidOrMismatchedEntryCannotSubmit(
        string name, string email, string password, string confirmation)
    {
        Assert.False(AndroidRegistrationPolicy.CanSubmit(
            name, email, password, confirmation));
    }

    [Fact]
    public void ValidAccountEntryCanSubmit()
    {
        Assert.True(AndroidRegistrationPolicy.CanSubmit(
            "Learner", "learner@example.test", "Secure123", "Secure123"));
    }
}
