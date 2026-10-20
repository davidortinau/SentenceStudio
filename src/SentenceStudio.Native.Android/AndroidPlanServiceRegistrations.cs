using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Native.Android;

internal static class AndroidPlanServiceRegistrations
{
    internal static void AddProduction(IServiceCollection services)
    {
        services.AddScoped<DeterministicPlanBuilder>();
        services.AddSingleton<GeneratedPlanValidator>();
        services.AddScoped<IDeterministicPlanGenerator, DeterministicPlanGenerator>();
    }

    internal static void AddCommon(IServiceCollection services)
    {
        services.AddSingleton<IPlanCopyProvider, EnglishPlanCopyProvider>();
        services.AddScoped<IPlanService, PlanService>();
        services.AddScoped<UserProfileRepository>();
        services.AddScoped<LearningResourceRepository>();
        services.AddScoped<SkillProfileRepository>();
        services.AddScoped<VocabularyProgressRepository>();
    }
}
