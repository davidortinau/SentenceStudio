using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SentenceStudio.Data;

/// <summary>
/// Design-time factory for ApplicationDbContext to support EF Core migrations.
/// This is only used by dotnet ef tools, not at runtime.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        
        // Use PostgreSQL for design-time migrations (server target)
        optionsBuilder.UseNpgsql("Host=localhost;Database=sentencestudio_design;Username=postgres;Password=postgres");
        
        return new ApplicationDbContext(optionsBuilder.Options);
    }
}
