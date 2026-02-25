using Microsoft.EntityFrameworkCore;
using SentenceStudio.Domain.Entities;
using SentenceStudio.Infrastructure.Persistence.Configurations;

namespace SentenceStudio.Infrastructure.Persistence;

public sealed class ServerDbContext : DbContext
{
    public ServerDbContext(DbContextOptions<ServerDbContext> options)
        : base(options)
    {
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new TenantConfiguration());
        modelBuilder.ApplyConfiguration(new UserConfiguration());
    }
}
