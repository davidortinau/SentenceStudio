using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SentenceStudio.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDataServices(this IServiceCollection services, string databasePath)
    {
        services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseSqlite($"Data Source={databasePath}");
            
            // Reduce EF query logging noise during development
#if DEBUG
            options.LogTo(message => System.Diagnostics.Debug.WriteLine(message), LogLevel.Warning);
#else
            options.LogTo(message => { }, LogLevel.None);
#endif
        });
        return services;
    }
}
