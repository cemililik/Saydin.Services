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
        builder.ToTable("inflation_rates", t => t.HasCheckConstraint(
            "chk_inflation_rates_source",
            $"source IN ('{InflationSources.Tuik}', '{InflationSources.SeedApproximation}')"));

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
