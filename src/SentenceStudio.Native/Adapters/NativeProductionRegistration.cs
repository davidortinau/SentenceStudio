using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// Registers the production plan and quiz ports after the head has registered
/// SQLite services and AddSentenceStudioNativeSlice(). Synchronization remains
/// fail-closed until the head separately opts into owner-bound readiness.
/// </summary>
public static class NativeProductionRegistration
{
    public static IServiceCollection AddSentenceStudioNativeProductionAdapters(
        this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<
            INativePlanCreation, NativePlanCreationAdapter>());
        services.Replace(ServiceDescriptor.Singleton<
            INativeQuizAttemptCommitter, NativeQuizAttemptCommitterAdapter>());
        services.Replace(ServiceDescriptor.Scoped<
            INativeQuizChoices, NativeQuizChoicesAdapter>());
        services.Replace(ServiceDescriptor.Scoped<
            INativeQuizEligibility, NativeQuizEligibilityAdapter>());
        services.Replace(ServiceDescriptor.Singleton<
            INativeQuizGrader, NativeQuizGraderAdapter>());
        return services;
    }
}
