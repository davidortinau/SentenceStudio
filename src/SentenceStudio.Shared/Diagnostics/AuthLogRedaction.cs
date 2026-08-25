using System.Globalization;
using System.Text;

namespace SentenceStudio.Shared.Diagnostics;

/// <summary>
/// The one place that decides how an account is named in a log line.
/// </summary>
/// <remarks>
/// <para>
/// Auth and account code writes a line on every registration, sign-in, failed sign-in, password
/// change and deletion. Put an address in those lines and the ordinary application log becomes a
/// roster of who uses the product and when — personal data at rest, retained wherever logs are
/// retained and readable by anyone who can read a log. Found by the verification gate on
/// 2026-08-19: an Aspire structured-log search for a test account's address returned it in full,
/// in both the rendered message and an <c>Email</c> attribute.
/// </para>
/// <para>
/// This lives in <c>SentenceStudio.Shared</c> rather than beside any one caller because the API,
/// the WebApp, the shared Blazor UI and the MAUI client all write these lines. A private copy per
/// project is how one of them ends up a version behind — which is the shape of the first partial
/// fix, where four call sites were masked and the rest were not.
/// </para>
/// <para>
/// Shape matches the convention <c>DataRecoveryService</c> already established
/// (<c>dav***@ortinau.com</c>): a short prefix, then the domain. Keeping the domain leaves a
/// wrong-tenant or wrong-environment mistake diagnosable; dropping the local part is what stops
/// the line identifying a person.
/// </para>
/// </remarks>
public static partial class AuthLogRedaction
{
    /// <summary>Stand-in for a value that was absent.</summary>
    public const string EmptyMarker = "(empty)";

    /// <summary>Stand-in for a value that could not be reduced to anything safe to print.</summary>
    public const string RedactedMarker = "***";

    /// <summary>Stand-in for an error list that turned out to be empty.</summary>
    public const string NoErrorsMarker = "(none)";

    /// <summary>
    /// How many leading characters of the local part survive. Three is the existing convention.
    /// </summary>
    private const int PrefixLength = 3;

    /// <summary>Upper bound on the domain we echo, so a hostile value can't pad the log.</summary>
    private const int MaxDomainLength = 64;

    /// <summary>Upper bound on how many distinct error codes one line reports.</summary>
    private const int MaxErrorCodes = 10;

    /// <summary>Upper bound on a single error code's length.</summary>
    private const int MaxErrorCodeLength = 64;

    /// <summary>
    /// Reduces an email address to a form that identifies the account to an operator without
    /// recording the learner's address.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The local part survives only when it is strictly longer than the prefix we show. A
    /// three-character local part masked to a three-character prefix is not masked at all — it is
    /// the whole thing with decoration, which is how <c>ab@example.com</c> came back as
    /// <c>ab***@example.com</c> in the first attempt.
    /// </para>
    /// <para>
    /// Everything that isn't an address — null, blank, no <c>@</c>, a leading <c>@</c>, a trailing
    /// <c>@</c>, a domain carrying characters a domain cannot carry — collapses to
    /// <see cref="RedactedMarker"/>. Echoing a value because it failed a parse is the back door
    /// through which the unmasked string reaches the log.
    /// </para>
    /// </remarks>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return EmptyMarker;
        }

        var value = email.Trim();

        // Last '@' wins: in "a@b@c.test" the local part is "a@b", and splitting on the first '@'
        // would print "a" as a prefix of a local part it isn't a prefix of.
        var at = value.LastIndexOf('@');
        if (at <= 0 || at == value.Length - 1)
        {
            // No separator, nothing before it, or nothing after it. In all three the only thing
            // present is learner-supplied text, so none of it is printable.
            return RedactedMarker;
        }

        var domain = value[at..];
        if (!IsPrintableDomain(domain))
        {
            return RedactedMarker;
        }

        if (domain.Length > MaxDomainLength)
        {
            domain = string.Concat(domain.AsSpan(0, MaxDomainLength), "...");
        }

        var local = value[..at];
        var prefix = TakeLeadingTextElements(local, PrefixLength, out var localElementCount);

        // Only reveal a prefix when there is something it is a prefix *of*.
        return localElementCount > PrefixLength
            ? $"{prefix}{RedactedMarker}{domain}"
            : $"{RedactedMarker}{domain}";
    }

    /// <summary>
    /// Masks an Identity user name. In this application the user name is the email address, so
    /// anything shaped like one is masked as one; anything else is treated as opaque and withheld.
    /// </summary>
    /// <remarks>
    /// <c>HttpContext.User.Identity.Name</c> and <c>ApplicationUser.UserName</c> both carry the
    /// address on every account created through registration. A line that logs "the user name"
    /// rather than "the email" is the same disclosure under a different label.
    /// </remarks>
    public static string MaskUserName(string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return EmptyMarker;
        }

        return userName.Contains('@', StringComparison.Ordinal)
            ? MaskEmail(userName)
            : RedactedMarker;
    }

    /// <summary>
    /// Reports whether a display name was present, without printing it.
    /// </summary>
    /// <remarks>
    /// A display name is learner-supplied and frequently a real name, so its value is never worth
    /// a log line. Whether one was set sometimes is — it distinguishes "profile linked with no
    /// name" from "profile linked". Use a profile or user id when the line needs to identify
    /// <i>which</i> account.
    /// </remarks>
    public static string DescribeDisplayName(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? "(unset)" : "(set)";

    /// <summary>
    /// Renders a bounded, deduplicated list of Identity error codes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IdentityError.Description</c> is a localized sentence built from the offending value:
    /// <c>DuplicateUserName</c> renders as <c>"User name 'someone@example.com' is already taken."</c>
    /// Logging descriptions therefore logs the address, which is how a duplicate-registration line
    /// leaked one while the neighbouring lines were masked.
    /// </para>
    /// <para>
    /// <c>Code</c> is a closed set of framework identifiers with no interpolation, so it carries
    /// the diagnostic value — <i>which</i> rule was violated — with none of the payload. It is
    /// still bounded here rather than trusted: a custom <c>IdentityErrorDescriber</c> can mint
    /// codes, and a code is only printable if it looks like an identifier.
    /// </para>
    /// </remarks>
    public static string DescribeErrorCodes(IEnumerable<string?>? codes)
    {
        if (codes is null)
        {
            return NoErrorsMarker;
        }

        var seen = new List<string>(MaxErrorCodes);
        var truncated = false;

        foreach (var code in codes)
        {
            var safe = SafeErrorCode(code);
            if (seen.Contains(safe, StringComparer.Ordinal))
            {
                continue;
            }

            if (seen.Count == MaxErrorCodes)
            {
                truncated = true;
                break;
            }

            seen.Add(safe);
        }

        if (seen.Count == 0)
        {
            return NoErrorsMarker;
        }

        var rendered = string.Join(", ", seen);
        return truncated ? rendered + ", ..." : rendered;
    }

    /// <summary>
    /// A code is printable only if it is an identifier. Anything else becomes <c>UnknownCode</c>
    /// — a custom describer must not be able to smuggle a value through the one field this class
    /// promises is bounded.
    /// </summary>
    private static string SafeErrorCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "UnknownCode";
        }

        var trimmed = code.Trim();
        if (trimmed.Length > MaxErrorCodeLength)
        {
            return "UnknownCode";
        }

        foreach (var ch in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_')
            {
                return "UnknownCode";
            }
        }

        return trimmed;
    }

    /// <summary>
    /// A domain we are willing to echo: letters, digits, dots, hyphens, underscores and the
    /// leading separator. This is deliberately narrower than the RFCs, because its job is not to
    /// validate an address — it is to guarantee that nothing we print can carry a newline, an
    /// ANSI escape, or a quotation mark into a log line.
    /// </summary>
    private static bool IsPrintableDomain(string domain)
    {
        // domain[0] is the '@' separator; a bare "@" was already rejected by the caller.
        for (var i = 1; i < domain.Length; i++)
        {
            var ch = domain[i];
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Takes the first <paramref name="count"/> text elements and reports how many the string has
    /// in total (counting stops once the answer can no longer change the caller's decision).
    /// </summary>
    /// <remarks>
    /// Slicing by <c>char</c> would split a surrogate pair or strip a combining mark off its base
    /// character, producing a mangled prefix for a Korean, emoji-bearing or accented local part.
    /// Text elements are the unit a human reads.
    /// </remarks>
    private static string TakeLeadingTextElements(string value, int count, out int totalElements)
    {
        var builder = new StringBuilder(count * 2);
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        totalElements = 0;

        while (enumerator.MoveNext())
        {
            totalElements++;
            if (totalElements <= count)
            {
                builder.Append((string)enumerator.Current);
            }
            else
            {
                // One past the prefix is all the caller needs to know it isn't the whole string.
                break;
            }
        }

        return builder.ToString();
    }
}
