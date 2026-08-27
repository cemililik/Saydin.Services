using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class PricePointConfiguration : IEntityTypeConfiguration<PricePoint>
{
    public void Configure(EntityTypeBuilder<PricePoint> builder)
    {
        builder.ToTable("price_points", table =>
        {
            table.HasCheckConstraint(
                "chk_price_points_authority_tuple",
                "(provider_source IS NULL AND source_observation_id IS NULL AND as_of_at IS NULL " +
                "AND price_kind IS NULL AND is_final IS NULL AND observation_sha256 IS NULL " +
                "AND authority_contract_version IS NULL) OR (provider_source IS NOT NULL " +
                "AND source_observation_id IS NOT NULL AND as_of_at IS NOT NULL " +
                "AND price_kind IS NOT NULL AND is_final IS TRUE AND observation_sha256 IS NOT NULL " +
                "AND authority_contract_version > 0 AND source_raw IS NOT NULL " +
                "AND octet_length(source_observation_id) BETWEEN 1 AND 256 " +
                "AND octet_length(observation_sha256) = 32 " +
                "AND observation_sha256 <> decode(repeat('00', 32), 'hex') " +
                "AND saydin_source_raw_allowed(source_raw) " +
                "AND source_raw->>'provider_source' = provider_source " +
                "AND source_raw->>'observation_id' = source_observation_id " +
                "AND observation_sha256 = sha256(convert_to(saydin_canonical_observation(source_raw)::text, 'UTF8')))");
            table.HasCheckConstraint(
                "chk_price_points_provider_kind",
                "provider_source IS NULL OR (provider_source, price_kind) IN " +
                "(('tcmb','official_reference'),('coingecko','daily_utc_reference')," +
                "('openexchangerates','daily_reference'),('twelvedata','daily_close'))");
            table.HasCheckConstraint(
                "chk_price_points_numeric",
                "close::text NOT IN ('NaN','Infinity','-Infinity') AND close > 0 " +
                "AND (volume IS NULL OR (volume::text NOT IN ('NaN','Infinity','-Infinity') AND volume >= 0)) " +
                "AND (open IS NULL OR open::text NOT IN ('NaN','Infinity','-Infinity')) " +
                "AND (high IS NULL OR high::text NOT IN ('NaN','Infinity','-Infinity')) " +
                "AND (low IS NULL OR low::text NOT IN ('NaN','Infinity','-Infinity')) " +
                "AND ((open IS NULL AND high IS NULL AND low IS NULL) OR " +
                "(open IS NOT NULL AND high IS NOT NULL AND low IS NOT NULL AND open > 0 " +
                "AND high > 0 AND low > 0 AND high >= GREATEST(open, close, low) " +
                "AND low <= LEAST(open, close, high)))");
            table.HasCheckConstraint(
                "chk_price_points_provider_shape",
                "provider_source IS NULL OR (provider_source IN ('tcmb','coingecko','openexchangerates') " +
                "AND open IS NULL AND high IS NULL AND low IS NULL AND volume IS NULL) OR " +
                "(provider_source = 'twelvedata' AND open IS NOT NULL AND high IS NOT NULL AND low IS NOT NULL)");
            table.HasCheckConstraint(
                "chk_price_points_as_of",
                "provider_source IS NULL OR (provider_source = 'twelvedata' " +
                "AND (as_of_at AT TIME ZONE 'Europe/Istanbul')::date = price_date " +
                "AND (as_of_at AT TIME ZONE 'Europe/Istanbul')::time = time '00:00:00') OR " +
                "(provider_source <> 'twelvedata' AND (as_of_at AT TIME ZONE 'UTC')::date = price_date " +
                "AND (provider_source <> 'coingecko' OR " +
                "(as_of_at AT TIME ZONE 'UTC')::time = time '00:00:00'))");
        });
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
