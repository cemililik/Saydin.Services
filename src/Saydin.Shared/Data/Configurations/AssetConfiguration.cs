using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.ToTable("assets");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Symbol).HasMaxLength(20).IsRequired();
        builder.Property(a => a.DisplayName).HasMaxLength(100).IsRequired();
        builder.Property(a => a.Source).HasMaxLength(50).IsRequired();
        builder.Property(a => a.SourceId).HasMaxLength(100);
        builder.Property(a => a.Category).HasColumnType("asset_category");

        builder.HasIndex(a => a.Symbol).IsUnique().HasDatabaseName("uq_assets_symbol");
    }
}
