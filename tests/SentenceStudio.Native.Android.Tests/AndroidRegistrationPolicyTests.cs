using SentenceStudio.Native.Android;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidRegistrationPolicyTests
{
    [Theory]
    [InlineData("", "learner@example.test", "Secure123!", "Secure123!")]
    [InlineData("Learner", "", "Secure123!", "Secure123!")]
    [InlineData("Learner", "learner@example.test", "short1A", "short1A")]
    [InlineData("Learner", "learner@example.test", "Throwaway9Pass", "Throwaway9Pass")]
    [InlineData("Learner", "learner@example.test", "alllowercase123", "alllowercase123")]
    [InlineData("Learner", "learner@example.test", "ALLUPPERCASE", "ALLUPPERCASE")]
    [InlineData("Learner", "learner@example.test", "Secure123!", "Wrong123!")]
    public void InvalidOrMismatchedEntryCannotSubmit(
        string name, string email, string password, string confirmation)
    {
        Assert.False(AndroidRegistrationPolicy.CanSubmit(
            name, email, password, confirmation));
    }

    [Fact]
    public void PasswordChecklistListsEveryRuleWithItsStateOnceTyping()
    {
        Assert.Empty(AndroidRegistrationPolicy.PasswordRules(null));
        Assert.Empty(AndroidRegistrationPolicy.PasswordRules(string.Empty));

        // Source RegisterPage: met rules stay listed (text-success), not only the unmet ones.
        var rules = AndroidRegistrationPolicy.PasswordRules("abc");
        Assert.Equal(SentenceStudio.Contracts.Auth.PasswordPolicy.Rules.Count, rules.Count);
        Assert.Equal(["length", "uppercase", "lowercase", "number", "symbol"],
            rules.Select(rule => rule.AutomationSuffix));
        Assert.True(rules.Single(rule => rule.AutomationSuffix == "lowercase").Met);
        Assert.False(rules.Single(rule => rule.AutomationSuffix == "length").Met);
        Assert.Equal("Register_OneLowercase",
            rules.Single(rule => rule.AutomationSuffix == "lowercase").LabelKey);

        Assert.All(AndroidRegistrationPolicy.PasswordRules("Secure123!"),
            rule => Assert.True(rule.Met));
    }

    [Fact]
    public void ValidAccountEntryCanSubmit()
    {
        Assert.True(AndroidRegistrationPolicy.CanSubmit(
            "Learner", "learner@example.test", "Secure123!", "Secure123!"));
    }
}
