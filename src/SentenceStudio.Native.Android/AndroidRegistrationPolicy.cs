namespace SentenceStudio.Native.Android;

internal static class AndroidRegistrationPolicy
{
    public static bool CanSubmit(string? name, string? email,
        string? password, string? confirmation) =>
        !string.IsNullOrWhiteSpace(name)
        && !string.IsNullOrWhiteSpace(email)
        && SentenceStudio.Contracts.Auth.PasswordPolicy.IsSatisfied(password)
        && password == confirmation;
}
