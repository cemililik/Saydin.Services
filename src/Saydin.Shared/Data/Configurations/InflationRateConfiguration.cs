using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
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
            "source IN ('tuik', 'seed-approximation')"));
        builder.HasKey(r => r.PeriodDate);

        builder.Property(r => r.IndexValue).HasPrecision(12, 4).IsRequired();
        builder.Property(r => r.Source).HasMaxLength(20).IsRequired().HasDefaultValue("tuik");

        builder.Property(r => r.CreatedAt).HasDefaultValueSql("NOW()");
        builder.Property(r => r.UpdatedAt).HasDefaultValueSql("NOW()");
    }
}
