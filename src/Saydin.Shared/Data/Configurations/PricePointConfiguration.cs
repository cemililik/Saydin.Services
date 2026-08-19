using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata;
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
        // Volume kolonu DB tarafında NUMERIC(24,4) — kripto işlem hacimleri için 18,6
        // taşmaya yol açıyordu. EF Core precision'ı DB ile birebir hizala (review F1.5-2).
        builder.Property(pp => pp.Volume).HasPrecision(24, 4);
        builder.Property(pp => pp.ProviderSource)
            .HasMaxLength(ObservationAuthorityLimits.ProviderSourceBytes);
        builder.Property(pp => pp.SourceObservationId)
            .HasMaxLength(ObservationAuthorityLimits.SourceObservationIdBytes);
        builder.Property(pp => pp.PriceKind)
            .HasMaxLength(ObservationAuthorityLimits.PriceKindBytes);
        builder.Ignore(pp => pp.PayloadSha256);
        builder.Ignore(pp => pp.PayloadByteLength);
        builder.Ignore(pp => pp.IngestionWindowId);
        builder.Property(pp => pp.ObservationSha256).HasColumnType("bytea");
        builder.Property(pp => pp.SourceRaw).HasColumnType("jsonb");
        var ingestedAt = builder.Property(pp => pp.IngestedAt)
            .HasDefaultValueSql("NOW()")
            .ValueGeneratedOnAdd();
        ingestedAt.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        ingestedAt.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

        builder.HasOne(pp => pp.Asset)
               .WithMany(a => a.PricePoints)
               .HasForeignKey(pp => pp.AssetId)
               .OnDelete(DeleteBehavior.Restrict)
               .HasConstraintName("fk_price_points_asset");
    }
}
