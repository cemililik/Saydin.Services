namespace Saydin.DatabaseRoleBootstrap.Tests;

public sealed class DatabaseFailureCodeTests
{
    [Theory]
    [InlineData("23505", "database_operation_failed_sqlstate_23505")]
    [InlineData("XX001", "database_operation_failed_sqlstate_xx001")]
    [InlineData("42501", "admin_privilege_insufficient")]
    [InlineData("3D000", "target_database_missing")]
    public void Sqlstate_is_diagnostic_but_contains_no_server_text(
        string sqlState,
        string expected)
    {
        var code = RoleBootstrapRunner.DatabaseCode(sqlState);

        Assert.Equal(expected, code);
        Assert.DoesNotContain(' ', code);
        Assert.DoesNotContain('=', code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("secret-query-text")]
    [InlineData("28p01")]
    public void Invalid_sqlstate_shape_is_not_reflected(string value)
    {
        Assert.Equal("database_operation_failed_sqlstate_invalid",
            RoleBootstrapRunner.DatabaseCode(value));
    }
}
