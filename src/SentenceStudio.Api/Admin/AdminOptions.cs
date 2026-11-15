namespace SentenceStudio.Api.Admin;

/// <summary>Settings for the admin portal, bound from <c>Admin</c>.</summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>
    /// The addresses that may be admins (<c>Admin:Emails:0</c>, <c>Admin:Emails:1</c>, ...).
    /// </summary>
    /// <remarks>
    /// Being listed here is half of being an admin; the other half is the Admin role row in the
    /// database. Either alone gives nothing, and an empty list means nobody is an admin. The list
    /// is read on every request, so removing an address takes effect at once.
    /// </remarks>
    public List<string> Emails { get; set; } = [];

    /// <summary>
    /// The form both sides of the comparison are reduced to. The same rule Identity uses for
    /// <c>NormalizedEmail</c>, so a listed address matches the account it names and no other.
    /// </summary>
    public static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().Normalize().ToUpperInvariant();
}
