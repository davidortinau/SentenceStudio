using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Maui.Hosting;
using Plugin.Maui.HelpKit.Ingestion;
using Plugin.Maui.HelpKit.Localization;
using Plugin.Maui.HelpKit.Presenters;
using Plugin.Maui.HelpKit.RateLimit;
using Plugin.Maui.HelpKit.ShellIntegration;
using Plugin.Maui.HelpKit.Storage;
using Plugin.Maui.HelpKit.Ui;

namespace Plugin.Maui.HelpKit;

/// <summary>
/// <see cref="MauiAppBuilder"/> extensions for registering HelpKit.
/// </summary>
public static class HelpKitServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IHelpKit"/>, options, presenters, the content
    /// filter, and the AI resolver. Safe to call multiple times; subsequent
    /// calls layer additional <paramref name="configure"/> deltas over the
    /// existing options instance.
    /// </summary>
    public static MauiAppBuilder AddHelpKit(
        this MauiAppBuilder builder,
        Action<HelpKitOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;

        // Options binding
        services.AddOptions<HelpKitOptions>();
        if (configure is not null)
            services.Configure(configure);

        // If no filter was provided in options, the resolver below substitutes
        // the default redactor. This lets hosts leave ContentFilter null and
        // still get safe behavior without an explicit DI registration.
        services.TryAddSingleton<IHelpKitContentFilter>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<HelpKitOptions>>().Value;
            return opts.ContentFilter ?? new DefaultSecretRedactor();
        });

        // Concrete presenters registered so hosts can pick a specific one
        // explicitly if they need to.
        services.TryAddSingleton<ShellPresenter>();
        services.TryAddSingleton<WindowPresenter>();
        services.TryAddSingleton<MauiReactorPresenter>();

        // Default presenter resolution: DefaultPresenterSelector inspects
        // the runtime (Shell present? windows available?) per-resolution.
        // Registered Transient so the selection re-evaluates on each
        // IHelpKit.ShowAsync — apps whose Shell spins up after DI build
        // still get the Shell path once it's available. Hosts can override
        // by registering their own IHelpKitPresenter.
        services.TryAddTransient<IHelpKitPresenter>(sp => DefaultPresenterSelector.Resolve(sp));

        // AI resolver (keyed → unkeyed fallback)
        services.TryAddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<HelpKitOptions>>().Value;
            return new HelpKitAiResolver(sp, opts);
        });

        // Storage + ingestion + rate limiting + answer cache. Singletons
        // so state (connections, vector cache, per-user buckets) is shared
        // across resolutions of IHelpKit.
        services.TryAddSingleton<HelpKitDatabase>();
        services.TryAddSingleton<ConversationRepository>();
        services.TryAddSingleton<MessageRepository>();
        services.TryAddSingleton<VectorStore>();
        services.TryAddSingleton<AnswerCache>();
        services.TryAddSingleton<FileIngestionSource>();
        services.TryAddSingleton<IngestionCoordinator>();
        services.TryAddSingleton<RateLimiter>();

        // Core service
        services.TryAddSingleton<IHelpKit, HelpKitService>();

        // Localization + UI surface. Page/VM are transient so the UI gets a
        // fresh conversation list per ShowAsync().
        services.TryAddSingleton<HelpKitLocalizer>();
        services.TryAddTransient<HelpKitPageViewModel>();
        services.TryAddTransient<HelpKitPage>();

        return builder;
    }

    /// <summary>
    /// Opt-in helper that adds a Help <c>FlyoutItem</c> to the current Shell.
    /// No-op when the host is not a Shell app. This method does NOT mutate
    /// Shell implicitly — call it only if you want the flyout entry.
    /// </summary>
    public static MauiAppBuilder AddHelpKitShellFlyout(
        this MauiAppBuilder builder,
        string title = "Help",
        string? icon = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.Configure<HelpKitShellFlyoutOptions>(o =>
        {
            o.Enabled = true;
            o.Title = title;
            o.Icon = icon;
        });

        // Register the one-shot initializer that attaches to Shell at app
        // ready. Safe to call AddHelpKitShellFlyout() multiple times — only
        // one initializer runs.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IMauiInitializeService, HelpKitShellFlyoutInitializer>());

        return builder;
    }
}

/// <summary>
/// Internal marker options populated by
/// <see cref="HelpKitServiceCollectionExtensions.AddHelpKitShellFlyout"/>.
/// Kaylee's Shell integration reads this at startup.
/// </summary>
public sealed class HelpKitShellFlyoutOptions
{
    public bool Enabled { get; set; }
    public string Title { get; set; } = "Help";
    public string? Icon { get; set; }
}
