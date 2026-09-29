using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Contracts.Startup;

namespace SentenceStudio.WebApp.Startup;

public static class ApiMigrationReadinessGate
{
    public const string ClientName = "ApiMigrationReadiness";

    public static void RegisterClient(IServiceCollection services, Uri apiBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        if (!apiBaseAddress.IsAbsoluteUri ||
            apiBaseAddress.Scheme is not ("https" or "http" or "https+http") ||
            !string.IsNullOrEmpty(apiBaseAddress.UserInfo))
        {
            throw new ArgumentException(
                "ApiBaseUrl must be an HTTP(S) or Aspire service-discovery address without credentials.");
        }

        // Keep the gate's own retry deadline; don't follow a redirect into a login page
        // or another API that happens to return a convincing JSON version response.
#pragma warning disable EXTEXP0001
        services.AddHttpClient(ClientName, client =>
        {
            client.BaseAddress = apiBaseAddress;
            client.Timeout = TimeSpan.FromSeconds(15);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        })
        .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
    }

    public static async Task WaitAsync(
        HttpClient api,
        Func<CancellationToken, Task<PostgresDatabaseReadiness>> getLocalReadiness,
        string expectedApplicationMigrationId,
        TimeSpan? timeout = null,
        TimeSpan? retryInterval = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(getLocalReadiness);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedApplicationMigrationId);

        // Bounds only the WebApp's readiness gate, not the API's startup work or ACA probes.
        var startupTimeout = timeout ?? TimeSpan.FromMinutes(3);
        var retryDelay = retryInterval ?? TimeSpan.FromSeconds(2);
        if (startupTimeout <= TimeSpan.Zero || retryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Startup timeout and retry interval must be positive.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(startupTimeout);
        var lastObservation = "no response from the API";

        try
        {
            while (true)
            {
                HttpResponseMessage? response = null;
                try
                {
                    response = await api.GetAsync(
                        "/api/version/info", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                }
                catch (HttpRequestException) when (!deadline.IsCancellationRequested)
                {
                    lastObservation = "API connection unavailable";
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                {
                    lastObservation = "API request timed out";
                }

                if (response is not null)
                {
                    using (response)
                    {
                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            var (incompatibility, apiIdentity) = await GetCompatibilityAsync(
                                response, expectedApplicationMigrationId, deadline.Token);
                            if (incompatibility is not null)
                            {
                                lastObservation = incompatibility;
                            }
                            else
                            {
                                lastObservation = "compatible API ready; PostgreSQL identity check did not complete";
                                try
                                {
                                    var local = await getLocalReadiness(deadline.Token);
                                    if (!PostgresDatabaseIdentity.IsCanonicalFingerprint(local.DatabaseIdentity))
                                    {
                                        throw new InvalidOperationException(
                                            "WebApp cannot start: local PostgreSQL database identity is invalid.");
                                    }
                                    if (!string.Equals(local.DatabaseIdentity, apiIdentity, StringComparison.Ordinal))
                                    {
                                        throw new InvalidOperationException(
                                            "WebApp cannot start: API PostgreSQL database identity does not match the configured WebApp database.");
                                    }

                                    if (local.PendingMigrationCount < 0)
                                    {
                                        throw new InvalidOperationException(
                                            "The application migration count cannot be negative.");
                                    }

                                    if (local.PendingMigrationCount == 0)
                                    {
                                        return;
                                    }

                                    lastObservation = $"{local.PendingMigrationCount} application migration(s) still pending";
                                }
                                catch (PostgresDatabaseIdentityException ex) when (!deadline.IsCancellationRequested)
                                {
                                    lastObservation = $"PostgreSQL database identity unavailable ({ex.Reason})";
                                }
                                catch (DbException) when (!deadline.IsCancellationRequested)
                                {
                                    lastObservation = "PostgreSQL database identity or migration history unavailable";
                                }
                                catch (TimeoutException) when (!deadline.IsCancellationRequested)
                                {
                                    lastObservation = "PostgreSQL database identity or migration history query timed out";
                                }
                                catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                                {
                                    lastObservation = "PostgreSQL database identity or migration history query timed out";
                                }
                            }
                        }
                        else if (IsRetryable(response.StatusCode))
                        {
                            lastObservation = $"API returned HTTP {(int)response.StatusCode}";
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                $"WebApp cannot start: API migration owner readiness returned HTTP {(int)response.StatusCode}.");
                        }
                    }
                }

                await Task.Delay(retryDelay, deadline.Token);
            }
        }
        catch (OperationCanceledException ex) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"WebApp cannot start: API migration owner and application schema did not become ready " +
                $"within {startupTimeout.TotalSeconds:0} seconds; last observation: {lastObservation}.",
                ex);
        }
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    private static async Task<(string? Incompatibility, string? DatabaseIdentity)> GetCompatibilityAsync(
        HttpResponseMessage response, string expectedApplicationMigrationId,
        CancellationToken cancellationToken)
    {
        if (response.Content is not { } content ||
            content.Headers.ContentType?.MediaType != "application/json")
        {
            throw new InvalidOperationException(
                "WebApp cannot start: API migration owner readiness did not return JSON.");
        }

        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var body = document.RootElement;
            if (body.ValueKind != JsonValueKind.Object ||
                string.IsNullOrWhiteSpace(ReadString(body, "version", "Version")))
            {
                throw new InvalidOperationException(
                    "WebApp cannot start: API migration owner readiness returned an invalid version response.");
            }

            var contract = ReadString(body, "applicationStartupContract", "ApplicationStartupContract");
            if (contract != ApiApplicationStartupContract.Current)
            {
                return ("API application startup contract is not compatible with this WebApp", null);
            }

            var migrationId = ReadString(body, "applicationMigrationId", "ApplicationMigrationId");
            if (migrationId != expectedApplicationMigrationId)
            {
                return ("API application migration identity does not match this WebApp", null);
            }

            var databaseIdentity = ReadString(body, "databaseIdentity", "DatabaseIdentity");
            if (!PostgresDatabaseIdentity.IsCanonicalFingerprint(databaseIdentity))
            {
                throw new InvalidOperationException(
                    "WebApp cannot start: API migration owner readiness returned an invalid databaseIdentity.");
            }

            return (null, databaseIdentity);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "WebApp cannot start: API migration owner readiness returned invalid JSON.", ex);
        }
    }

    private static string? ReadString(JsonElement body, string camelCase, string pascalCase)
    {
        if (!body.TryGetProperty(camelCase, out var value) &&
            !body.TryGetProperty(pascalCase, out value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException(
                $"WebApp cannot start: API migration owner readiness returned an invalid {camelCase}.");
        }

        return value.GetString();
    }
}
