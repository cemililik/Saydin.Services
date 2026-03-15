using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class SavedScenarioConfiguration : IEntityTypeConfiguration<SavedScenario>
{
    public void Configure(EntityTypeBuilder<SavedScenario> builder)
    {
        builder.ToTable("saved_scenarios");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Quantity).HasColumnType("numeric(18,8)").IsRequired();
        builder.Property(s => s.QuantityUnit).HasMaxLength(20).IsRequired();
        builder.Property(s => s.Label).HasMaxLength(200);

        builder.HasOne(s => s.User)
            .WithMany(u => u.SavedScenarios)
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.Asset)
            .WithMany()
            .HasForeignKey(s => s.AssetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => new { s.UserId, s.CreatedAt })
            .HasDatabaseName("idx_saved_scenarios_user");
    }
}
