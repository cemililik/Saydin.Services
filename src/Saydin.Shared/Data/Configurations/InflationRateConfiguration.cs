using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saydin.Shared.Constants;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class InflationRateConfiguration : IEntityTypeConfiguration<InflationRate>
{
    public void Configure(EntityTypeBuilder<InflationRate> builder)
    {
        // SHRD-011: inflation_rates.source CHECK migration 011'de eklendi → EF tarafında sync.
        // SHRD-019/020: created_at/updated_at DB-side DEFAULT NOW() — Add-Migration drift'i kapatır.
        builder.ToTable("inflation_rates", table =>
        {
            table.HasCheckConstraint(
                "chk_inflation_rates_source",
                $"source IN ('{InflationSources.Tuik}', '{InflationSources.SeedApproximation}')");
            table.HasCheckConstraint(
                "chk_inflation_rates_authority_tuple",
                "(provider_source IS NULL AND source_observation_id IS NULL AND as_of_at IS NULL " +
                "AND price_kind IS NULL AND is_final IS NULL AND observation_sha256 IS NULL " +
                "AND authority_contract_version IS NULL AND source_raw IS NULL) OR " +
                "(provider_source = 'evds' AND source_observation_id IS NOT NULL AND as_of_at IS NOT NULL " +
                "AND price_kind = 'cpi_index' AND is_final IS TRUE AND observation_sha256 IS NOT NULL " +
                "AND authority_contract_version > 0 AND source_raw IS NOT NULL " +
                "AND octet_length(source_observation_id) BETWEEN 1 AND 256 " +
                "AND octet_length(observation_sha256) = 32 " +
                "AND observation_sha256 <> decode(repeat('00', 32), 'hex') " +
                "AND saydin_source_raw_allowed(source_raw) " +
                "AND source_raw->>'provider_source' = provider_source " +
                "AND source_raw->>'observation_id' = source_observation_id " +
                "AND observation_sha256 = sha256(convert_to(saydin_canonical_observation(source_raw)::text, 'UTF8')))");
            table.HasCheckConstraint(
                "chk_inflation_rates_numeric",
                "index_value::text NOT IN ('NaN','Infinity','-Infinity') AND index_value > 0 " +
                "AND EXTRACT(day FROM period_date) = 1");
            table.HasCheckConstraint(
                "chk_inflation_rates_as_of",
                "provider_source IS NULL OR ((as_of_at AT TIME ZONE 'UTC')::date = period_date " +
                "AND (as_of_at AT TIME ZONE 'UTC')::time = time '00:00:00')");
        });

        // F2.7-5 ([C-G-004-2]): composite PK (period_date, source) — migration 012 ile sync.
        // Aynı ay için hem seed-approximation hem tuik satırı bir arada tutulur (audit).
        builder.HasKey(r => new { r.PeriodDate, r.Source });

        builder.Property(r => r.IndexValue).HasPrecision(12, 4).IsRequired();
        builder.Property(r => r.Source).HasMaxLength(20).IsRequired().HasDefaultValue(InflationSources.Tuik);

        builder.Property(r => r.CreatedAt).HasDefaultValueSql("NOW()");
        builder.Property(r => r.UpdatedAt).HasDefaultValueSql("NOW()");
        builder.Property(r => r.ProviderSource)
            .HasMaxLength(ObservationAuthorityLimits.ProviderSourceBytes);
        builder.Property(r => r.SourceObservationId)
            .HasMaxLength(ObservationAuthorityLimits.SourceObservationIdBytes);
        builder.Property(r => r.PriceKind)
            .HasMaxLength(ObservationAuthorityLimits.PriceKindBytes);
        builder.Ignore(r => r.PayloadSha256);
        builder.Ignore(r => r.PayloadByteLength);
        builder.Ignore(r => r.IngestionWindowId);
        builder.Property(r => r.ObservationSha256).HasColumnType("bytea");
        builder.Property(r => r.SourceRaw).HasColumnType("jsonb");
    }
}
