using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

public class VersionCheckResult
{
    public string LatestVersion { get; set; } = "";
    public string Date { get; set; } = "";
    public string Title { get; set; } = "";
}

public class VersionCheckService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<VersionCheckService>? _logger;
    private VersionCheckResult? _cachedResult;

    public bool IsUpdateAvailable { get; private set; }
    public string LatestVersion { get; private set; } = "";

    public VersionCheckService(HttpClient httpClient, ILogger<VersionCheckService>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task CheckForUpdateAsync(string currentVersion)
    {
        if (_cachedResult != null)
        {
            CompareVersions(currentVersion);
            return;
        }

        try
        {
            var result = await _httpClient.GetFromJsonAsync<VersionCheckResult>("/api/version/latest");
            if (result != null)
            {
                _cachedResult = result;
                LatestVersion = result.LatestVersion;
                CompareVersions(currentVersion);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Version check failed — will try again next launch");
        }
    }

    private void CompareVersions(string currentVersion)
    {
        if (_cachedResult == null) return;

        // Simple string comparison — versions are "Major.Minor" format
        IsUpdateAvailable = string.Compare(
            _cachedResult.LatestVersion, currentVersion, StringComparison.Ordinal) > 0;
        LatestVersion = _cachedResult.LatestVersion;
    }
}
