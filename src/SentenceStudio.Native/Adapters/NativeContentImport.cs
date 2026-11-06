using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Features.Import;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// Runs the shared <see cref="ContentImportService"/> (the service the web and MAUI Import
/// Content page uses) for one verified owner: its AI prompts go through the owner-bound API
/// gateway, its prompt templates come from this assembly, and its reads and writes use the
/// owner's device database (the service scopes every query to the active profile). The owner
/// must be the published device owner and the profile pointer before and after each call;
/// otherwise nothing is read or written.
/// </summary>
public sealed class NativeContentImport(
    IServiceProvider services,
    DeviceUserScopeProvider userScope,
    IPreferencesService preferences,
    NativePendingWrites pendingWrites,
    ILoggerFactory loggers,
    IOwnerBoundNativeAiGateway? ai = null) : INativeContentImport
{
    private readonly ILogger _logger = loggers.CreateLogger<NativeContentImport>();

    public async Task<ContentClassificationResult> ClassifyAsync(
        string owner, string content, CancellationToken cancellationToken)
    {
        RequireOwner(owner);
        using var scope = services.CreateScope();
        var result = await Service(scope, owner, cancellationToken)
            .ClassifyContentAsync(content, null, cancellationToken);
        RequireOwner(owner);
        return result;
    }

    public async Task<ContentImportPreview> PreviewAsync(
        string owner, ContentImportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireOwner(owner);
        using var scope = services.CreateScope();
        var import = Service(scope, owner, cancellationToken);
        var preview = await import.ParseContentAsync(request, cancellationToken);
        RequireOwner(owner);
        await import.EnrichPreviewWithDuplicateInfoAsync(preview, cancellationToken);
        RequireOwner(owner);
        return preview;
    }

    public async Task<ContentImportResult> CommitAsync(
        string owner, ContentImportCommit commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        RequireOwner(owner);
        var stamp = userScope.CaptureStamp();
        if (stamp.PublishedOwner != owner)
            throw new UnauthorizedAccessException("Content import requires the published device owner.");
        using var scope = services.CreateScope();
        var result = await Service(scope, owner, cancellationToken)
            .CommitImportAsync(commit, cancellationToken);
        // The rows are committed on this device; record them for sync like every native write.
        try
        {
            pendingWrites.MarkPending(owner);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Content import committed but sync tracking was not confirmed.");
        }
        return result;
    }

    public async Task<IReadOnlyList<NativeImportTargetResource>> ListTargetResourcesAsync(
        string owner, CancellationToken cancellationToken)
    {
        RequireOwner(owner);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.LearningResources.AsNoTracking()
            .Where(resource => resource.UserProfileId == owner && !resource.IsSmartResource)
            .Select(resource => new { resource.Id, resource.Title, resource.Language })
            .ToListAsync(cancellationToken);
        RequireOwner(owner);
        // Source: .OrderBy(r => r.Title) in memory (the default string comparer).
        return rows
            .OrderBy(row => row.Title, Comparer<string?>.Default)
            .Select(row => new NativeImportTargetResource(row.Id, row.Title ?? string.Empty, row.Language))
            .ToList();
    }

    private ContentImportService Service(
        IServiceScope scope, string owner, CancellationToken cancellationToken) =>
        new(services,
            scope.ServiceProvider.GetRequiredService<LearningResourceRepository>(),
            loggers.CreateLogger<ContentImportService>(),
            new OwnerBoundImportAi(owner, ai, IsActiveOwner, cancellationToken),
            NativeImportPrompts.Instance);

    private void RequireOwner(string owner)
    {
        if (!IsActiveOwner(owner))
        {
            _logger.LogWarning("Content import refused an absent or changed owner.");
            throw new UnauthorizedAccessException("Content import requires the current device owner.");
        }
    }

    private bool IsActiveOwner(string owner) =>
        !string.IsNullOrWhiteSpace(owner)
        && userScope.TryGetUserProfileId(out var active)
        && string.Equals(active, owner, StringComparison.Ordinal)
        && string.Equals(
            preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
            owner, StringComparison.Ordinal);
}

/// <summary>
/// The shared import service's <see cref="IAiService"/>: its text prompts go to the
/// owner-bound gateway for the import's owner. The import uses only text prompts; image and
/// speech requests are not offered here.
/// </summary>
internal sealed class OwnerBoundImportAi(
    string owner, IOwnerBoundNativeAiGateway? gateway, Func<string, bool> isActiveOwner,
    CancellationToken cancellationToken) : IAiService
{
    public async Task<T> SendPrompt<T>(string prompt, AiTier tier = AiTier.Fast,
        string? reasoningEffort = null)
    {
        if (gateway is null)
            throw new InvalidOperationException("Content import requires the owner-bound AI gateway.");
        if (reasoningEffort is not null)
            throw new NotSupportedException("Content import prompts use the tier's default effort.");
        if (!isActiveOwner(owner))
            throw new UnauthorizedAccessException("Content import AI requires the current device owner.");
        return (await gateway.SendPromptAsync<T>(owner, prompt, tier, cancellationToken))!;
    }

    public Task<string> SendImage(string imagePath, string prompt) =>
        throw new NotSupportedException("Content import does not send images.");

    public Task<Stream> TextToSpeechAsync(string text, string voice, float speed = 1.0f) =>
        throw new NotSupportedException("Content import does not synthesize speech.");
}

/// <summary>The import prompt templates (AppLib Resources/Raw), embedded in this assembly.</summary>
internal sealed class NativeImportPrompts : IFileSystemService
{
    internal static readonly NativeImportPrompts Instance = new();

    private static readonly HashSet<string> Templates = new(StringComparer.Ordinal)
    {
        "ClassifyImportContent.scriban-txt",
        "ExtractVocabularyFromPhrases.scriban-txt",
        "ExtractVocabularyFromSentences.scriban-txt",
        "ExtractVocabularyFromTranscript.scriban-txt",
        "FreeTextToVocab.scriban-txt",
        "ShareTextToVocab.scriban-txt",
        "TranslateMissingNativeTerms.scriban-txt",
    };

    public string AppDataDirectory =>
        throw new NotSupportedException("Content import reads only its prompt templates.");

    public Task<Stream> OpenAppPackageFileAsync(string filename)
    {
        if (!Templates.Contains(filename))
            throw new FileNotFoundException("Content import prompt is not embedded.", filename);
        var stream = typeof(NativeImportPrompts).Assembly.GetManifestResourceStream(
                "SentenceStudio.Native.Prompts." + filename)
            ?? throw new FileNotFoundException("Content import prompt is missing.", filename);
        return Task.FromResult(stream);
    }
}
