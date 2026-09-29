using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Abstractions;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Operations.Handlers;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Data;
using SentenceStudio.Services;

namespace SentenceStudio.Api.Tests.Coach.Operations;

public sealed class CoachYouTubeImportDependencyInjectionTests
{
    [Fact]
    public void Registered_import_handler_activates_with_interface_only_youtube_service()
    {
        var handlerRegistration = new ServiceCollection()
            .AddCoachReadOnlyTools()
            .Single(descriptor =>
                descriptor.ServiceType == typeof(ICoachWriteHandler)
                && descriptor.ImplementationType == typeof(CoachYouTubeImportHandler));

        IServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite("Data Source=:memory:"));
        services.AddSingleton<IFileSystemService>(new NoDiskFileSystem());
        services.AddSingleton<LearningResourceRepository>();
        services.AddScoped<CoachWriteOwnership>();
        services.AddSingleton<AudioAnalyzer>();
        services.AddSingleton<IYouTubeImportService, YouTubeImportService>();
        services.Add(handlerRegistration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICoachWriteHandler>()
            .Should().BeOfType<CoachYouTubeImportHandler>();
        scope.ServiceProvider.GetService<YouTubeImportService>().Should().BeNull();
    }

    private sealed class NoDiskFileSystem : IFileSystemService
    {
        public string AppDataDirectory => AppContext.BaseDirectory;

        public Task<Stream> OpenAppPackageFileAsync(string filename) =>
            throw new NotSupportedException("Dependency injection validation must not read package files.");
    }
}
