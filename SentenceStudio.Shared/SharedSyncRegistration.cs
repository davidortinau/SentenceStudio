using CoreSync.Shared;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Shared
{
    public static class SharedSyncRegistration
    {
        public static void RegisterSyncEntities(this IServiceCollection services)
        {
            services.AddSyncEntity<LearningResource>();
            services.AddSyncEntity<ResourceVocabularyMapping>();
            services.AddSyncEntity<VocabularyWord>();
            // Add more entities as needed
        }
    }
}
