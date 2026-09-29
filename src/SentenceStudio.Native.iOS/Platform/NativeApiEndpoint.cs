using System.Reflection;

namespace SentenceStudio.Native.iOS.Platform;

public static class NativeApiEndpoint
{
    public static Uri? FromAssembly()
    {
        var value = typeof(NativeApiEndpoint).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute =>
                attribute.Key == "SentenceStudio.Native.ApiBaseUrl")?.Value;
        return string.IsNullOrWhiteSpace(value)
            ? null
            : Validate(value, allowLocalHttp: IsDebugBuild);
    }

    public static Uri Validate(string value, bool allowLocalHttp)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || endpoint.AbsolutePath != "/"
            || (endpoint.Scheme != Uri.UriSchemeHttps
                && !(allowLocalHttp
                    && endpoint.Scheme == Uri.UriSchemeHttp
                    && endpoint.IsLoopback)))
            throw new ArgumentException("The API endpoint must be a root HTTPS URL or Debug loopback HTTP URL.", nameof(value));

        return endpoint;
    }

#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif
}
