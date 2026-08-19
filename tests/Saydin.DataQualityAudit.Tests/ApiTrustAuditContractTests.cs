using FluentAssertions;

namespace Saydin.DataQualityAudit.Tests;

public sealed class ApiTrustAuditContractTests
{
    [Fact]
    public void StructureFingerprint_PinsAllEightFunctionsAndFourCatalogTriggers()
    {
        var functions = new[]
        {
            "compute_asset_catalog_sha256",
            "refresh_asset_catalog_state",
            "register_installation",
            "resolve_installation",
            "begin_installation_rotation",
            "commit_installation_rotation",
            "revoke_installation",
            "get_asset_catalog_state",
        };
        var triggers = new[]
        {
            "trg_asset_catalog_revision_insert",
            "trg_asset_catalog_revision_update",
            "trg_asset_catalog_revision_delete",
            "trg_asset_catalog_revision_truncate",
        };

        foreach (var function in functions)
            ApiTrustAuditSql.Structure.Should().Contain($"('{function}'");
        foreach (var trigger in triggers)
            ApiTrustAuditSql.Structure.Should().Contain($"'{trigger}'");

        ApiTrustAuditSql.Structure.Should()
            .Contain("security_definer")
            .And.Contain("function.prosrc")
            .And.Contain("function.proconfig")
            .And.Contain("function_acl_drift")
            .And.Contain("table_acl_drift")
            .And.Contain("column_acl_drift")
            .And.Contain("constraint_drift")
            .And.Contain("index_drift")
            .And.Contain("trigger_drift");
    }

    [Fact]
    public void CatalogEvidence_RecomputesCanonicalHashWithoutCredentialData()
    {
        ApiTrustAuditSql.AssetCatalogState.Should()
            .Contain("jsonb_agg")
            .And.Contain("ORDER BY asset.id")
            .And.Contain("octet_length(state.catalog_sha256)=32")
            .And.NotContain("installation_credentials")
            .And.NotContain("secret_hash");
    }
}
