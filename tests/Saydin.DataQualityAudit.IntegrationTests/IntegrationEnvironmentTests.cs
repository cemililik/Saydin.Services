using FluentAssertions;

namespace Saydin.DataQualityAudit.IntegrationTests;

[Collection(AuditDatabaseCollection.Name)]
public sealed class IntegrationEnvironmentTests
{
    [Fact]
    public void Require_RejectsDatabaseNameThatDoesNotMatchRunId_BeforeFixtureDml()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["SAYDIN_AUDIT_TEST_REQUIRED"] = "true",
            ["SAYDIN_AUDIT_TEST_RUN_ID"] = new string('a', 32),
            ["SAYDIN_AUDIT_TEST_EXPECTED_HOST"] = "postgres",
        };
        var action = () => IntegrationEnvironment.Require(
            name => values.GetValueOrDefault(name));
        action.Should().Throw<InvalidOperationException>();
    }
}
