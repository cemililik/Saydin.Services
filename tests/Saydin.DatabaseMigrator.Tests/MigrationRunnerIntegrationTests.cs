using System.Diagnostics;
using FluentAssertions;
using Npgsql;
using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseMigrator.Tests;

[Collection("migration-integration")]
public sealed class MigrationRunnerIntegrationTests
{
    [SkippableFact]
    public async Task ImpactTransactional_SignedSmallIndex_PassesPreflightAndPostcondition()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await CreateImpactFixtureTableAsync(database, rowCount: 32);
        using var package = ImpactTestPackage.Create(
            database,
            "CREATE INDEX ix_dbm004_fixture_marker ON public.dbm004_fixture(marker);\n",
            "transactional", ["create-index-nonconcurrent"], "public.dbm004_fixture",
            postconditionKind: "index-valid", postconditionIndex: "ix_dbm004_fixture_marker");

        var result = await new MigrationRunner(
            database.Options(package.MigrationsDirectory,
                impactConfiguration: package.Configuration), TextWriter.Null).RunAsync();

        result.Applied.Should().Be(1);
        (await database.ScalarAsync<string>("""
            SELECT state FROM public.schema_migrations WHERE version='026_impact_test'
            """)).Should().Be("succeeded");
        (await database.ScalarAsync<bool>("""
            SELECT indisvalid AND indisready
              FROM pg_catalog.pg_index
             WHERE indexrelid='public.ix_dbm004_fixture_marker'::pg_catalog.regclass
            """)).Should().BeTrue();
    }

    [SkippableFact]
    public async Task ImpactPreflight_RestoresPreexistingSessionTimeouts()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await CreateImpactFixtureTableAsync(database, rowCount: 4);
        using var package = ImpactTestPackage.Create(
            database,
            "CREATE INDEX ix_timeout_restore_probe ON public.dbm004_fixture(marker);\n",
            "transactional", ["create-index-nonconcurrent"], "public.dbm004_fixture",
            postconditionKind: "index-valid", postconditionIndex: "ix_timeout_restore_probe");
        var options = database.Options(
            package.MigrationsDirectory, impactConfiguration: package.Configuration);
        var definition = package.Manifest.Migrations.Single(
            migration => migration.Version == "026_impact_test");
        var impact = MigrationImpactSet.LoadAndVerify(
            package.Manifest, MigratorMigrationTrustRoot.Versions.Count, package.Configuration)
            .For(definition.Version);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var setLock = new NpgsqlCommand("SET lock_timeout='321ms'", connection))
            await setLock.ExecuteNonQueryAsync();
        await using (var setStatement = new NpgsqlCommand(
                         "SET statement_timeout='6543ms'", connection))
            await setStatement.ExecuteNonQueryAsync();

        await MigrationImpactPreflight.VerifyAsync(
            connection, options, definition, impact, CancellationToken.None);

        await using var current = new NpgsqlCommand(
            "SELECT current_setting('lock_timeout'),current_setting('statement_timeout')", connection);
        await using var reader = await current.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("321ms");
        reader.GetString(1).Should().Be("6543ms");
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [SkippableFact]
    public async Task ImpactLargeOperation_WithoutManifest_RejectsBeforeDatabaseMutation()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await CreateImpactFixtureTableAsync(database, rowCount: 1);
        using var package = ImpactTestPackage.Create(
            database, "UPDATE public.dbm004_fixture SET marker='x';\n",
            "transactional", ["large-dml"], "public.dbm004_fixture");

        var action = async () => await new MigrationRunner(
            database.Options(package.MigrationsDirectory), TextWriter.Null).RunAsync();

        (await action.Should().ThrowAsync<MigratorRejectedException>()).Which.Code
            .Should().Be("migration_impact_configuration_required");
        await AssertImpactPreflightLeftDatabaseUnmutatedAsync(database);
    }

    [SkippableFact]
    public async Task ImpactPreflight_DiskHeadroomAndWrongTarget_RejectBeforeDatabaseMutation()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await CreateImpactFixtureTableAsync(database, rowCount: 1);
        using (var disk = ImpactTestPackage.Create(
                   database,
                   "CREATE INDEX ix_dbm004_fixture_marker ON public.dbm004_fixture(marker);\n",
                   "transactional", ["create-index-nonconcurrent"], "public.dbm004_fixture",
                   postconditionKind: "index-valid", postconditionIndex: "ix_dbm004_fixture_marker",
                   mutateBudgets: budgets => budgets["declaredTablespaceCapacityBytes"] = 1L))
        {
            var action = async () => await new MigrationRunner(
                database.Options(disk.MigrationsDirectory,
                    impactConfiguration: disk.Configuration), TextWriter.Null).RunAsync();
            (await action.Should().ThrowAsync<MigratorRejectedException>()).Which.Code
                .Should().Be("migration_impact_disk_headroom_insufficient");
            await AssertImpactPreflightLeftDatabaseUnmutatedAsync(database);
        }
        using (var target = ImpactTestPackage.Create(
                   database,
                   "CREATE INDEX ix_dbm004_fixture_marker ON public.dbm004_fixture(marker);\n",
                   "transactional", ["create-index-nonconcurrent"], "public.dbm004_fixture",
                   postconditionKind: "index-valid", postconditionIndex: "ix_dbm004_fixture_marker",
                   mutateTarget: values => values["database"] = "wrong_target"))
        {
            var action = async () => await new MigrationRunner(
                database.Options(target.MigrationsDirectory,
                    impactConfiguration: target.Configuration), TextWriter.Null).RunAsync();
            (await action.Should().ThrowAsync<MigratorRejectedException>()).Which.Code
                .Should().Be("migration_impact_target_mismatch");
            await AssertImpactPreflightLeftDatabaseUnmutatedAsync(database);
        }
    }

    [SkippableFact]
    public async Task ImpactPreflight_OldRelationLockAndInactiveWalSlot_RejectWithinBudget()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await CreateImpactFixtureTableAsync(database, rowCount: 1);
        using var package = ImpactTestPackage.Create(
            database,
            "CREATE INDEX ix_dbm004_fixture_marker ON public.dbm004_fixture(marker);\n",
            "transactional", ["create-index-nonconcurrent"], "public.dbm004_fixture",
            postconditionKind: "index-valid", postconditionIndex: "ix_dbm004_fixture_marker",
            mutateBudgets: budgets => budgets["maxBlockingTransactionAgeSeconds"] = 1);

        await using (var blocker = new NpgsqlConnection(database.ConnectionString))
        {
            await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand(
                             "LOCK TABLE public.dbm004_fixture IN ACCESS EXCLUSIVE MODE", blocker, transaction))
                await command.ExecuteNonQueryAsync();
            await database.ExecuteAsync("SELECT pg_catalog.pg_sleep(1.1)");
            var action = async () => await new MigrationRunner(
                database.Options(package.MigrationsDirectory,
                    impactConfiguration: package.Configuration), TextWriter.Null).RunAsync();
            (await action.Should().ThrowAsync<MigratorRejectedException>()).Which.Code
                .Should().Be("migration_impact_lock_budget_exceeded");
            await transaction.RollbackAsync();
        }
        await AssertImpactPreflightLeftDatabaseUnmutatedAsync(database);

        var slotName = $"dbm004_{Guid.NewGuid():N}";
        await database.ExecuteAsync($"SELECT * FROM pg_catalog.pg_create_physical_replication_slot('{slotName}',true)");
        try
        {
            var action = async () => await new MigrationRunner(
                database.Options(package.MigrationsDirectory,
                    impactConfiguration: package.Configuration), TextWriter.Null).RunAsync();
            (await action.Should().ThrowAsync<MigratorRejectedException>()).Which.Code
                .Should().Be("migration_impact_slot_budget_exceeded");
            await AssertImpactPreflightLeftDatabaseUnmutatedAsync(database);
        }
        finally
        {
            await database.ExecuteAsync(
                $"SELECT pg_catalog.pg_drop_replication_slot('{slotName}')");
        }
    }

    [SkippableFact]
    public async Task ImpactOnline_KillAfterCommittedBatch_ResumesWithoutSkipOrDuplicate()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        const int rowCount = 257;
        await CreateImpactFixtureTableAsync(database, rowCount);
        var plan = OnlineFixturePlan("public.dbm004_fixture", batchSize: 41);
        using var package = ImpactTestPackage.Create(
            database, "-- bounded generated execution only\n",
            "resumable-online", ["resumable-online"], "public.dbm004_fixture", plan,
            postconditionKind: "column-no-null", postconditionColumn: "marker");
        var options = database.Options(
            package.MigrationsDirectory, impactConfiguration: package.Configuration);
        var interrupted = async () => await new MigrationRunner(
            options, TextWriter.Null, new CancelAfterFirstOnlineCommit("026_impact_test")).RunAsync();

        await interrupted.Should().ThrowAsync<OperationCanceledException>();
        var afterKill = await database.ScalarAsync<long>(
            "SELECT count(*) FROM public.dbm004_fixture WHERE marker='redacted'");
        afterKill.Should().Be(41);

        await database.ExecuteAsync("""
            ALTER TABLE public.saydin_online_migration_checkpoints
                ADD COLUMN unauthorized_probe integer NULL
            """);
        var tampered = async () => await new MigrationRunner(options, TextWriter.Null).RunAsync();
        var tamperFailure = (await tampered.Should().ThrowAsync<MigratorRejectedException>()).Which;
        tamperFailure.Code.Should().Be("migration_online_failed");
        tamperFailure.InnerException.Should().BeOfType<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_online_checkpoint_contract_mismatch");
        await database.ExecuteAsync("""
            ALTER TABLE public.saydin_online_migration_checkpoints
                DROP COLUMN unauthorized_probe
            """);

        var resumed = await new MigrationRunner(options, TextWriter.Null).RunAsync();
        resumed.Applied.Should().Be(1);
        (await database.ScalarAsync<long>(
            "SELECT count(*) FROM public.dbm004_fixture WHERE marker='redacted'"))
            .Should().Be(rowCount);
        (await database.ScalarAsync<long>("""
            SELECT processed_rows FROM public.saydin_online_migration_checkpoints
             WHERE migration_version='026_impact_test' AND state='succeeded'
            """)).Should().Be(rowCount);

        var duplicate = await new MigrationRunner(options, TextWriter.Null).RunAsync();
        duplicate.Applied.Should().Be(0);
        duplicate.AlreadyApplied.Should().Be(28, "27 canonical migrations plus the signed 026 tail");
        (await database.ScalarAsync<long>(
            "SELECT count(*) FROM public.dbm004_fixture WHERE marker='redacted'"))
            .Should().Be(rowCount);
    }

    [SkippableFact]
    public async Task ImpactOnline_ForeignLeaseNonceAfterCommit_IsRejectedBeforeNextBatch()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await CreateImpactFixtureTableAsync(database, rowCount: 90);
        var plan = OnlineFixturePlan("public.dbm004_fixture", batchSize: 30);
        using var package = ImpactTestPackage.Create(
            database, "-- bounded generated execution only\n",
            "resumable-online", ["resumable-online"], "public.dbm004_fixture", plan,
            postconditionKind: "column-no-null", postconditionColumn: "marker");
        var options = database.Options(
            package.MigrationsDirectory, impactConfiguration: package.Configuration);

        var action = async () => await new MigrationRunner(
            options, TextWriter.Null,
            new ReplaceOnlineLeaseAfterFirstBody("026_impact_test")).RunAsync();

        var failure = (await action.Should().ThrowAsync<MigratorRejectedException>()).Which;
        failure.Code.Should().Be("migration_online_failed");
        failure.InnerException.Should().BeOfType<MigratorRejectedException>()
            .Which.Code.Should().Be("migration_online_lease_lost");
        (await database.ScalarAsync<long>(
            "SELECT count(*) FROM public.dbm004_fixture WHERE marker='redacted'"))
            .Should().Be(30);
    }

    [SkippableFact]
    public async Task ImpactOnline_CompressedHypertable_RestoresPolicyAfterCrashStyleResume()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await CreateCompressedImpactFixtureAsync(database, rowCount: 96);
        var plan = OnlineFixturePlan("public.dbm004_compressed_fixture", batchSize: 29);
        plan["pauseCompressionPolicy"] = true;
        using var package = ImpactTestPackage.Create(
            database, "-- bounded generated execution only\n",
            "resumable-online", ["resumable-online"],
            "public.dbm004_compressed_fixture", plan,
            postconditionKind: "column-no-null", postconditionColumn: "marker",
            includeChunks: true, includeCompressed: true);
        var options = database.Options(
            package.MigrationsDirectory, impactConfiguration: package.Configuration);
        var interrupted = async () => await new MigrationRunner(
            options, TextWriter.Null, new CancelAfterFirstOnlineCommit("026_impact_test")).RunAsync();

        await interrupted.Should().ThrowAsync<OperationCanceledException>();
        (await database.ScalarAsync<bool>("""
            SELECT scheduled FROM timescaledb_information.jobs
             WHERE hypertable_schema='public' AND hypertable_name='dbm004_compressed_fixture'
               AND proc_name='policy_compression'
            """)).Should().BeTrue("bounded exception cleanup must restore the original policy state");
        (await database.ScalarAsync<long>("""
            SELECT count(*) FROM public.dbm004_compressed_fixture WHERE marker='redacted'
            """)).Should().Be(29);

        await database.ExecuteAsync("""
            SELECT public.alter_job(job_id,scheduled=>false)
              FROM timescaledb_information.jobs
             WHERE hypertable_schema='public' AND hypertable_name='dbm004_compressed_fixture'
               AND proc_name='policy_compression'
            """);
        var resumed = await new MigrationRunner(options, TextWriter.Null).RunAsync();

        resumed.Applied.Should().Be(1);
        (await database.ScalarAsync<long>("""
            SELECT count(*) FROM public.dbm004_compressed_fixture WHERE marker='redacted'
            """)).Should().Be(96);
        (await database.ScalarAsync<bool>("""
            SELECT scheduled FROM timescaledb_information.jobs
             WHERE hypertable_schema='public' AND hypertable_name='dbm004_compressed_fixture'
               AND proc_name='policy_compression'
            """)).Should().BeTrue("the durable checkpoint must restore the pre-crash policy state");
        (await database.ScalarAsync<long>("""
            SELECT count(*) FROM timescaledb_information.chunks
             WHERE hypertable_schema='public' AND hypertable_name='dbm004_compressed_fixture'
               AND is_compressed
            """)).Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task BackupPostBootstrap_FreshDatabase_IsPhaseAwareAndFailClosedUntilPostBootstrap()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);

        (await database.ScalarAsync<bool>($"""
            SELECT EXISTS(
                SELECT 1 FROM pg_catalog.pg_roles
                 WHERE rolname='{database.Contract.BackupLogin(1, database.BackupV1ValidUntilUtc).Name}')
            """)).Should().BeFalse("pre-migration role bootstrap must not create the backup identity");

        var first = await new MigrationRunner(
            Options(database.ConnectionString, TestPaths.MigrationsDirectory), TextWriter.Null).RunAsync();

        first.BackupPostBootstrapRequired.Should().BeTrue();
        (await database.ScalarAsync<string>(
            "SELECT state FROM saydin_migration_control WHERE singleton=1")).Should().Be("ready");

        var absent = async () => await new MigrationRunner(
            Options(database.ConnectionString, TestPaths.MigrationsDirectory), TextWriter.Null).RunAsync();
        (await absent.Should().ThrowAsync<MigratorRejectedException>()).Which.Code
            .Should().Be("managed_role_contract_mismatch");

        await database.EnsureRolesAsync();
        var final = await new MigrationRunner(
            Options(database.ConnectionString, TestPaths.MigrationsDirectory), TextWriter.Null).RunAsync();
        final.BackupPostBootstrapRequired.Should().BeFalse();
        final.AlreadyApplied.Should().Be(27);

        var verifyExit = await MigratorApplication.RunAsync(
            ["--verify-only"], ApplicationEnvironment(database.ConnectionString),
            TextWriter.Null, TextWriter.Null);
        verifyExit.Should().Be(0);
    }

    [SkippableFact]
    public async Task BackupPostBootstrap_LegacyUpgrade_IsPhaseAwareAndFailClosedUntilPostBootstrap()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await database.PrepareLegacy014Async();

        var first = await new MigrationRunner(
            Options(database.ConnectionString, TestPaths.MigrationsDirectory, legacyCutover: true),
            TextWriter.Null).RunAsync();

        first.Applied.Should().Be(11, "015 through 025 are pending after the 014 baseline");
        first.BackupPostBootstrapRequired.Should().BeTrue();
        (await database.ScalarAsync<bool>($"""
            SELECT EXISTS(
                SELECT 1 FROM pg_catalog.pg_roles
                 WHERE rolname='{database.Contract.BackupLogin(1, database.BackupV1ValidUntilUtc).Name}')
            """)).Should().BeFalse();

        var absent = async () => await new MigrationRunner(
            Options(database.ConnectionString, TestPaths.MigrationsDirectory), TextWriter.Null).RunAsync();
        (await absent.Should().ThrowAsync<MigratorRejectedException>()).Which.Code
            .Should().Be("managed_role_contract_mismatch");

        await database.EnsureRolesAsync();
        var final = await new MigrationRunner(
            Options(database.ConnectionString, TestPaths.MigrationsDirectory), TextWriter.Null).RunAsync();
        final.BackupPostBootstrapRequired.Should().BeFalse();
        final.AlreadyApplied.Should().Be(27);

        var verifyExit = await MigratorApplication.RunAsync(
            ["--verify-only"], ApplicationEnvironment(database.ConnectionString),
            TextWriter.Null, TextWriter.Null);
        verifyExit.Should().Be(0);
    }

    [SkippableFact]
    public async Task BackupRotation_V1AndV2RemainPhysicalOnlyAndMigratorVerificationStaysStable()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateHbaBoundAsync(
            admin, HbaBoundTestFixture.BackupRotation);
        var migrated = await new MigrationRunner(
            Options(database.ConnectionString, TestPaths.MigrationsDirectory), TextWriter.Null).RunAsync();
        migrated.BackupPostBootstrapRequired.Should().BeTrue();

        await database.EnsureRolesThroughApplicationAsync();
        var postBootstrap = await new MigrationRunner(
            Options(database.ConnectionString, TestPaths.MigrationsDirectory), TextWriter.Null).RunAsync();
        postBootstrap.BackupPostBootstrapRequired.Should().BeFalse();
        postBootstrap.AlreadyApplied.Should().Be(27);

        var rotated = await database.RotateBackupV2Async();
        var v1 = database.Contract.BackupLogin(1, database.BackupV1ValidUntilUtc);
        var v2 = database.Contract.BackupLogin(2, rotated.ValidUntilUtc);
        (await database.ScalarAsync<bool>($"""
            SELECT count(*)=2
               AND bool_and(rolcanlogin AND rolreplication AND NOT rolsuper
                   AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolinherit
                   AND NOT rolbypassrls AND rolconnlimit=2 AND rolconfig IS NULL)
              FROM pg_catalog.pg_roles
             WHERE rolname IN ('{v1.Name}','{v2.Name}')
            """)).Should().BeTrue();
        (await database.ScalarAsync<bool>($"""
            SELECT NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_auth_members membership
                 WHERE membership.roleid IN ('{v1.Name}'::regrole,'{v2.Name}'::regrole)
                    OR membership.member IN ('{v1.Name}'::regrole,'{v2.Name}'::regrole))
               AND NOT has_database_privilege('{v1.Name}',current_database(),'CONNECT')
               AND NOT has_database_privilege('{v2.Name}',current_database(),'CONNECT')
               AND NOT has_schema_privilege('{v1.Name}','public','USAGE')
               AND NOT has_schema_privilege('{v2.Name}','public','USAGE')
               AND NOT has_table_privilege('{v1.Name}','public.assets','SELECT')
               AND NOT has_table_privilege('{v2.Name}','public.assets','SELECT')
            """)).Should().BeTrue();

        foreach (var (role, password) in new[]
                 {
                     (v1, database.BackupV1Password),
                     (v2, rotated.Password),
                 })
        {
            var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            {
                Username = role.Name,
                Password = password,
                Pooling = false,
                IncludeErrorDetail = false,
            };
            await using var sql = new NpgsqlConnection(builder.ConnectionString);
            var connect = async () => await sql.OpenAsync();
            (await connect.Should().ThrowAsync<PostgresException>()).Which.SqlState
                .Should().Be(PostgresErrorCodes.InvalidAuthorizationSpecification,
                    "the exact backup HBA admits physical replication and rejects every SQL database connection");
        }

        var verify = await new MigrationRunner(
            Options(database.ConnectionString, TestPaths.MigrationsDirectory), TextWriter.Null).RunAsync();
        verify.BackupPostBootstrapRequired.Should().BeFalse();
        verify.AlreadyApplied.Should().Be(27);
    }

    [SkippableTheory]
    [InlineData("attribute", "managed_role_contract_mismatch")]
    [InlineData("membership", "managed_role_membership_contract_mismatch")]
    [InlineData("connect", "schema_fingerprint_mismatch")]
    [InlineData("app_select", "schema_fingerprint_mismatch")]
    public async Task BackupIdentityDrift_IsRejectedBeforeMigrationMutation(
        string drift,
        string expectedCode)
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        var backup = database.Contract.BackupLogin(1, database.BackupV1ValidUntilUtc).Name;
        var (mutate, restore) = drift switch
        {
            "attribute" => ($"ALTER ROLE \"{backup}\" NOREPLICATION",
                $"ALTER ROLE \"{backup}\" REPLICATION"),
            "membership" => ($"GRANT \"{database.Contract.AuditCapability.Name}\" TO \"{backup}\"",
                $"REVOKE \"{database.Contract.AuditCapability.Name}\" FROM \"{backup}\""),
            "connect" => ($"GRANT CONNECT ON DATABASE \"{database.Name}\" TO \"{backup}\"",
                $"REVOKE CONNECT ON DATABASE \"{database.Name}\" FROM \"{backup}\""),
            "app_select" => ($"GRANT SELECT ON public.assets TO \"{backup}\"",
                $"REVOKE SELECT ON public.assets FROM \"{backup}\""),
            _ => throw new ArgumentOutOfRangeException(nameof(drift)),
        };

        await database.ExecuteAsync(mutate);
        try
        {
            var action = async () => await new MigrationRunner(
                Options(database.ConnectionString, TestPaths.MigrationsDirectory), TextWriter.Null).RunAsync();

            (await action.Should().ThrowAsync<MigratorRejectedException>()).Which.Code
                .Should().Be(expectedCode);
            (await database.ScalarAsync<string>(
                "SELECT state FROM saydin_migration_control WHERE singleton=1")).Should().Be("ready");
        }
        finally
        {
            await database.ExecuteAsync(restore);
        }

        var final = await new MigrationRunner(
            Options(database.ConnectionString, TestPaths.MigrationsDirectory), TextWriter.Null).RunAsync();
        final.BackupPostBootstrapRequired.Should().BeFalse();
    }

    [SkippableFact]
    public async Task BlankDatabase_AppliesTwentyFiveVersionsAndCreatesTwoHypertables()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);

        var result = await RunAsync(database.ConnectionString);

        result.Applied.Should().Be(27);
        (await database.ScalarAsync<long>("SELECT COUNT(*) FROM schema_migrations WHERE state IN ('succeeded','skipped_optional')"))
            .Should().Be(27);
        (await database.ScalarAsync<long>("SELECT COUNT(*) FROM schema_migrations WHERE checksum IS NOT NULL"))
            .Should().Be(27);
        (await database.ScalarAsync<long>("""
            SELECT COUNT(*) FROM timescaledb_information.hypertables
            WHERE hypertable_schema='public' AND hypertable_name IN ('price_points','activity_logs')
            """)).Should().Be(2);
        (await database.ScalarAsync<string>("SELECT state FROM saydin_migration_control WHERE singleton=1"))
            .Should().Be("ready");

        var assetId = Guid.CreateVersion7();
        var assetSymbol = $"CHUNK{assetId:N}"[..20];
        var leaseToken = Guid.CreateVersion7();
        await database.ExecuteAsync($"""
            INSERT INTO public.assets(id,symbol,display_name,category,is_active,source,source_id)
            VALUES ('{assetId:D}','{assetSymbol}',
                    'Migrator chunk fingerprint fixture','crypto'::public.asset_category,
                    TRUE,'coingecko','chunk-{assetId:N}')
            """);
        var ingestionOptions = database.RuntimeOptions(LoginPurpose.Ingestion);
        await using (var ingestion = await RuntimeDatabase.OpenVerifiedDataSourceAsync(
                         ingestionOptions))
        await using (var connection = await ingestion.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Guid windowId;
            await using (var claim = new NpgsqlCommand("""
                INSERT INTO public.ingestion_windows(
                    source,asset_id,job_type,range_start,range_end,contract_version,
                    state,lease_owner,lease_token,lease_until,attempt_count)
                VALUES ('coingecko',$1,'daily_update','2024-01-03','2024-01-03',1,
                        'running','migrator-chunk-fixture',$2,clock_timestamp()+INTERVAL '5 minutes',1)
                RETURNING id
                """, connection, transaction))
            {
                claim.Parameters.AddWithValue(assetId);
                claim.Parameters.AddWithValue(leaseToken);
                windowId = (Guid)(await claim.ExecuteScalarAsync())!;
            }

            await using (var capability = new NpgsqlCommand("""
                SELECT set_config('saydin.ingestion_window_id',$1,TRUE),
                       set_config('saydin.ingestion_lease_token',$2,TRUE)
                """, connection, transaction))
            {
                capability.Parameters.AddWithValue(windowId.ToString("D"));
                capability.Parameters.AddWithValue(leaseToken.ToString("D"));
                await capability.ExecuteNonQueryAsync();
            }

            await using (var write = new NpgsqlCommand("""
                WITH evidence(raw) AS (VALUES(jsonb_build_object(
                  'as_of_at','2024-01-03T00:00:00Z','close',1,'date','2024-01-03',
                  'observation_id','coingecko:'||$2||':try:1704240000000',
                  'provider_source','coingecko','quote_currency','TRY',
                  'source_timestamp_ms',1704240000000,'symbol',$2)))
                INSERT INTO public.price_points(
                  asset_id,price_date,close,provider_source,source_observation_id,as_of_at,
                  price_kind,is_final,observation_sha256,authority_contract_version,source_raw)
                SELECT $1,'2024-01-03',1,'coingecko',
                       'coingecko:'||$2||':try:1704240000000','2024-01-03Z',
                       'daily_utc_reference',true,
                       sha256(convert_to(saydin_canonical_observation(raw)::text,'UTF8')),1,raw
                  FROM evidence
                """, connection, transaction))
            {
                write.Parameters.AddWithValue(assetId);
                write.Parameters.AddWithValue($"chunk-{assetId:N}");
                await write.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }
        (await database.ScalarAsync<long>("""
            SELECT count(*) FROM timescaledb_information.chunks
             WHERE hypertable_schema='public' AND hypertable_name='price_points'
            """)).Should().BeGreaterThan(0);
        (await database.ScalarAsync<long>("""
            SELECT count(*) FROM pg_trigger
             WHERE tgname='trg_price_points_ingestion_fence' AND NOT tgisinternal
            """)).Should().BeGreaterThan(1,
                "TimescaleDB propagates the root fence trigger to new chunks");

        var chunk = await database.PriceChunkAsync(assetId, new DateOnly(2024, 1, 3));
        await using (var ingestion = await RuntimeDatabase.OpenVerifiedDataSourceAsync(
                         database.RuntimeOptions(LoginPurpose.Ingestion)))
        await using (var connection = await ingestion.OpenConnectionAsync())
        {
            (await database.ScalarAsync<bool>($"""
                SELECT has_table_privilege(
                    '{database.Contract.Login(LoginPurpose.Ingestion, 1).Name}',
                    '{chunk.Schema}.{chunk.Table}','SELECT')
                """)).Should().BeTrue(
                    "TimescaleDB propagates the root table grant to the physical chunk");
            await using var directChunk = new NpgsqlCommand(
                $"SELECT 1 FROM {Quote(chunk.Schema)}.{Quote(chunk.Table)} LIMIT 1", connection);
            (await directChunk.ExecuteScalarAsync()).Should().Be(1,
                "TimescaleDB requires inherited internal-schema USAGE and mirrors root SELECT onto chunks");

            await using var directInsert = new NpgsqlCommand($"""
                INSERT INTO {Quote(chunk.Schema)}.{Quote(chunk.Table)}(
                    asset_id,price_date,close,ingested_at)
                VALUES ($1,'2024-01-03',2,clock_timestamp())
                """, connection);
            directInsert.Parameters.AddWithValue(assetId);
            var insert = async () => await directInsert.ExecuteNonQueryAsync();
            (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState
                .Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                    "the propagated fence must reject a direct chunk write without a live window");

            await using var directUpdate = new NpgsqlCommand($"""
                UPDATE {Quote(chunk.Schema)}.{Quote(chunk.Table)} SET close=2
                 WHERE asset_id=$1 AND price_date='2024-01-03'
                """, connection);
            directUpdate.Parameters.AddWithValue(assetId);
            var update = async () => await directUpdate.ExecuteNonQueryAsync();
            (await update.Should().ThrowAsync<PostgresException>()).Which.SqlState
                .Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                    "the propagated fence must reject a direct chunk update without a live window");
        }

        var foreignRole = $"saydin_foreign_{Guid.NewGuid():N}"[..31];
        await database.ExecuteAsync($"CREATE ROLE {Quote(foreignRole)} NOLOGIN");
        try
        {
            (await database.ScalarAsync<bool>($"""
                SELECT NOT has_schema_privilege(
                           '{foreignRole}','_timescaledb_internal','USAGE')
                   AND NOT has_table_privilege(
                           '{foreignRole}','{chunk.Schema}.{chunk.Table}','SELECT')
                """)).Should().BeTrue();
            await using var foreign = new NpgsqlConnection(database.ConnectionString);
            await foreign.OpenAsync();
            await using var transaction = await foreign.BeginTransactionAsync();
            await using (var becomeForeign = new NpgsqlCommand(
                             $"SET LOCAL ROLE {Quote(foreignRole)}", foreign, transaction))
                await becomeForeign.ExecuteNonQueryAsync();
            await using var directForeign = new NpgsqlCommand(
                $"SELECT 1 FROM {Quote(chunk.Schema)}.{Quote(chunk.Table)} LIMIT 1",
                foreign, transaction);
            var direct = async () => await directForeign.ExecuteScalarAsync();
            (await direct.Should().ThrowAsync<PostgresException>()).Which.SqlState
                .Should().Be(PostgresErrorCodes.InsufficientPrivilege);

            await database.ExecuteAsync($"""
                GRANT USAGE ON SCHEMA _timescaledb_internal TO {Quote(foreignRole)}
                """);
            var driftedEnsure = () => database.EnsureRolesThroughApplicationAsync();
            (await driftedEnsure.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("timescale_transition_not_consumed");
            await database.ExecuteAsync($"""
                REVOKE USAGE ON SCHEMA _timescaledb_internal FROM {Quote(foreignRole)}
                """);
        }
        finally
        {
            await database.ExecuteAsync($"""
                REVOKE USAGE ON SCHEMA _timescaledb_internal FROM {Quote(foreignRole)}
                """);
            await database.ExecuteAsync($"DROP ROLE IF EXISTS {Quote(foreignRole)}");
        }

        await database.EnsureRolesAsync();
        await database.EnsureRolesAsync();

        var verifyExit = await MigratorApplication.RunAsync(
            ["--verify-only"], ApplicationEnvironment(database.ConnectionString),
            TextWriter.Null, TextWriter.Null);
        verifyExit.Should().Be(0);

        static string Quote(string identifier) =>
            new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
    }

    [SkippableFact]
    public async Task BlankDatabase_WithSignedTail_AppliesCanonicalPrefixBeforeImpactPreflight()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        using var package = ImpactTestPackage.Create(
            database,
            "CREATE INDEX ix_blank_signed_tail ON public.schema_migrations(state);\n",
            "transactional", ["create-index-nonconcurrent"], "public.schema_migrations",
            postconditionKind: "index-valid", postconditionIndex: "ix_blank_signed_tail");

        var result = await new MigrationRunner(
            database.Options(package.MigrationsDirectory,
                impactConfiguration: package.Configuration), TextWriter.Null,
            allowCanonicalPrefixFixture: true).RunAsync();

        result.Applied.Should().Be(28, "27 canonical migrations plus the signed 026 tail");
        (await database.ScalarAsync<string>(
            "SELECT state FROM schema_migrations WHERE version='026_impact_test'"))
            .Should().Be("succeeded");
        (await database.ScalarAsync<bool>("""
            SELECT indisvalid AND indisready
              FROM pg_catalog.pg_index
             WHERE indexrelid='public.ix_blank_signed_tail'::regclass
            """)).Should().BeTrue();
    }

    [SkippableFact]
    public async Task SchedulerBridgeIsConsumed_DirectLoginRejected_AndScheduledBackgroundCompressionSucceeds()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);

        (await database.ScalarAsync<bool>($"""
            SELECT to_regnamespace('saydin_role_control') IS NULL
               AND NOT has_schema_privilege(
                   '{database.Contract.TimescaleScheduler.Name}',
                   '_timescaledb_internal','CREATE')
            """)).Should().BeTrue();
        (await database.ScalarAsync<long>("""
            SELECT count(*) FROM pg_proc
             WHERE pronamespace='public'::regnamespace AND prosecdef
               AND proname IN ('seal_market_calendar_release',
                   'activate_market_calendar_release','enforce_market_calendar_release_assembly')
            """)).Should().Be(3);

        var denied = () => OpenSchedulerConnectionAsync(database);
        var loginFailure = await denied.Should().ThrowAsync<NpgsqlException>();
        loginFailure.Which.GetBaseException().Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().BeOneOf(
                PostgresErrorCodes.TooManyConnections, PostgresErrorCodes.InvalidPassword);

        await database.ExecuteAsync("""
            INSERT INTO activity_logs(
                id,device_id,action,status_code,created_at)
            VALUES (gen_random_uuid(),'scheduler-bgw-fixture','config_fetch',200,
                    now()-INTERVAL '30 days')
            """);
        var jobId = await database.ScalarAsync<int>("""
            SELECT job_id FROM timescaledb_information.jobs
             WHERE hypertable_schema='public' AND hypertable_name='activity_logs'
            """);
        var startedAt = DateTimeOffset.UtcNow;
        await database.ExecuteAsync($"""
            SELECT public.alter_job({jobId},scheduled=>true,
                schedule_interval=>INTERVAL '1 minute',next_start=>now()+INTERVAL '1 second')
            """);

        var completed = false;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            completed = await database.ScalarAsync<bool>($"""
                SELECT coalesce((SELECT last_run_status='Success'
                                      AND last_successful_finish>='{startedAt:O}'::timestamptz
                                   FROM timescaledb_information.job_stats
                                  WHERE job_id={jobId}),false)
                """);
            if (completed) break;
            await Task.Delay(500);
        }
        completed.Should().BeTrue("the Timescale scheduled background worker must run as the scheduler role");
        (await database.ScalarAsync<bool>($"""
            SELECT owner::text='{database.Contract.TimescaleScheduler.Name}'
              FROM timescaledb_information.jobs WHERE job_id={jobId}
            """)).Should().BeTrue();
        (await database.ScalarAsync<bool>($"""
            SELECT NOT EXISTS (
                SELECT 1 FROM timescaledb_information.chunks chunks
                JOIN pg_namespace namespace ON namespace.nspname=chunks.chunk_schema
                JOIN pg_class relation ON relation.relnamespace=namespace.oid
                                      AND relation.relname=chunks.chunk_name
                WHERE chunks.hypertable_schema='public'
                  AND chunks.hypertable_name='activity_logs'
                  AND pg_get_userbyid(relation.relowner)<>'{database.Contract.TimescaleScheduler.Name}')
            """)).Should().BeTrue("new and compressed activity chunks must remain scheduler-owned");
        (await database.ScalarAsync<long>("""
            SELECT count(*) FROM timescaledb_information.chunks
             WHERE hypertable_schema='public' AND hypertable_name='activity_logs' AND is_compressed
            """)).Should().BeGreaterThan(0);
        await database.ExecuteAsync($"""
            SELECT public.alter_job({jobId},scheduled=>true,
                schedule_interval=>INTERVAL '12 hours',next_start=>now()+INTERVAL '12 hours')
            """);
        var verifyExit = await MigratorApplication.RunAsync(
            ["--verify-only"], ApplicationEnvironment(database.ConnectionString),
            TextWriter.Null, TextWriter.Null);
        verifyExit.Should().Be(0,
            "legitimate Timescale source and compressed chunk ACL propagation must remain restart-safe");
    }

    [SkippableFact]
    public async Task MigratorV2RotationAndV1Retirement_PreserveStableContractAndVerifyPasses()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        var contractBefore = await database.ScalarAsync<string>(
            "SELECT contract_sha256 FROM saydin_role_contract WHERE singleton=1");
        var establishedBefore = await database.ScalarAsync<DateTime>(
            "SELECT established_at FROM saydin_role_contract WHERE singleton=1");

        await database.RotateMigratorV2Async();

        var v2Result = await new MigrationRunner(
            database.Options(TestPaths.MigrationsDirectory, loginVersion: 2),
            TextWriter.Null).RunAsync();
        v2Result.Applied.Should().Be(0);
        v2Result.AlreadyApplied.Should().Be(27);
        var verifyExit = await MigratorApplication.RunAsync(
            ["--verify-only"], database.ApplicationEnvironment(loginVersion: 2),
            TextWriter.Null, TextWriter.Null);
        verifyExit.Should().Be(0);
        (await database.ScalarAsync<string>(
            "SELECT contract_sha256 FROM saydin_role_contract WHERE singleton=1"))
            .Should().Be(contractBefore);
        (await database.ScalarAsync<DateTime>(
            "SELECT established_at FROM saydin_role_contract WHERE singleton=1"))
            .Should().Be(establishedBefore);
        (await database.ScalarAsync<long>($"""
            SELECT count(*) FROM pg_roles
             WHERE rolname IN ('{database.Contract.Login(LoginPurpose.Migrator, 1).Name}',
                               '{database.Contract.Login(LoginPurpose.Migrator, 2).Name}')
               AND rolcanlogin
            """)).Should().Be(2, "rotation must not retire the still-valid v1 login");

        await database.RetireMigratorV1Async();
        var postRetirement = await new MigrationRunner(
            database.Options(TestPaths.MigrationsDirectory, loginVersion: 2),
            TextWriter.Null).RunAsync();
        postRetirement.Applied.Should().Be(0);
        postRetirement.AlreadyApplied.Should().Be(27);
        (await MigratorApplication.RunAsync(
            ["--verify-only"], database.ApplicationEnvironment(loginVersion: 2),
            TextWriter.Null, TextWriter.Null)).Should().Be(0);
        (await database.ScalarAsync<long>($"""
            SELECT count(*) FROM pg_roles
             WHERE rolname='{database.Contract.Login(LoginPurpose.Migrator, 1).Name}'
            """)).Should().Be(0);
    }

    [SkippableTheory]
    [InlineData("trigger_type")]
    [InlineData("function_body")]
    public async Task VerifyOnly_RejectsWriterFenceFingerprintDrift(string drift)
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        if (drift == "trigger_type")
        {
            await database.ExecuteAsync("""
                DROP TRIGGER trg_price_points_ingestion_fence ON public.price_points;
                CREATE TRIGGER trg_price_points_ingestion_fence
                AFTER DELETE ON public.price_points
                FOR EACH ROW EXECUTE FUNCTION public.enforce_price_point_ingestion_fence();
                """);
        }
        else
        {
            await database.ExecuteAsync("""
                CREATE OR REPLACE FUNCTION public.enforce_price_point_ingestion_fence()
                RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
                BEGIN RETURN NEW; END
                $$;
                """);
        }
        var error = new StringWriter();

        var exit = await MigratorApplication.RunAsync(
            ["--verify-only"], ApplicationEnvironment(database.ConnectionString),
            TextWriter.Null, error);

        exit.Should().Be(3);
        error.ToString().Should().Contain("code=schema_fingerprint_mismatch");
        error.ToString().Should().Contain("fingerprint=ingestion_write_fence_missing");
    }

    [SkippableFact]
    public async Task AuthoritativeCalendars_AreExactSealedAndDatabaseGuarded()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);

        (await database.ScalarAsync<long>("SELECT count(*) FROM market_calendar_days"))
            .Should().Be(8_630);
        (await database.ScalarAsync<long>("SELECT count(*) FROM market_calendar_release_sources"))
            .Should().Be(274);
        (await database.ScalarAsync<long>("""
            SELECT count(*) FROM market_calendar_days day
            JOIN market_calendar_releases release ON release.id=day.release_id
            WHERE (release.calendar_code='tcmb_indicative_fx'
                   AND day.calendar_date='2026-08-17' AND day.observation_expected)
               OR (release.calendar_code='bist_pay_xist'
                   AND day.calendar_date='2026-10-28'
                   AND day.market_state='partial_session' AND day.observation_expected)
               OR (release.calendar_code='bist_pay_xist'
                   AND day.calendar_date='2026-10-29'
                   AND day.market_state='closed' AND NOT day.observation_expected)
            """)).Should().Be(3);
        (await database.ScalarAsync<long>("""
            SELECT count(*) FROM market_calendar_releases release
            JOIN market_calendar_active_releases active
              ON active.calendar_code=release.calendar_code AND active.release_id=release.id
            WHERE release.sealed_at IS NOT NULL
            """)).Should().Be(2);

        await database.ExecuteAsync("""
            INSERT INTO market_calendar_releases(
                id,calendar_code,snapshot_set_id,release_version,coverage_from,coverage_through,
                row_count,normalized_sha256,source_bundle_sha256,released_at)
            VALUES ('ca100000-0000-7000-8000-000000000099','tcmb_indicative_fx',
                    'unsealed-negative',99,'2099-01-01','2099-01-01',1,
                    repeat('a',64),repeat('b',64),NOW())
            """);
        await AssertSqlStateAsync(() => database.ExecuteAsync("""
            UPDATE market_calendar_releases SET normalized_sha256=repeat('c',64)
             WHERE id='ca100000-0000-7000-8000-000000000099'
            """), "55000");
        await AssertSqlStateAsync(() => database.ExecuteAsync("""
            UPDATE market_calendar_releases SET sealed_at=NOW()
             WHERE id='ca100000-0000-7000-8000-000000000099'
            """), "55000");
        await AssertSqlStateAsync(() => database.ExecuteAsync("""
            UPDATE market_calendar_active_releases
               SET release_id='ca100000-0000-7000-8000-000000000099'
             WHERE calendar_code='tcmb_indicative_fx'
            """), PostgresErrorCodes.CheckViolation);

        const string tcmbRelease = "ca100000-0000-7000-8000-000000000001";
        await AssertSqlStateAsync(() => database.ExecuteAsync($"""
            UPDATE market_calendar_releases SET released_at=NOW() WHERE id='{tcmbRelease}'
            """), "55000");
        await AssertSqlStateAsync(() => database.ExecuteAsync($"""
            DELETE FROM market_calendar_releases WHERE id='{tcmbRelease}'
            """), "55000");
        await AssertSqlStateAsync(() => database.ExecuteAsync($"""
            INSERT INTO market_calendar_release_sources(
                release_id,source_id,source_kind,source_role,source_uri,media_type,
                retrieved_at,raw_sha256,snapshot_path)
            VALUES ('{tcmbRelease}','late-source','tcmbPolicyFaq','policy',
                    'https://www.tcmb.gov.tr/late','text/html',NOW(),repeat('d',64),
                    'snapshots/sha256/' || repeat('d',64) || '.html')
            """), "55000");
        await AssertSqlStateAsync(() => database.ExecuteAsync($"""
            DELETE FROM market_calendar_release_sources
             WHERE release_id='{tcmbRelease}' AND source_id='tcmb-policy-faq'
            """), "55000");
        await AssertSqlStateAsync(() => database.ExecuteAsync($"""
            UPDATE market_calendar_days SET reason_code='forged'
             WHERE release_id='{tcmbRelease}' AND calendar_date='2006-01-01'
            """), "55000");
        await AssertSqlStateAsync(() => database.ExecuteAsync($"""
            UPDATE market_calendar_release_sources
               SET release_id='ca100000-0000-7000-8000-000000000099'
             WHERE release_id='{tcmbRelease}' AND source_id='tcmb-policy-faq'
            """), "55000");
        await AssertSqlStateAsync(() => database.ExecuteAsync($"""
            UPDATE market_calendar_days
               SET release_id='ca100000-0000-7000-8000-000000000099'
             WHERE release_id='{tcmbRelease}' AND calendar_date='2006-01-01'
            """), "55000");
        (await database.ScalarAsync<bool>($"""
            SELECT public.verify_market_calendar_release_payload('{tcmbRelease}')
            """)).Should().BeTrue();
        await AssertSqlStateAsync(() => database.ExecuteAsync("TRUNCATE market_calendar_days"), "55000");
        await AssertSqlStateAsync(() => database.ExecuteAsync(
            "TRUNCATE market_calendar_release_sources CASCADE"), "55000");
        await AssertSqlStateAsync(() => database.ExecuteAsync(
            "TRUNCATE market_calendar_releases CASCADE"), "55000");
        await AssertSqlStateAsync(() => database.ExecuteAsync(
            "DELETE FROM market_calendar_active_releases WHERE calendar_code='tcmb_indicative_fx'"), "55000");
        await AssertSqlStateAsync(() => database.ExecuteAsync(
            "TRUNCATE market_calendar_active_releases"), "55000");

        await AssertSqlStateAsync(() => database.ExecuteAsync("""
            UPDATE asset_market_calendars
               SET calendar_code='bist_pay_xist'
             WHERE asset_id=(SELECT id FROM assets WHERE source='tcmb' ORDER BY symbol LIMIT 1)
            """), "55000");
        await AssertSqlStateAsync(() => database.ExecuteAsync("""
            DELETE FROM asset_market_calendars
             WHERE asset_id=(SELECT id FROM assets WHERE source='tcmb' ORDER BY symbol LIMIT 1)
            """), "55000");
        var bindingCount = await database.ScalarAsync<long>("SELECT count(*) FROM asset_market_calendars");
        await AssertSqlStateAsync(() => database.ExecuteAsync(
            "TRUNCATE asset_market_calendars"), "55000");
        (await database.ScalarAsync<long>("SELECT count(*) FROM asset_market_calendars"))
            .Should().Be(bindingCount);
        await AssertSqlStateAsync(() => database.ExecuteAsync("""
            INSERT INTO ingestion_windows(
                source,asset_id,job_type,range_start,range_end,contract_version,state,calendar_release_id)
            SELECT 'tcmb',id,'historical_backfill','2026-08-17','2026-08-17',2,'pending',
                   'ca100000-0000-7000-8000-000000000002'
              FROM assets WHERE source='tcmb' ORDER BY symbol LIMIT 1
            """), PostgresErrorCodes.CheckViolation);
        await database.ExecuteAsync("""
            INSERT INTO ingestion_windows(
                id,source,asset_id,job_type,range_start,range_end,
                contract_version,state,calendar_release_id)
            SELECT 'ca200000-0000-7000-8000-000000000001','tcmb',id,
                   'historical_backfill','2026-08-17','2026-08-17',2,'pending',
                   'ca100000-0000-7000-8000-000000000001'
              FROM assets WHERE source='tcmb' ORDER BY symbol LIMIT 1
            """);
        await AssertSqlStateAsync(() => database.ExecuteAsync("""
            UPDATE ingestion_windows
               SET source='twelvedata',
                   asset_id=(SELECT id FROM assets WHERE source='twelvedata' ORDER BY symbol LIMIT 1)
             WHERE id='ca200000-0000-7000-8000-000000000001'
            """), "55000");
        await AssertSqlStateAsync(() => database.ExecuteAsync("""
            UPDATE ingestion_windows SET range_end='2026-08-18'
             WHERE id='ca200000-0000-7000-8000-000000000001'
            """), "55000");
        await AssertSqlStateAsync(() => database.ExecuteAsync("""
            UPDATE ingestion_windows SET contract_version=3
             WHERE id='ca200000-0000-7000-8000-000000000001'
            """), "55000");
        await AssertSqlStateAsync(() => database.ExecuteAsync("""
            INSERT INTO ingestion_windows(
                source,asset_id,job_type,range_start,range_end,contract_version,state,calendar_release_id)
            SELECT 'tcmb',id,'daily_update','2099-01-01','2099-01-01',2,'pending',
                   'ca100000-0000-7000-8000-000000000099'
              FROM assets WHERE source='tcmb' ORDER BY symbol LIMIT 1
            """), PostgresErrorCodes.CheckViolation);
        await AssertSqlStateAsync(() => database.ExecuteAsync("""
            INSERT INTO ingestion_windows(
                source,asset_id,job_type,range_start,range_end,contract_version,state)
            SELECT 'tcmb',id,'daily_update','2026-08-17','2026-08-17',2,'running'
              FROM assets WHERE source='tcmb' ORDER BY symbol LIMIT 1
            """), PostgresErrorCodes.CheckViolation);

        foreach (var (releaseId, version, mutation) in new[]
        {
            ("ca100000-0000-7000-8000-000000000090", 90,
                "DELETE FROM market_calendar_days WHERE release_id='ca100000-0000-7000-8000-000000000090' AND calendar_date='2024-01-01'"),
            ("ca100000-0000-7000-8000-000000000091", 91,
                "UPDATE market_calendar_days SET reason_code='corrupt' WHERE release_id='ca100000-0000-7000-8000-000000000091' AND calendar_date='2024-01-01'"),
            ("ca100000-0000-7000-8000-000000000092", 92,
                "INSERT INTO market_calendar_days(release_id,calendar_date,observation_expected,market_state,reason_code,evidence_raw_sha256) SELECT 'ca100000-0000-7000-8000-000000000092','2027-01-01',FALSE,'closed','extra',evidence_raw_sha256 FROM market_calendar_days WHERE release_id='ca100000-0000-7000-8000-000000000092' LIMIT 1"),
            ("ca100000-0000-7000-8000-000000000093", 93,
                "UPDATE market_calendar_release_sources SET source_uri=source_uri || '?corrupt=1' WHERE release_id='ca100000-0000-7000-8000-000000000093' AND source_id='bist-pay-2026'"),
        })
        {
            await AssertSqlStateAsync(() => database.ExecuteAsync($"""
                BEGIN;
                INSERT INTO market_calendar_releases(
                    id,calendar_code,snapshot_set_id,release_version,coverage_from,coverage_through,
                    row_count,normalized_sha256,source_bundle_sha256,released_at)
                SELECT '{releaseId}',calendar_code,'negative-transaction',{version},coverage_from,
                       coverage_through,row_count,normalized_sha256,source_bundle_sha256,released_at
                  FROM market_calendar_releases
                 WHERE id='ca100000-0000-7000-8000-000000000002';
                INSERT INTO market_calendar_release_sources
                SELECT '{releaseId}',source_id,source_kind,source_role,source_uri,media_type,
                       retrieved_at,raw_sha256,snapshot_path,source_year,source_month
                  FROM market_calendar_release_sources
                 WHERE release_id='ca100000-0000-7000-8000-000000000002';
                INSERT INTO market_calendar_days
                SELECT '{releaseId}',calendar_date,observation_expected,market_state,reason_code,evidence_raw_sha256
                  FROM market_calendar_days
                 WHERE release_id='ca100000-0000-7000-8000-000000000002';
                {mutation};
                SELECT public.seal_market_calendar_release('{releaseId}');
                COMMIT;
                """), "55000");
        }
        (await database.ScalarAsync<long>("""
            SELECT count(*) FROM market_calendar_releases
             WHERE id IN ('ca100000-0000-7000-8000-000000000090',
                          'ca100000-0000-7000-8000-000000000091',
                          'ca100000-0000-7000-8000-000000000092',
                          'ca100000-0000-7000-8000-000000000093')
            """)).Should().Be(0, "failed staging transactions must leave no unsealed release");
        (await database.ScalarAsync<Guid>("""
            SELECT release_id FROM market_calendar_active_releases
             WHERE calendar_code='bist_pay_xist'
            """)).Should().Be(Guid.Parse("ca100000-0000-7000-8000-000000000002"));

        await AssertTempShadowCannotBypassGuardsAsync(database);

        static async Task AssertSqlStateAsync(Func<Task> action, string sqlState)
        {
            var failure = await action.Should().ThrowAsync<PostgresException>();
            failure.Which.SqlState.Should().Be(sqlState);
        }
    }

    private static async Task AssertTempShadowCannotBypassGuardsAsync(TestDatabase database)
    {
        var role = $"cal_shadow_{Guid.NewGuid():N}";
        role.Should().MatchRegex("^cal_shadow_[0-9a-f]{32}$");
        await database.ExecuteAsync($"""
            CREATE ROLE "{role}" NOLOGIN;
            GRANT CONNECT,TEMP ON DATABASE "{database.Name}" TO "{role}";
            GRANT USAGE ON SCHEMA public TO "{role}";
            GRANT USAGE ON SCHEMA _timescaledb_internal TO "{role}";
            GRANT SELECT ON public.assets,public.market_calendar_releases,
                public.market_calendar_active_releases,public.asset_market_calendars TO "{role}";
            GRANT UPDATE ON public.market_calendar_releases TO "{role}";
            GRANT INSERT,UPDATE,DELETE ON public.market_calendar_release_sources,
                public.market_calendar_days,public.market_calendar_active_releases,
                public.asset_market_calendars TO "{role}";
            """);
        try
        {
            await AssertSqlStateAsync(() => database.ExecuteAsync($"""
                SET ROLE "{role}";
                CREATE TEMP TABLE market_calendar_releases(
                    id uuid,calendar_code text,sealed_at timestamptz);
                INSERT INTO market_calendar_releases VALUES
                    ('ca100000-0000-7000-8000-000000000001','tcmb_indicative_fx',NULL);
                INSERT INTO public.market_calendar_release_sources(
                    release_id,source_id,source_kind,source_role,source_uri,media_type,
                    retrieved_at,raw_sha256,snapshot_path)
                VALUES ('ca100000-0000-7000-8000-000000000001','temp-shadow',
                    'tcmbPolicyFaq','policy','https://www.tcmb.gov.tr/temp-shadow',
                    'text/html',NOW(),repeat('e',64),
                    'snapshots/sha256/' || repeat('e',64) || '.html');
                """), "55000");

            await AssertSqlStateAsync(() => database.ExecuteAsync($"""
                SET ROLE "{role}";
                CREATE TEMP TABLE market_calendar_releases(
                    id uuid,calendar_code text,sealed_at timestamptz);
                INSERT INTO market_calendar_releases VALUES
                    ('ca100000-0000-7000-8000-000000000099','tcmb_indicative_fx',NOW());
                UPDATE public.market_calendar_active_releases
                   SET release_id='ca100000-0000-7000-8000-000000000099'
                 WHERE calendar_code='tcmb_indicative_fx';
                """), PostgresErrorCodes.CheckViolation);

            await AssertSqlStateAsync(() => database.ExecuteAsync($"""
                SET ROLE "{role}";
                CREATE TEMP TABLE assets(id uuid,source text);
                INSERT INTO assets
                SELECT id,'tcmb' FROM public.assets
                 WHERE source NOT IN ('tcmb','twelvedata') ORDER BY symbol LIMIT 1;
                INSERT INTO public.asset_market_calendars(asset_id,source,calendar_code)
                SELECT id,'tcmb','tcmb_indicative_fx' FROM assets LIMIT 1;
                """), PostgresErrorCodes.CheckViolation);
        }
        finally
        {
            await database.ExecuteAsync($"""
                DROP OWNED BY "{role}";
                DROP ROLE "{role}";
                """);
        }

        static async Task AssertSqlStateAsync(Func<Task> action, string sqlState)
        {
            var failure = await action.Should().ThrowAsync<PostgresException>();
            failure.Which.SqlState.Should().Be(sqlState);
        }
    }

    [SkippableFact]
    public async Task CalendarSealAndPayloadDml_SerializeAcrossCommitOrders()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        const string sourceRelease = "ca100000-0000-7000-8000-000000000002";
        const string payloadFirst = "ca100000-0000-7000-8000-000000000080";
        const string sealFirst = "ca100000-0000-7000-8000-000000000081";
        await database.ExecuteAsync($"""
            INSERT INTO market_calendar_releases(
                id,calendar_code,snapshot_set_id,release_version,coverage_from,coverage_through,
                row_count,normalized_sha256,source_bundle_sha256,released_at)
            SELECT '{payloadFirst}',calendar_code,'race-payload-first',80,coverage_from,
                   coverage_through,row_count,normalized_sha256,source_bundle_sha256,released_at
              FROM market_calendar_releases WHERE id='{sourceRelease}';
            INSERT INTO market_calendar_releases(
                id,calendar_code,snapshot_set_id,release_version,coverage_from,coverage_through,
                row_count,normalized_sha256,source_bundle_sha256,released_at)
            SELECT '{sealFirst}',calendar_code,'race-seal-first',81,coverage_from,
                   coverage_through,row_count,normalized_sha256,source_bundle_sha256,released_at
              FROM market_calendar_releases WHERE id='{sourceRelease}';
            INSERT INTO market_calendar_release_sources
            SELECT target.id,source_id,source_kind,source_role,source_uri,media_type,
                   retrieved_at,raw_sha256,snapshot_path,source_year,source_month
              FROM market_calendar_release_sources source
              CROSS JOIN (VALUES ('{payloadFirst}'::uuid),('{sealFirst}'::uuid)) target(id)
             WHERE source.release_id='{sourceRelease}';
            INSERT INTO market_calendar_days
            SELECT target.id,calendar_date,observation_expected,market_state,reason_code,
                   evidence_raw_sha256
              FROM market_calendar_days day
              CROSS JOIN (VALUES ('{payloadFirst}'::uuid),('{sealFirst}'::uuid)) target(id)
             WHERE day.release_id='{sourceRelease}';
            """);

        await using (var payloadConnection = new NpgsqlConnection(database.ConnectionString))
        await using (var sealConnection = new NpgsqlConnection(database.ConnectionString))
        {
            await payloadConnection.OpenAsync();
            await sealConnection.OpenAsync();
            await using var payloadTransaction = await payloadConnection.BeginTransactionAsync();
            await using var sealTransaction = await sealConnection.BeginTransactionAsync();
            await using (var mutate = new NpgsqlCommand($"""
                UPDATE market_calendar_days SET reason_code='race-corrupt'
                 WHERE release_id='{payloadFirst}' AND calendar_date='2024-01-01'
                """, payloadConnection, payloadTransaction))
                await mutate.ExecuteNonQueryAsync();
            await using var seal = new NpgsqlCommand(
                $"SELECT public.seal_market_calendar_release('{payloadFirst}')",
                sealConnection, sealTransaction)
            { CommandTimeout = 5 };
            var sealTask = seal.ExecuteNonQueryAsync();
            await Task.Delay(100);
            sealTask.IsCompleted.Should().BeFalse(
                "the seal must wait for the payload transaction's parent-row lock");
            await payloadTransaction.CommitAsync();
            var failure = await FluentActions.Awaiting(async () => await sealTask)
                .Should().ThrowAsync<PostgresException>();
            failure.Which.SqlState.Should().Be("55000");
            await sealTransaction.RollbackAsync();
        }

        await using (var sealConnection = new NpgsqlConnection(database.ConnectionString))
        await using (var payloadConnection = new NpgsqlConnection(database.ConnectionString))
        {
            await sealConnection.OpenAsync();
            await payloadConnection.OpenAsync();
            await using var sealTransaction = await sealConnection.BeginTransactionAsync();
            await using var payloadTransaction = await payloadConnection.BeginTransactionAsync();
            await using (var seal = new NpgsqlCommand(
                $"SELECT public.seal_market_calendar_release('{sealFirst}')",
                sealConnection, sealTransaction)
            { CommandTimeout = 5 })
                await seal.ExecuteNonQueryAsync();
            await using var mutate = new NpgsqlCommand($"""
                UPDATE market_calendar_days SET reason_code=reason_code
                 WHERE release_id='{sealFirst}' AND calendar_date='2024-01-01'
                """, payloadConnection, payloadTransaction) { CommandTimeout = 5 };
            var mutationTask = mutate.ExecuteNonQueryAsync();
            await Task.Delay(100);
            mutationTask.IsCompleted.Should().BeFalse(
                "payload DML must wait for the sealing transaction's parent-row lock");
            await sealTransaction.CommitAsync();
            var failure = await FluentActions.Awaiting(async () => await mutationTask)
                .Should().ThrowAsync<PostgresException>();
            failure.Which.SqlState.Should().Be("55000");
            await payloadTransaction.RollbackAsync();
        }

        (await database.ScalarAsync<bool>($"""
            SELECT sealed_at IS NOT NULL
               AND public.verify_market_calendar_release_payload(id)
              FROM market_calendar_releases WHERE id='{sealFirst}'
            """)).Should().BeTrue();
        (await database.ScalarAsync<bool>($"""
            SELECT sealed_at IS NULL FROM market_calendar_releases WHERE id='{payloadFirst}'
            """)).Should().BeTrue();
    }

    [SkippableFact]
    public async Task Complete014Legacy_CutoverRestoresAdminIdentityAndNormalVerifyOnlyPasses()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await database.PrepareLegacy014Async();
        await InstallPublicRegexOperatorHijackAsync(database);
        var before = await database.ScalarAsync<string>("""
            SELECT md5(string_agg(symbol || ':' || source, ',' ORDER BY symbol)) FROM assets
            """);

        var result = await RunAsync(database.ConnectionString, legacyCutover: true);

        result.Applied.Should().Be(11, "015 through 025 must be applied after the verified 014 baseline");
        (await database.ScalarAsync<string>("""
            SELECT md5(string_agg(symbol || ':' || source, ',' ORDER BY symbol)) FROM assets
            """)).Should().Be(before);
        (await database.ScalarAsync<long>("SELECT COUNT(*) FROM schema_migrations WHERE checksum IS NOT NULL"))
            .Should().Be(27);
        (await database.ScalarAsync<bool>(
            "SELECT to_regclass('public.ingestion_windows') IS NOT NULL"))
            .Should().BeTrue();
        (await database.ScalarAsync<long>("""
            SELECT count(*) FROM pg_trigger
             WHERE tgname IN ('trg_price_points_ingestion_fence',
                              'trg_inflation_rates_ingestion_fence')
               AND NOT tgisinternal
            """)).Should().Be(2);
        (await database.ScalarAsync<string>("""
            SELECT state FROM schema_migrations WHERE version='012b_create_exporter_role'
            """)).Should().Be("skipped_optional");
        (await database.ScalarAsync<string>("SELECT state FROM saydin_migration_control WHERE singleton=1"))
            .Should().Be("ready");
        await AssertPublicRegexOperatorWasIsolatedAsync(database);

        var verifyExit = await MigratorApplication.RunAsync(
            ["--verify-only"], ApplicationEnvironment(database.ConnectionString),
            TextWriter.Null, TextWriter.Null);
        verifyExit.Should().Be(0, "normal verification must authenticate as the migrator login after cutover");
    }

    [SkippableTheory]
    [InlineData("function_body")]
    [InlineData("trigger_disabled")]
    [InlineData("function_acl")]
    [InlineData("column_acl")]
    [InlineData("row_security")]
    public async Task Legacy019Preflight_RejectsSecurityDriftBeforeOwnershipOrContractCommit(string drift)
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await database.PrepareLegacy014Async();
        using var pre019 = Pre019Directory();
        await RunAsync(database.ConnectionString, pre019.Path, legacyCutover: true);

        var sql = drift switch
        {
            "function_body" => """
                CREATE OR REPLACE FUNCTION public.enforce_saved_scenario_hard_cap()
                RETURNS trigger LANGUAGE plpgsql
                SET search_path=pg_catalog,public,pg_temp AS $$ BEGIN RETURN NEW; END $$
                """,
            "trigger_disabled" =>
                "ALTER TABLE public.saved_scenarios DISABLE TRIGGER trg_saved_scenarios_hard_cap",
            "function_acl" => $"""
                GRANT EXECUTE ON FUNCTION public.verify_market_calendar_release_payload(uuid)
                    TO {database.Contract.ApiCapability.Name}
                """,
            "column_acl" => $"""
                GRANT SELECT(symbol) ON TABLE public.assets TO {database.Contract.ApiCapability.Name}
                """,
            "row_security" => "ALTER TABLE public.price_points ENABLE ROW LEVEL SECURITY",
            _ => throw new ArgumentOutOfRangeException(nameof(drift)),
        };
        await database.ExecuteAsync(sql);

        var act = () => RunAsync(database.ConnectionString, legacyCutover: true);
        await act.Should().ThrowAsync<MigratorRejectedException>();

        (await database.ScalarAsync<bool>(
            "SELECT to_regclass('public.saydin_role_contract') IS NULL")).Should().BeTrue();
        (await database.ScalarAsync<bool>("""
            SELECT coalesce((SELECT state<>'succeeded' FROM schema_migrations
             WHERE version='019_privilege_separation'),true)
            """)).Should().BeTrue();
        (await database.ScalarAsync<string>("""
            SELECT pg_get_userbyid(relowner) FROM pg_class
             WHERE oid='public.users'::regclass
            """)).Should().Be(new NpgsqlConnectionStringBuilder(admin).Username);
    }

    [SkippableTheory]
    [InlineData("database_acl")]
    [InlineData("schema_acl")]
    [InlineData("pg_control_acl")]
    [InlineData("pg_parameter_acl")]
    [InlineData("table_acl")]
    [InlineData("column_acl")]
    [InlineData("type_acl")]
    [InlineData("function_acl")]
    [InlineData("default_acl")]
    [InlineData("row_security")]
    public async Task VerifyOnly_RejectsEveryPrivilegeSeparationFingerprintDrift(string drift)
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        var sql = drift switch
        {
            "database_acl" => $"GRANT TEMPORARY ON DATABASE {database.Name} TO {database.Contract.ApiCapability.Name}",
            "schema_acl" => $"GRANT CREATE ON SCHEMA public TO {database.Contract.ApiCapability.Name}",
            "pg_control_acl" => $"GRANT EXECUTE ON FUNCTION pg_catalog.pg_control_system() TO {database.Contract.ApiCapability.Name}",
            "pg_parameter_acl" => $"GRANT SET ON PARAMETER session_replication_role TO {database.Contract.ApiCapability.Name}",
            "table_acl" => "GRANT SELECT ON TABLE public.assets TO PUBLIC",
            "column_acl" => $"GRANT UPDATE(email) ON TABLE public.users TO {database.Contract.ApiCapability.Name}",
            "type_acl" => $"GRANT USAGE ON TYPE public.asset_category TO {database.Contract.AuditCapability.Name}",
            "function_acl" => $"GRANT EXECUTE ON FUNCTION public.seal_market_calendar_release(uuid) TO {database.Contract.ApiCapability.Name}",
            "default_acl" => $"ALTER DEFAULT PRIVILEGES FOR ROLE {database.Contract.Owner.Name} IN SCHEMA public GRANT SELECT ON TABLES TO PUBLIC",
            "row_security" => "ALTER TABLE public.price_points ENABLE ROW LEVEL SECURITY",
            _ => throw new ArgumentOutOfRangeException(nameof(drift)),
        };
        try
        {
            await database.ExecuteAsync(sql);
            var error = new StringWriter();

            var exit = await MigratorApplication.RunAsync(
                ["--verify-only"], ApplicationEnvironment(database.ConnectionString),
                TextWriter.Null, error);

            exit.Should().Be(3);
            error.ToString().Should().Contain("code=schema_fingerprint_mismatch");
            (await database.ScalarAsync<string>(
                "SELECT state FROM saydin_migration_control WHERE singleton=1")).Should().Be("ready",
                "verify-only must never mutate control state");
        }
        finally
        {
            if (drift == "pg_parameter_acl")
                await database.ExecuteAsync(
                    $"REVOKE ALL PRIVILEGES ON PARAMETER session_replication_role FROM {database.Contract.ApiCapability.Name}");
        }
    }

    [SkippableTheory]
    [InlineData("function_body")]
    [InlineData("trigger_disabled")]
    [InlineData("constraint_weakened")]
    [InlineData("foreign_key_replaced")]
    [InlineData("index_replaced")]
    [InlineData("foreign_grant")]
    [InlineData("column_grant")]
    [InlineData("chunk_column_grant")]
    [InlineData("unexpected_trigger")]
    public async Task VerifyOnly_RejectsEveryPriceAuthorityFingerprintDrift(string drift)
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        var foreignRole = database.Contract.ApiCapability.Name;
        var sql = drift switch
        {
            "function_body" => """
                CREATE OR REPLACE FUNCTION public.enforce_fetch_payload_insert()
                RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp
                AS $$ BEGIN NEW.first_observed_at:=clock_timestamp(); RETURN NEW; END $$
                """,
            "trigger_disabled" =>
                "ALTER TABLE public.provider_fetch_payloads DISABLE TRIGGER trg_fetch_payload_live_lease",
            "constraint_weakened" => """
                ALTER TABLE public.provider_fetch_payloads
                  DROP CONSTRAINT chk_provider_fetch_payloads_length,
                  ADD CONSTRAINT chk_provider_fetch_payloads_length CHECK(payload_byte_length>0)
                """,
            "foreign_key_replaced" => """
                ALTER TABLE public.price_observation_attributions
                  DROP CONSTRAINT fk_price_attribution_window,
                  ADD CONSTRAINT fk_price_attribution_window FOREIGN KEY(ingestion_window_id)
                    REFERENCES public.ingestion_windows(id) ON DELETE CASCADE
                """,
            "index_replaced" => """
                ALTER TABLE public.provider_fetch_payloads DROP CONSTRAINT pk_provider_fetch_payloads CASCADE;
                CREATE UNIQUE INDEX pk_provider_fetch_payloads
                  ON public.provider_fetch_payloads(payload_sha256,provider_source)
                """,
            "foreign_grant" =>
                $"GRANT UPDATE ON public.provider_fetch_payloads TO {foreignRole}",
            "column_grant" =>
                $"GRANT INSERT(first_observed_at) ON public.provider_fetch_payloads TO {database.Contract.IngestionCapability.Name}",
            "chunk_column_grant" => $"""
                DO $drift$
                DECLARE target_schema text; target_table text;
                BEGIN
                  PERFORM set_config('session_replication_role','replica',true);
                  INSERT INTO public.assets(
                    id,symbol,display_name,category,is_active,source,source_id)
                  VALUES ('a0200000-0000-7000-8000-000000000099','ACLCHUNK020',
                          'Authority chunk ACL drift','crypto'::public.asset_category,
                          true,'coingecko','acl-chunk-020');
                  INSERT INTO public.price_points(asset_id,price_date,close)
                  VALUES ('a0200000-0000-7000-8000-000000000099','2031-01-01',1);
                  SELECT chunk_schema,chunk_name INTO STRICT target_schema,target_table
                    FROM timescaledb_information.chunks
                   WHERE hypertable_schema='public' AND hypertable_name='price_points'
                   ORDER BY chunk_schema,chunk_name LIMIT 1;
                  EXECUTE format('GRANT UPDATE(close) ON %I.%I TO %I',
                    target_schema,target_table,'{database.Contract.IngestionCapability.Name}');
                END
                $drift$
                """,
            "unexpected_trigger" => """
                CREATE TRIGGER trg_price_points_unexpected_authority
                BEFORE INSERT ON public.price_points FOR EACH ROW
                EXECUTE FUNCTION public.enforce_price_point_authority()
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(drift)),
        };
        await database.ExecuteAsync(sql);
        var error = new StringWriter();

        var exit = await MigratorApplication.RunAsync(
            ["--verify-only"], ApplicationEnvironment(database.ConnectionString),
            TextWriter.Null, error);

        exit.Should().Be(3);
        error.ToString().Should().Contain("code=schema_fingerprint_mismatch");
        (await database.ScalarAsync<string>(
            "SELECT state FROM saydin_migration_control WHERE singleton=1")).Should().Be("ready");
    }

    [SkippableTheory]
    [InlineData("column_default")]
    [InlineData("constraint_definition")]
    [InlineData("index_definition")]
    [InlineData("function_body")]
    [InlineData("function_acl")]
    [InlineData("rehash_function_body")]
    [InlineData("rehash_function_acl")]
    [InlineData("trigger_disabled")]
    [InlineData("unexpected_catalog_trigger")]
    [InlineData("catalog_hash")]
    [InlineData("active_principal_without_credential")]
    public async Task VerifyOnly_RejectsEveryApiTrustFingerprintDrift(string drift)
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        var sql = drift switch
        {
            "column_default" =>
                "ALTER TABLE public.users ALTER COLUMN principal_status SET DEFAULT 'active'",
            "constraint_definition" => """
                ALTER TABLE public.installation_credentials
                  DROP CONSTRAINT chk_installation_credentials_secret_hash,
                  ADD CONSTRAINT chk_installation_credentials_secret_hash
                    CHECK(octet_length(secret_hash)>=16)
                """,
            "index_definition" => """
                DROP INDEX public.uq_installation_credentials_active_principal;
                CREATE UNIQUE INDEX uq_installation_credentials_active_principal
                  ON public.installation_credentials(principal_id,generation)
                  WHERE state='active'
                """,
            "function_body" => """
                CREATE OR REPLACE FUNCTION public.get_asset_catalog_state()
                RETURNS TABLE(revision bigint,catalog_sha256 bytea,updated_at timestamptz)
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,pg_temp
                AS $$ SELECT 1::bigint,decode(repeat('00',32),'hex'),clock_timestamp() $$
                """,
            "function_acl" =>
                "GRANT EXECUTE ON FUNCTION public.resolve_installation(bytea,smallint) TO PUBLIC",
            "rehash_function_body" => """
                CREATE OR REPLACE FUNCTION public.resolve_installation_and_rehash(
                    p_secret_hash bytea,p_key_version smallint,
                    p_active_secret_hash bytea,p_active_key_version smallint)
                RETURNS TABLE(principal_id uuid,credential_id uuid,generation integer,
                    tier varchar,principal_status varchar,credential_state varchar)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                SET search_path=pg_catalog,pg_temp
                AS $$ BEGIN RETURN; END $$
                """,
            "rehash_function_acl" =>
                "GRANT EXECUTE ON FUNCTION public.resolve_installation_and_rehash(bytea,smallint,bytea,smallint) TO PUBLIC",
            "trigger_disabled" =>
                "ALTER TABLE public.assets DISABLE TRIGGER trg_asset_catalog_revision_update",
            "unexpected_catalog_trigger" => """
                CREATE TRIGGER trg_asset_catalog_revision_extra
                AFTER INSERT ON public.assets FOR EACH STATEMENT
                EXECUTE FUNCTION public.refresh_asset_catalog_state()
                """,
            "catalog_hash" => """
                UPDATE public.asset_catalog_state
                   SET catalog_sha256=decode(repeat('ff',32),'hex')
                 WHERE singleton=1
                """,
            "active_principal_without_credential" => """
                INSERT INTO public.users(
                    id,device_id,email,tier,principal_status,principal_contract_version,
                    principal_quarantined_at,principal_revoked_at,principal_expires_at,
                    created_at,last_seen_at)
                VALUES ('a0210000-0000-7000-8000-000000000099',NULL,NULL,'free','active',1,
                        NULL,NULL,NULL,clock_timestamp(),clock_timestamp())
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(drift)),
        };
        await database.ExecuteAsync(sql);
        var error = new StringWriter();

        var exit = await MigratorApplication.RunAsync(
            ["--verify-only"], ApplicationEnvironment(database.ConnectionString),
            TextWriter.Null, error);

        exit.Should().Be(3);
        error.ToString().Should().Contain("code=schema_fingerprint_mismatch");
        (await database.ScalarAsync<string>(
            "SELECT state FROM saydin_migration_control WHERE singleton=1")).Should().Be("ready",
            "verify-only must report drift without mutating the control plane");
    }

    [SkippableTheory]
    [InlineData("constraint_definition")]
    [InlineData("trigger_disabled")]
    [InlineData("function_body")]
    [InlineData("public_acl")]
    public async Task VerifyOnly_RejectsEveryApiSecurityAdmissionFingerprintDrift(string drift)
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        var sql = drift switch
        {
            "constraint_definition" => $"""
                SET ROLE "{database.Contract.TimescaleScheduler.Name}";
                ALTER TABLE public.activity_logs SET (timescaledb.compress=false);
                ALTER TABLE public.activity_logs ADD CONSTRAINT chk_activity_action
                    CHECK (action IS NOT NULL) NOT VALID;
                RESET ROLE;
                """,
            "trigger_disabled" => $"""
                SET ROLE "{database.Contract.TimescaleScheduler.Name}";
                DROP TRIGGER trg_activity_action_allowlist ON public.activity_logs;
                RESET ROLE;
                """,
            "function_body" => """
                CREATE OR REPLACE FUNCTION public.installation_verifier_matches(
                    p_expected bytea,p_candidate bytea)
                RETURNS boolean LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE
                SET search_path=pg_catalog,pg_temp
                AS $$ BEGIN RETURN true; END $$
                """,
            "public_acl" => """
                GRANT EXECUTE ON FUNCTION
                    public.resolve_installation_rotation_commit(uuid,bytea,smallint) TO PUBLIC
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(drift)),
        };
        await database.ExecuteAsync(sql);
        var error = new StringWriter();

        var exit = await MigratorApplication.RunAsync(
            ["--verify-only"], ApplicationEnvironment(database.ConnectionString),
            TextWriter.Null, error);

        exit.Should().Be(3);
        error.ToString().Should().Contain("code=schema_fingerprint_mismatch");
        (await database.ScalarAsync<string>(
            "SELECT state FROM saydin_migration_control WHERE singleton=1"))
            .Should().Be("ready");
    }

    [SkippableFact]
    public async Task ApiSecurityAdmissionUpgrade_RejectsPermissiveNamedPredecessor()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        using var through022 = MigrationDirectoryThrough(
            "023_installation_lifecycle_admission.sql");
        (await RunAsync(database.ConnectionString, through022.Path)).Applied.Should().Be(24);
        await database.ExecuteAsync($"""
            SET ROLE "{database.Contract.TimescaleScheduler.Name}";
            ALTER TABLE public.activity_logs SET (timescaledb.compress=false);
            ALTER TABLE public.activity_logs DROP CONSTRAINT chk_activity_action;
            ALTER TABLE public.activity_logs ADD CONSTRAINT chk_activity_action
                CHECK (action IS NOT NULL);
            RESET ROLE;
            """);

        var action = async () => await RunAsync(database.ConnectionString);

        var failure = (await action.Should().ThrowAsync<MigratorRejectedException>()).Which;
        failure.Code.Should().Be("migration_failed");
        failure.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await database.ScalarAsync<bool>("""
            SELECT pg_catalog.to_regprocedure(
                       'public.resolve_installation_rotation_commit(uuid,bytea,smallint)') IS NULL
               AND (SELECT state='failed' FROM public.schema_migrations
                     WHERE version='023_installation_lifecycle_admission')
            """)).Should().BeTrue("the rejected migration transaction must leave no partial objects");
    }

    [SkippableFact]
    public async Task PendingCommitResolver_IsRotationBoundAndActiveRetryIsExact()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        const string principal = "a0230000-0000-7000-8000-000000000001";
        const string activeCredential = "a0230000-0000-7000-8000-000000000002";
        const string pendingCredential = "a0230000-0000-7000-8000-000000000003";
        const string rotation = "a0230000-0000-7000-8000-000000000004";
        const string otherPrincipal = "a0230000-0000-7000-8000-000000000011";
        const string otherCredential = "a0230000-0000-7000-8000-000000000012";
        await database.ExecuteAsync($"""
            SELECT * FROM public.register_installation(
                '{principal}','{activeCredential}',decode(repeat('11',32),'hex'),1::smallint);
            SELECT * FROM public.begin_installation_rotation(
                decode(repeat('11',32),'hex'),1::smallint,'{rotation}',
                '{pendingCredential}',decode(repeat('22',32),'hex'),1::smallint);
            SELECT * FROM public.register_installation(
                '{otherPrincipal}','{otherCredential}',decode(repeat('33',32),'hex'),1::smallint);
            """);

        (await database.ScalarAsync<long>($"""
            SELECT count(*) FROM public.resolve_installation(
                decode(repeat('22',32),'hex'),1::smallint)
            """)).Should().Be(0,
            "pending credentials must remain inaccessible to active business endpoints");
        (await database.ScalarAsync<bool>($"""
            SELECT count(*)=1 AND bool_and(principal_id='{principal}'::uuid
                                           AND credential_id='{pendingCredential}'::uuid
                                           AND credential_state='pending')
              FROM public.resolve_installation_rotation_commit(
                  '{rotation}',decode(repeat('22',32),'hex'),1::smallint)
            """)).Should().BeTrue();
        (await database.ScalarAsync<bool>($"""
            SELECT (SELECT count(*)=0 FROM public.resolve_installation_rotation_commit(
                       gen_random_uuid(),decode(repeat('22',32),'hex'),1::smallint))
               AND (SELECT count(*)=0 FROM public.resolve_installation_rotation_commit(
                       '{rotation}',decode(repeat('23',32),'hex'),1::smallint))
               AND (SELECT count(*)=0 FROM public.resolve_installation_rotation_commit(
                       '{rotation}',decode(repeat('33',32),'hex'),1::smallint))
            """)).Should().BeTrue("rotation id, verifier, and principal binding are all exact");

        await database.ExecuteAsync($"""
            SELECT * FROM public.commit_installation_rotation(
                '{rotation}',decode(repeat('22',32),'hex'),1::smallint)
            """);

        (await database.ScalarAsync<bool>($"""
            SELECT count(*)=1 AND bool_and(principal_id='{principal}'::uuid
                                           AND credential_id='{pendingCredential}'::uuid
                                           AND credential_state='active')
              FROM public.resolve_installation_rotation_commit(
                  '{rotation}',decode(repeat('22',32),'hex'),1::smallint)
            """)).Should().BeTrue(
            "an ACK-loss retry is admitted only by the same rotation id and new active verifier");
        (await database.ScalarAsync<bool>($"""
            SELECT (SELECT count(*)=0 FROM public.resolve_installation_rotation_commit(
                       gen_random_uuid(),decode(repeat('22',32),'hex'),1::smallint))
               AND (SELECT count(*)=0 FROM public.resolve_installation_rotation_commit(
                       '{rotation}',decode(repeat('11',32),'hex'),1::smallint))
               AND (SELECT count(*)=0 FROM public.resolve_installation_rotation_commit(
                       '{rotation}',decode(repeat('33',32),'hex'),1::smallint))
               AND NOT has_function_privilege(
                       '{database.Contract.ApiCapability.Name}',
                       'public.installation_verifier_matches(bytea,bytea)','EXECUTE')
               AND has_function_privilege(
                       '{database.Contract.ApiCapability.Name}',
                       'public.resolve_installation_rotation_commit(uuid,bytea,smallint)','EXECUTE')
               AND NOT has_function_privilege(
                       '{database.Contract.AuditCapability.Name}',
                       'public.resolve_installation_rotation_commit(uuid,bytea,smallint)','EXECUTE')
            """)).Should().BeTrue();
    }

    [SkippableFact]
    public async Task ApiSecurityAdmissionUpgrade_PreservesCompressedHistoricalChunks()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        using var through022 = MigrationDirectoryThrough(
            "023_installation_lifecycle_admission.sql");
        (await RunAsync(database.ConnectionString, through022.Path)).Applied.Should().Be(24);
        await database.ExecuteAsync($"""
            INSERT INTO public.activity_logs(
                id,user_id,device_id,action,status_code,created_at)
            VALUES ('a0230000-0000-7000-8000-000000000021',NULL,
                    'compressed-023-upgrade','config_fetch',200,'2023-01-03T12:00:00Z');
            DO $compress$
            DECLARE old_chunk regclass;
            BEGIN
                SELECT activity.tableoid INTO STRICT old_chunk
                  FROM public.activity_logs activity
                 WHERE activity.id='a0230000-0000-7000-8000-000000000021';
                EXECUTE pg_catalog.format('SET LOCAL ROLE %I',
                    '{database.Contract.TimescaleScheduler.Name}');
                PERFORM public.compress_chunk(old_chunk,if_not_compressed=>true);
                RESET ROLE;
            END
            $compress$;
            """);

        (await RunAsync(database.ConnectionString)).Applied.Should().Be(2);

        await database.ExecuteAsync($"""
            SET ROLE "{database.Contract.ApiCapability.Name}";
            INSERT INTO public.activity_logs(
                id,user_id,device_id,action,status_code,created_at)
            VALUES
                ('a0230000-0000-7000-8000-000000000022',NULL,
                 'new-action-023','installation_register',201,clock_timestamp()),
                ('a0230000-0000-7000-8000-000000000023',NULL,
                 'new-action-023','installation_rotation_begin',200,clock_timestamp()),
                ('a0230000-0000-7000-8000-000000000024',NULL,
                 'new-action-023','installation_rotation_commit',204,clock_timestamp()),
                ('a0230000-0000-7000-8000-000000000025',NULL,
                 'new-action-023','installation_revoke',204,'2032-01-03T12:00:00Z')
            ;
            RESET ROLE;
            """);
        var invalidInsert = async () => await database.ExecuteAsync($"""
            SET ROLE "{database.Contract.ApiCapability.Name}";
            INSERT INTO public.activity_logs(
                id,user_id,device_id,action,status_code,created_at)
            VALUES ('a0230000-0000-7000-8000-000000000026',NULL,
                    'invalid-action-023','unknown_action',200,clock_timestamp())
            """);
        (await invalidInsert.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.CheckViolation);
        var invalidUpdate = async () => await database.ExecuteAsync($"""
            SET ROLE "{database.Contract.TimescaleScheduler.Name}";
            UPDATE public.activity_logs SET action='unknown_action'
             WHERE id='a0230000-0000-7000-8000-000000000022'
            """);
        (await invalidUpdate.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.CheckViolation);

        (await database.ScalarAsync<bool>($"""
            SELECT (SELECT count(*)=5 FROM public.activity_logs
                     WHERE id::text LIKE 'a0230000-0000-7000-8000-00000000002%')
               AND EXISTS (
                       SELECT 1 FROM timescaledb_information.chunks chunk
                        WHERE chunk.hypertable_schema='public'
                          AND chunk.hypertable_name='activity_logs'
                          AND chunk.is_compressed
                          AND '2023-01-03T12:00:00Z'::timestamptz>=chunk.range_start
                          AND '2023-01-03T12:00:00Z'::timestamptz<chunk.range_end)
               AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_constraint
                     WHERE conrelid='public.activity_logs'::regclass
                       AND conname='chk_activity_action')
               AND (SELECT count(*)=1 AND bool_and(job.scheduled
                           AND job.config->>'compress_after'='7 days')
                      FROM timescaledb_information.jobs job
                     WHERE job.hypertable_schema='public'
                       AND job.hypertable_name='activity_logs'
                       AND job.proc_name='policy_compression')
               AND NOT EXISTS (
                    SELECT 1
                      FROM timescaledb_information.chunks chunk
                      JOIN pg_catalog.pg_namespace namespace
                        ON namespace.nspname=chunk.chunk_schema
                      JOIN pg_catalog.pg_class relation
                        ON relation.relnamespace=namespace.oid
                       AND relation.relname=chunk.chunk_name
                     WHERE chunk.hypertable_schema='public'
                       AND chunk.hypertable_name='activity_logs'
                       AND NOT EXISTS (
                           SELECT 1 FROM pg_catalog.pg_trigger trigger
                            WHERE trigger.tgrelid=relation.oid
                              AND trigger.tgname='trg_activity_action_allowlist'
                              AND trigger.tgenabled='O' AND trigger.tgtype=23
                              AND NOT trigger.tgisinternal))
               AND NOT EXISTS (
                    SELECT 1
                      FROM timescaledb_information.chunks chunk
                      JOIN pg_catalog.pg_namespace namespace
                        ON namespace.nspname=chunk.chunk_schema
                      JOIN pg_catalog.pg_class relation
                        ON relation.relnamespace=namespace.oid
                       AND relation.relname=chunk.chunk_name
                     WHERE chunk.hypertable_schema='public'
                       AND chunk.hypertable_name='activity_logs'
                       AND pg_catalog.has_table_privilege(
                           '{database.Contract.Owner.Name}',relation.oid,'TRIGGER'))
            """)).Should().BeTrue();
    }

    [SkippableTheory]
    [InlineData("function_body")]
    [InlineData("function_owner")]
    [InlineData("function_acl")]
    [InlineData("trigger_disabled")]
    [InlineData("unexpected_trigger")]
    [InlineData("foreign_key_action")]
    [InlineData("scheduler_acl_removed")]
    [InlineData("broad_owner_update")]
    [InlineData("compressed_chunk_acl")]
    [InlineData("transition_residual")]
    [InlineData("compression_policy_removed")]
    public async Task VerifyOnly_RejectsEveryPrincipalRetentionFingerprintDrift(string drift)
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        var sql = drift switch
        {
            "function_body" => """
                CREATE OR REPLACE FUNCTION public.redact_activity_logs_before_principal_delete()
                RETURNS trigger LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                SET search_path=pg_catalog,pg_temp
                AS $$ BEGIN RETURN OLD; END $$
                """,
            "function_owner" => $"""
                ALTER FUNCTION public.redact_activity_logs_before_principal_delete()
                    OWNER TO "{database.Contract.Owner.Name}"
                """,
            "function_acl" => """
                GRANT EXECUTE ON FUNCTION
                    public.redact_activity_logs_before_principal_delete() TO PUBLIC
                """,
            "trigger_disabled" => """
                ALTER TABLE public.users
                    DISABLE TRIGGER trg_users_principal_retention_redact
                """,
            "unexpected_trigger" => """
                CREATE TRIGGER trg_users_principal_retention_unexpected
                BEFORE DELETE ON public.users FOR EACH ROW
                EXECUTE FUNCTION public.redact_activity_logs_before_principal_delete()
                """,
            "foreign_key_action" => """
                UPDATE pg_catalog.pg_constraint SET confdeltype='n'
                 WHERE conrelid='public.activity_logs'::regclass
                   AND conname='activity_logs_user_id_fkey'
                """,
            "scheduler_acl_removed" => $"""
                REVOKE UPDATE ON TABLE public.activity_logs
                    FROM "{database.Contract.TimescaleScheduler.Name}"
                """,
            "broad_owner_update" => $"""
                GRANT UPDATE ON TABLE public.activity_logs
                    TO "{database.Contract.Owner.Name}"
                """,
            "compressed_chunk_acl" => $"""
                INSERT INTO public.activity_logs(
                    id,user_id,device_id,action,status_code,created_at)
                VALUES ('a0220000-0000-7000-8000-000000000901',NULL,
                        'compressed-acl-drift','config_fetch',200,
                        '2023-01-03T12:00:00Z');
                DO $drift$
                DECLARE source_chunk regclass; compressed_relation regclass;
                        admin_role text:=current_user;
                BEGIN
                  SELECT activity.tableoid INTO STRICT source_chunk
                    FROM public.activity_logs activity
                   WHERE activity.id='a0220000-0000-7000-8000-000000000901';
                  EXECUTE format('SET LOCAL ROLE %I',
                      '{database.Contract.TimescaleScheduler.Name}');
                  PERFORM public.compress_chunk(source_chunk,if_not_compressed=>true);
                  EXECUTE format('SET LOCAL ROLE %I',admin_role);
                  SELECT format('%I.%I',compressed_chunk.schema_name,
                                compressed_chunk.table_name)::regclass
                    INTO STRICT compressed_relation
                    FROM _timescaledb_catalog.chunk source
                    JOIN _timescaledb_catalog.chunk compressed_chunk
                      ON compressed_chunk.id=source.compressed_chunk_id
                   WHERE format('%I.%I',source.schema_name,source.table_name)::regclass=
                         source_chunk;
                  EXECUTE format('GRANT UPDATE ON %s TO %I',compressed_relation,
                                 '{database.Contract.Owner.Name}');
                END
                $drift$
                """,
            "transition_residual" => "CREATE SCHEMA saydin_principal_retention_control",
            "compression_policy_removed" => """
                SELECT public.remove_compression_policy(
                    'public.activity_logs'::regclass,if_exists=>false)
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(drift)),
        };
        await database.ExecuteAsync(sql);
        var error = new StringWriter();

        var exit = await MigratorApplication.RunAsync(
            ["--verify-only"], ApplicationEnvironment(database.ConnectionString),
            TextWriter.Null, error);

        exit.Should().Be(3);
        error.ToString().Should().Contain("code=schema_fingerprint_mismatch");
        (await database.ScalarAsync<string>(
            "SELECT state FROM saydin_migration_control WHERE singleton=1")).Should().Be("ready",
            "verify-only must report retention drift without mutating control state");
    }

    [SkippableFact]
    public async Task ExistingLegacyUser_UpgradeQuarantinesWithoutClaim_AndRegistrationCreatesNewActivePrincipal()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await database.PrepareLegacy014Async();
        const string legacyId = "a0210000-0000-7000-8000-000000000001";
        const string principalId = "a0210000-0000-7000-8000-000000000002";
        const string credentialId = "a0210000-0000-7000-8000-000000000003";
        await database.ExecuteAsync($"""
            INSERT INTO public.users(id,device_id,tier,created_at,last_seen_at)
            VALUES ('{legacyId}','legacy-device-proof-must-not-claim','free',
                    clock_timestamp(),clock_timestamp())
            """);

        await RunAsync(database.ConnectionString, legacyCutover: true);

        (await database.ScalarAsync<bool>($"""
            SELECT principal_status='legacy_quarantined'
               AND principal_quarantined_at IS NOT NULL
               AND device_id='legacy-device-proof-must-not-claim'
               AND NOT EXISTS (SELECT 1 FROM public.installation_credentials
                                WHERE principal_id='{legacyId}')
              FROM public.users WHERE id='{legacyId}'
            """)).Should().BeTrue();
        await database.ExecuteAsync($"""
            SELECT * FROM public.register_installation(
                '{principalId}','{credentialId}',decode(repeat('11',32),'hex'),1::smallint)
            """);
        (await database.ScalarAsync<bool>($"""
            SELECT principal_status='active' AND device_id IS NULL
               AND principal_quarantined_at IS NULL
               AND (SELECT count(*) FROM public.installation_credentials credential
                     WHERE credential.principal_id=principal.id
                       AND credential.state='active')=1
              FROM public.users principal WHERE id='{principalId}'
            """)).Should().BeTrue();

        var verifyExit = await MigratorApplication.RunAsync(
            ["--verify-only"], ApplicationEnvironment(database.ConnectionString),
            TextWriter.Null, TextWriter.Null);
        verifyExit.Should().Be(0);
    }

    [SkippableFact]
    public async Task PrincipalRetentionUpgrade_RedactsCurrentFutureAndCompressedActivity_ThenCascadesPrincipal()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        using var through021 = MigrationDirectoryThrough("022_principal_retention.sql");
        var prefixResult = await RunAsync(database.ConnectionString, through021.Path);
        prefixResult.Applied.Should().Be(23);

        const string principalId = "a0220000-0000-7000-8000-000000000001";
        const string credentialId = "a0220000-0000-7000-8000-000000000002";
        const string scenarioId = "a0220000-0000-7000-8000-000000000003";
        await database.ExecuteAsync($"""
            SELECT * FROM public.register_installation(
                '{principalId}','{credentialId}',decode(repeat('22',32),'hex'),1::smallint);
            INSERT INTO public.saved_scenarios(
                id,user_id,asset_id,buy_date,sell_date,quantity,quantity_unit,label,
                created_at,type,extra_data,asset_symbol,asset_display_name)
            SELECT '{scenarioId}','{principalId}',asset.id,'2024-01-02',NULL,100,'try',
                   'principal-retention-cascade',clock_timestamp(),'what_if',NULL,
                   asset.symbol,asset.display_name
              FROM public.assets asset WHERE asset.symbol='USDTRY';
            INSERT INTO public.activity_logs(
                id,user_id,device_id,action,status_code,created_at)
            VALUES
                ('a0220000-0000-7000-8000-000000000011','{principalId}',
                 'raw-device-must-redact-old','config_fetch',200,'2024-01-03T12:00:00Z'),
                ('a0220000-0000-7000-8000-000000000012','{principalId}',
                 'raw-device-must-redact-current','config_fetch',200,clock_timestamp()),
                ('a0220000-0000-7000-8000-000000000013','{principalId}',
                 'raw-device-must-redact-future','config_fetch',200,'2032-01-03T12:00:00Z');
            DO $compress_old$
            DECLARE
                scheduler_role text;
                old_chunk regclass;
            BEGIN
                SELECT contract.timescale_scheduler_role INTO scheduler_role
                  FROM public.saydin_role_contract contract WHERE contract.singleton=1;
                SELECT activity.tableoid INTO old_chunk
                  FROM public.activity_logs activity
                 WHERE activity.id='a0220000-0000-7000-8000-000000000011';
                EXECUTE pg_catalog.format('SET LOCAL ROLE %I',scheduler_role);
                PERFORM public.compress_chunk(old_chunk,if_not_compressed=>true);
            END
            $compress_old$;
            """);

        var pre022Delete = async () => await database.ExecuteAsync($"""
            SET ROLE "{database.Contract.Owner.Name}";
            DELETE FROM public.users WHERE id='{principalId}';
            """);
        var pre022Failure = await pre022Delete.Should().ThrowAsync<PostgresException>();
        pre022Failure.Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        var result = await RunAsync(database.ConnectionString);
        result.Applied.Should().Be(3);
        result.AlreadyApplied.Should().Be(23);

        (await database.ScalarAsync<bool>($"""
            SELECT pg_catalog.pg_get_userbyid(
                       (SELECT relowner FROM pg_catalog.pg_class
                         WHERE oid='public.activity_logs'::pg_catalog.regclass))=
                       '{database.Contract.TimescaleScheduler.Name}'
               AND (SELECT pg_catalog.pg_get_userbyid(proowner)=
                              '{database.Contract.TimescaleScheduler.Name}' AND prosecdef
                     FROM pg_catalog.pg_proc
                     WHERE oid='public.redact_activity_logs_before_principal_delete()'::regprocedure)
            """)).Should().BeTrue();
        await database.ExecuteAsync($"""
            SET ROLE "{database.Contract.ApiCapability.Name}";
            INSERT INTO public.activity_logs(
                id,user_id,device_id,action,status_code,created_at)
            VALUES ('a0220000-0000-7000-8000-000000000014','{principalId}',
                    'raw-device-must-redact-new-future','config_fetch',200,
                    '2040-01-03T12:00:00Z');
            """);
        (await database.ScalarAsync<bool>($"""
            WITH relation_set AS (
                SELECT 'public.activity_logs'::regclass AS oid
                UNION
                SELECT format('%I.%I',chunk.chunk_schema,chunk.chunk_name)::regclass
                  FROM timescaledb_information.chunks chunk
                 WHERE chunk.hypertable_schema='public'
                   AND chunk.hypertable_name='activity_logs'
                UNION
                SELECT format('%I.%I',compressed.schema_name,compressed.table_name)::regclass
                  FROM _timescaledb_catalog.hypertable source
                  JOIN _timescaledb_catalog.hypertable compressed
                    ON compressed.id=source.compressed_hypertable_id
                 WHERE source.schema_name='public' AND source.table_name='activity_logs'
                UNION
                SELECT format('%I.%I',compressed_chunk.schema_name,
                               compressed_chunk.table_name)::regclass
                  FROM _timescaledb_catalog.hypertable source
                  JOIN _timescaledb_catalog.chunk source_chunk
                    ON source_chunk.hypertable_id=source.id
                   AND source_chunk.compressed_chunk_id IS NOT NULL
                  JOIN _timescaledb_catalog.chunk compressed_chunk
                    ON compressed_chunk.id=source_chunk.compressed_chunk_id
                 WHERE source.schema_name='public' AND source.table_name='activity_logs'),
            expected_acl AS (VALUES
                ('{database.Contract.ApiCapability.Name}',
                 '{database.Contract.TimescaleScheduler.Name}','INSERT',false),
                ('{database.Contract.TimescaleScheduler.Name}',
                 '{database.Contract.TimescaleScheduler.Name}','SELECT',false),
                ('{database.Contract.TimescaleScheduler.Name}',
                 '{database.Contract.TimescaleScheduler.Name}','UPDATE',false),
                ('{database.Contract.TimescaleScheduler.Name}',
                 '{database.Contract.TimescaleScheduler.Name}','TRIGGER',false)),
            actual_acl AS (
                SELECT relation.oid,grantee.rolname,grantor.rolname,
                       acl.privilege_type,acl.is_grantable
                  FROM relation_set
                  JOIN pg_catalog.pg_class relation ON relation.oid=relation_set.oid
                  CROSS JOIN LATERAL pg_catalog.aclexplode(relation.relacl) acl
                  LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                  LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor),
            acl_differences AS (
                (SELECT relation_set.oid,expected_acl.* FROM relation_set CROSS JOIN expected_acl
                 EXCEPT ALL SELECT * FROM actual_acl)
                UNION ALL
                (SELECT * FROM actual_acl EXCEPT ALL
                 SELECT relation_set.oid,expected_acl.* FROM relation_set CROSS JOIN expected_acl))
            SELECT (SELECT count(*)>=7 AND bool_and(
                               pg_catalog.pg_get_userbyid(relation.relowner)=
                                   '{database.Contract.TimescaleScheduler.Name}')
                      FROM relation_set
                      JOIN pg_catalog.pg_class relation ON relation.oid=relation_set.oid)
               AND NOT EXISTS (SELECT 1 FROM acl_differences)
               AND NOT EXISTS (
                    SELECT 1 FROM relation_set
                    JOIN pg_catalog.pg_attribute attribute
                      ON attribute.attrelid=relation_set.oid
                    CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
                    WHERE attribute.attnum>0 AND NOT attribute.attisdropped)
            """)).Should().BeTrue();
        await database.ExecuteAsync($"""
            BEGIN;
            SET LOCAL ROLE "{database.Contract.TimescaleScheduler.Name}";
            UPDATE public.activity_logs SET device_id=device_id
             WHERE user_id='{principalId}';
            ROLLBACK;
            """);

        await database.ExecuteAsync($"""
            SET ROLE "{database.Contract.Owner.Name}";
            DELETE FROM public.users WHERE id='{principalId}';
            """);

        (await database.ScalarAsync<bool>($"""
            SELECT (SELECT count(*)=0 FROM public.users WHERE id='{principalId}')
               AND (SELECT count(*)=0 FROM public.installation_credentials
                     WHERE principal_id='{principalId}')
               AND (SELECT count(*)=0 FROM public.saved_scenarios
                     WHERE user_id='{principalId}')
               AND (SELECT count(*)=4 AND bool_and(
                           user_id IS NULL AND device_id='server-redacted')
                      FROM public.activity_logs
                     WHERE id IN ('a0220000-0000-7000-8000-000000000011',
                                  'a0220000-0000-7000-8000-000000000012',
                                  'a0220000-0000-7000-8000-000000000013',
                                  'a0220000-0000-7000-8000-000000000014'))
               AND EXISTS (
                    SELECT 1 FROM timescaledb_information.chunks chunk
                     WHERE chunk.hypertable_schema='public'
                       AND chunk.hypertable_name='activity_logs'
                       AND chunk.is_compressed
                       AND '2024-01-03T12:00:00Z'::timestamptz>=chunk.range_start
                       AND '2024-01-03T12:00:00Z'::timestamptz<chunk.range_end)
               AND pg_catalog.to_regnamespace(
                       'saydin_principal_retention_control') IS NULL
            """)).Should().BeTrue();
    }

    [SkippableFact]
    public async Task PrincipalRetention_ConcurrentActivityInsertAndPrincipalDelete_SerializesFailClosed()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);

        const string insertFirstPrincipal = "a0220000-0000-7000-8000-000000000101";
        const string insertFirstCredential = "a0220000-0000-7000-8000-000000000102";
        const string insertFirstActivity = "a0220000-0000-7000-8000-000000000103";
        const string deleteFirstPrincipal = "a0220000-0000-7000-8000-000000000201";
        const string deleteFirstCredential = "a0220000-0000-7000-8000-000000000202";
        const string deleteFirstActivity = "a0220000-0000-7000-8000-000000000203";
        await database.ExecuteAsync($"""
            SELECT * FROM public.register_installation(
                '{insertFirstPrincipal}','{insertFirstCredential}',
                decode(repeat('31',32),'hex'),1::smallint);
            SELECT * FROM public.register_installation(
                '{deleteFirstPrincipal}','{deleteFirstCredential}',
                decode(repeat('32',32),'hex'),1::smallint);
            """);

        await using (var insertConnection = new NpgsqlConnection(database.ConnectionString))
        await using (var deleteConnection = new NpgsqlConnection(database.ConnectionString))
        {
            await insertConnection.OpenAsync();
            await deleteConnection.OpenAsync();
            await using var insertTransaction = await insertConnection.BeginTransactionAsync();
            await using var deleteTransaction = await deleteConnection.BeginTransactionAsync();
            await using (var insert = new NpgsqlCommand($"""
                SET LOCAL ROLE "{database.Contract.ApiCapability.Name}";
                INSERT INTO public.activity_logs(
                    id,user_id,device_id,action,status_code,created_at)
                VALUES ('{insertFirstActivity}','{insertFirstPrincipal}',
                        'concurrent-device-must-redact','config_fetch',200,clock_timestamp())
                """, insertConnection, insertTransaction) { CommandTimeout = 5 })
                await insert.ExecuteNonQueryAsync();

            await using var delete = new NpgsqlCommand($"""
                SET LOCAL ROLE "{database.Contract.Owner.Name}";
                DELETE FROM public.users WHERE id='{insertFirstPrincipal}'
                """, deleteConnection, deleteTransaction) { CommandTimeout = 5 };
            var deleteTask = delete.ExecuteNonQueryAsync();
            await Task.Delay(100);
            deleteTask.IsCompleted.Should().BeFalse(
                "principal deletion must wait for an in-flight activity FK reference");
            await insertTransaction.CommitAsync();
            (await deleteTask).Should().Be(1);
            await deleteTransaction.CommitAsync();
        }

        (await database.ScalarAsync<bool>($"""
            SELECT NOT EXISTS (SELECT 1 FROM public.users
                                WHERE id='{insertFirstPrincipal}')
               AND (SELECT user_id IS NULL AND device_id='server-redacted'
                      FROM public.activity_logs WHERE id='{insertFirstActivity}')
            """)).Should().BeTrue();

        await using (var deleteConnection = new NpgsqlConnection(database.ConnectionString))
        await using (var insertConnection = new NpgsqlConnection(database.ConnectionString))
        {
            await deleteConnection.OpenAsync();
            await insertConnection.OpenAsync();
            await using var deleteTransaction = await deleteConnection.BeginTransactionAsync();
            await using var insertTransaction = await insertConnection.BeginTransactionAsync();
            await using (var delete = new NpgsqlCommand($"""
                SET LOCAL ROLE "{database.Contract.Owner.Name}";
                DELETE FROM public.users WHERE id='{deleteFirstPrincipal}'
                """, deleteConnection, deleteTransaction) { CommandTimeout = 5 })
                (await delete.ExecuteNonQueryAsync()).Should().Be(1);

            await using var insert = new NpgsqlCommand($"""
                SET LOCAL ROLE "{database.Contract.ApiCapability.Name}";
                INSERT INTO public.activity_logs(
                    id,user_id,device_id,action,status_code,created_at)
                VALUES ('{deleteFirstActivity}','{deleteFirstPrincipal}',
                        'concurrent-device-must-not-survive','config_fetch',200,clock_timestamp())
                """, insertConnection, insertTransaction) { CommandTimeout = 5 };
            var insertTask = insert.ExecuteNonQueryAsync();
            await Task.Delay(100);
            insertTask.IsCompleted.Should().BeFalse(
                "an activity FK insert must wait for the uncommitted principal deletion");
            await deleteTransaction.CommitAsync();
            var failure = await FluentActions.Awaiting(async () => await insertTask)
                .Should().ThrowAsync<PostgresException>();
            failure.Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
            await insertTransaction.RollbackAsync();
        }

        (await database.ScalarAsync<bool>($"""
            SELECT NOT EXISTS (SELECT 1 FROM public.users
                                WHERE id='{deleteFirstPrincipal}')
               AND NOT EXISTS (SELECT 1 FROM public.activity_logs
                                WHERE id='{deleteFirstActivity}')
            """)).Should().BeTrue();
    }

    [SkippableFact]
    public async Task PrincipalRetention_FaultAfterBodyRollsBackTransitionAndRerunConverges()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        using var through021 = MigrationDirectoryThrough("022_principal_retention.sql");
        await RunAsync(database.ConnectionString, through021.Path);
        var runner = new MigrationRunner(
            database.Options(TestPaths.MigrationsDirectory), TextWriter.Null,
            new ThrowAfterBodyFault("022_principal_retention"));

        var act = () => runner.RunAsync();

        await act.Should().ThrowAsync<MigratorRejectedException>();
        (await database.ScalarAsync<bool>("""
            SELECT (SELECT state='failed' FROM public.schema_migrations
                     WHERE version='022_principal_retention')
               AND pg_catalog.to_regprocedure(
                       'public.redact_activity_logs_before_principal_delete()') IS NULL
               AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_trigger
                     WHERE tgrelid='public.users'::regclass
                       AND tgname='trg_users_principal_retention_redact')
               AND (SELECT count(*)=1 AND bool_and(confdeltype='n')
                      FROM pg_catalog.pg_constraint
                     WHERE conrelid='public.activity_logs'::regclass
                       AND conname='activity_logs_user_id_fkey')
               AND pg_catalog.to_regprocedure(
                       'saydin_principal_retention_control.consume_principal_retention_transition()')
                   IS NOT NULL
               AND (SELECT count(*)=1 AND bool_and(compression_enabled)
                      FROM timescaledb_information.hypertables
                     WHERE hypertable_schema='public' AND hypertable_name='activity_logs')
               AND (SELECT count(*)=1 AND bool_and(job.scheduled)
                      FROM timescaledb_information.jobs job
                     WHERE job.hypertable_schema='public'
                       AND job.hypertable_name='activity_logs'
                       AND job.proc_name='policy_compression')
            """)).Should().BeTrue();

        var rerun = await RunAsync(database.ConnectionString);
        rerun.Applied.Should().Be(3);
        rerun.AlreadyApplied.Should().Be(23);
        var verifyExit = await MigratorApplication.RunAsync(
            ["--verify-only"], ApplicationEnvironment(database.ConnectionString),
            TextWriter.Null, TextWriter.Null);
        verifyExit.Should().Be(0);
    }

    [SkippableFact]
    public async Task UnsignedTailMigration_IsRejectedBeforeConnectionOrDdl()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        using var migrations = MigrationDirectoryWith("999_unknown_tail.sql", """
            CREATE TABLE public.must_never_execute(id integer);
            """);

        var act = () => new MigrationRunner(
            database.Options(migrations.Path), TextWriter.Null).RunAsync();

        var failure = await act.Should().ThrowAsync<MigratorRejectedException>();
        failure.Which.Code.Should().Be("migration_impact_configuration_required");
        (await database.ScalarAsync<bool>(
            "SELECT to_regclass('public.must_never_execute') IS NULL")).Should().BeTrue();
        (await database.ScalarAsync<bool>(
            "SELECT to_regclass('public.schema_migrations') IS NULL")).Should().BeTrue();
    }

    [SkippableTheory]
    [InlineData("'[]'::jsonb", "chk_saved_scenarios_extra_data_object")]
    [InlineData(
        "jsonb_build_object('v',repeat('a',8193-octet_length(jsonb_build_object('v','')::text)))",
        "chk_saved_scenarios_extra_data_size")]
    public async Task ScenarioIntegrityPreflight_InvalidExistingRowFailsWithoutRepair(
        string extraDataExpression,
        string expectedConstraint)
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        using var pre018 = Pre018Directory();
        await database.PrepareLegacy014Async();
        await RunAsync(database.ConnectionString, pre018.Path, legacyCutover: true);
        using var pre019 = Pre019Directory();
        var userId = Guid.CreateVersion7();
        var scenarioId = Guid.CreateVersion7();
        await database.ExecuteAsync($"""
            INSERT INTO users(id,device_id,tier,created_at,last_seen_at)
            VALUES ('{userId}','preflight-{userId:N}','premium',NOW(),NOW());
            INSERT INTO saved_scenarios(
                id,user_id,asset_id,buy_date,sell_date,quantity,quantity_unit,
                label,created_at,asset_symbol,asset_display_name,type,extra_data)
            VALUES ('{scenarioId}','{userId}',NULL,'2020-01-01',NULL,100,'try',NULL,NOW(),
                    'PORTFOLIO','PORTFOLIO','portfolio',{extraDataExpression});
            """);

        var act = () => RunAsync(database.ConnectionString, pre019.Path, legacyCutover: true);

        var failure = await act.Should().ThrowAsync<MigratorRejectedException>();
        failure.Which.Code.Should().Be("migration_failed");
        failure.Which.GetBaseException().Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be(expectedConstraint);
        (await database.ScalarAsync<long>($"""
            SELECT count(*) FROM saved_scenarios WHERE id='{scenarioId}'
            """)).Should().Be(1, "018 must not delete or repair incompatible rows");
        (await database.ScalarAsync<bool>("""
            SELECT NOT EXISTS (
                SELECT 1 FROM pg_constraint
                 WHERE conname='chk_saved_scenarios_extra_data_object')
            """)).Should().BeTrue("the failed migration transaction must roll back all 018 DDL");
    }

    [SkippableFact]
    public async Task ScenarioIntegrityPreflight_ObjectSqlNullAndJsonNull_AllMigrateWithoutRewrite()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        using var pre018 = Pre018Directory();
        await database.PrepareLegacy014Async();
        await RunAsync(database.ConnectionString, pre018.Path, legacyCutover: true);
        using var pre019 = Pre019Directory();
        var userId = Guid.CreateVersion7();
        await database.ExecuteAsync($"""
            INSERT INTO users(id,device_id,tier,created_at,last_seen_at)
            VALUES ('{userId}','preflight-valid-{userId:N}','premium',NOW(),NOW());
            INSERT INTO saved_scenarios(
                id,user_id,asset_id,buy_date,sell_date,quantity,quantity_unit,
                label,created_at,asset_symbol,asset_display_name,type,extra_data)
            VALUES
                (gen_random_uuid(),'{userId}',NULL,'2020-01-01',NULL,100,'try',NULL,NOW(),
                 'PORTFOLIO','PORTFOLIO','portfolio',NULL),
                (gen_random_uuid(),'{userId}',NULL,'2020-01-01',NULL,100,'try',NULL,NOW(),
                 'PORTFOLIO','PORTFOLIO','portfolio','null'::jsonb),
                (gen_random_uuid(),'{userId}',NULL,'2020-01-01',NULL,100,'try',NULL,NOW(),
                 'PORTFOLIO','PORTFOLIO','portfolio',jsonb_build_object('v',1));
            """);

        var result = await RunAsync(database.ConnectionString, pre019.Path, legacyCutover: true);

        result.Applied.Should().Be(1);
        (await database.ScalarAsync<long>($"""
            SELECT count(*) FROM saved_scenarios WHERE user_id='{userId}'
            """)).Should().Be(3);
        (await database.ScalarAsync<string>("""
            SELECT state FROM schema_migrations WHERE version='018_scenario_integrity'
            """)).Should().Be("succeeded");
    }

    [SkippableFact]
    public async Task ScenarioIntegrityPreflight_UserAboveHardCapFailsWithoutDeletingRows()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        using var pre018 = Pre018Directory();
        await database.PrepareLegacy014Async();
        await RunAsync(database.ConnectionString, pre018.Path, legacyCutover: true);
        using var pre019 = Pre019Directory();
        var userId = Guid.CreateVersion7();
        await database.ExecuteAsync($"""
            INSERT INTO users(id,device_id,tier,created_at,last_seen_at)
            VALUES ('{userId}','preflight-cap-{userId:N}','premium',NOW(),NOW());
            INSERT INTO saved_scenarios(
                id,user_id,asset_id,buy_date,sell_date,quantity,quantity_unit,
                label,created_at,asset_symbol,asset_display_name,type,extra_data)
            SELECT gen_random_uuid(),'{userId}',NULL,'2020-01-01',NULL,100,'try',NULL,
                   NOW()-(ordinal || ' seconds')::interval,
                   'PORTFOLIO','PORTFOLIO','portfolio',NULL
              FROM generate_series(1,101) ordinal;
            """);

        var act = () => RunAsync(database.ConnectionString, pre019.Path, legacyCutover: true);

        var failure = await act.Should().ThrowAsync<MigratorRejectedException>();
        failure.Which.GetBaseException().Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be("chk_saved_scenarios_hard_cap");
        (await database.ScalarAsync<long>($"""
            SELECT count(*) FROM saved_scenarios WHERE user_id='{userId}'
            """)).Should().Be(101);
    }

    [SkippableFact]
    public async Task PartialUnownedDatabase_IsRejectedWithoutDropOrBackRegistration()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await database.ExecuteAsync("CREATE TABLE assets(id uuid PRIMARY KEY)");

        var act = () => RunAsync(database.ConnectionString);

        (await act.Should().ThrowAsync<MigratorRejectedException>())
            .Which.Code.Should().Be("database_partial_or_ambiguous");
        (await database.ScalarAsync<bool>("SELECT to_regclass('public.assets') IS NOT NULL")).Should().BeTrue();
        (await database.ScalarAsync<bool>("SELECT to_regclass('public.schema_migrations') IS NOT NULL")).Should().BeFalse();
    }

    [SkippableFact]
    public async Task PartialLegacyTracking_IsRejectedWithoutAutomaticBaseline()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await database.ExecuteAsync("""
            CREATE TABLE schema_migrations (
                version text PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT now(),
                checksum text NULL);
            INSERT INTO schema_migrations(version) VALUES ('001_initial');
            """);

        var act = () => RunAsync(database.ConnectionString);

        (await act.Should().ThrowAsync<MigratorRejectedException>())
            .Which.Code.Should().Be("database_partial_or_ambiguous");
        (await database.ScalarAsync<long>("SELECT COUNT(*) FROM schema_migrations")).Should().Be(1);
        (await database.ScalarAsync<bool>("SELECT to_regclass('public.saydin_migration_control') IS NOT NULL"))
            .Should().BeFalse();
    }

    [SkippableFact]
    public async Task UnknownSchemaVersion_IsRejected()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await database.ExecuteAsync("""
            INSERT INTO schema_migrations(version, checksum, state)
            VALUES ('999_unknown', repeat('a',64), 'succeeded')
            """);

        var error = new StringWriter();

        var exitCode = await MigratorApplication.RunAsync(
            [], ApplicationEnvironment(database.ConnectionString), TextWriter.Null, error);

        exitCode.Should().Be(3);
        error.ToString().Should().Contain("code=schema_version_unknown");
    }

    [SkippableFact]
    public async Task AppliedChecksumMismatch_IsRejected()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await database.ExecuteAsync("""
            UPDATE schema_migrations SET checksum=repeat('0',64) WHERE version='001_initial'
            """);

        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        var sentinel = connection.Password ?? throw new InvalidOperationException("Test connection password missing.");
        var output = new StringWriter();
        var error = new StringWriter();
        var environment = ApplicationEnvironment(connection.ConnectionString);

        var exitCode = await MigratorApplication.RunAsync([], environment, output, error);

        exitCode.Should().Be(3);
        error.ToString().Should().Contain("code=migration_checksum_mismatch");
        (output.ToString() + error.ToString()).Should().NotContain(sentinel);
    }

    [SkippableFact]
    public async Task ManagedSchemaFingerprintDrift_IsRejectedAfterMigrationVerification()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await database.ExecuteAsync("ALTER TABLE ingestion_jobs DROP COLUMN source");

        var act = () => RunAsync(database.ConnectionString);

        (await act.Should().ThrowAsync<MigratorRejectedException>())
            .Which.Code.Should().Be("schema_fingerprint_mismatch");
        (await database.ScalarAsync<string>("SELECT state FROM saydin_migration_control WHERE singleton=1"))
            .Should().Be("failed");
    }

    [SkippableFact]
    public async Task MissingIngestionWriteFence_IsRejectedAndMarksControlFailed()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await database.ExecuteAsync("DROP TRIGGER trg_price_points_ingestion_fence ON price_points");
        var act = () => RunAsync(database.ConnectionString);

        var failure = await act.Should().ThrowAsync<MigratorRejectedException>();
        failure.Which.Code.Should().Be("schema_fingerprint_mismatch");
        failure.Which.Message.Should().Contain("ingestion_write_fence_missing");
        (await database.ScalarAsync<string>("SELECT state FROM saydin_migration_control WHERE singleton=1"))
            .Should().Be("failed");
    }

    [SkippableFact]
    public async Task DisabledCalendarGuard_VerifyOnlyFailsFingerprint()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await database.ExecuteAsync("""
            ALTER TABLE market_calendar_days
                DISABLE TRIGGER trg_market_calendar_days_immutable
            """);
        var error = new StringWriter();

        var exitCode = await MigratorApplication.RunAsync(
            ["--verify-only"], ApplicationEnvironment(database.ConnectionString),
            TextWriter.Null, error);

        exitCode.Should().Be(3);
        error.ToString().Should().Contain("code=schema_fingerprint_mismatch");
    }

    [SkippableFact]
    public async Task ReplacedCalendarGuardFunction_VerifyOnlyFailsFingerprint()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await database.ExecuteAsync("""
            CREATE OR REPLACE FUNCTION public.enforce_active_market_calendar_release()
            RETURNS trigger LANGUAGE plpgsql
            SET search_path = pg_catalog, public, pg_temp AS $$
            BEGIN RETURN NEW; END
            $$
            """);
        var error = new StringWriter();

        var exitCode = await MigratorApplication.RunAsync(
            ["--verify-only"], ApplicationEnvironment(database.ConnectionString),
            TextWriter.Null, error);

        exitCode.Should().Be(3);
        error.ToString().Should().Contain("code=schema_fingerprint_mismatch");
    }

    [SkippableFact]
    public async Task DisabledScenarioHardCapTrigger_VerifyOnlyFailsFingerprint()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await database.ExecuteAsync("""
            ALTER TABLE saved_scenarios DISABLE TRIGGER trg_saved_scenarios_hard_cap
            """);
        var act = () => RunAsync(database.ConnectionString);

        var failure = await act.Should().ThrowAsync<MigratorRejectedException>();
        failure.Which.Code.Should().Be("schema_fingerprint_mismatch");
        failure.Which.Message.Should().Contain("scenario_hard_cap_guard_missing");
    }

    [SkippableFact]
    public async Task ReplacedScenarioHardCapFunction_VerifyOnlyFailsFingerprint()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await database.ExecuteAsync("""
            CREATE OR REPLACE FUNCTION public.enforce_saved_scenario_hard_cap()
            RETURNS trigger LANGUAGE plpgsql
            SET search_path = pg_catalog, public, pg_temp AS $$
            BEGIN RETURN NEW; END
            $$
            """);
        var act = () => RunAsync(database.ConnectionString);

        var failure = await act.Should().ThrowAsync<MigratorRejectedException>();
        failure.Which.Code.Should().Be("schema_fingerprint_mismatch");
        failure.Which.Message.Should().Contain("scenario_hard_cap_guard_missing");
    }

    [SkippableFact]
    public async Task MissingScenarioKeysetIndex_VerifyOnlyFailsFingerprint()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await database.ExecuteAsync("DROP INDEX idx_saved_scenarios_user_created_id_desc");
        var act = () => RunAsync(database.ConnectionString);

        var failure = await act.Should().ThrowAsync<MigratorRejectedException>();
        failure.Which.Code.Should().Be("schema_fingerprint_mismatch");
        failure.Which.Message.Should().Contain("scenario_keyset_index_missing");
    }

    [SkippableFact]
    public async Task ReintroducedLegacyScenarioIndex_VerifyOnlyFailsFingerprint()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await database.ExecuteAsync("""
            CREATE INDEX idx_saved_scenarios_user
                ON saved_scenarios(user_id, created_at DESC)
            """);
        var act = () => RunAsync(database.ConnectionString);

        var failure = await act.Should().ThrowAsync<MigratorRejectedException>();
        failure.Which.Code.Should().Be("schema_fingerprint_mismatch");
        failure.Which.Message.Should().Contain("scenario_keyset_index_missing");
    }

    [SkippableFact]
    public async Task WrongScenarioHardCapTriggerShape_VerifyOnlyFailsFingerprint()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await database.ExecuteAsync("""
            DROP TRIGGER trg_saved_scenarios_hard_cap ON saved_scenarios;
            CREATE TRIGGER trg_saved_scenarios_hard_cap
            AFTER INSERT ON saved_scenarios
            FOR EACH ROW EXECUTE FUNCTION enforce_saved_scenario_hard_cap()
            """);
        var act = () => RunAsync(database.ConnectionString);

        var failure = await act.Should().ThrowAsync<MigratorRejectedException>();
        failure.Which.Code.Should().Be("schema_fingerprint_mismatch");
        failure.Which.Message.Should().Contain("scenario_hard_cap_guard_missing");
    }

    [SkippableFact]
    public async Task VerifyOnly_NonReadyControlIsRejectedWithoutMutatingState()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        await database.ExecuteAsync("""
            UPDATE saydin_migration_control SET state='bootstrapping' WHERE singleton=1
            """);
        var error = new StringWriter();

        var exitCode = await MigratorApplication.RunAsync(
            ["--verify-only"], ApplicationEnvironment(database.ConnectionString),
            TextWriter.Null, error);

        exitCode.Should().Be(3);
        error.ToString().Should().Contain("code=migration_control_not_ready");
        (await database.ScalarAsync<string>("SELECT state FROM saydin_migration_control WHERE singleton=1"))
            .Should().Be("bootstrapping");
    }

    [SkippableFact]
    public async Task ConcurrentRunners_ExecutePendingBodyExactlyOnce()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        using var package = ImpactTestPackage.Create(
            database,
            "CREATE INDEX ix_migration_concurrency_probe ON public.schema_migrations(state);\n",
            "transactional", ["create-index-nonconcurrent"], "public.schema_migrations",
            postconditionKind: "index-valid",
            postconditionIndex: "ix_migration_concurrency_probe",
            migrationVersion: "900_concurrency_probe");
        var options = database.Options(
            package.MigrationsDirectory, impactConfiguration: package.Configuration);
        var first = new MigrationRunner(
            options, TextWriter.Null, allowCanonicalPrefixFixture: true).RunAsync();
        var second = new MigrationRunner(
            options, TextWriter.Null, allowCanonicalPrefixFixture: true).RunAsync();

        await Task.WhenAll(first, second);

        (await database.ScalarAsync<bool>("""
            SELECT indisvalid AND indisready
              FROM pg_catalog.pg_index
             WHERE indexrelid='public.ix_migration_concurrency_probe'::regclass
            """)).Should().BeTrue();
        new[] { first.Result.Applied, second.Result.Applied }.Should().BeEquivalentTo([0, 1]);
    }

    [SkippableFact]
    public async Task TransactionSessionKill_RollsBackAndOwnedRerunConverges()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        using var package = ImpactTestPackage.Create(
            database,
            "CREATE INDEX ix_migration_kill_probe ON public.schema_migrations(state);\n",
            "transactional", ["create-index-nonconcurrent"], "public.schema_migrations",
            postconditionKind: "index-valid", postconditionIndex: "ix_migration_kill_probe",
            migrationVersion: "900_kill_probe");
        var fault = new TerminateSessionAfterBodyFault(admin, "900_kill_probe");

        var act = () => new MigrationRunner(
            database.Options(package.MigrationsDirectory,
                impactConfiguration: package.Configuration), TextWriter.Null, fault,
            allowCanonicalPrefixFixture: true).RunAsync();

        await act.Should().ThrowAsync<MigratorRejectedException>();
        await database.WaitUntilReachableAsync();
        (await database.ScalarAsync<bool>(
            "SELECT to_regclass('public.ix_migration_kill_probe') IS NOT NULL"))
            .Should().BeFalse("the killed session's transaction must roll back");

        var rerun = await new MigrationRunner(
            database.Options(package.MigrationsDirectory,
                impactConfiguration: package.Configuration), TextWriter.Null,
            allowCanonicalPrefixFixture: true).RunAsync();
        rerun.Applied.Should().Be(1);
        (await database.ScalarAsync<bool>(
            "SELECT to_regclass('public.ix_migration_kill_probe') IS NOT NULL")).Should().BeTrue();
        (await database.ScalarAsync<string>("SELECT state FROM schema_migrations WHERE version='900_kill_probe'"))
            .Should().Be("succeeded");
    }

    [SkippableFact]
    public async Task TransactionFault_RecordsFailedRollsBackAndOwnedRerunConverges()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        using var package = ImpactTestPackage.Create(
            database,
            "CREATE INDEX ix_migration_transaction_probe ON public.schema_migrations(state);\n",
            "transactional", ["create-index-nonconcurrent"], "public.schema_migrations",
            postconditionKind: "index-valid",
            postconditionIndex: "ix_migration_transaction_probe",
            migrationVersion: "900_transaction_probe");
        var fault = new ThrowAfterBodyFault("900_transaction_probe");

        var act = () => new MigrationRunner(
            database.Options(package.MigrationsDirectory,
                impactConfiguration: package.Configuration), TextWriter.Null, fault,
            allowCanonicalPrefixFixture: true).RunAsync();

        await act.Should().ThrowAsync<MigratorRejectedException>();
        (await database.ScalarAsync<bool>(
            "SELECT to_regclass('public.ix_migration_transaction_probe') IS NOT NULL"))
            .Should().BeFalse();
        (await database.ScalarAsync<string>("SELECT state FROM schema_migrations WHERE version='900_transaction_probe'"))
            .Should().Be("failed");

        var rerun = await new MigrationRunner(
            database.Options(package.MigrationsDirectory,
                impactConfiguration: package.Configuration), TextWriter.Null,
            allowCanonicalPrefixFixture: true).RunAsync();
        rerun.Applied.Should().Be(1);
        (await database.ScalarAsync<bool>(
            "SELECT to_regclass('public.ix_migration_transaction_probe') IS NOT NULL")).Should().BeTrue();
    }

    [SkippableFact]
    public async Task CommitAcknowledgementLoss_ReconcilesAndRerunIsNoOp()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateAsync(admin);
        await RunAsync(database.ConnectionString);
        using var package = ImpactTestPackage.Create(
            database,
            "CREATE INDEX ix_migration_commit_probe ON public.schema_migrations(state);\n",
            "transactional", ["create-index-nonconcurrent"], "public.schema_migrations",
            postconditionKind: "index-valid", postconditionIndex: "ix_migration_commit_probe",
            migrationVersion: "900_commit_probe");
        var fault = new ThrowAfterCommitFault("900_commit_probe");

        var first = await new MigrationRunner(
            database.Options(package.MigrationsDirectory,
                impactConfiguration: package.Configuration), TextWriter.Null, fault,
            allowCanonicalPrefixFixture: true).RunAsync();
        var rerun = await new MigrationRunner(
            database.Options(package.MigrationsDirectory,
                impactConfiguration: package.Configuration), TextWriter.Null,
            allowCanonicalPrefixFixture: true).RunAsync();

        first.Applied.Should().Be(1);
        rerun.Applied.Should().Be(0);
        (await database.ScalarAsync<bool>(
            "SELECT to_regclass('public.ix_migration_commit_probe') IS NOT NULL")).Should().BeTrue();
    }

    [SkippableFact]
    public async Task Legacy019WireCommitAcknowledgementLoss_NewProcessWithSameCutoverArgumentsReconciles()
    {
        var admin = IntegrationEnvironment.RequirePrimary();
        await using var database = await TestDatabase.CreateHbaBoundAsync(
            admin, HbaBoundTestFixture.LegacyAck);
        await database.PrepareLegacy014Async();
        using var pre019 = Pre019Directory();
        await RunAsync(database.ConnectionString, pre019.Path, legacyCutover: true);
        await InstallPublicRegexOperatorHijackAsync(database);
        var backend = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        await using var proxy = PostgresCommitAckDropProxy.Start(backend.Host!, backend.Port);
        var proxiedEnvironment = new Dictionary<string, string?>(
            database.ApplicationEnvironment(), StringComparer.Ordinal)
        {
            ["PGHOST"] = "127.0.0.1",
            ["PGPORT"] = proxy.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // The test proxy intentionally inspects PostgreSQL frames and is not a TLS
            // terminator. Make that transport contract explicit instead of depending on
            // the source fixture's Npgsql SSL default (Prefer differs across harnesses).
            ["PGSSLMODE"] = "Disable",
        };
        var proxiedAdmin = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Host = "127.0.0.1",
            Port = proxy.Port,
            Pooling = false,
            SslMode = SslMode.Disable,
        };
        var proxiedAdminFile = database.WriteAdditionalSecret(
            "admin-proxy", proxiedAdmin.ConnectionString);
        var arguments = new[]
        {
            "--legacy-privilege-cutover", "--admin-connection-file", proxiedAdminFile,
        };

        var uncertain = await RunMigratorProcessAsync(arguments, proxiedEnvironment);

        uncertain.ExitCode.Should().Be(3,
            "the deliberately unacknowledged server commit must be reported as an uncertain outcome; stdout={0}; stderr={1}",
            uncertain.StandardOutput, uncertain.StandardError);
        proxy.DroppedCommitAcknowledgement.Should().BeTrue(
            "the server committed 019 but CommandComplete/ReadyForQuery was not forwarded; stdout={0}; stderr={1}",
            uncertain.StandardOutput, uncertain.StandardError);
        (await database.ScalarAsync<string>("""
            SELECT state FROM schema_migrations WHERE version='019_privilege_separation'
            """)).Should().Be("succeeded", "the server committed before the acknowledgement was lost");

        var reconciled = await RunMigratorProcessAsync(arguments, proxiedEnvironment);

        reconciled.ExitCode.Should().Be(0,
            "the same cutover arguments must reconcile after the lost acknowledgement; stdout={0}; stderr={1}",
            reconciled.StandardOutput, reconciled.StandardError);
        reconciled.StandardOutput.Should().NotContain("applied: 019_privilege_separation.sql");
        reconciled.StandardOutput.Should().Contain("backup_postbootstrap_required=true");
        (await database.ScalarAsync<string>(
            "SELECT state FROM saydin_migration_control WHERE singleton=1")).Should().Be("ready");
        await AssertPublicRegexOperatorWasIsolatedAsync(database);

        await database.EnsureRolesThroughApplicationAsync();
        var converged = await new MigrationRunner(
            Options(database.ConnectionString, TestPaths.MigrationsDirectory), TextWriter.Null).RunAsync();
        converged.Applied.Should().Be(0);
        converged.AlreadyApplied.Should().Be(27);
        converged.BackupPostBootstrapRequired.Should().BeFalse();

        var verifyExit = await MigratorApplication.RunAsync(
            ["--verify-only"], ApplicationEnvironment(database.ConnectionString),
            TextWriter.Null, TextWriter.Null);
        verifyExit.Should().Be(0);
    }

    [SkippableFact]
    public async Task OptionalExporter_IsSkippedAndManagedExporterCapabilityRemainsMonitorOnly()
    {
        var primaryAdmin = IntegrationEnvironment.RequirePrimary();
        var secondaryAdmin = IntegrationEnvironment.RequireSecondary();
        await using var primary = await TestDatabase.CreateAsync(primaryAdmin);
        await using var secondary = await TestDatabase.CreateAsync(secondaryAdmin);
        var output = new StringWriter();

        await new MigrationRunner(
            Options(primary.ConnectionString, TestPaths.MigrationsDirectory), output).RunAsync();

        (await primary.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='saydin_exporter')"))
            .Should().BeFalse();
        (await primary.ScalarAsync<string>("""
            SELECT state FROM schema_migrations WHERE version='012b_create_exporter_role'
            """)).Should().Be("skipped_optional");
        (await primary.ScalarAsync<bool>($"""
            SELECT pg_has_role('{primary.Contract.ExporterCapability.Name}','pg_monitor','MEMBER')
            """)).Should().BeTrue();
        (await secondary.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='saydin_exporter')"))
            .Should().BeFalse();
        (await secondary.ScalarAsync<bool>("SELECT to_regclass('public.schema_migrations') IS NOT NULL"))
            .Should().BeFalse();
        output.ToString().Should().Contain("skipped optional: 012b_create_exporter_role.sh");
    }

    private static async Task<MigrationRunResult> RunAsync(
        string connectionString,
        string? directory = null,
        bool legacyCutover = false)
    {
        var result = await new MigrationRunner(
            Options(connectionString, directory ?? TestPaths.MigrationsDirectory, legacyCutover), TextWriter.Null,
            allowCanonicalPrefixFixture: directory is not null).RunAsync();
        if (result.BackupPostBootstrapRequired)
            await TestDatabase.ForConnection(connectionString).EnsureRolesAsync();
        return result;
    }

    private static TemporaryDirectory MigrationDirectoryThrough(string exclusiveFileName)
    {
        var directory = TemporaryDirectory.Create();
        foreach (var source in Directory.EnumerateFiles(TestPaths.MigrationsDirectory)
                     .Where(path => Path.GetExtension(path) is ".sql" or ".sh")
                     .Where(path => string.CompareOrdinal(
                         Path.GetFileName(path), exclusiveFileName) < 0))
            File.Copy(source, Path.Combine(directory.Path, Path.GetFileName(source)));
        return directory;
    }

    private static async Task OpenSchedulerConnectionAsync(TestDatabase database)
    {
        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = database.Contract.TimescaleScheduler.Name,
            Password = "SCHEDULER-MUST-NEVER-AUTHENTICATE-A9!",
            Pooling = false,
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
    }

    private static async Task<ProcessResult> RunMigratorProcessAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "Saydin.DatabaseMigrator.dll");
        File.Exists(assembly).Should().BeTrue(
            "the child must execute the migrator built in the active test configuration");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(assembly);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("DATABASE_URL");
        start.Environment.Remove("PGPASSWORD");
        start.Environment.Remove("POSTGRES_EXPORTER_PASSWORD");
        foreach (var (name, value) in environment)
        {
            if (value is null) start.Environment.Remove(name);
            else start.Environment[name] = value;
        }
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Failed to start migrator child process.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync(timeout.Token);
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static MigratorOptions Options(
        string connectionString,
        string directory,
        bool legacyCutover = false) =>
        TestDatabase.ForConnection(connectionString).Options(directory, legacyCutover);

    private static IReadOnlyDictionary<string, string?> ApplicationEnvironment(string connectionString) =>
        TestDatabase.ForConnection(connectionString).ApplicationEnvironment();

    private static TemporaryDirectory MigrationDirectoryWith(string fileName, string sql)
    {
        var directory = TemporaryDirectory.Create();
        foreach (var source in Directory.EnumerateFiles(TestPaths.MigrationsDirectory))
        {
            if (Path.GetExtension(source) is ".sql" or ".sh")
                File.Copy(source, Path.Combine(directory.Path, Path.GetFileName(source)));
        }
        File.WriteAllText(Path.Combine(directory.Path, fileName), sql);
        return directory;
    }

    private static TemporaryDirectory Historical014Directory()
    {
        var directory = TemporaryDirectory.Create();
        foreach (var source in Directory.EnumerateFiles(TestPaths.MigrationsDirectory))
        {
            var fileName = Path.GetFileName(source);
            if (Path.GetExtension(source) is ".sql" or ".sh"
                && string.CompareOrdinal(fileName, "015_") < 0)
                File.Copy(source, Path.Combine(directory.Path, fileName));
        }
        return directory;
    }

    private static async Task CreateImpactFixtureTableAsync(TestDatabase database, int rowCount)
    {
        await database.ExecuteAsync($"""
            CREATE TABLE public.dbm004_fixture (
                id uuid PRIMARY KEY,
                marker text NULL
            );
            ALTER TABLE public.dbm004_fixture OWNER TO "{database.Contract.Owner.Name}";
            REVOKE ALL ON public.dbm004_fixture FROM PUBLIC;
            INSERT INTO public.dbm004_fixture(id,marker)
            SELECT pg_catalog.gen_random_uuid(),NULL
              FROM pg_catalog.generate_series(1,{rowCount});
            """);
    }

    private static async Task CreateCompressedImpactFixtureAsync(TestDatabase database, int rowCount)
    {
        await database.ExecuteAsync($"""
            CREATE TABLE public.dbm004_compressed_fixture (
                id uuid NOT NULL,
                marker text NULL,
                created_at timestamptz NOT NULL
            );
            ALTER TABLE public.dbm004_compressed_fixture
                OWNER TO "{database.Contract.TimescaleScheduler.Name}";
            REVOKE ALL ON public.dbm004_compressed_fixture FROM PUBLIC;
            SELECT public.create_hypertable(
                'public.dbm004_compressed_fixture','created_at',
                chunk_time_interval=>interval '1 day');
            ALTER TABLE public.dbm004_compressed_fixture SET (
                timescaledb.compress,
                timescaledb.compress_orderby='created_at DESC');
            SELECT public.add_compression_policy(
                'public.dbm004_compressed_fixture',interval '1 day');
            INSERT INTO public.dbm004_compressed_fixture(id,marker,created_at)
            SELECT pg_catalog.gen_random_uuid(),NULL,
                   pg_catalog.clock_timestamp()-interval '30 days'
                     +(value*interval '1 second')
              FROM pg_catalog.generate_series(1,{rowCount}) value;
            SELECT public.compress_chunk(chunk,true)
              FROM public.show_chunks(
                  'public.dbm004_compressed_fixture',older_than=>interval '2 days') chunk;
            """);
    }

    private static Dictionary<string, object?> OnlineFixturePlan(string relation, int batchSize) => new()
    {
        ["batchSize"] = batchSize,
        ["keyColumn"] = "id",
        ["maxBatchMilliseconds"] = 2_000,
        ["pauseCompressionPolicy"] = false,
        ["planKind"] = "uuid-keyset-set-constant-where-null",
        ["relation"] = relation,
        ["targetColumn"] = "marker",
        ["targetType"] = "text",
        ["targetValue"] = "redacted",
    };

    private static async Task AssertImpactPreflightLeftDatabaseUnmutatedAsync(TestDatabase database)
    {
        (await database.ScalarAsync<string>("""
            SELECT state FROM public.saydin_migration_control WHERE singleton=1
            """)).Should().Be("ready");
        (await database.ScalarAsync<bool>("""
            SELECT NOT EXISTS (
                SELECT 1 FROM public.schema_migrations WHERE version='026_impact_test')
               AND pg_catalog.to_regclass('public.ix_dbm004_fixture_marker') IS NULL
               AND pg_catalog.to_regclass('public.saydin_online_migration_checkpoints') IS NULL
            """)).Should().BeTrue();
    }

    private static TemporaryDirectory Pre018Directory()
    {
        var directory = TemporaryDirectory.Create();
        foreach (var source in Directory.EnumerateFiles(TestPaths.MigrationsDirectory))
        {
            var fileName = Path.GetFileName(source);
            if (Path.GetExtension(source) is ".sql" or ".sh"
                && string.CompareOrdinal(fileName, "018_") < 0)
                File.Copy(source, Path.Combine(directory.Path, fileName));
        }
        return directory;
    }

    private static TemporaryDirectory Pre019Directory()
    {
        var directory = TemporaryDirectory.Create();
        foreach (var source in Directory.EnumerateFiles(TestPaths.MigrationsDirectory))
        {
            var fileName = Path.GetFileName(source);
            if (Path.GetExtension(source) is ".sql" or ".sh"
                && string.CompareOrdinal(fileName, "019_") < 0)
                File.Copy(source, Path.Combine(directory.Path, fileName));
        }
        return directory;
    }

    private static async Task InstallPublicRegexOperatorHijackAsync(TestDatabase database)
    {
        await database.ExecuteAsync("""
            CREATE SCHEMA saydin_operator_hijack_probe;
            CREATE TABLE saydin_operator_hijack_probe.sentinel(hit integer NOT NULL);
            CREATE FUNCTION public.saydin_test_text_regex_not_match(
                left_value text, pattern_value text)
            RETURNS boolean
            LANGUAGE plpgsql
            VOLATILE
            AS $function$
            BEGIN
                INSERT INTO saydin_operator_hijack_probe.sentinel(hit) VALUES (1);
                RETURN left_value OPERATOR(pg_catalog.!~) pattern_value;
            END
            $function$;
            CREATE OPERATOR public.!~ (
                LEFTARG = text,
                RIGHTARG = text,
                FUNCTION = public.saydin_test_text_regex_not_match
            );
            """);

        (await database.ScalarAsync<bool>(
            "SELECT 'catalog-value' OPERATOR(pg_catalog.!~) '^does-not-match$'"))
            .Should().BeTrue();
        (await database.ScalarAsync<bool>(
            "SELECT 'catalog-value' OPERATOR(public.!~) '^does-not-match$'"))
            .Should().BeTrue("the malicious overload must preserve the catalog result while recording use");
        (await database.ScalarAsync<long>(
            "SELECT count(*) FROM saydin_operator_hijack_probe.sentinel"))
            .Should().Be(1, "the test must prove its public operator overload is executable");
        await database.ExecuteAsync("TRUNCATE TABLE saydin_operator_hijack_probe.sentinel");
    }

    private static async Task AssertPublicRegexOperatorWasIsolatedAsync(TestDatabase database)
    {
        (await database.ScalarAsync<long>(
            "SELECT count(*) FROM saydin_operator_hijack_probe.sentinel"))
            .Should().Be(0,
                "runner-owned control, historical, and privilege-separation transactions must resolve catalog operators first");
        (await database.ScalarAsync<bool>(
            "SELECT pg_catalog.to_regoperator('public.!~(text,text)') IS NOT NULL"))
            .Should().BeTrue(
                "unknown public extension/user operators are isolated by search_path rather than destructively removed");
    }

    private sealed class ThrowAfterCommitFault(string version) : IMigrationFaultInjector
    {
        private bool _thrown;

        public ValueTask AfterBodyAsync(
            MigrationDefinition migration,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask AfterCommitAsync(MigrationDefinition migration, CancellationToken cancellationToken)
        {
            if (!_thrown && migration.Version == version)
            {
                _thrown = true;
                throw new IOException("simulated commit acknowledgement loss");
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancelAfterFirstOnlineCommit(string version) : IMigrationFaultInjector
    {
        private bool cancelled;

        public ValueTask AfterBodyAsync(
            MigrationDefinition migration,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask AfterCommitAsync(
            MigrationDefinition migration,
            CancellationToken cancellationToken)
        {
            if (!cancelled && migration.Version == version)
            {
                cancelled = true;
                throw new OperationCanceledException("simulated process termination after committed batch");
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReplaceOnlineLeaseAfterFirstBody(string version) : IMigrationFaultInjector
    {
        private bool replaced;

        public async ValueTask AfterBodyAsync(
            MigrationDefinition migration,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            if (replaced || migration.Version != version)
                return;
            replaced = true;
            await using var command = new NpgsqlCommand("""
                UPDATE public.saydin_online_migration_checkpoints
                   SET lease_nonce=pg_catalog.gen_random_uuid(),
                       lease_expires_at=pg_catalog.clock_timestamp()+INTERVAL '1 hour'
                 WHERE migration_version=$1
                """, connection, transaction);
            command.Parameters.AddWithValue(version);
            (await command.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }

        public ValueTask AfterCommitAsync(
            MigrationDefinition migration,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class ThrowAfterBodyFault(string version) : IMigrationFaultInjector
    {
        private bool _thrown;

        public ValueTask AfterBodyAsync(
            MigrationDefinition migration,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            if (!_thrown && migration.Version == version)
            {
                _thrown = true;
                throw new IOException("simulated transaction fault");
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask AfterCommitAsync(MigrationDefinition migration, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class TerminateSessionAfterBodyFault(string adminConnectionString, string version)
        : IMigrationFaultInjector
    {
        private bool _terminated;

        public async ValueTask AfterBodyAsync(
            MigrationDefinition migration,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
        {
            if (_terminated || migration.Version != version)
                return;
            _terminated = true;
            await using var admin = new NpgsqlConnection(adminConnectionString);
            await admin.OpenAsync(cancellationToken);
            await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend($1)", admin);
            terminate.Parameters.AddWithValue(connection.ProcessID);
            (await terminate.ExecuteScalarAsync(cancellationToken)).Should().Be(true);
        }

        public ValueTask AfterCommitAsync(MigrationDefinition migration, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

[CollectionDefinition("migration-integration", DisableParallelization = true)]
public sealed class MigrationIntegrationCollection;
