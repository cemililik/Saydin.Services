using FluentAssertions;

namespace Saydin.DataRepair.IntegrationTests;

public sealed class RepairLiveTrustIntegrationTests(RepairDatabaseFixture fixture)
    : IClassFixture<RepairDatabaseFixture>
{
    [Theory]
    [InlineData("database")]
    [InlineData("system")]
    public async Task PhysicalDatabaseIdentityDrift_IsRejectedByLiveTrust(string drift)
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            var target = drift == "database"
                ? repair.Plan.Target with { Database = "wrong_repair_database" }
                : repair.Plan.Target with { SystemIdentifierSha256 = new string('0', 64) };

            var action = () => fixture.VerifyLiveTrustAfterAsync(repair, target: target);

            (await action.Should().ThrowAsync<RepairRejectedException>()).Which.Code
                .Should().Be("repair_physical_target_mismatch");
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Theory]
    [InlineData("deployment")]
    [InlineData("role-prefix")]
    public async Task SignedDeploymentOrRoleIdentityDrift_IsRejectedByLiveContract(string drift)
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            var target = drift == "deployment"
                ? repair.Plan.Target with { DeploymentId = "bad-target" }
                : repair.Plan.Target with
                {
                    RolePrefix = "saydin_bad_000000000000000000000000",
                };

            var action = () => fixture.VerifyLiveTrustAfterAsync(repair, target: target);

            (await action.Should().ThrowAsync<RepairRejectedException>()).Which.Code
                .Should().Be("repair_role_contract_mismatch");
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task LiveRoleContractRowDrift_IsRejected()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            var action = () => fixture.VerifyLiveTrustAfterAsync(repair, async () =>
                await fixture.ExecuteAdminAsReplicaAsync("""
                    UPDATE public.saydin_role_contract
                       SET deployment_id='bad-target' WHERE singleton=1;
                    """));

            (await action.Should().ThrowAsync<RepairRejectedException>()).Which.Code
                .Should().Be("repair_role_contract_mismatch");
        }
        finally
        {
            await fixture.ExecuteAdminAsReplicaAsync("""
                UPDATE public.saydin_role_contract SET deployment_id=$1 WHERE singleton=1;
                """, repair.Plan.Target.DeploymentId);
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task MigrationControlNotReady_IsRejected()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            var action = () => fixture.VerifyLiveTrustAfterAsync(repair, async () =>
                await fixture.ExecuteAdminAsync(
                    "UPDATE public.saydin_migration_control SET state='failed' WHERE singleton=1"));

            (await action.Should().ThrowAsync<RepairRejectedException>()).Which.Code
                .Should().Be("repair_migration_control_not_ready");
        }
        finally
        {
            await fixture.ExecuteAdminAsync(
                "UPDATE public.saydin_migration_control SET state='ready' WHERE singleton=1");
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task MigrationSetMissingRow_IsRejected()
    {
        var repair = await fixture.CreateCaseAsync();
        const string version = "001_initial";
        var rowJson = await fixture.LoadMigrationRowJsonAsync(version);
        try
        {
            var action = () => fixture.VerifyLiveTrustAfterAsync(repair, async () =>
                await fixture.ExecuteAdminAsync(
                    "DELETE FROM public.schema_migrations WHERE version=$1",
                    version));

            (await action.Should().ThrowAsync<RepairRejectedException>()).Which.Code
                .Should().Be("repair_migration_set_mismatch");
        }
        finally
        {
            if (await fixture.ExecuteAdminScalarAsync(
                    "SELECT count(*) FROM public.schema_migrations WHERE version=$1", version) is 0L)
                await fixture.RestoreMigrationRowJsonAsync(rowJson);
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task AuditLoginWithMutationGrant_IsRejectedAsNotReadOnly()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            var action = () => fixture.VerifyLiveTrustAfterAsync(repair, async () =>
                await fixture.ExecuteAdminAsync(
                    $"GRANT UPDATE ON public.ingestion_windows TO {fixture.AuditLogin}"));

            (await action.Should().ThrowAsync<RepairRejectedException>()).Which.Code
                .Should().Be("repair_audit_role_not_read_only");
        }
        finally
        {
            await fixture.ExecuteAdminAsync(
                $"REVOKE UPDATE ON public.ingestion_windows FROM {fixture.AuditLogin}");
            await fixture.CleanupAsync(repair);
        }
    }
}
