using FluentAssertions;
using Npgsql;

namespace Saydin.DataRepair.IntegrationTests;

public sealed class RepairGuardIntegrationTests(RepairDatabaseFixture fixture)
    : IClassFixture<RepairDatabaseFixture>
{
    [Theory]
    [InlineData("price_points")]
    [InlineData("inflation_rates")]
    public async Task GuardSourceTableSelectAcl_IsRequiredByPreflight(string table)
    {
        var capability = $"{fixture.RolePrefix}_ingestion_cap";
        try
        {
            var action = () => fixture.VerifyPreflightAfterAsync(async () =>
                await fixture.ExecuteAdminAsync(
                    $"REVOKE SELECT ON public.{table} FROM {capability}"));

            (await action.Should().ThrowAsync<RepairRejectedException>()).Which.Code
                .Should().Be("repair_database_acl_rejected");
        }
        finally
        {
            await fixture.ExecuteAdminAsync(
                $"GRANT SELECT ON public.{table} TO {capability}");
        }
    }

    [Fact]
    public async Task IngestionLoginWithMigrationControlRead_IsRejectedByPreflight()
    {
        try
        {
            var action = () => fixture.VerifyPreflightAfterAsync(async () =>
                await fixture.ExecuteAdminAsync(
                    $"GRANT SELECT ON public.schema_migrations TO {fixture.IngestionLogin}"));

            (await action.Should().ThrowAsync<RepairRejectedException>()).Which.Code
                .Should().Be("repair_database_acl_rejected");
        }
        finally
        {
            await fixture.ExecuteAdminAsync(
                $"REVOKE SELECT ON public.schema_migrations FROM {fixture.IngestionLogin}");
        }
    }

    [Fact]
    public async Task MissingPlannedWindow_IsRejectedWithoutReceipt()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            var missing = Guid.CreateVersion7();
            fixture.RewritePlan(repair, repair.Plan with
            {
                Operations =
                [
                    repair.Plan.Operations[0] with { WindowId = missing },
                    repair.Plan.Operations[1],
                ],
            });

            var result = await fixture.RunAsync(repair, "apply");

            result.Exit.Should().Be(RepairExitCodes.Rejected);
            result.Error.Should().Contain("repair_window_missing");
            Directory.EnumerateFileSystemEntries(repair.Files.ReceiptRoot).Should().BeEmpty();
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task RunningJobRejectsApplyWithoutMutation()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            await fixture.SeedRunningJobAsync(repair.Files.WindowId);

            var result = await fixture.RunAsync(repair, "apply");

            result.Exit.Should().Be(RepairExitCodes.Rejected);
            result.Error.Should().Contain("repair_running_job_rejected");
            (await fixture.LoadSnapshotAsync(repair.Files.WindowId)).State
                .Should().Be("permanent_failed");
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterOrSameEndEncompassingTerminalWindow_RejectsApply(bool sameRangeEnd)
    {
        var repair = await fixture.CreateCaseAsync();
        Guid newer = default;
        try
        {
            newer = await fixture.SeedTerminalWindowAsync(repair, sameRangeEnd);

            var result = await fixture.RunAsync(repair, "apply");

            result.Exit.Should().Be(RepairExitCodes.Rejected);
            result.Error.Should().Contain("repair_newer_terminal_window_rejected");
        }
        finally
        {
            if (newer != default)
                await fixture.ExecuteAdminAsync(
                    "DELETE FROM public.ingestion_windows WHERE id=$1", newer);
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task GuardRowBudget_IsFailClosedAcrossPreAndPostImages()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            await fixture.SeedRelatedGuardStateAsync(repair);

            var result = await fixture.RunAsync(repair, "apply", maximumGuardRows: 4);

            result.Exit.Should().Be(RepairExitCodes.Rejected);
            result.Error.Should().Contain("repair_guard_row_budget_exceeded");
            (await fixture.LoadSnapshotAsync(repair.Files.WindowId)).State
                .Should().Be("permanent_failed");
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task RelatedStateMutationInsideTransaction_IsRejectedAndRolledBack()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            var fault = new SqlCheckpointFault(
                RepairDatabaseCheckpoint.ApplyAfterCasBeforePostGuard,
                """
                INSERT INTO public.ingestion_jobs(
                    asset_id,job_type,started_at,finished_at,status,error_message,
                    date_range_start,date_range_end,source,window_id,outcome_code)
                SELECT NULL,'inflation_daily',pg_catalog.clock_timestamp(),
                       pg_catalog.clock_timestamp(),'failed','transaction drift',
                       range_start,range_end,'evds',id,'transaction_drift'
                  FROM public.ingestion_windows WHERE id=@window
                """);

            var result = await fixture.RunAsync(
                repair, "apply", databaseFaultInjector: fault);

            result.Exit.Should().Be(RepairExitCodes.Rejected);
            result.Error.Should().Contain("repair_guard_changed_inside_transaction");
            (await fixture.LoadSnapshotAsync(repair.Files.WindowId)).State
                .Should().Be("permanent_failed");
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task ApplyFullRowCasRejectsInjectedWindowDrift()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            var fault = new SqlCheckpointFault(
                RepairDatabaseCheckpoint.ApplyBeforeCas,
                "UPDATE public.ingestion_windows SET attempt_count=attempt_count+1 WHERE id=@window");

            var result = await fixture.RunAsync(
                repair, "apply", databaseFaultInjector: fault);

            result.Exit.Should().Be(RepairExitCodes.Rejected);
            result.Error.Should().Contain("repair_cas_failed");
            RepairDatabase.SnapshotSha256(await fixture.LoadSnapshotAsync(repair.Files.WindowId))
                .Should().Be(repair.Plan.Operations[0].PreimageSha256);
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task RollbackRejectsRelatedGuardDrift()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            await fixture.SeedRelatedGuardStateAsync(repair);
            (await fixture.RunAsync(repair, "apply")).Exit.Should().Be(RepairExitCodes.Success);
            await fixture.ExecuteAdminAsync("""
                UPDATE public.ingestion_jobs SET error_message='related state drift'
                 WHERE window_id=$1
                """, repair.Files.WindowId);

            var result = await fixture.RunAsync(repair, "rollback");

            result.Exit.Should().Be(RepairExitCodes.Rejected);
            result.Error.Should().Contain("rollback_related_state_changed");
            (await fixture.LoadSnapshotAsync(repair.Files.WindowId)).State
                .Should().Be("retryable_failed");
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Theory]
    [InlineData("RollbackBeforeCas", "rollback_cas_failed")]
    [InlineData("RollbackAfterCasBeforeVerification",
        "rollback_preimage_restore_failed")]
    public async Task RollbackCasAndRestoreVerification_AreFailClosed(
        string checkpointName,
        string expectedCode)
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            (await fixture.RunAsync(repair, "apply")).Exit.Should().Be(RepairExitCodes.Success);
            var checkpoint = Enum.Parse<RepairDatabaseCheckpoint>(checkpointName);
            var fault = new SqlCheckpointFault(checkpoint,
                "UPDATE public.ingestion_windows SET attempt_count=attempt_count+1 WHERE id=@window");

            var result = await fixture.RunAsync(
                repair, "rollback", databaseFaultInjector: fault);

            result.Exit.Should().Be(RepairExitCodes.Rejected);
            result.Error.Should().Contain(expectedCode);
            (await fixture.LoadSnapshotAsync(repair.Files.WindowId)).State
                .Should().Be("retryable_failed");
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task RollbackCommitThenPublishFailure_ReconcilesPendingBeforeApplyPostimageCheck()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            (await fixture.RunAsync(repair, "apply")).Exit.Should().Be(RepairExitCodes.Success);
            var failed = await fixture.RunAsync(repair, "rollback", receiptCheckpoint: checkpoint =>
            {
                if (checkpoint == ReceiptStoreCheckpoint.BeforePromoteRename)
                    throw new IOException("deterministic rollback promote failure");
            });
            failed.Exit.Should().Be(RepairExitCodes.Rejected);
            failed.Error.Should().Contain("receipt_publish_after_commit_failed");
            Directory.EnumerateDirectories(repair.Files.ReceiptRoot)
                .Should().ContainSingle(path => Path.GetFileName(path).StartsWith(".pending-"));

            var reconciled = await fixture.RunAsync(repair, "rollback");

            reconciled.Exit.Should().Be(RepairExitCodes.Success, reconciled.Error);
            reconciled.Output.Should().Contain("status=reconciled");
            RepairDatabase.SnapshotSha256(await fixture.LoadSnapshotAsync(repair.Files.WindowId))
                .Should().Be(repair.Plan.Operations[0].PreimageSha256);
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task TargetLeaseLossBeforeMutation_IsRejectedInsideDatabaseTransaction()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            var result = await fixture.RunAsync(repair, "apply", afterLiveTrust: async (lease, token) =>
            {
                var processId = await lease.GetBackendProcessIdAsync(token);
                (await fixture.ExecuteAdminScalarAsync(
                    "SELECT pg_catalog.pg_terminate_backend($1)", processId)).Should().Be(true);
            });

            result.Exit.Should().Be(RepairExitCodes.TargetRejected);
            result.Error.Should().Contain("repair_target_lock_lost");
            (await fixture.LoadSnapshotAsync(repair.Files.WindowId)).State
                .Should().Be("permanent_failed");
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task FinalApplyReceipt_RemainsIdempotentAfterNormalIngestionClaimsWindow()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            (await fixture.RunAsync(repair, "apply")).Exit.Should().Be(RepairExitCodes.Success);
            await fixture.AdvanceByNormalIngestionAsync(repair);

            var replay = await fixture.RunAsync(repair, "apply");

            replay.Exit.Should().Be(RepairExitCodes.Success, replay.Error);
            replay.Output.Should().Contain("status=idempotent");
            (await fixture.LoadSnapshotAsync(repair.Files.WindowId)).State.Should().Be("running");
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task TamperedFinalReceipt_IsRejectedBeforeIdempotentReplay()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            (await fixture.RunAsync(repair, "apply")).Exit.Should().Be(RepairExitCodes.Success);
            var final = Directory.EnumerateDirectories(repair.Files.ReceiptRoot).Single();
            var signature = Path.Combine(final, "receipt.sig");
            var bytes = await File.ReadAllBytesAsync(signature);
            bytes[0] ^= 0xff;
            await File.WriteAllBytesAsync(signature, bytes);

            var replay = await fixture.RunAsync(repair, "apply");

            replay.Exit.Should().Be(RepairExitCodes.ReceiptFailure);
            replay.Error.Should().Contain("receipt_signature_invalid");
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    private sealed class SqlCheckpointFault(
        RepairDatabaseCheckpoint expected,
        string sql) : IRepairDatabaseFaultInjector
    {
        public async Task OnCheckpointAsync(
            RepairDatabaseCheckpoint checkpoint,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid windowId,
            CancellationToken cancellationToken)
        {
            if (checkpoint != expected) return;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("window", windowId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
