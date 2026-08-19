using FluentAssertions;

namespace Saydin.DataQualityAudit.Tests;

public sealed class PrincipalRetentionAuditContractTests
{
    [Fact]
    public void StructurePinsFunctionTriggerFkAclCompressionAndConsumedTransition()
    {
        PrincipalRetentionAuditSql.Structure.Should().ContainAll(
            "redact_activity_logs_before_principal_delete",
            "trg_users_principal_retention_redact",
            "activity_logs_user_id_fkey",
            "35bba6df01802e7850bd1a753b95ff643a2a01ec56aa476981cbe9dc42705cf3",
            "be2799e95d3e4abc7621598bcc116b0f8d5df0a931e4e1c5af6cb2c42cae66e6",
            "saydin_principal_retention_control",
            "compressed_chunk_id",
            "timescale_scheduler_role",
            "api_capability_role",
            "compress_after");
        PrincipalRetentionAuditSql.Structure.Should().Contain("'DELETE'");
        PrincipalRetentionAuditSql.Structure.Should().Contain("'UPDATE'");
    }
}
