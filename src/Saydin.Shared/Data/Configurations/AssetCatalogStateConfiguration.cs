using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saydin.Shared.Entities;

namespace Saydin.Shared.Data.Configurations;

public sealed class AssetCatalogStateConfiguration : IEntityTypeConfiguration<AssetCatalogState>
{
    public void Configure(EntityTypeBuilder<AssetCatalogState> builder)
    {
        builder.ToTable("asset_catalog_state", table =>
        {
            table.HasCheckConstraint("chk_asset_catalog_state_singleton", "singleton = 1");
            table.HasCheckConstraint("chk_asset_catalog_state_revision", "revision > 0");
            table.HasCheckConstraint(
                "chk_asset_catalog_state_sha256",
                "octet_length(catalog_sha256) = 32");
        });

        builder.HasKey(state => state.Singleton);
        builder.Property(state => state.CatalogSha256).HasColumnType("bytea").IsRequired();
    }
}
