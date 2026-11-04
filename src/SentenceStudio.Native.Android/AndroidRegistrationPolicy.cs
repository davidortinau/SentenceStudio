using SentenceStudio.Contracts.Auth;

namespace SentenceStudio.Native.Android;

/// <summary>One password checklist row: its label key, automation id suffix and state.</summary>
internal sealed record AndroidPasswordRuleRow(string LabelKey, string AutomationSuffix, bool Met);

internal static class AndroidRegistrationPolicy
{
    public static bool CanSubmit(string? name, string? email,
        string? password, string? confirmation) =>
        !string.IsNullOrWhiteSpace(name)
        && !string.IsNullOrWhiteSpace(email)
        && PasswordPolicy.IsSatisfied(password)
        && password == confirmation;

    /// <summary>
    /// Source RegisterPage: once a password is typed, every rule is listed and
    /// marked met or not (not only the unmet ones); nothing before typing.
    /// </summary>
    public static IReadOnlyList<AndroidPasswordRuleRow> PasswordRules(string? password) =>
        string.IsNullOrEmpty(password)
            ? []
            : PasswordPolicy.Rules.Select(rule => new AndroidPasswordRuleRow(
                PasswordPolicy.LabelKey(rule), PasswordPolicy.AutomationSuffix(rule),
                PasswordPolicy.IsMet(rule, password))).ToArray();
}
