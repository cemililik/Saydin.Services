using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class InflationRateConfiguration : IEntityTypeConfiguration<InflationRate>
{
    public void Configure(EntityTypeBuilder<InflationRate> builder)
    {
        builder.ToTable("inflation_rates");
        builder.HasKey(r => r.PeriodDate);

        builder.Property(r => r.IndexValue).HasPrecision(12, 4).IsRequired();
        builder.Property(r => r.Source).HasMaxLength(20).IsRequired();
    }
}
