using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SentenceStudio.WebUI.Services;

public static class BlazorUIServiceExtensions
{
    /// <summary>
    /// Registers the shared Blazor UI services used by both MAUI Hybrid and server-side Blazor hosts.
    /// </summary>
    public static IServiceCollection AddBlazorUIServices(
        this IServiceCollection services,
        bool useCircuitScopedActivityTimer = false)
    {
        services.AddSingleton<ToastService>();
        services.AddSingleton<ModalService>();
        services.AddScoped<BlazorLocalizationService>();
        services.AddSingleton<BlazorNavigationService>();
        services.AddScoped<NavigationMemoryService>();
        services.AddScoped<JsInteropService>();
        // One shared coach workspace per circuit. Scoped (not singleton) so two learners on the
        // server never share a coach session, and so the wide overlay and the /coach route are
        // compositions over the same instance.
        services.AddScoped<CoachWorkspaceState>();
        // The conversation shelf the workspace takes a thread from. Scoped for the same reason:
        // one learner's list of conversations is never another's.
        services.AddScoped<CoachFeatureFlags>();
        services.AddScoped<CoachConversationDirectory>();
        services.AddScoped<CoachMemoryDirectory>();
        // The one path that tears all of the above down when the signed-in account changes.
        // Scoped like everything it clears, and holding its own subscription to the
        // AuthenticationStateProvider, because in the MAUI BlazorWebView the scope outlives
        // sign-out and the layout that would otherwise own the subscription is rebuilt around
        // exactly the transition being watched for.
        services.AddScoped<CoachAccountBoundary>();
        // The coach's name, resolved from the language the learner is studying rather than from
        // the interface language. Scoped like everything else that belongs to one learner, and
        // registered after the boundary because it subscribes to it to re-resolve on an account
        // change. TryAdd on the source so a host with a cheaper answer can register its own first.
        services.TryAddScoped<ICoachPersonaLanguageSource, UserProfileCoachPersonaLanguageSource>();
        services.AddScoped<CoachPersona>();
        if (useCircuitScopedActivityTimer)
        {
            services.AddScoped<SentenceStudio.Services.Timer.IActivityTimerService, SentenceStudio.Services.Timer.ActivityTimerService>();
        }
        else
        {
            services.AddSingleton<SentenceStudio.Services.Timer.IActivityTimerService, SentenceStudio.Services.Timer.ActivityTimerService>();
        }
        services.AddSingleton<IImportResultStore, ImportResultStore>();
        return services;
    }
}
