using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SentenceStudio.Api.Admin.Persistence;

namespace SentenceStudio.Api.Admin;

public static class AdminServiceCollectionExtensions
{
    /// <summary>
    /// Registers the admin portal's gate, its password-again proof and its audit log.
    /// </summary>
    /// <remarks>
    /// Nothing here makes anyone an admin. With no <c>Admin:Emails</c> configured the routes exist
    /// and answer 404 to everybody.
    /// </remarks>
    public static IServiceCollection AddAdminPortal(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AdminOptions>().Bind(configuration.GetSection(AdminOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);

        services.AddAuthorizationBuilder().AddPolicy(AdminPolicy.Name, AdminPolicy.Build());
        services.AddScoped<AdminAccessEvaluator>();
        services.AddSingleton<IAuthorizationHandler, AdminAuthorizationHandler>();

        // Replace, not add: there is one result handler per host, and this one defers to the
        // default for every policy but the admin one.
        services.Replace(ServiceDescriptor.Singleton<
            IAuthorizationMiddlewareResultHandler, AdminNotFoundAuthorizationResultHandler>());

        services.AddSingleton<AdminStepUpService>();
        services.AddScoped<AdminPasswordCheck>();

        // Own migrations history table, and no PendingModelChangesWarning suppression: see AdminDbContext.
        services.AddDbContext<AdminDbContext>(options =>
        {
            var connectionString =
                configuration.GetConnectionString("admin")
                ?? configuration.GetConnectionString("sentencestudio");

            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(AdminSchema.MigrationsHistoryTable));
        });

        services.AddScoped<AdminAuditWriter>();

        return services;
    }

    /// <summary>
    /// Adds the middleware that guards and audits every admin route. Must come after
    /// <c>UseAuthorization</c>, which is what admits the caller it records.
    /// </summary>
    public static IApplicationBuilder UseAdminPortal(this IApplicationBuilder app) =>
        app.UseMiddleware<AdminRequestMiddleware>();
}
