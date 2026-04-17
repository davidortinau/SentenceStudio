using System.ComponentModel;

namespace Plugin.Maui.HelpKit;

/// <summary>
/// Configures <see cref="IHelpKit"/> and supporting services. All properties
/// have sensible Alpha defaults; only <see cref="ContentDirectories"/> is
/// typically required for a useful experience.
/// </summary>
public sealed class HelpKitOptions
{
    /// <summary>
    /// Paths (relative to <c>FileSystem.AppPackageDirectory</c> / MauiAsset
    /// layout, or absolute) where HelpKit looks for Markdown sources to
    /// ingest. Empty by default.
    /// </summary>
    public IList<string> ContentDirectories { get; } = new List<string>();

    /// <summary>
    /// How long to retain chat history. Use <see cref="TimeSpan.MaxValue"/>
    /// to keep forever. Default: 30 days.
    /// </summary>
    public TimeSpan HistoryRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Per-session rate limit. Default: 10 questions per minute.
    /// </summary>
    public int MaxQuestionsPerMinute { get; set; } = 10;

    /// <summary>
    /// Optional resolver that returns the current user id. Used to scope
    /// chat history in multi-profile apps. Return <c>null</c> for a
    /// single-user app.
    /// </summary>
    public Func<IServiceProvider, string?>? CurrentUserProvider { get; set; }

    /// <summary>
    /// Ingestion-time filter applied to every chunk before embedding. When
    /// <c>null</c>, <see cref="DefaultSecretRedactor"/> is registered.
    /// </summary>
    public IHelpKitContentFilter? ContentFilter { get; set; }

    /// <summary>
    /// UI chrome language. Alpha supports <c>"en"</c> and <c>"ko"</c>.
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Keyed-DI discriminator HelpKit uses to resolve
    /// <see cref="Microsoft.Extensions.AI.IChatClient"/> and
    /// <see cref="Microsoft.Extensions.AI.IEmbeddingGenerator{TInput, TEmbedding}"/>.
    /// Falls back to unkeyed registration if no keyed service is present.
    /// </summary>
    public string HelpKitServiceKey { get; set; } = "helpkit";

    /// <summary>
    /// Number of retrieved chunks per query. Default: 5.
    /// </summary>
    public int RetrievalTopK { get; set; } = 5;

    /// <summary>
    /// Optional override for the cosine similarity threshold. When
    /// <c>null</c>, HelpKit looks up a per-embedding-model default from
    /// its internal table.
    /// </summary>
    public double? SimilarityThresholdOverride { get; set; }

    /// <summary>
    /// Whether to cache answers keyed by
    /// <c>hash(normalized_query + top-K chunk ids)</c>. Default: true.
    /// </summary>
    public bool EnableAnswerCache { get; set; } = true;

    /// <summary>
    /// How long cached answers remain valid. Default: 7 days.
    /// </summary>
    public TimeSpan AnswerCacheTtl { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Absolute storage path. When <c>null</c>, HelpKit uses
    /// <c>{FileSystem.AppDataDirectory}/helpkit/</c>.
    /// </summary>
    public string? StoragePath { get; set; }
}
