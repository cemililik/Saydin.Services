using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saydin.Shared.Constants;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class ActivityLogConfiguration : IEntityTypeConfiguration<ActivityLog>
{
    public void Configure(EntityTypeBuilder<ActivityLog> builder)
    {
        // The action allowlist is enforced by the scheduler-owned invoker trigger
        // introduced in migration 023. EF must not resurrect its dropped CHECK.
        builder.ToTable("activity_logs", table =>
        {
            table.HasCheckConstraint(
                "chk_activity_data_size",
                "data IS NULL OR pg_column_size(data) <= 10000");
        });
        builder.HasKey(a => new { a.Id, a.CreatedAt });

        // LOGR-007: ActivityLogLimits constants tek source-of-truth. Sabitler değişirse
        // hem migration hem bu konfigürasyon güncellenmelidir.
        builder.Property(a => a.DeviceId).HasMaxLength(ActivityLogLimits.DeviceIdMaxLength).IsRequired();
        builder.Property(a => a.Action).HasMaxLength(ActivityLogLimits.ActionMaxLength).IsRequired();
        builder.Property(a => a.IpAddress).HasColumnType("inet");
        builder.Property(a => a.Country).HasMaxLength(ActivityLogLimits.CountryMaxLength);
        builder.Property(a => a.City).HasMaxLength(ActivityLogLimits.CityMaxLength);
        builder.Property(a => a.DeviceOs).HasMaxLength(ActivityLogLimits.DeviceOsMaxLength);
        builder.Property(a => a.OsVersion).HasMaxLength(ActivityLogLimits.OsVersionMaxLength);
        builder.Property(a => a.AppVersion).HasMaxLength(ActivityLogLimits.AppVersionMaxLength);
        builder.Property(a => a.Data).HasColumnType("jsonb");
        builder.Property(a => a.StatusCode).IsRequired();
        // LOGR-024: DurationMs migration 011 ile BIGINT — EF tarafında da explicit.
        builder.Property(a => a.DurationMs).HasColumnType("bigint");
        builder.Property(a => a.ErrorCode).HasMaxLength(ActivityLogLimits.ErrorCodeMaxLength);

        builder.HasOne(a => a.User)
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.UserId, a.CreatedAt })
            .HasDatabaseName("idx_activity_logs_user")
            .IsDescending(false, true);

        builder.HasIndex(a => new { a.Action, a.CreatedAt })
            .HasDatabaseName("idx_activity_logs_action")
            .IsDescending(false, true);

        builder.HasIndex(a => new { a.Country, a.CreatedAt })
            .HasDatabaseName("idx_activity_logs_country")
            .IsDescending(false, true);

        // SHRD-013: GIN index adı kolonu (data JSONB) yansıtır — migration 011 ile rename.
        builder.HasIndex(a => a.Data)
            .HasDatabaseName("idx_activity_logs_data_gin")
            .HasMethod("GIN")
            .HasOperators("jsonb_path_ops");
    }
}
