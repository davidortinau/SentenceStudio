using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SentenceStudio.Domain.Entities;

namespace SentenceStudio.Infrastructure.Persistence.Configurations;

public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(tenant => tenant.Id);
        builder.Property(tenant => tenant.Name)
            .IsRequired()
            .HasMaxLength(200);
        builder.Property(tenant => tenant.CreatedAt)
            .HasDefaultValueSql("now()");
    }
}
