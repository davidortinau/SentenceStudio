using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace SentenceStudio;

public static class ConfigurationExtensions
{
    private const string ReferenceHeadResource = "SentenceStudio.reference-head.generated.json";

    public static void AddEmbeddedAppSettings(this IConfigurationBuilder configuration)
    {
        var assembly = typeof(SentenceStudioAppBuilder).Assembly;

        using var baseStream = assembly.GetManifestResourceStream("SentenceStudio.appsettings.json");
        using var referenceHeadStream = assembly.GetManifestResourceStream(ReferenceHeadResource);
        if (baseStream is null && referenceHeadStream is null)
            throw new InvalidOperationException(
                "No embedded base appsettings.json or generated reference-head configuration was found. "
                + "For standalone reference heads, generate a config with scripts/run-reference-head.py "
                + "and rebuild with -p:ReferenceHeadConfig=<absolute generated JSON path>. "
                + "Otherwise copy appsettings.template.json to src/SentenceStudio.AppLib/appsettings.json "
                + "and rebuild.");

        if (baseStream is not null)
            configuration.AddJsonStream(baseStream);

#if DEBUG
        const string envName = "Development";
#else
        const string envName = "Production";
#endif
        using var envStream = assembly.GetManifestResourceStream($"SentenceStudio.appsettings.{envName}.json");
        if (envStream != null)
            configuration.AddJsonStream(envStream);

        configuration.AddEnvironmentVariables();

        if (referenceHeadStream is not null)
        {
            using var buffer = new MemoryStream();
            referenceHeadStream.CopyTo(buffer);
            var json = buffer.ToArray();
            var endpoint = ValidateReferenceHeadConfig(json);

            // The packaged tunnel must win over embedded defaults AND a stale host environment.
            using var overlay = new MemoryStream(json);
            configuration.AddJsonStream(overlay);

            if (configuration is IConfiguration effectiveConfiguration)
            {
                var https = effectiveConfiguration.GetSection("Services:api:https");
                var endpoints = https.GetChildren().ToArray();
                if (https.Value is not null ||
                    endpoints.Length != 1 ||
                    endpoints[0].Key != "0" ||
                    !string.Equals(endpoints[0].Value, endpoint, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Standalone reference-head configuration requires exactly one effective "
                        + "Services:api:https endpoint. Remove extra endpoint overrides before launching.");
                }
            }
        }
    }

    private static string ValidateReferenceHeadConfig(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("Services", out var services) ||
                services.ValueKind != JsonValueKind.Object ||
                services.EnumerateObject().Count() != 1 ||
                !services.TryGetProperty("api", out var api) ||
                api.ValueKind != JsonValueKind.Object ||
                api.EnumerateObject().Count() != 1 ||
                !api.TryGetProperty("https", out var https) ||
                https.ValueKind != JsonValueKind.Array ||
                https.GetArrayLength() != 1 ||
                https[0].ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    "Generated reference-head configuration must contain only "
                    + "Services:api:https with one public HTTPS tunnel URL.");
            }

            var endpoint = https[0].GetString();
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !uri.Host.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase) ||
                !uri.IsDefaultPort ||
                uri.AbsolutePath != "/" ||
                uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 ||
                uri.Fragment.Length != 0)
            {
                throw new InvalidOperationException(
                    "Generated reference-head configuration has no valid public HTTPS dev tunnel endpoint.");
            }

            return endpoint!;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Generated reference-head configuration is not valid JSON.", exception);
        }
    }
}
