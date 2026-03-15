using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.DeviceId).HasMaxLength(200);
        builder.Property(u => u.Email).HasMaxLength(200);
        builder.Property(u => u.Tier).HasMaxLength(20).IsRequired();

        builder.HasIndex(u => u.DeviceId).IsUnique().HasDatabaseName("uq_users_device_id");
        builder.HasIndex(u => u.Email).IsUnique().HasDatabaseName("uq_users_email");
    }
}
