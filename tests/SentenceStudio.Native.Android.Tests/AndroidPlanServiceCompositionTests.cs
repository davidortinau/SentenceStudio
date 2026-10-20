using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidPlanServiceCompositionTests
{
    [Fact]
    public void ProductionPlanGraphResolvesInDistinctValidatedScopes()
    {
        using var provider = CreateProductionServices().BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var firstGenerator = firstScope.ServiceProvider
            .GetRequiredService<IDeterministicPlanGenerator>();
        var secondGenerator = secondScope.ServiceProvider
            .GetRequiredService<IDeterministicPlanGenerator>();
        var firstBuilder = firstScope.ServiceProvider
            .GetRequiredService<DeterministicPlanBuilder>();
        var secondBuilder = secondScope.ServiceProvider
            .GetRequiredService<DeterministicPlanBuilder>();
        var firstRepository = firstScope.ServiceProvider
            .GetRequiredService<UserProfileRepository>();
        var secondRepository = secondScope.ServiceProvider
            .GetRequiredService<UserProfileRepository>();
        var firstDatabase = firstScope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();
        var secondDatabase = secondScope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();
        var firstPlan = firstScope.ServiceProvider.GetRequiredService<IPlanService>();
        var secondPlan = secondScope.ServiceProvider.GetRequiredService<IPlanService>();

        Assert.Same(firstGenerator, firstScope.ServiceProvider
            .GetRequiredService<IDeterministicPlanGenerator>());
        Assert.Same(firstBuilder, Dependency<DeterministicPlanBuilder>(
            firstGenerator, "_builder"));
        Assert.Same(secondBuilder, Dependency<DeterministicPlanBuilder>(
            secondGenerator, "_builder"));
        Assert.Same(firstRepository, Dependency<UserProfileRepository>(
            firstBuilder, "_userProfileRepo"));
        Assert.Same(secondRepository, Dependency<UserProfileRepository>(
            secondBuilder, "_userProfileRepo"));
        Assert.Same(firstScope.ServiceProvider, Dependency<IServiceProvider>(
            firstRepository, "_serviceProvider"));
        Assert.Same(secondScope.ServiceProvider, Dependency<IServiceProvider>(
            secondRepository, "_serviceProvider"));
        Assert.Same(firstDatabase, Dependency<ApplicationDbContext>(
            firstPlan, "_db"));
        Assert.Same(secondDatabase, Dependency<ApplicationDbContext>(
            secondPlan, "_db"));
        Assert.Same(firstGenerator, Dependency<IDeterministicPlanGenerator>(
            firstPlan, "_deterministic"));
        Assert.Same(secondGenerator, Dependency<IDeterministicPlanGenerator>(
            secondPlan, "_deterministic"));
        Assert.NotSame(firstGenerator, secondGenerator);
        Assert.NotSame(firstBuilder, secondBuilder);
        Assert.NotSame(firstRepository, secondRepository);
        Assert.NotSame(firstDatabase, secondDatabase);
    }

    [Fact]
    public void PreviousSingletonPlanGraphIsRejectedByScopeValidation()
    {
        var services = CreateProductionServices();
        services.Replace(ServiceDescriptor.Singleton<DeterministicPlanBuilder,
            DeterministicPlanBuilder>());
        services.Replace(ServiceDescriptor.Singleton<IDeterministicPlanGenerator,
            DeterministicPlanGenerator>());

        var exception = Assert.Throws<AggregateException>(() =>
            services.BuildServiceProvider(
                new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
        Assert.Contains("Cannot consume scoped service", exception.ToString());
        Assert.Contains(nameof(UserProfileRepository), exception.ToString());
    }

    private static ServiceCollection CreateProductionServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite("Data Source=:memory:"));
        services.AddSingleton<IPreferencesService>(new UnusedPreferences());
        services.AddSingleton<IFileSystemService>(new UnusedFiles());
        services.AddSingleton<DeviceUserScopeProvider>();
        services.AddSingleton<IUserScopeProvider>(provider =>
            provider.GetRequiredService<DeviceUserScopeProvider>());
        services.AddTransient<IPlanDateContext>(_ =>
            new PlanDateContext(TimeZoneInfo.Local));
        AndroidPlanServiceRegistrations.AddProduction(services);
        AndroidPlanServiceRegistrations.AddCommon(services);
        return services;
    }

    private static T Dependency<T>(object service, string name)
    {
        var field = service.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"Missing {service.GetType().Name}.{name} dependency.");
        return Assert.IsAssignableFrom<T>(field.GetValue(service));
    }

    private sealed class UnusedPreferences : IPreferencesService
    {
        public T Get<T>(string key, T defaultValue) => defaultValue;
        public void Set<T>(string key, T value) => throw new NotSupportedException();
        public void Remove(string key) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
    }

    private sealed class UnusedFiles : IFileSystemService
    {
        public string AppDataDirectory => throw new NotSupportedException();
        public Task<Stream> OpenAppPackageFileAsync(string filename) =>
            throw new NotSupportedException();
    }
}
