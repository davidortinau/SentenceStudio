namespace SentenceStudio.Native.Android;

internal static class AndroidRegistrationPolicy
{
    public static bool CanSubmit(string? name, string? email,
        string? password, string? confirmation) =>
        !string.IsNullOrWhiteSpace(name)
        && !string.IsNullOrWhiteSpace(email)
        && password is { Length: >= 8 }
        && password.Any(char.IsUpper)
        && password.Any(char.IsDigit)
        && password == confirmation;
}
