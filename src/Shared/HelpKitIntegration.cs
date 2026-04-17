#if NET11_0_OR_GREATER
// HelpKit integration for SentenceStudio.
//
// Dormant under net10 TFMs. Compiles and wires when the MAUI head project
// targets net11.0-* AND has a ProjectReference to Plugin.Maui.HelpKit.
//
// TFM tension: SentenceStudio dev builds target net10, while HelpKit is
// net11-only (per Zoe's locked API contract). Captain's iOS Release publish
// workflow switches global.json to net11 preview; that is the path under
// which this file activates today. See
// .squad/decisions/inbox/wash-helpkit-ss-integration.md for the full
// diagnosis and the options to unblock everyday dev builds.

using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenAI;
using Plugin.Maui.HelpKit;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio;

/// <summary>
/// Additive HelpKit integration hook for SentenceStudio MAUI heads.
/// Call from <c>MauiProgram.CreateMauiApp</c> after
/// <c>UseSentenceStudioApp()</c>.
/// </summary>
public static class HelpKitIntegration
{
    /// <summary>
    /// Registers HelpKit options, services, and a matching
    /// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> pulled from the
    /// same OpenAI client SentenceStudio already uses. The existing unkeyed
    /// <see cref="IChatClient"/> registration is reused via
    /// HelpKit's keyed-DI-then-unkeyed-fallback resolver; no duplicate chat
    /// client is created here.
    /// </summary>
    public static MauiAppBuilder UseHelpKit(this MauiAppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Help content is copied from the MAUI app package to AppDataDirectory
        // on first run (see InitializeHelpContentAsync below). HelpKit ingests
        // from that directory so files stay writable/diffable per install.
        var helpRoot = Path.Combine(FileSystem.AppDataDirectory, "sentencestudio-help");

        builder.AddHelpKit(options =>
        {
            options.ContentDirectories.Add(helpRoot);

            // SentenceStudio ships English and Korean UIs. Honor whichever
            // the user selected (key matches existing app convention).
            options.Language = Preferences.Get("AppLanguage", "en");

            // Scope history to the active user profile so switching profiles
            // does not leak another learner's questions. Synchronous lookup
            // uses the same preference key UserProfileRepository writes on
            // login (ActiveProfileIdKey = "active_profile_id").
            options.CurrentUserProvider = _ =>
            {
                var id = Preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty);
                return string.IsNullOrWhiteSpace(id) ? null : id;
            };

            options.HistoryRetention = TimeSpan.FromDays(30);
            options.MaxQuestionsPerMinute = 10;
        });

        // ---- IEmbeddingGenerator registration ----
        // SentenceStudio already registers IChatClient via the OpenAI client.
        // HelpKit needs an embedding generator too. We build one from the
        // same API key used by the chat client so Captain does not manage a
        // second credential.
        //
        // TODO(Captain): confirm the embedding deployment on your account.
        // text-embedding-3-small is the standard OpenAI default — cheap,
        // 1536 dims, plenty for in-app help retrieval. Swap here if you
        // prefer text-embedding-3-large or a custom deployment.
        const string embeddingModel = "text-embedding-3-small";

        builder.Services.TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var openAiApiKey = (DeviceInfo.Idiom == DeviceIdiom.Desktop)
                ? Environment.GetEnvironmentVariable("AI__OpenAI__ApiKey")
                : sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>()?
                    .GetSection("Settings").Get<Settings>()?.OpenAIKey;

            if (string.IsNullOrWhiteSpace(openAiApiKey))
            {
                sp.GetService<ILoggerFactory>()?
                    .CreateLogger("HelpKitIntegration")
                    .LogWarning("OpenAI API key not found — HelpKit embedding generator will fail on ingest.");
            }

            return new OpenAIClient(openAiApiKey)
                .GetEmbeddingClient(embeddingModel)
                .AsIEmbeddingGenerator();
        });

        // Optional keyed alias. HelpKit's resolver prefers keyed("helpkit")
        // then falls back to unkeyed, so this is belt-and-suspenders for a
        // future day when SentenceStudio hosts more than one IChatClient.
        builder.Services.AddKeyedSingleton<IChatClient>("helpkit",
            (sp, _) => sp.GetRequiredService<IChatClient>());
        builder.Services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>("helpkit",
            (sp, _) => sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());

        // SentenceStudio is Blazor Hybrid — there is no MAUI Shell flyout
        // to attach to. The host triggers help from a Razor button that
        // injects IHelpKit and calls ShowAsync(). Do NOT call
        // AddHelpKitShellFlyout — it is a no-op without Shell and noise
        // in the options graph.

        return builder;
    }

    /// <summary>
    /// Copies bundled help markdown from the MAUI app package to the
    /// per-install AppDataDirectory so HelpKit ingest can read + watch it.
    /// Idempotent: only copies files that are missing. Existing files are
    /// preserved so per-install edits (if any) are not clobbered.
    /// </summary>
    public static async Task InitializeHelpContentAsync(ILogger? logger = null)
    {
        var destRoot = Path.Combine(FileSystem.AppDataDirectory, "sentencestudio-help");
        Directory.CreateDirectory(destRoot);

        // Paths are relative to Resources/Raw/ in the MAUI package.
        // Keep this list in sync with files under
        // src/SentenceStudio.AppLib/Resources/Raw/sentencestudio-help/.
        var helpFiles = new[]
        {
            "getting-started.md",
            "dashboard.md",
            "user-profile.md",
            "sync-and-offline.md",
            "settings.md",
            "activities/cloze.md",
            "activities/writing.md",
            "activities/translation.md",
            "activities/vocabulary.md",
            "activities/word-association.md",
            "activities/conversation.md",
        };

        foreach (var rel in helpFiles)
        {
            var dest = Path.Combine(destRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            if (File.Exists(dest)) continue;

            try
            {
                await using var src = await FileSystem.OpenAppPackageFileAsync(
                    $"sentencestudio-help/{rel}");
                await using var dst = File.Create(dest);
                await src.CopyToAsync(dst);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to stage HelpKit content file {Path}", rel);
            }
        }
    }

    /// <summary>
    /// Fire-and-forget kick of <see cref="IHelpKit.IngestAsync"/> after content
    /// staging. Call from app startup; errors are logged, never thrown —
    /// help is a secondary feature and must not block boot.
    /// </summary>
    public static void TriggerBackgroundIngest(IServiceProvider services)
    {
        _ = Task.Run(async () =>
        {
            var logger = services.GetService<ILoggerFactory>()?.CreateLogger("HelpKitIngest");
            try
            {
                await InitializeHelpContentAsync(logger);
                var helpKit = services.GetService<IHelpKit>();
                if (helpKit is null)
                {
                    logger?.LogWarning("IHelpKit not registered; skipping ingest.");
                    return;
                }
                await helpKit.IngestAsync();
                logger?.LogInformation("HelpKit ingest completed.");
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "HelpKit ingest failed (non-fatal).");
            }
        });
    }
}
#endif
