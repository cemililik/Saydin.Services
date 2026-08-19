using Npgsql;
using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseRoleBootstrap.IntegrationTests;

public sealed class RoleBootstrapIntegrationTests
{
    [Fact]
    public async Task Invalid_prefix_is_never_a_cleanup_selector_and_partial_setup_is_fully_removed()
    {
        RoleBootstrapPgHarness.SetupTargets? setup = null;
        RoleBootstrapPgHarness.AfterFirstDatabaseCreatedForTests = targets =>
        {
            setup = targets;
            throw new InvalidOperationException("injected_second_database_create_failure");
        };
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RoleBootstrapPgHarness.CreateAsync());
        }
        finally
        {
            RoleBootstrapPgHarness.AfterFirstDatabaseCreatedForTests = null;
        }
        Assert.NotNull(setup);
        await using (var cluster = await OpenClusterAdminAsync())
        {
            Assert.Equal(0L, Convert.ToInt64(await ScalarWithParameterAsync(cluster,
                "SELECT count(*) FROM pg_catalog.pg_database WHERE datname IN ($1,$2)",
                setup!.TargetDatabase, setup.SecondaryDatabase)));
            Assert.Equal(0L, Convert.ToInt64(await ScalarWithParameterAsync(cluster, """
                SELECT count(*) FROM pg_catalog.pg_roles
                 WHERE pg_catalog.left(rolname,pg_catalog.length($1)+1)=$1||'_'
                """, setup.Prefix)));
        }
        Assert.False(Directory.Exists(setup!.SecretDirectory));

        var sentinel = $"shared_cleanup_sentinel_{Guid.NewGuid():N}";
        await using var harness = await RoleBootstrapPgHarness.CreateAsync();
        await using (var admin = await harness.OpenAdminAsync())
            await ExecuteIdentifierAsync(admin, "CREATE ROLE %I NOLOGIN", sentinel);
        try
        {
            (await harness.RunEnsureAsync(prefix: "shared")).AssertFailure(
                BootstrapExitCodes.InvalidArguments, "role_contract_invalid");
            await harness.DisposeAsync();
            await using var cluster = await OpenClusterAdminAsync();
            Assert.Equal(1L, Convert.ToInt64(await ScalarWithParameterAsync(cluster,
                "SELECT count(*) FROM pg_catalog.pg_roles WHERE rolname=$1", sentinel)));
        }
        finally
        {
            await using var cluster = await OpenClusterAdminAsync();
            await ExecuteIdentifierAsync(cluster, "DROP ROLE IF EXISTS %I", sentinel);
        }
    }

    [Fact]
    public async Task Cleanup_fault_attempts_every_target_and_retry_completes_exactly()
    {
        var harness = await RoleBootstrapPgHarness.CreateAsync();
        (await harness.RunEnsureAsync()).AssertSuccess();
        var attempted = new List<string>();
        var injected = false;
        RoleBootstrapPgHarness.BeforeCleanupTargetForTests = target =>
        {
            attempted.Add(target);
            if (!injected && target == $"database:{harness.TargetDatabase}")
            {
                injected = true;
                throw new InvalidOperationException("injected_first_database_drop_failure");
            }
        };
        try
        {
            await Assert.ThrowsAsync<AggregateException>(() => harness.DisposeAsync().AsTask());
            Assert.Contains($"database:{harness.TargetDatabase}", attempted);
            Assert.Contains($"database:{harness.SecondaryDatabase}", attempted);
            Assert.Contains("roles", attempted);
            Assert.Contains("secret-directory", attempted);
            Assert.False(Directory.Exists(harness.SecretDirectory));
        }
        finally
        {
            RoleBootstrapPgHarness.BeforeCleanupTargetForTests = null;
        }

        await harness.DisposeAsync();
        await using var cluster = await OpenClusterAdminAsync();
        Assert.Equal(0L, Convert.ToInt64(await ScalarWithParameterAsync(cluster,
            "SELECT count(*) FROM pg_catalog.pg_database WHERE datname IN ($1,$2)",
            harness.TargetDatabase, harness.SecondaryDatabase)));
        Assert.Equal(0L, Convert.ToInt64(await ScalarWithParameterAsync(cluster, """
            SELECT count(*) FROM pg_catalog.pg_roles
             WHERE pg_catalog.left(rolname,pg_catalog.length($1)+1)=$1||'_'
            """, harness.Contract.Prefix)));
    }

    [Fact]
    public async Task Fresh_concurrent_and_idempotent_ensure_establishes_exact_contract()
    {
        await using var harness = await RoleBootstrapPgHarness.CreateAsync(precreateExtension: "uuid-ossp");

        var conflicting = await Task.WhenAll(
            harness.RunEnsureAsync(),
            harness.RunEnsureAsync(timescaleVersion: "0.0.0"));
        Assert.Single(conflicting, result => result.ExitCode == BootstrapExitCodes.Success);
        var rejectedClaim = Assert.Single(conflicting,
            result => result.ExitCode == BootstrapExitCodes.TopologyRejected);
        Assert.Equal(string.Empty, rejectedClaim.Output);
        Assert.Contains(rejectedClaim.Error.Trim(), new[]
        {
            "role-bootstrap failed: code=extension_expected_version_unavailable",
            "role-bootstrap failed: code=extension_contract_mismatch",
        });

        var concurrent = await Task.WhenAll(harness.RunEnsureAsync(), harness.RunEnsureAsync());
        Assert.All(concurrent, result => result.AssertSuccess());
        (await harness.RunEnsureAsync()).AssertSuccess();
        (await harness.RunVerifyAsync()).AssertSuccess();

        await AssertExactRoleAndMembershipTopologyAsync(harness);
        await AssertExactExtensionsAsync(harness);
        foreach (var purpose in Enum.GetValues<LoginPurpose>())
            await using (await harness.OpenLoginAsync(purpose, 1)) { }
    }

    [Fact]
    public async Task Different_deployment_claims_for_the_same_physical_database_are_serialized()
    {
        await using var harness = await RoleBootstrapPgHarness.CreateAsync();

        var results = await Task.WhenAll(
            harness.RunEnsureAsync(),
            harness.RunEnsureAsync(deployment: "other-claim"));

        var winnerIndex = Array.FindIndex(results, result => result.ExitCode == BootstrapExitCodes.Success);
        Assert.InRange(winnerIndex, 0, 1);
        var loserIndex = 1 - winnerIndex;
        results[loserIndex].AssertFailure(
            BootstrapExitCodes.TopologyRejected, "database_owner_claim_conflict");
        var winnerDeployment = winnerIndex == 0 ? harness.DeploymentId : "other-claim";
        var loserDeployment = winnerIndex == 0 ? "other-claim" : harness.DeploymentId;
        var winnerContract = RoleContract.Create(winnerDeployment, harness.TargetDatabase, harness.SystemHash,
            RoleContract.DerivePrefix(winnerDeployment, harness.TargetDatabase, harness.SystemHash));
        var loserPrefix = RoleContract.DerivePrefix(
            loserDeployment, harness.TargetDatabase, harness.SystemHash);
        Assert.Equal(winnerContract.Owner.Name, await harness.DatabaseOwnerAsync());
        Assert.Equal(14, await harness.CountRolesAsync(winnerContract.Prefix));
        Assert.Equal(0, await harness.CountRolesAsync(loserPrefix));
        (await harness.RunVerifyAsync(winnerDeployment)).AssertSuccess();
    }

    [Fact]
    public async Task Target_prefix_marker_collision_and_secondary_guards_fail_before_mutation()
    {
        await using var harness = await RoleBootstrapPgHarness.CreateAsync(precreateExtension: "timescaledb");
        var secondaryBefore = await harness.SecondaryFingerprintAsync();

        var wrongTargetAdmin = harness.WriteAdminFor(
            harness.SecondaryDatabase, "wrong-target-admin");
        (await harness.RunEnsureAsync(adminFile: wrongTargetAdmin)).AssertFailure(
            BootstrapExitCodes.TargetRejected, "admin_connection_target_mismatch");

        var wrongHash = new string(harness.SystemHash[0] == 'a' ? 'b' : 'a', 64);
        (await harness.RunEnsureAsync(systemHash: wrongHash)).AssertFailure(
            BootstrapExitCodes.TargetRejected, "target_system_identifier_mismatch");

        (await harness.RunEnsureAsync(prefix: "saydin_itx_deadbeef0000000000000000")).AssertFailure(
            BootstrapExitCodes.TargetRejected, "role_prefix_contract_mismatch");

        var alternateAdmin = $"alternate_admin_{Guid.NewGuid():N}";
        var alternatePassword = $"Alternate-admin-{Guid.NewGuid():N}-A9!";
        await using (var bootstrapAdmin = await harness.OpenAdminAsync())
        {
            await using var create = new NpgsqlCommand(
                "SELECT pg_catalog.format('CREATE ROLE %I LOGIN SUPERUSER PASSWORD %L',$1,$2)",
                bootstrapAdmin);
            create.Parameters.AddWithValue(alternateAdmin);
            create.Parameters.AddWithValue(alternatePassword);
            await RoleBootstrapPgHarness.ExecuteAsync(
                bootstrapAdmin, Convert.ToString(await create.ExecuteScalarAsync())!);
        }
        try
        {
            var alternateFile = harness.WriteAdminForRole(
                alternateAdmin, alternatePassword, "alternate-superuser-admin");
            (await harness.RunEnsureAsync(adminFile: alternateFile)).AssertFailure(
                BootstrapExitCodes.TargetRejected, "admin_or_server_contract_rejected");
        }
        finally
        {
            await using var bootstrapAdmin = await harness.OpenAdminAsync();
            await ExecuteIdentifierAsync(bootstrapAdmin, "DROP ROLE IF EXISTS %I", alternateAdmin);
        }

        var collisionDeployment = "collision-a";
        var collisionPrefix = RoleContract.DerivePrefix(
            collisionDeployment, harness.TargetDatabase, harness.SystemHash);
        var collisionRole = collisionPrefix + "_owner";
        await using (var admin = await harness.OpenAdminAsync())
        {
            await ExecuteIdentifierAsync(admin, "CREATE ROLE %I NOLOGIN", collisionRole);
            try
            {
                (await harness.RunEnsureAsync(deployment: collisionDeployment)).AssertFailure(
                    BootstrapExitCodes.RoleCollision, "managed_role_name_collision");
                var state = await ReadRoleStateAsync(admin, collisionRole);
                Assert.Equal((false, null), state);
            }
            finally
            {
                await ExecuteIdentifierAsync(admin, "DROP ROLE IF EXISTS %I", collisionRole);
            }

            await ExecuteIdentifierAsync(admin, "CREATE ROLE %I NOLOGIN", collisionRole);
            await CommentRoleAsync(admin, collisionRole, "foreign-control-plane/v9");
            try
            {
                (await harness.RunEnsureAsync(deployment: collisionDeployment)).AssertFailure(
                    BootstrapExitCodes.RoleCollision, "managed_role_name_collision");
                Assert.Equal((false, "foreign-control-plane/v9"),
                    await ReadRoleStateAsync(admin, collisionRole));
            }
            finally
            {
                await ExecuteIdentifierAsync(admin, "DROP ROLE IF EXISTS %I", collisionRole);
            }
        }

        Assert.Equal(secondaryBefore, await harness.SecondaryFingerprintAsync());
        await using var inspect = await harness.OpenAdminAsync();
        await using var count = new NpgsqlCommand("""
            SELECT count(*) FROM pg_catalog.pg_roles
             WHERE pg_catalog.left(rolname,pg_catalog.length($1)+1)=$1||'_'
            """, inspect);
        count.Parameters.AddWithValue(harness.Contract.Prefix);
        Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Ensure_preserves_v1_password_and_rotate_adds_v2_without_retiring_v1()
    {
        await using var harness = await RoleBootstrapPgHarness.CreateAsync();
        (await harness.RunEnsureAsync()).AssertSuccess();
        var original = harness.V1Password(LoginPurpose.Api);
        var wrongV1 = $"Wrong-v1-{Guid.NewGuid():N}-Z9!";
        var wrongSecrets = Enum.GetValues<LoginPurpose>().ToDictionary(
            purpose => purpose,
            purpose => purpose == LoginPurpose.Api ? wrongV1 : harness.V1Password(purpose));

        (await harness.RunEnsureAsync(wrongSecrets)).AssertFailure(
            BootstrapExitCodes.AuthenticationRejected, "login_authentication_failed");
        await using (await harness.OpenLoginAsync(LoginPurpose.Api, 1, original)) { }
        await AssertAuthenticationRejectedAsync(harness, LoginPurpose.Api, 1, wrongV1);

        var v2 = $"Saydin-api-v2-{Guid.NewGuid():N}-B8!";
        (await harness.RunRotateAsync(LoginPurpose.Api, v2)).AssertSuccess();
        await using (await harness.OpenLoginAsync(LoginPurpose.Api, 1, original)) { }
        await using (await harness.OpenLoginAsync(LoginPurpose.Api, 2, v2)) { }
        await AssertAuthenticationRejectedAsync(harness, LoginPurpose.Api, 1, v2);
        await AssertAuthenticationRejectedAsync(harness, LoginPurpose.Api, 2, original);

        await using (var membershipAdmin = await harness.OpenAdminAsync())
        {
            var apiCapability = harness.Contract.ApiCapability.Name;
            var apiV2 = harness.Contract.Login(LoginPurpose.Api, 2).Name;
            await RoleBootstrapPgHarness.ExecuteAsync(membershipAdmin,
                $"REVOKE {QuoteIdentifier(apiCapability)} FROM {QuoteIdentifier(apiV2)}");
            (await harness.RunVerifyAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "managed_role_membership_set_mismatch");
            await RoleBootstrapPgHarness.ExecuteAsync(membershipAdmin,
                $"GRANT {QuoteIdentifier(apiCapability)} TO {QuoteIdentifier(apiV2)} " +
                "WITH ADMIN FALSE, INHERIT TRUE, SET FALSE");
        }

        var rejectedReplacement = $"Saydin-api-v2-replace-{Guid.NewGuid():N}-C7!";
        (await harness.RunRotateAsync(LoginPurpose.Api, rejectedReplacement)).AssertFailure(
            BootstrapExitCodes.AuthenticationRejected, "login_authentication_failed");
        await using (await harness.OpenLoginAsync(LoginPurpose.Api, 2, v2)) { }
        await AssertAuthenticationRejectedAsync(harness, LoginPurpose.Api, 2, rejectedReplacement);

        (await harness.RunEnsureAsync()).AssertSuccess();
        (await harness.RunVerifyAsync()).AssertSuccess();
        await using var admin = await harness.OpenAdminAsync();
        var v1State = await ReadRoleStateAsync(admin, harness.Contract.Login(LoginPurpose.Api, 1).Name);
        var v2State = await ReadRoleStateAsync(admin, harness.Contract.Login(LoginPurpose.Api, 2).Name);
        Assert.True(v1State.CanLogin);
        Assert.True(v2State.CanLogin);
    }

    [Fact]
    public async Task Runtime_acl_and_control_plane_drift_are_fail_closed()
    {
        await using var harness = await RoleBootstrapPgHarness.CreateAsync();
        (await harness.RunEnsureAsync()).AssertSuccess();

        await using (var api = await harness.OpenLoginAsync(LoginPurpose.Api, 1))
        {
            Assert.Equal("42501", (await RoleBootstrapPgHarness.RejectsAsync(
                api, "CREATE TEMP TABLE forbidden_temp(id integer)")).SqlState);
            Assert.Equal("42501", (await RoleBootstrapPgHarness.RejectsAsync(
                api, "CREATE TABLE public.forbidden_table(id integer)")).SqlState);
            Assert.Equal("42501", (await RoleBootstrapPgHarness.RejectsAsync(
                api, "SELECT * FROM pg_catalog.pg_control_system()")).SqlState);
            Assert.Equal("42501", (await RoleBootstrapPgHarness.RejectsAsync(
                api, $"SET ROLE {QuoteIdentifier(harness.Contract.Owner.Name)}")).SqlState);
        }

        await using (var audit = await harness.OpenLoginAsync(LoginPurpose.Audit, 1))
            Assert.NotNull(await RoleBootstrapPgHarness.ScalarAsync(
                audit, "SELECT system_identifier FROM pg_catalog.pg_control_system()"));
        await using (var exporter = await harness.OpenLoginAsync(LoginPurpose.Exporter, 1))
        {
            Assert.True(await RoleBootstrapPgHarness.ScalarAsync(exporter,
                "SELECT pg_catalog.pg_has_role(current_user,'pg_monitor','MEMBER')") is true);
            Assert.False(await RoleBootstrapPgHarness.ScalarAsync(exporter,
                "SELECT pg_catalog.has_schema_privilege(current_user,'public','USAGE')") is true);
        }
        await using (var migrator = await harness.OpenLoginAsync(LoginPurpose.Migrator, 1))
        {
            await RoleBootstrapPgHarness.ExecuteAsync(migrator,
                $"SET ROLE {QuoteIdentifier(harness.Contract.Owner.Name)}");
            Assert.Equal(harness.Contract.Owner.Name,
                Convert.ToString(await RoleBootstrapPgHarness.ScalarAsync(migrator, "SELECT current_user")));
        }

        await using var admin = await harness.OpenAdminAsync();
        await RoleBootstrapPgHarness.ExecuteAsync(admin, """
            ALTER FUNCTION saydin_principal_retention_control.
                consume_principal_retention_transition()
                SET search_path TO public, pg_temp
            """);
        (await harness.RunVerifyAsync()).AssertFailure(
            BootstrapExitCodes.TopologyRejected,
            "principal_retention_transition_contract_mismatch");
        await RoleBootstrapPgHarness.ExecuteAsync(admin, """
            ALTER FUNCTION saydin_principal_retention_control.
                consume_principal_retention_transition()
                SET search_path TO pg_catalog, pg_temp
            """);
        await RoleBootstrapPgHarness.ExecuteAsync(admin, """
            GRANT EXECUTE ON FUNCTION saydin_principal_retention_control.
                consume_principal_retention_transition() TO PUBLIC
            """);
        (await harness.RunVerifyAsync()).AssertFailure(
            BootstrapExitCodes.TopologyRejected,
            "principal_retention_transition_contract_mismatch");
        await RoleBootstrapPgHarness.ExecuteAsync(admin, """
            REVOKE ALL ON FUNCTION saydin_principal_retention_control.
                consume_principal_retention_transition() FROM PUBLIC
            """);
        (await harness.RunVerifyAsync()).AssertSuccess();

        var bootstrapAdminRole = AdminConnectionBuilder().Username!;
        var functionDriftRole = $"function_drift_{Guid.NewGuid():N}";
        await ExecuteIdentifierAsync(admin, "CREATE ROLE %I NOLOGIN", functionDriftRole);
        try
        {
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"ALTER FUNCTION pg_catalog.pg_control_system() OWNER TO {QuoteIdentifier(functionDriftRole)}");
            (await harness.RunVerifyAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "pg_control_acl_unexpected");
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"ALTER FUNCTION pg_catalog.pg_control_system() OWNER TO {QuoteIdentifier(bootstrapAdminRole)}");

            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"GRANT EXECUTE ON FUNCTION pg_catalog.pg_control_system() TO " +
                $"{QuoteIdentifier(functionDriftRole)} WITH GRANT OPTION");
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"SET ROLE {QuoteIdentifier(functionDriftRole)}; " +
                "GRANT EXECUTE ON FUNCTION pg_catalog.pg_control_system() TO " +
                $"{QuoteIdentifier(harness.Contract.ApiCapability.Name)}; RESET ROLE");
            (await harness.RunVerifyAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "pg_control_acl_mismatch");
        }
        finally
        {
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"SET ROLE {QuoteIdentifier(functionDriftRole)}; " +
                "REVOKE EXECUTE ON FUNCTION pg_catalog.pg_control_system() FROM " +
                $"{QuoteIdentifier(harness.Contract.ApiCapability.Name)}; RESET ROLE");
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"ALTER FUNCTION pg_catalog.pg_control_system() OWNER TO {QuoteIdentifier(bootstrapAdminRole)}");
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"REVOKE ALL ON FUNCTION pg_catalog.pg_control_system() FROM " +
                QuoteIdentifier(functionDriftRole));
            await ExecuteIdentifierAsync(admin, "DROP ROLE IF EXISTS %I", functionDriftRole);
        }
        (await harness.RunVerifyAsync()).AssertSuccess();

        var apiLogin = harness.Contract.Login(LoginPurpose.Api, 1).Name;
        await RoleBootstrapPgHarness.ExecuteAsync(admin,
            $"GRANT {QuoteIdentifier(harness.Contract.Owner.Name)} TO {QuoteIdentifier(apiLogin)} " +
            "WITH ADMIN FALSE, INHERIT FALSE, SET TRUE");
        try
        {
            (await harness.RunVerifyAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "managed_role_membership_set_mismatch");
        }
        finally
        {
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"REVOKE {QuoteIdentifier(harness.Contract.Owner.Name)} FROM {QuoteIdentifier(apiLogin)}");
        }
        (await harness.RunVerifyAsync()).AssertSuccess();

        var apiCapability = harness.Contract.ApiCapability.Name;
        await RoleBootstrapPgHarness.ExecuteAsync(admin,
            $"REVOKE {QuoteIdentifier(apiCapability)} FROM {QuoteIdentifier(apiLogin)}");
        (await harness.RunVerifyAsync()).AssertFailure(
            BootstrapExitCodes.TopologyRejected, "managed_role_membership_set_mismatch");
        (await harness.RunEnsureAsync()).AssertSuccess();

        var alternateGrantor = $"alternate_grantor_{Guid.NewGuid():N}";
        await ExecuteIdentifierAsync(admin, "CREATE ROLE %I NOLOGIN", alternateGrantor);
        try
        {
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"GRANT {QuoteIdentifier(apiCapability)} TO {QuoteIdentifier(alternateGrantor)} " +
                "WITH ADMIN TRUE, INHERIT FALSE, SET TRUE");
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"GRANT {QuoteIdentifier(apiCapability)} TO {QuoteIdentifier(apiLogin)} " +
                $"WITH ADMIN FALSE, INHERIT TRUE, SET FALSE GRANTED BY {QuoteIdentifier(alternateGrantor)}");
            await using (var wrongGrantor = new NpgsqlCommand("""
                SELECT count(*)=1
                  FROM pg_catalog.pg_auth_members membership
                  JOIN pg_catalog.pg_roles granted ON granted.oid=membership.roleid
                  JOIN pg_catalog.pg_roles member ON member.oid=membership.member
                  JOIN pg_catalog.pg_roles grantor ON grantor.oid=membership.grantor
                 WHERE granted.rolname=$1 AND member.rolname=$2 AND grantor.rolname=$3
                """, admin))
            {
                wrongGrantor.Parameters.AddWithValue(apiCapability);
                wrongGrantor.Parameters.AddWithValue(apiLogin);
                wrongGrantor.Parameters.AddWithValue(alternateGrantor);
                Assert.True(await wrongGrantor.ExecuteScalarAsync() is true);
            }
            (await harness.RunVerifyAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "managed_role_membership_set_mismatch");
            (await harness.RunEnsureAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "membership_grantor_ambiguous");
        }
        finally
        {
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"REVOKE {QuoteIdentifier(apiCapability)} FROM {QuoteIdentifier(apiLogin)} " +
                $"GRANTED BY {QuoteIdentifier(alternateGrantor)}");
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"REVOKE {QuoteIdentifier(apiCapability)} FROM {QuoteIdentifier(alternateGrantor)}");
            await ExecuteIdentifierAsync(admin, "DROP ROLE IF EXISTS %I", alternateGrantor);
        }
        (await harness.RunVerifyAsync()).AssertSuccess();

        await RoleBootstrapPgHarness.ExecuteAsync(admin,
            $"ALTER ROLE {QuoteIdentifier(apiLogin)} SET search_path=pg_catalog");
        try
        {
            (await harness.RunVerifyAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "managed_role_attribute_mismatch");
        }
        finally
        {
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"ALTER ROLE {QuoteIdentifier(apiLogin)} RESET ALL");
        }

        await RoleBootstrapPgHarness.ExecuteAsync(admin,
            $"GRANT CONNECT ON DATABASE {QuoteIdentifier(harness.TargetDatabase)} TO " +
            $"{QuoteIdentifier(apiCapability)} WITH GRANT OPTION");
        try
        {
            (await harness.RunVerifyAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "database_acl_set_mismatch");
            (await harness.RunEnsureAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "database_acl_set_mismatch");
        }
        finally
        {
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"REVOKE GRANT OPTION FOR CONNECT ON DATABASE " +
                $"{QuoteIdentifier(harness.TargetDatabase)} FROM {QuoteIdentifier(apiCapability)}");
        }

        await RoleBootstrapPgHarness.ExecuteAsync(admin,
            $"GRANT TEMPORARY ON DATABASE {QuoteIdentifier(harness.TargetDatabase)} TO PUBLIC");
        try
        {
            (await harness.RunEnsureAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "database_acl_set_mismatch");
            Assert.True(await RoleBootstrapPgHarness.ScalarAsync(admin,
                $"SELECT pg_catalog.has_database_privilege('public'," +
                $"'{harness.TargetDatabase}','TEMP')") is true);
        }
        finally
        {
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"REVOKE TEMPORARY ON DATABASE {QuoteIdentifier(harness.TargetDatabase)} FROM PUBLIC");
        }

        var foreignRole = $"foreign_acl_{Guid.NewGuid():N}";
        await ExecuteIdentifierAsync(admin, "CREATE ROLE %I NOLOGIN", foreignRole);
        try
        {
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"GRANT CONNECT ON DATABASE {QuoteIdentifier(harness.TargetDatabase)} TO " +
                QuoteIdentifier(foreignRole));
            (await harness.RunVerifyAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "database_acl_set_mismatch");
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"REVOKE CONNECT ON DATABASE {QuoteIdentifier(harness.TargetDatabase)} FROM " +
                QuoteIdentifier(foreignRole));

            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"GRANT USAGE ON SCHEMA public TO {QuoteIdentifier(apiLogin)}");
            (await harness.RunVerifyAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "schema_acl_set_mismatch");
            (await harness.RunEnsureAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "schema_acl_set_mismatch");
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"REVOKE USAGE ON SCHEMA public FROM {QuoteIdentifier(apiLogin)}");

            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"GRANT TEMPORARY ON DATABASE {QuoteIdentifier(harness.TargetDatabase)} TO " +
                QuoteIdentifier(apiLogin));
            (await harness.RunVerifyAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "database_acl_set_mismatch");
            (await harness.RunEnsureAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "database_acl_set_mismatch");
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"REVOKE TEMPORARY ON DATABASE {QuoteIdentifier(harness.TargetDatabase)} FROM " +
                QuoteIdentifier(apiLogin));

            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"GRANT CREATE ON SCHEMA public TO {QuoteIdentifier(apiLogin)}");
            (await harness.RunVerifyAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "schema_acl_set_mismatch");
            (await harness.RunEnsureAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "schema_acl_set_mismatch");
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"REVOKE CREATE ON SCHEMA public FROM {QuoteIdentifier(apiLogin)}");

            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"GRANT USAGE ON SCHEMA public TO {QuoteIdentifier(foreignRole)}");
            (await harness.RunVerifyAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "schema_acl_set_mismatch");
            (await harness.RunEnsureAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "schema_acl_set_mismatch");
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"REVOKE USAGE ON SCHEMA public FROM {QuoteIdentifier(foreignRole)}");
        }
        finally
        {
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"REVOKE CONNECT ON DATABASE {QuoteIdentifier(harness.TargetDatabase)} FROM " +
                QuoteIdentifier(foreignRole));
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"REVOKE USAGE ON SCHEMA public FROM {QuoteIdentifier(foreignRole)}");
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"REVOKE TEMPORARY ON DATABASE {QuoteIdentifier(harness.TargetDatabase)} FROM " +
                QuoteIdentifier(apiLogin));
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"REVOKE CREATE, USAGE ON SCHEMA public FROM {QuoteIdentifier(apiLogin)}");
            await ExecuteIdentifierAsync(admin, "DROP ROLE IF EXISTS %I", foreignRole);
        }

        var adminRole = AdminConnectionBuilder().Username ??
                        throw new InvalidOperationException("admin username unavailable");
        await RoleBootstrapPgHarness.ExecuteAsync(admin,
            $"ALTER DATABASE {QuoteIdentifier(harness.TargetDatabase)} OWNER TO {QuoteIdentifier(adminRole)}");
        try
        {
            (await harness.RunEnsureAsync()).AssertFailure(
                BootstrapExitCodes.TopologyRejected, "database_owner_mismatch");
        }
        finally
        {
            await RoleBootstrapPgHarness.ExecuteAsync(admin,
                $"ALTER DATABASE {QuoteIdentifier(harness.TargetDatabase)} OWNER TO " +
                QuoteIdentifier(harness.Contract.Owner.Name));
        }
        (await harness.RunVerifyAsync()).AssertSuccess();
    }

    private static async Task AssertExactRoleAndMembershipTopologyAsync(RoleBootstrapPgHarness harness)
    {
        await using var admin = await harness.OpenAdminAsync();
        await using var roles = new NpgsqlCommand("""
            SELECT count(*),
                   count(*) FILTER (WHERE role.rolsuper OR role.rolcreatedb OR role.rolcreaterole OR
                       role.rolinherit OR role.rolreplication OR role.rolbypassrls OR
                       (role.rolname<>$2 AND role.rolconnlimit<>-1) OR
                       (role.rolname=$2 AND role.rolconnlimit<>0) OR
                       role.rolconfig IS NOT NULL),
                   count(*) FILTER (WHERE role.rolcanlogin),
                   count(*) FILTER (WHERE NOT role.rolcanlogin),
                   count(*) FILTER (WHERE role.rolname=$2 AND role.rolcanlogin
                       AND role.rolconnlimit=0 AND auth.rolpassword IS NULL)
              FROM pg_catalog.pg_roles role
              JOIN pg_catalog.pg_authid auth ON auth.oid=role.oid
             WHERE pg_catalog.left(role.rolname,pg_catalog.length($1)+1)=$1||'_'
            """, admin);
        roles.Parameters.AddWithValue(harness.Contract.Prefix);
        roles.Parameters.AddWithValue(harness.Contract.TimescaleScheduler.Name);
        await using (var reader = await roles.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(14, reader.GetInt64(0));
            Assert.Equal(0, reader.GetInt64(1));
            Assert.Equal(7, reader.GetInt64(2));
            Assert.Equal(7, reader.GetInt64(3));
            Assert.Equal(1, reader.GetInt64(4));
        }

        await using var membership = new NpgsqlCommand("""
            SELECT count(*),
                   count(*) FILTER (WHERE admin_option),
                   count(*) FILTER (WHERE inherit_option),
                   count(*) FILTER (WHERE set_option),
                   count(*) FILTER (WHERE grantor.rolname=$2)
              FROM pg_catalog.pg_auth_members membership
              JOIN pg_catalog.pg_roles granted ON granted.oid=membership.roleid
              JOIN pg_catalog.pg_roles member ON member.oid=membership.member
              JOIN pg_catalog.pg_roles grantor ON grantor.oid=membership.grantor
             WHERE pg_catalog.left(granted.rolname,pg_catalog.length($1)+1)=$1||'_'
                OR pg_catalog.left(member.rolname,pg_catalog.length($1)+1)=$1||'_'
            """, admin);
        membership.Parameters.AddWithValue(harness.Contract.Prefix);
        membership.Parameters.AddWithValue(AdminConnectionBuilder().Username!);
        await using var membershipReader = await membership.ExecuteReaderAsync();
        Assert.True(await membershipReader.ReadAsync());
        Assert.Equal(9, membershipReader.GetInt64(0));
        Assert.Equal(0, membershipReader.GetInt64(1));
        Assert.Equal(7, membershipReader.GetInt64(2));
        Assert.Equal(2, membershipReader.GetInt64(3));
        Assert.Equal(9, membershipReader.GetInt64(4));
        await membershipReader.DisposeAsync();

        Assert.Equal(harness.Contract.Owner.Name,
            Convert.ToString(await RoleBootstrapPgHarness.ScalarAsync(admin, """
                SELECT pg_catalog.pg_get_userbyid(datdba)
                  FROM pg_catalog.pg_database WHERE datname=current_database()
                """)));
        Assert.True(await RoleBootstrapPgHarness.ScalarAsync(admin, """
            SELECT NOT pg_catalog.has_database_privilege('public',current_database(),'CONNECT')
               AND NOT pg_catalog.has_database_privilege('public',current_database(),'TEMP')
               AND NOT pg_catalog.has_schema_privilege('public','public','USAGE')
               AND NOT pg_catalog.has_schema_privilege('public','public','CREATE')
            """) is true);
        await using (var schedulerAcl = new NpgsqlCommand("""
            SELECT pg_catalog.has_database_privilege($1,current_database(),'CONNECT')
               AND NOT pg_catalog.has_database_privilege($1,current_database(),'TEMP')
               AND pg_catalog.has_schema_privilege($1,'public','USAGE')
               AND NOT pg_catalog.has_schema_privilege($1,'public','CREATE')
               AND NOT pg_catalog.has_function_privilege(
                   $1,'pg_catalog.pg_control_system()','EXECUTE')
            """, admin))
        {
            schedulerAcl.Parameters.AddWithValue(harness.Contract.TimescaleScheduler.Name);
            Assert.True(await schedulerAcl.ExecuteScalarAsync() is true);
        }
        await using (var internalSchema = new NpgsqlCommand("""
            WITH target AS (
                SELECT namespace.nspowner,namespace.nspacl,extension.extowner
                  FROM pg_catalog.pg_namespace namespace
                  JOIN pg_catalog.pg_extension extension ON extension.extname='timescaledb'
                 WHERE namespace.nspname='_timescaledb_internal'),
            actual AS (
                SELECT coalesce(grantee.rolname,'PUBLIC') AS grantee,
                       grantor.rolname AS grantor,acl.privilege_type,acl.is_grantable
                  FROM target
                  CROSS JOIN LATERAL pg_catalog.aclexplode(
                      coalesce(target.nspacl,pg_catalog.acldefault('n',target.nspowner))) acl
                  LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                  LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor),
            expected(grantee,grantor,privilege_type,is_grantable) AS (VALUES
                ($1,$1,'CREATE',false),($1,$1,'USAGE',false),
                ('PUBLIC',$1,'USAGE',false),($2,$1,'CREATE',false))
            SELECT (SELECT count(*)=1 AND bool_and(nspowner=extowner) FROM target)
               AND NOT EXISTS ((SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)
                               UNION ALL
                               (SELECT * FROM expected EXCEPT ALL SELECT * FROM actual))
            """, admin))
        {
            internalSchema.Parameters.AddWithValue(AdminConnectionBuilder().Username!);
            internalSchema.Parameters.AddWithValue(harness.Contract.TimescaleScheduler.Name);
            Assert.True(await internalSchema.ExecuteScalarAsync() is true);
        }

        var schedulerBuilder = AdminConnectionBuilder();
        schedulerBuilder.Database = harness.TargetDatabase;
        schedulerBuilder.Username = harness.Contract.TimescaleScheduler.Name;
        schedulerBuilder.Password = $"SchedulerMustNotAuthenticate_{Guid.NewGuid():N}";
        schedulerBuilder.Pooling = false;
        var schedulerFailure = await Assert.ThrowsAnyAsync<NpgsqlException>(async () =>
        {
            await using var scheduler = new NpgsqlConnection(schedulerBuilder.ConnectionString);
            await scheduler.OpenAsync();
        });
        var schedulerSqlState = schedulerFailure is PostgresException postgres
            ? postgres.SqlState
            : (schedulerFailure.InnerException as PostgresException)?.SqlState;
        Assert.Contains(schedulerSqlState, new[] { "28P01", "53300" });

        await using var pgControl = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE acl.grantee=0),
                   count(*) FILTER (WHERE role.rolname = ANY($1))
              FROM pg_catalog.pg_proc function
              CROSS JOIN LATERAL pg_catalog.aclexplode(function.proacl) acl
              LEFT JOIN pg_catalog.pg_roles role ON role.oid=acl.grantee
             WHERE function.oid='pg_catalog.pg_control_system()'::pg_catalog.regprocedure
               AND acl.privilege_type='EXECUTE'
            """, admin);
        pgControl.Parameters.AddWithValue(new[]
        {
            harness.Contract.Owner.Name,
            harness.Contract.MigratorCapability.Name,
            harness.Contract.AuditCapability.Name,
        });
        await using var controlReader = await pgControl.ExecuteReaderAsync();
        Assert.True(await controlReader.ReadAsync());
        Assert.Equal(0, controlReader.GetInt64(0));
        Assert.Equal(3, controlReader.GetInt64(1));
    }

    private static async Task AssertExactExtensionsAsync(RoleBootstrapPgHarness harness)
    {
        await using var admin = await harness.OpenAdminAsync();
        await using var command = new NpgsqlCommand("""
            SELECT extname,extversion,pg_catalog.pg_get_userbyid(extowner),namespace.nspname
              FROM pg_catalog.pg_extension extension
              JOIN pg_catalog.pg_namespace namespace ON namespace.oid=extension.extnamespace
             WHERE extname IN ('timescaledb','uuid-ossp') ORDER BY extname COLLATE "C"
            """, admin);
        var rows = new List<(string, string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        var adminRole = AdminConnectionBuilder().Username ??
                        throw new InvalidOperationException("admin username unavailable");
        Assert.Equal(new[]
        {
            ("timescaledb", harness.TimescaleVersion, adminRole, "public"),
            ("uuid-ossp", harness.UuidVersion, adminRole, "public"),
        }, rows);
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
        Assert.True(exception is PostgresException { SqlState: "28P01" } ||
                    exception.InnerException is PostgresException { SqlState: "28P01" });
    }

    private static async Task ExecuteIdentifierAsync(
        NpgsqlConnection connection,
        string format,
        string identifier)
    {
        await using var command = new NpgsqlCommand("SELECT pg_catalog.format($1,$2)", connection);
        command.Parameters.AddWithValue(format);
        command.Parameters.AddWithValue(identifier);
        var sql = Convert.ToString(await command.ExecuteScalarAsync())!;
        await RoleBootstrapPgHarness.ExecuteAsync(connection, sql);
    }

    private static async Task<NpgsqlConnection> OpenClusterAdminAsync()
    {
        var builder = AdminConnectionBuilder();
        builder.Database = "postgres";
        builder.Pooling = false;
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static NpgsqlConnectionStringBuilder AdminConnectionBuilder()
    {
        var file = Environment.GetEnvironmentVariable(
            "SAYDIN_ROLE_BOOTSTRAP_TEST_ADMIN_CONNECTION_FILE") ??
            throw new InvalidOperationException(
                "SAYDIN_ROLE_BOOTSTRAP_TEST_ADMIN_CONNECTION_FILE is required");
        return new NpgsqlConnectionStringBuilder(SecureSecretFile.ReadConnectionString(file));
    }

    private static async Task<object?> ScalarWithParameterAsync(
        NpgsqlConnection connection,
        string sql,
        params string[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return await command.ExecuteScalarAsync();
    }

    private static async Task CommentRoleAsync(
        NpgsqlConnection connection,
        string role,
        string marker)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.format('COMMENT ON ROLE %I IS %L',$1,$2)", connection);
        command.Parameters.AddWithValue(role);
        command.Parameters.AddWithValue(marker);
        await RoleBootstrapPgHarness.ExecuteAsync(
            connection, Convert.ToString(await command.ExecuteScalarAsync())!);
    }

    private static async Task<(bool CanLogin, string? Marker)> ReadRoleStateAsync(
        NpgsqlConnection connection,
        string role)
    {
        await using var command = new NpgsqlCommand("""
            SELECT rolcanlogin,pg_catalog.shobj_description(oid,'pg_authid')
              FROM pg_catalog.pg_roles WHERE rolname=$1
            """, connection);
        command.Parameters.AddWithValue(role);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetBoolean(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static string QuoteIdentifier(string value) =>
        '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
