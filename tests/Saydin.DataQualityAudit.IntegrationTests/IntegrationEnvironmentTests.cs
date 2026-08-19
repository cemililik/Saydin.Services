using FluentAssertions;

namespace Saydin.DataQualityAudit.IntegrationTests;

[Collection(AuditDatabaseCollection.Name)]
public sealed class IntegrationEnvironmentTests
{
    [Fact]
    public void Require_RejectsDatabaseNameThatDoesNotMatchRunId_BeforeFixtureDml()
    {
        var snapshot = new Dictionary<string, string?>
        {
            ["SAYDIN_AUDIT_TEST_REQUIRED"] = Environment.GetEnvironmentVariable("SAYDIN_AUDIT_TEST_REQUIRED"),
            ["SAYDIN_AUDIT_TEST_RUN_ID"] = Environment.GetEnvironmentVariable("SAYDIN_AUDIT_TEST_RUN_ID"),
            ["SAYDIN_AUDIT_TEST_EXPECTED_HOST"] = Environment.GetEnvironmentVariable("SAYDIN_AUDIT_TEST_EXPECTED_HOST"),
        };
        try
        {
            Environment.SetEnvironmentVariable("SAYDIN_AUDIT_TEST_REQUIRED", "true");
            Environment.SetEnvironmentVariable("SAYDIN_AUDIT_TEST_RUN_ID", new string('a', 32));
            Environment.SetEnvironmentVariable("SAYDIN_AUDIT_TEST_EXPECTED_HOST", "postgres");
            var action = IntegrationEnvironment.Require;
            action.Should().Throw<InvalidOperationException>()
                .WithMessage("Unsafe audit integration database target.");
        }
        finally
        {
            foreach (var (key, value) in snapshot)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
