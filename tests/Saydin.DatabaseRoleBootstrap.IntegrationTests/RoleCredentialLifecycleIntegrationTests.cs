using Npgsql;
using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseRoleBootstrap.IntegrationTests;

public sealed class RoleCredentialLifecycleIntegrationTests
{
    [Fact]
    public async Task Client_generated_scram_verifiers_authenticate_and_plaintext_never_enters_query_text()
    {
        await using var harness = await RoleBootstrapPgHarness.CreateAsync();
        (await harness.RunEnsureAsync()).AssertSuccess();

        await using var admin = await harness.OpenAdminAsync();
        await using (var hashes = new NpgsqlCommand("""
            SELECT auth.rolpassword
              FROM pg_catalog.pg_authid auth
              JOIN pg_catalog.pg_roles role ON role.oid=auth.oid
             WHERE pg_catalog.left(role.rolname,pg_catalog.length($1)+1)=$1||'_'
               AND role.rolcanlogin AND auth.rolpassword IS NOT NULL
            """, admin))
        {
            hashes.Parameters.AddWithValue(harness.Contract.Prefix);
            var observed = new List<string>();
            await using var reader = await hashes.ExecuteReaderAsync();
            while (await reader.ReadAsync()) observed.Add(reader.GetString(0));
            Assert.Equal(6, observed.Count);
            Assert.All(observed, verifier =>
                Assert.StartsWith("SCRAM-SHA-256$4096:", verifier, StringComparison.Ordinal));
            Assert.All(harness.V1Passwords, plaintext =>
                Assert.All(observed, verifier =>
                    Assert.DoesNotContain(plaintext, verifier, StringComparison.Ordinal)));
        }

        foreach (var plaintext in harness.V1Passwords)
        {
            await using var activity = new NpgsqlCommand("""
                SELECT count(*) FROM pg_catalog.pg_stat_activity
                 WHERE pg_catalog.strpos(query,$1)>0
                """, admin);
            activity.Parameters.AddWithValue(plaintext);
            Assert.Equal(0L, Convert.ToInt64(await activity.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task Rotation_is_reusable_through_v3_and_preserves_exact_version_memberships()
    {
        await using var harness = await RoleBootstrapPgHarness.CreateAsync();
        (await harness.RunEnsureAsync()).AssertSuccess();
        var v2 = $"Api-v2-{Guid.NewGuid():N}-A9!";
        var v3 = $"Api-v3-{Guid.NewGuid():N}-B8!";

        (await harness.RunRotateAsync(
            LoginPurpose.Api, $"Api-v1-rewrite-{Guid.NewGuid():N}-Z9!", version: 1))
            .AssertFailure(BootstrapExitCodes.TopologyRejected, "login_version_not_next");
        (await harness.RunRotateAsync(LoginPurpose.Api, v2, version: 2)).AssertSuccess();
        (await harness.RunRotateAsync(LoginPurpose.Api, v3, version: 3)).AssertSuccess();

        await using (await harness.OpenLoginAsync(LoginPurpose.Api, 1)) { }
        await using (await harness.OpenLoginAsync(LoginPurpose.Api, 2, v2)) { }
        await using (await harness.OpenLoginAsync(LoginPurpose.Api, 3, v3)) { }
        await using var admin = await harness.OpenAdminAsync();
        await using var memberships = new NpgsqlCommand("""
            SELECT count(*) FROM pg_catalog.pg_auth_members membership
              JOIN pg_catalog.pg_roles member ON member.oid=membership.member
              JOIN pg_catalog.pg_roles granted ON granted.oid=membership.roleid
             WHERE member.rolname = ANY($1) AND granted.rolname=$2
            """, admin);
        memberships.Parameters.AddWithValue(new[]
        {
            harness.Contract.Login(LoginPurpose.Api, 1).Name,
            harness.Contract.Login(LoginPurpose.Api, 2).Name,
            harness.Contract.Login(LoginPurpose.Api, 3).Name,
        });
        memberships.Parameters.AddWithValue(harness.Contract.ApiCapability.Name);
        Assert.Equal(3L, Convert.ToInt64(await memberships.ExecuteScalarAsync()));
        (await harness.RunVerifyAsync()).AssertSuccess();
    }

    [Fact]
    public async Task Current_password_reset_invalidates_compromised_password_without_changing_identity()
    {
        await using var harness = await RoleBootstrapPgHarness.CreateAsync();
        (await harness.RunEnsureAsync()).AssertSuccess();
        var compromised = $"Api-v2-compromised-{Guid.NewGuid():N}-A9!";
        var replacement = $"Api-v2-reset-{Guid.NewGuid():N}-B8!";
        (await harness.RunRotateAsync(LoginPurpose.Api, compromised)).AssertSuccess();

        (await harness.RunResetPasswordAsync(LoginPurpose.Api, 2, replacement)).AssertSuccess();

        await AssertAuthenticationRejectedAsync(harness, LoginPurpose.Api, 2, compromised);
        await using (await harness.OpenLoginAsync(LoginPurpose.Api, 2, replacement)) { }
        (await harness.RunResetPasswordAsync(
            LoginPurpose.Api, 1, $"Api-v1-forbidden-{Guid.NewGuid():N}-C7!"))
            .AssertFailure(BootstrapExitCodes.TopologyRejected, "reset_target_not_current");
    }

    [Fact]
    public async Task Retire_removes_old_role_only_after_exact_replacement_is_current()
    {
        await using var harness = await RoleBootstrapPgHarness.CreateAsync();
        (await harness.RunEnsureAsync()).AssertSuccess();
        var v2 = $"Api-v2-{Guid.NewGuid():N}-A9!";
        (await harness.RunRotateAsync(LoginPurpose.Api, v2)).AssertSuccess();

        (await harness.RunRetireAsync(LoginPurpose.Api, 1, 2)).AssertSuccess();

        await AssertAuthenticationRejectedAsync(
            harness, LoginPurpose.Api, 1, harness.V1Password(LoginPurpose.Api));
        await using (await harness.OpenLoginAsync(LoginPurpose.Api, 2, v2)) { }
        await using var admin = await harness.OpenAdminAsync();
        await using var oldRole = new NpgsqlCommand(
            "SELECT count(*) FROM pg_catalog.pg_roles WHERE rolname=$1", admin);
        oldRole.Parameters.AddWithValue(harness.Contract.Login(LoginPurpose.Api, 1).Name);
        Assert.Equal(0L, Convert.ToInt64(await oldRole.ExecuteScalarAsync()));
        var currentPasswords = Enum.GetValues<LoginPurpose>().ToDictionary(
            purpose => purpose,
            purpose => purpose == LoginPurpose.Api ? v2 : harness.V1Password(purpose));
        (await harness.RunEnsureAsync(currentPasswords)).AssertSuccess();
        (await harness.RunVerifyAsync()).AssertSuccess();
    }

    [Fact]
    public async Task Active_old_session_causes_bounded_safe_red_then_retry_finishes_retirement()
    {
        await using var harness = await RoleBootstrapPgHarness.CreateAsync();
        (await harness.RunEnsureAsync()).AssertSuccess();
        var v2 = $"Api-v2-{Guid.NewGuid():N}-A9!";
        (await harness.RunRotateAsync(LoginPurpose.Api, v2)).AssertSuccess();
        await using var oldSession = await harness.OpenLoginAsync(LoginPurpose.Api, 1);

        (await harness.RunRetireAsync(LoginPurpose.Api, 1, 2, drainTimeoutSeconds: 1))
            .AssertFailure(BootstrapExitCodes.TopologyRejected, "retired_login_sessions_active");
        Assert.Equal(harness.Contract.Login(LoginPurpose.Api, 1).Name,
            Convert.ToString(await RoleBootstrapPgHarness.ScalarAsync(oldSession, "SELECT current_user")));
        await AssertAuthenticationRejectedAsync(
            harness, LoginPurpose.Api, 1, harness.V1Password(LoginPurpose.Api));

        var currentPasswords = Enum.GetValues<LoginPurpose>().ToDictionary(
            purpose => purpose,
            purpose => purpose == LoginPurpose.Api ? v2 : harness.V1Password(purpose));
        (await harness.RunEnsureAsync(currentPasswords)).AssertSuccess();

        await oldSession.DisposeAsync();
        (await harness.RunRetireAsync(LoginPurpose.Api, 1, 2)).AssertSuccess();
        await using (await harness.OpenLoginAsync(LoginPurpose.Api, 2, v2)) { }
        (await harness.RunVerifyAsync()).AssertSuccess();
    }

    private static async Task AssertAuthenticationRejectedAsync(
        RoleBootstrapPgHarness harness,
        LoginPurpose purpose,
        int version,
        string password)
    {
        var exception = await Assert.ThrowsAnyAsync<NpgsqlException>(async () =>
        {
            await using var connection = await harness.OpenLoginAsync(purpose, version, password);
        });
        var sqlState = exception is PostgresException postgres
            ? postgres.SqlState
            : (exception.InnerException as PostgresException)?.SqlState;
        Assert.Contains(sqlState, new[] { "28P01", "28000" });
    }
}
