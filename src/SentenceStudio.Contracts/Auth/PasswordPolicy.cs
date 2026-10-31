namespace SentenceStudio.Contracts.Auth;

/// <summary>One requirement of the account password policy.</summary>
public enum PasswordRule
{
    MinimumLength,
    Uppercase,
    Lowercase,
    Digit,
    Symbol
}

/// <summary>
/// Single source of truth for account password requirements. The API and WebApp
/// Identity configuration read these constants, and every client (native iOS,
/// native Android, Blazor) evaluates the same rules for its live checklist, so
/// the checklist cannot drift from what the server enforces.
/// Character classes mirror ASP.NET Core Identity's PasswordValidator exactly
/// (ASCII letters and digits; any other character counts as a symbol).
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 8;
    public const bool RequireUppercase = true;
    public const bool RequireLowercase = true;
    public const bool RequireDigit = true;
    public const bool RequireSymbol = true;
    public const int RequiredUniqueChars = 1;

    /// <summary>Rules in display order.</summary>
    public static IReadOnlyList<PasswordRule> Rules { get; } =
    [
        PasswordRule.MinimumLength,
        PasswordRule.Uppercase,
        PasswordRule.Lowercase,
        PasswordRule.Digit,
        PasswordRule.Symbol
    ];

    public static bool IsMet(PasswordRule rule, string? password)
    {
        if (string.IsNullOrEmpty(password))
            return false;
        return rule switch
        {
            PasswordRule.MinimumLength => password.Length >= MinimumLength,
            PasswordRule.Uppercase => password.Any(IsAsciiUpper),
            PasswordRule.Lowercase => password.Any(IsAsciiLower),
            PasswordRule.Digit => password.Any(IsAsciiDigit),
            PasswordRule.Symbol => password.Any(c =>
                !IsAsciiUpper(c) && !IsAsciiLower(c) && !IsAsciiDigit(c)),
            _ => false
        };
    }

    public static bool IsSatisfied(string? password) =>
        Rules.All(rule => IsMet(rule, password));

    /// <summary>Resource key of the short checklist label for a rule.</summary>
    public static string LabelKey(PasswordRule rule) => rule switch
    {
        PasswordRule.MinimumLength => "Register_AtLeast8Chars",
        PasswordRule.Uppercase => "Register_OneUppercase",
        PasswordRule.Lowercase => "Register_OneLowercase",
        PasswordRule.Digit => "Register_OneNumber",
        PasswordRule.Symbol => "Register_OneSymbol",
        _ => throw new ArgumentOutOfRangeException(nameof(rule))
    };

    /// <summary>Stable automation-id suffix for a rule's checklist row.</summary>
    public static string AutomationSuffix(PasswordRule rule) => rule switch
    {
        PasswordRule.MinimumLength => "length",
        PasswordRule.Uppercase => "uppercase",
        PasswordRule.Lowercase => "lowercase",
        PasswordRule.Digit => "number",
        PasswordRule.Symbol => "symbol",
        _ => throw new ArgumentOutOfRangeException(nameof(rule))
    };

    /// <summary>Resource key of the sentence shown when the server rejects a password for a rule.</summary>
    public static string ServerErrorKey(PasswordRule rule) => rule switch
    {
        PasswordRule.MinimumLength => "PasswordPolicy_Error_Length",
        PasswordRule.Uppercase => "PasswordPolicy_Error_Uppercase",
        PasswordRule.Lowercase => "PasswordPolicy_Error_Lowercase",
        PasswordRule.Digit => "PasswordPolicy_Error_Digit",
        PasswordRule.Symbol => "PasswordPolicy_Error_Symbol",
        _ => throw new ArgumentOutOfRangeException(nameof(rule))
    };

    /// <summary>Generic key used when the server reports an unmapped password error.</summary>
    public const string GenericServerErrorKey = "PasswordPolicy_Error_Generic";

    /// <summary>Maps an ASP.NET Core Identity error code to a policy rule, if it is one.</summary>
    public static PasswordRule? RuleForIdentityErrorCode(string? code) => code switch
    {
        "PasswordTooShort" => PasswordRule.MinimumLength,
        "PasswordRequiresUpper" => PasswordRule.Uppercase,
        "PasswordRequiresLower" => PasswordRule.Lowercase,
        "PasswordRequiresDigit" => PasswordRule.Digit,
        "PasswordRequiresNonAlphanumeric" => PasswordRule.Symbol,
        _ => null
    };

    /// <summary>
    /// Resource key for a set of Identity error codes, or null when none concern the password.
    /// Other "Password*" codes (for example unique characters) fall back to the generic key.
    /// </summary>
    public static string? ServerErrorKeyFor(IEnumerable<string>? identityErrorCodes)
    {
        if (identityErrorCodes is null)
            return null;
        var codes = identityErrorCodes.Where(c => c is not null).ToList();
        foreach (var code in codes)
            if (RuleForIdentityErrorCode(code) is { } rule)
                return ServerErrorKey(rule);
        return codes.Any(c => c.StartsWith("Password", StringComparison.Ordinal))
            ? GenericServerErrorKey
            : null;
    }

    private static bool IsAsciiUpper(char c) => c is >= 'A' and <= 'Z';
    private static bool IsAsciiLower(char c) => c is >= 'a' and <= 'z';
    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';
}
