using FluentAssertions;

namespace Saydin.PriceIngestion.IntegrationTests;

public sealed class IngestionTestTargetGuardTests
{
    private const string RunId = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("false", RunId, "postgres", "Host=postgres;Database=saydin_ingestion_test_0123456789abcdef0123456789abcdef", "zorunludur")]
    [InlineData("true", "BAD", "postgres", "Host=postgres;Database=saydin_ingestion_test_BAD", "hex")]
    [InlineData("true", RunId, "postgres", "Host=prod-db;Database=saydin_ingestion_test_0123456789abcdef0123456789abcdef", "host")]
    [InlineData("true", RunId, "postgres", "Host=postgres;Database=app", "exact")]
    public void UnsafeTarget_FailsBeforeAnyDml(
        string required, string runId, string host, string connection, string message)
    {
        var act = () => IngestionTestTargetGuard.Validate(connection, required, runId, host);
        act.Should().Throw<InvalidOperationException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void ExactEphemeralTarget_IsAccepted()
    {
        var connection = $"Host=postgres;Database=saydin_ingestion_test_{RunId}";
        var act = () => IngestionTestTargetGuard.Validate(connection, "true", RunId, "postgres");
        act.Should().NotThrow();
    }
}
