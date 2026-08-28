using FluentAssertions;

namespace Saydin.DataQualityAudit.Tests;

public sealed class RepairRecommendationPolicyTests
{
    [Theory]
    [InlineData("DQ-003", (int)RepairAction.RestoreSchemaContract, false)]
    [InlineData("DQ-006", (int)RepairAction.RestoreCalendarRelease, true)]
    [InlineData("DQ-009", (int)RepairAction.ReconcileAuthorityEvidence, true)]
    public void PolicyFor_HasTypedDeterministicMappingForPreviouslyUnmappedChecks(
        string checkId,
        int expectedAction,
        bool expectedProviderEvidence)
    {
        var first = AuditAccumulator.PolicyFor(checkId, "any_violation_code");
        var second = AuditAccumulator.PolicyFor(checkId, "another_violation_code");

        first.Should().Be(second);
        first.Action.Should().Be((RepairAction)expectedAction);
        first.RequiresProviderEvidence.Should().Be(expectedProviderEvidence);
    }
}
