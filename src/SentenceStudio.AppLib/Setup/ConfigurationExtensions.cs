using Microsoft.Extensions.Configuration;
using System.Reflection;

namespace SentenceStudio;

public static class ConfigurationExtensions
{
    public static void AddEmbeddedAppSettings(this IConfigurationBuilder configuration)
    {
        var assembly = typeof(SentenceStudioAppBuilder).Assembly;
        using var stream = assembly.GetManifestResourceStream("SentenceStudio.appsettings.json");
        if (stream == null)
            throw new InvalidOperationException("Embedded appsettings.json not found in SentenceStudio.AppLib.");

        configuration.AddJsonStream(stream);
    }
}
