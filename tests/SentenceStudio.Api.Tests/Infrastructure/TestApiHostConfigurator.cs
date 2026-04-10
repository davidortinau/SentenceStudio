using CoreSync;
using CoreSync.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Tests.Infrastructure;

internal static class TestApiHostConfigurator
{
    public const string DummyPostgresConnectionString =
        "Host=localhost;Database=sentencestudio_test;Username=test;Password=test";

    public static void ConfigureSqliteDatabaseAndSync(IServiceCollection services, string dbPath)
    {
        services.RemoveAll<ApplicationDbContext>();
        services.RemoveAll<DbContextOptions>();
        services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
        services.RemoveAll<ISyncProvider>();

        foreach (var descriptor in services
                     .Where(descriptor =>
                         IsDbContextRegistration(descriptor)
                         || IsNpgsqlRegistration(descriptor))
                     .ToList())
        {
            services.Remove(descriptor);
        }

        services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseSqlite($"Data Source={dbPath}");
            options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        services.AddSingleton<ISyncProvider>(_ =>
        {
            var configurationBuilder = new SqliteSyncConfigurationBuilder($"Data Source={dbPath}")
                .ConfigureSyncTables();

            return new SqliteSyncProvider(configurationBuilder.Build(), ProviderMode.Remote);
        });
    }

    public static void InitializeSqliteDatabaseAndSync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();

        var syncProvider = scope.ServiceProvider.GetRequiredService<ISyncProvider>();
        syncProvider.ApplyProvisionAsync().GetAwaiter().GetResult();
    }

    private static bool IsNpgsqlRegistration(ServiceDescriptor descriptor)
    {
        return IsNpgsqlAssembly(descriptor.ServiceType.Assembly)
            || IsNpgsqlAssembly(descriptor.ImplementationType?.Assembly)
            || IsNpgsqlAssembly(descriptor.ImplementationInstance?.GetType().Assembly);
    }

    private static bool IsDbContextRegistration(ServiceDescriptor descriptor)
    {
        return descriptor.ServiceType == typeof(IDatabaseProvider)
            || (descriptor.ServiceType.IsGenericType
                && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IDbContextOptionsConfiguration<>)
                && descriptor.ServiceType.GenericTypeArguments[0] == typeof(ApplicationDbContext))
            || (descriptor.ServiceType.IsGenericType
                && descriptor.ServiceType.GenericTypeArguments.Contains(typeof(ApplicationDbContext)));
    }

    private static bool IsNpgsqlAssembly(System.Reflection.Assembly? assembly)
    {
        return assembly?.GetName().Name?.Contains(
            "Npgsql.EntityFrameworkCore.PostgreSQL",
            StringComparison.Ordinal) == true;
    }
}
