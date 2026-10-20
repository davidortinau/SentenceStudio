using System.Globalization;
using System.Resources;

namespace SentenceStudio.Native.iOS.Platform;

internal static class NativeLocalizedText
{
    // LocalizationManager substitutes a missing key with its name.
    private static readonly ResourceManager SharedResources = new(
        "SentenceStudio.Resources.Strings.AppResources",
        typeof(LocalizationManager).Assembly);

    internal static string Get(string key, CultureInfo culture)
    {
        if (SharedResources.GetString(key, culture) is null)
            throw new InvalidOperationException(
                $"Missing shared localization resource: {key}");

        return LocalizationManager.Instance.GetString(key, culture);
    }
}
