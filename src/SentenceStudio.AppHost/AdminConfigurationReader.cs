using Microsoft.Extensions.Configuration;

namespace SentenceStudio.AppHost;

/// <summary>
/// Pure-function reader for the admin portal values the AppHost forwards to the API.
/// </summary>
internal static class AdminConfigurationReader
{
    /// <summary>Maximum indexed entries scanned in <c>Admin:Emails:N</c>.</summary>
    internal const int MaxEmails = 16;

    /// <summary>
    /// Reads every nonblank <c>Admin:Emails:N</c> entry and returns the API environment variables
    /// for them, compacted to consecutive indices. Nothing configured returns nothing, which
    /// leaves the API with an empty list and therefore no admins.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ReadApiEnvironment(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var environmentVariables = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < MaxEmails; i++)
        {
            var email = configuration[$"Admin:Emails:{i}"]?.Trim();
            if (string.IsNullOrWhiteSpace(email) || !seen.Add(email))
            {
                continue;
            }

            environmentVariables[$"Admin__Emails__{environmentVariables.Count}"] = email;
        }

        return environmentVariables;
    }
}
