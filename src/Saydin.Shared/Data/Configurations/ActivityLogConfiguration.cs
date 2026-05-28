using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saydin.Shared.Constants;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class ActivityLogConfiguration : IEntityTypeConfiguration<ActivityLog>
{
    public void Configure(EntityTypeBuilder<ActivityLog> builder)
    {
        // F2.5-6 / F2.7-10 ([C-E-15], [C-G-008-4]): activity_logs.action CHECK constraint
        // kod tarafında modellenir. ActivityActions.All ile birebir aynı kalmalı.
        builder.ToTable("activity_logs", t => t.HasCheckConstraint(
            "chk_activity_action",
            $"action IN ({string.Join(", ", ActivityActions.All.Select(v => $"'{v}'"))})"));
        builder.HasKey(a => new { a.Id, a.CreatedAt });

        builder.Property(a => a.DeviceId).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Action).HasMaxLength(30).IsRequired();
        builder.Property(a => a.IpAddress).HasColumnType("inet");
        builder.Property(a => a.Country).HasMaxLength(2);
        builder.Property(a => a.City).HasMaxLength(100);
        builder.Property(a => a.DeviceOs).HasMaxLength(30);
        builder.Property(a => a.OsVersion).HasMaxLength(100);
        builder.Property(a => a.AppVersion).HasMaxLength(50);
        builder.Property(a => a.Data).HasColumnType("jsonb");
        builder.Property(a => a.StatusCode).IsRequired();
        builder.Property(a => a.ErrorCode).HasMaxLength(50);

        builder.HasOne(a => a.User)
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(a => new { a.UserId, a.CreatedAt })
            .HasDatabaseName("idx_activity_logs_user")
            .IsDescending(false, true);

        builder.HasIndex(a => new { a.Action, a.CreatedAt })
            .HasDatabaseName("idx_activity_logs_action")
            .IsDescending(false, true);

        builder.HasIndex(a => new { a.Country, a.CreatedAt })
            .HasDatabaseName("idx_activity_logs_country")
            .IsDescending(false, true);

        builder.HasIndex(a => a.Data)
            .HasDatabaseName("idx_activity_logs_asset_symbol")
            .HasMethod("GIN")
            .HasOperators("jsonb_path_ops");
    }
}
