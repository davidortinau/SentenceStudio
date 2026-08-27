using CoreSync;
using CoreSync.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Tests.Infrastructure;

internal static class TestApiHostConfigurator
{
    public const string DummyPostgresConnectionString =
        "Host=localhost;Database=sentencestudio_test;Username=test;Password=test";

    /// <summary>
    /// Config key that gates the real <c>IChatClient</c> registration in <c>Program.cs</c>.
    /// Named constant so the test-side and production-side conditions stay greppable together.
    /// </summary>
    public const string AiEndpointConfigKey = "AI:OpenAI:Endpoint";

    /// <summary>
    /// Registers <see cref="UnconfiguredAiChatClient"/> as <see cref="IChatClient"/> when the host
    /// has no AI endpoint configured, so DI validation can complete and the host can boot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is opt-in per factory rather than folded into <see cref="ConfigureSqliteDatabaseAndSync"/>
    /// on purpose. Every factory calls the SQLite configurator first and registers its own doubles
    /// afterwards; if the stub rode along with it, the stub would always land <i>before</i> those
    /// doubles and a <c>TryAdd</c> would then win over the real fake. Keeping it separate means a
    /// factory that supplies its own client simply never calls this.
    /// </para>
    /// <para>Two independent guards keep it from masking a deliberate replacement:</para>
    /// <list type="number">
    ///   <item>It no-ops when <see cref="AiEndpointConfigKey"/> is set — a host wired to an
    ///   endpoint gets whatever <c>Program.cs</c> builds.</item>
    ///   <item>It uses <c>TryAddSingleton</c>, so an <c>IChatClient</c> already in the collection
    ///   (from <c>Program.cs</c>, or from a factory that registered a fake first) always wins.</item>
    /// </list>
    /// </remarks>
    public static void AddStubChatClientWhenAiUnconfigured(
        IServiceCollection services,
        IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration[AiEndpointConfigKey]))
        {
            return;
        }

        services.TryAddSingleton<IChatClient>(new UnconfiguredAiChatClient());
    }

    public static void ConfigureSqliteDatabaseAndSync(
        IServiceCollection services,
        string dbPath,
        IInterceptor? interceptor = null)
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
            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }
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
