using Microsoft.Extensions.Configuration;
using System.Reflection;

namespace SentenceStudio;

public static class ConfigurationExtensions
{
    public static void AddEmbeddedAppSettings(this IConfigurationBuilder configuration)
    {
        var assembly = typeof(SentenceStudioAppBuilder).Assembly;

        // Load base configuration (localhost fallback)
        using var baseStream = assembly.GetManifestResourceStream("SentenceStudio.appsettings.json");
        if (baseStream == null)
            throw new InvalidOperationException("Embedded appsettings.json not found in SentenceStudio.AppLib.");
        configuration.AddJsonStream(baseStream);

        // Load environment-specific overrides: Production (Azure) for Release, Development for Debug
#if DEBUG
        const string envName = "Development";
#else
        const string envName = "Production";
#endif
        using var envStream = assembly.GetManifestResourceStream($"SentenceStudio.appsettings.{envName}.json");
        if (envStream != null)
            configuration.AddJsonStream(envStream);

        // Highest-precedence overrides via environment variables. Lets any build (including
        // Debug) be pointed at a different API without rebuilding config, e.g.:
        //   services__api__https__0=https://api.<env>.azurecontainerapps.io dotnet run ...
        // which service discovery resolves for the "https+http://api" client base address.
        configuration.AddEnvironmentVariables();
    }
}
