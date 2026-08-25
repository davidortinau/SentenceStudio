using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Abstractions;
using SentenceStudio.Application;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// The data-layer registrations a container needs before <c>AddCoachReadOnlyTools</c> can resolve.
/// </summary>
/// <remarks>
/// The tools read through the application query contracts, and those alias onto the same
/// repositories the app screens use. Any test that builds a real container for the tool surface
/// therefore has to register those repositories, and every such test would otherwise repeat the
/// same six lines and the same file-system stub. Keeping it here means a test that forgets fails
/// on the missing call rather than on a DI error nobody can read.
/// </remarks>
internal static class CoachToolContainer
{
    /// <summary>
    /// Adds the repositories and the typed read contracts, matching what the API host registers.
    /// The caller still supplies <c>ApplicationDbContext</c>, the user scope, and the date context.
    /// </summary>
    public static IServiceCollection AddCoachToolDataServices(this IServiceCollection services)
    {
        services.AddSingleton<IFileSystemService, StubFileSystemService>();
        services.AddSingleton<UserProfileRepository>();
        services.AddSingleton<SkillProfileRepository>();
        services.AddSingleton<LearningResourceRepository>();
        services.AddSingleton<VocabularyProgressRepository>();
        services.AddApplicationQueries();
        return services;
    }

    /// <summary>A file system the resource repository can construct against and never reads.</summary>
    private sealed class StubFileSystemService : IFileSystemService
    {
        public string AppDataDirectory { get; } =
            Path.Combine(AppContext.BaseDirectory, "coach-tool-container-tests");

        public Task<Stream> OpenAppPackageFileAsync(string filename) =>
            throw new NotSupportedException("Coach tool container tests do not read packaged files.");
    }
}
