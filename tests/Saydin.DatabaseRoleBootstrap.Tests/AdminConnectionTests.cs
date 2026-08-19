using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseRoleBootstrap.Tests;

public sealed class AdminConnectionTests
{
    private const string SystemHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("Host=db-a,db-b;Port=5432;Database=saydin;Username=postgres;Password=secret")]
    [InlineData("Host=db-a;Port=5432;Database=saydin;Username=postgres;Password=secret;Options=-c statement_timeout=0")]
    [InlineData("Host=db-a;Port=5432;Database=saydin;Username=postgres;Password=secret;Load Balance Hosts=true")]
    public void Privileged_connection_rejects_multi_host_and_caller_session_options(string secret)
    {
        var runner = new RoleBootstrapRunner(Options(), TextWriter.Null);

        var exception = Assert.Throws<BootstrapRejectedException>(() =>
            runner.BuildAdminConnection(secret));

        Assert.Equal("admin_connection_target_mismatch", exception.Code);
    }

    [Fact]
    public void Privileged_connection_is_rebuilt_from_the_explicit_allowlist()
    {
        var runner = new RoleBootstrapRunner(Options(), TextWriter.Null);

        var builder = runner.BuildAdminConnection(
            "Host=db-a;Port=5444;Database=saydin;Username=postgres;Password=secret;SSL Mode=Require");

        Assert.Equal("db-a", builder.Host);
        Assert.Equal(5444, builder.Port);
        Assert.False(builder.Pooling);
        Assert.Null(builder.Options);
        Assert.Null(builder.Passfile);
        Assert.False(builder.LoadBalanceHosts);
        Assert.Equal("pg_catalog,public,pg_temp", builder.SearchPath);
    }

    private static BootstrapOptions Options()
    {
        var prefix = RoleContract.DerivePrefix("prod-a", "saydin", SystemHash);
        return new BootstrapOptions(
            BootstrapCommand.Verify,
            "/run/secrets/admin",
            RoleContract.Create("prod-a", "saydin", SystemHash, prefix),
            "2.16.1",
            "1.1",
            new BootstrapTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)),
            new Dictionary<LoginPurpose, string>(),
            null,
            DateTimeOffset.Parse("2026-10-19T00:00:00Z"),
            null,
            false,
            null,
            null,
            null);
    }
}
