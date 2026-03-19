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

        builder.Property(s => s.AssetSymbol).HasMaxLength(100).IsRequired();
        builder.Property(s => s.AssetDisplayName).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Type).HasMaxLength(20).IsRequired().HasDefaultValue("what_if");
        builder.Property(s => s.ExtraData).HasColumnType("jsonb");

        builder.HasOne(s => s.User)
            .WithMany(u => u.SavedScenarios)
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.Asset)
            .WithMany()
            .HasForeignKey(s => s.AssetId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => new { s.UserId, s.CreatedAt })
            .HasDatabaseName("idx_saved_scenarios_user");
    }
}
