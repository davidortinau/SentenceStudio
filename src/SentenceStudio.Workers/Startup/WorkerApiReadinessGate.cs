using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Contracts.Startup;

namespace SentenceStudio.Workers.Startup;

internal static class WorkerApiReadinessGate
{
    internal const string ClientName = "WorkerApiMigrationReadiness";
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(5);

    internal static void RegisterClient(IServiceCollection services, Uri apiBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        if (!apiBaseAddress.IsAbsoluteUri ||
            apiBaseAddress.Scheme is not ("https" or "http" or "https+http") ||
            !string.IsNullOrEmpty(apiBaseAddress.UserInfo))
        {
            throw new ArgumentException("ApiBaseUrl must be an HTTP(S) or Aspire service-discovery address without credentials.");
        }

        // Service defaults supply discovery, but their automatic retries would outlive this
        // gate's deadline. Redirects and cookies could also make a login page look like readiness.
#pragma warning disable EXTEXP0001
        services.AddHttpClient(ClientName, client =>
        {
            client.BaseAddress = apiBaseAddress;
            client.Timeout = TimeSpan.FromSeconds(10);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        })
        .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
    }

    internal static async Task WaitAsync(
        HttpClient api,
        Func<CancellationToken, Task<PostgresDatabaseReadiness>> getLocalReadiness,
        string expectedApplicationMigrationId,
        TimeSpan? timeout = null,
        TimeSpan? initialRetryDelay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(getLocalReadiness);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedApplicationMigrationId);

        var startupTimeout = timeout ?? DefaultTimeout;
        var retryDelay = initialRetryDelay ?? InitialRetryDelay;
        if (startupTimeout <= TimeSpan.Zero || startupTimeout > DefaultTimeout || retryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout),
                "Worker startup timeout must be positive and at most three minutes; retry delay must be positive.");
        }

        retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds, MaximumRetryDelay.TotalMilliseconds));
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
                            var apiIdentity = await GetCompatibleIdentityAsync(
                                response, expectedApplicationMigrationId, deadline.Token);
                            try
                            {
                                var local = await getLocalReadiness(deadline.Token);
                                if (!PostgresDatabaseIdentity.IsCanonicalFingerprint(local.DatabaseIdentity))
                                {
                                    throw new InvalidOperationException(
                                        "Workers cannot start: local PostgreSQL database identity is invalid.");
                                }
                                if (!string.Equals(local.DatabaseIdentity, apiIdentity, StringComparison.Ordinal))
                                {
                                    throw new InvalidOperationException(
                                        "Workers cannot start: API PostgreSQL database identity does not match the configured Workers database.");
                                }

                                if (local.PendingMigrationCount < 0)
                                {
                                    throw new InvalidOperationException(
                                        "Workers cannot start: application migration count cannot be negative.");
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
                        else if (IsRetryable(response.StatusCode))
                        {
                            lastObservation = $"API returned HTTP {(int)response.StatusCode}";
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                $"Workers cannot start: API migration owner readiness returned HTTP {(int)response.StatusCode}.");
                        }
                    }
                }

                await Task.Delay(retryDelay, deadline.Token);
                retryDelay = NextRetryDelay(retryDelay);
            }
        }
        catch (OperationCanceledException ex) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Workers cannot start: API migration owner and application schema did not become ready " +
                $"within {startupTimeout.TotalSeconds:0.##} seconds; last observation: {lastObservation}.",
                ex);
        }
    }

    internal static TimeSpan NextRetryDelay(TimeSpan previous) =>
        TimeSpan.FromMilliseconds(Math.Min(previous.TotalMilliseconds * 2, MaximumRetryDelay.TotalMilliseconds));

    private static bool IsRetryable(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    private static async Task<string> GetCompatibleIdentityAsync(
        HttpResponseMessage response, string expectedApplicationMigrationId, CancellationToken cancellationToken)
    {
        if (response.Content is not { } content ||
            content.Headers.ContentType?.MediaType != "application/json")
        {
            throw new InvalidOperationException("Workers cannot start: API migration owner readiness did not return JSON.");
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
                    "Workers cannot start: API migration owner readiness returned an invalid version response.");
            }

            var contract = ReadString(body, "applicationStartupContract", "ApplicationStartupContract");
            if (contract is null)
            {
                throw new InvalidOperationException(
                    "Workers cannot start: API migration owner readiness returned an invalid applicationStartupContract.");
            }
            if (contract != ApiApplicationStartupContract.Current)
            {
                throw new InvalidOperationException(
                    "Workers cannot start: API application startup contract is not compatible with these Workers.");
            }

            var migrationId = ReadString(body, "applicationMigrationId", "ApplicationMigrationId");
            if (migrationId is null)
            {
                throw new InvalidOperationException(
                    "Workers cannot start: API migration owner readiness returned an invalid applicationMigrationId.");
            }
            if (migrationId != expectedApplicationMigrationId)
            {
                throw new InvalidOperationException(
                    "Workers cannot start: API application migration identity does not match these Workers.");
            }

            var databaseIdentity = ReadString(body, "databaseIdentity", "DatabaseIdentity");
            if (!PostgresDatabaseIdentity.IsCanonicalFingerprint(databaseIdentity))
            {
                throw new InvalidOperationException(
                    "Workers cannot start: API migration owner readiness returned an invalid databaseIdentity.");
            }

            return databaseIdentity!;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Workers cannot start: API migration owner readiness returned invalid JSON.", ex);
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
                $"Workers cannot start: API migration owner readiness returned an invalid {camelCase}.");
        }

        return value.GetString();
    }
}
