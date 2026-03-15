using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class PricePointConfiguration : IEntityTypeConfiguration<PricePoint>
{
    public void Configure(EntityTypeBuilder<PricePoint> builder)
    {
        builder.ToTable("price_points");
        builder.HasKey(pp => new { pp.AssetId, pp.PriceDate });

        builder.Property(pp => pp.Close).HasPrecision(18, 6).IsRequired();
        builder.Property(pp => pp.Open).HasPrecision(18, 6);
        builder.Property(pp => pp.High).HasPrecision(18, 6);
        builder.Property(pp => pp.Low).HasPrecision(18, 6);
        builder.Property(pp => pp.Volume).HasPrecision(18, 6);

        builder.HasOne(pp => pp.Asset)
               .WithMany(a => a.PricePoints)
               .HasForeignKey(pp => pp.AssetId)
               .HasConstraintName("fk_price_points_asset");
    }
}
