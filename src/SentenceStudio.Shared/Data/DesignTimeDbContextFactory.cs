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
        
        // Use a temporary in-memory database path for design-time only
        optionsBuilder.UseSqlite("Data Source=:memory:");
        
        return new ApplicationDbContext(optionsBuilder.Options);
    }
}
