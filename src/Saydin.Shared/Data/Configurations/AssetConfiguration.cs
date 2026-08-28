using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata;
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
        // F2.5-1: assets.metadata JSONB kolonu Asset entity'sine bağlandı.
        // Migration 001'de tanımlı kolon EF model tarafından da bilinmesi gerek —
        // aksi halde Add-Migration sırasında "drop column metadata" üretilir.
        builder.Property(a => a.Metadata).HasColumnType("jsonb");
        var createdAt = builder.Property(a => a.CreatedAt)
            .HasDefaultValueSql("NOW()")
            .ValueGeneratedOnAdd();
        createdAt.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        createdAt.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

        builder.HasIndex(a => a.Symbol).IsUnique().HasDatabaseName("uq_assets_symbol");
    }
}
