using FluentAssertions;
using Saydin.Api.IntegrationTests.Fixtures;

namespace Saydin.Api.IntegrationTests;

public sealed class IntegrationTestEnvironmentTests
{
    private const string RunId = "123456781234123412341234567890ab";

    [Fact]
    public void ValidateDatabase_ExactRunDatabase_AllowsTarget()
    {
        var act = () => IntegrationTestEnvironment.ValidateDatabase(
            $"Host=postgres;Database=saydin_test_{RunId};Username=saydin_ci;Password=test",
            RunId);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateDatabase_MismatchedRunDatabase_RejectsBeforeConnection()
    {
        var otherRunId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        var act = () => IntegrationTestEnvironment.ValidateDatabase(
            $"Host=postgres;Database=saydin_test_{otherRunId};Username=saydin_ci;Password=test",
            RunId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*run ile eşleşmiyor*");
    }

    [Theory]
    [InlineData("prod-db.internal", "saydin")]
    [InlineData("postgres", "saydin_staging")]
    public void ValidateDatabase_ProtectedEnvironmentMarker_RejectsBeforeConnection(
        string host,
        string database)
    {
        var act = () => IntegrationTestEnvironment.ValidateDatabase(
            $"Host={host};Database={database};Username=saydin;Password=test",
            RunId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*production/staging*");
    }

    [Fact]
    public void ValidateDatabase_NonDisposableHost_RejectsBeforeConnection()
    {
        var act = () => IntegrationTestEnvironment.ValidateDatabase(
            $"Host=db.internal;Database=saydin_test_{RunId};Username=saydin;Password=test",
            RunId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*disposable Compose postgres*");
    }

    [Fact]
    public void ValidateDatabase_InvalidRunId_RejectsBeforeConnection()
    {
        var act = () => IntegrationTestEnvironment.ValidateDatabase(
            "Host=postgres;Database=saydin_test_not_a_uuid;Username=saydin;Password=test",
            "not-a-uuid");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{IntegrationTestEnvironment.RunIdVariable}*");
    }

    [Fact]
    public void ValidateDatabase_EmptyGuid_RejectsBeforeConnection()
    {
        var act = () => IntegrationTestEnvironment.ValidateDatabase(
            "Host=postgres;Database=saydin_test_00000000000000000000000000000000;Username=saydin;Password=test",
            "00000000000000000000000000000000");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ValidateRedis_ComposeEndpoint_AllowsTarget()
    {
        var act = () => IntegrationTestEnvironment.ValidateRedis("redis:6379", RunId);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("cache-01.internal:6379")]
    [InlineData("redis:6380")]
    [InlineData("redis:6379,cache-01.internal:6379")]
    public void ValidateRedis_NonDisposableEndpoint_RejectsBeforeConnection(string endpoint)
    {
        var act = () => IntegrationTestEnvironment.ValidateRedis(endpoint, RunId);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EnsureRequiredRedisConnected_Disconnected_ThrowsInsteadOfSkipping()
    {
        var act = () => IntegrationTestEnvironment.EnsureRequiredRedisConnected(
            required: true,
            available: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*skip edilemez*");
    }
}
