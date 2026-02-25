using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SentenceStudio.Domain.Entities;

namespace SentenceStudio.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(user => user.Id);
        builder.Property(user => user.DisplayName)
            .IsRequired()
            .HasMaxLength(200);
        builder.Property(user => user.CreatedAt)
            .HasDefaultValueSql("now()");
    }
}
