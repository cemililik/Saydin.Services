using FluentAssertions;
using Npgsql;

namespace Saydin.DataRepair.IntegrationTests;

public sealed class RepairExecutorIntegrationTests(RepairDatabaseFixture fixture)
    : IClassFixture<RepairDatabaseFixture>
{
    [Fact]
    public async Task SchemaV2Requeue_ReleasesStaleCalendarBindingAndRollbackRestoresIt()
    {
        var repair = await fixture.CreateCaseAsync(calendarBound: true);
        try
        {
            repair.Preimage.CalendarReleaseId.Should().NotBeNull();

            var apply = await fixture.RunAsync(repair, "apply");
            apply.Exit.Should().Be(RepairExitCodes.Success, apply.Error);
            var rebound = await fixture.LoadSnapshotAsync(repair.Files.WindowId);
            rebound.State.Should().Be("pending");
            rebound.CalendarReleaseId.Should().BeNull();

            var rollback = await fixture.RunAsync(repair, "rollback");
            rollback.Exit.Should().Be(RepairExitCodes.Success, rollback.Error);
            var restored = await fixture.LoadSnapshotAsync(repair.Files.WindowId);
            restored.State.Should().Be("permanent_failed");
            restored.CalendarReleaseId.Should().Be(repair.Preimage.CalendarReleaseId);
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task ProductionTargetRejectsLocalDqaEvidenceBeforeDatabaseMutation()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            fixture.RewritePlan(repair, repair.Plan with
            {
                Target = repair.Plan.Target with { Environment = "production" },
            });
            var result = await fixture.RunAsync(repair, "dry-run", runtime: name =>
                name == "SAYDIN_ENVIRONMENT" ? "production" : fixture.RuntimeEnvironment(name));
            result.Exit.Should().Be(RepairExitCodes.SignatureFailure);
            result.Error.Should().Contain("evidence_manifest_invalid");
            (await fixture.LoadSnapshotAsync(repair.Files.WindowId)).State
                .Should().Be("permanent_failed");
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task SignedTargetMismatchRejectsBeforeDatabaseMutation()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            fixture.RewritePlan(repair, repair.Plan with
            {
                Target = repair.Plan.Target with { Environment = "staging" },
            });
            var result = await fixture.RunAsync(repair, "dry-run");
            result.Exit.Should().Be(RepairExitCodes.TargetRejected);
            result.Error.Should().Contain("repair_target_mismatch");
            (await fixture.LoadSnapshotAsync(repair.Files.WindowId)).State
                .Should().Be("permanent_failed");
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task DryRunApplyIdempotencyAndRollback_UseExactManagedRolesAndReceipts()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            (await fixture.DirectDryRunAsync(repair)).Should().Be(1);
            var dryRun = await fixture.RunAsync(repair, "dry-run");
            dryRun.Exit.Should().Be(RepairExitCodes.Success, dryRun.Error);
            dryRun.Output.Should().Contain("database_writes=0").And.Contain("work_orders=1");
            RepairDatabase.SnapshotSha256(await fixture.LoadSnapshotAsync(repair.Files.WindowId))
                .Should().Be(repair.Plan.Operations[0].PreimageSha256);

            var apply = await fixture.RunAsync(repair, "apply");
            apply.Exit.Should().Be(RepairExitCodes.Success, apply.Error);
            apply.Output.Should().Contain("status=applied")
                .And.Contain("mutated_operations=1").And.Contain("work_orders=1")
                .And.NotContain(repair.Files.WindowId.ToString());
            (await fixture.LoadSnapshotAsync(repair.Files.WindowId)).State.Should().Be("retryable_failed");
            (await fixture.RunAsync(repair, "apply")).Output.Should().Contain("status=idempotent");

            fixture.RewritePlan(repair, repair.Plan with { ChangeTicket = "CHG-REPLAY-CONFLICT" });
            var replayConflict = await fixture.RunAsync(repair, "apply");
            replayConflict.Exit.Should().Be(RepairExitCodes.Rejected);
            replayConflict.Error.Should().Contain("receipt_plan_binding_mismatch");
            fixture.RewritePlan(repair, repair.Plan);

            var rollback = await fixture.RunAsync(repair, "rollback");
            rollback.Exit.Should().Be(RepairExitCodes.Success);
            rollback.Output.Should().Contain("status=rolled_back");
            RepairDatabase.SnapshotSha256(await fixture.LoadSnapshotAsync(repair.Files.WindowId))
                .Should().Be(repair.Plan.Operations[0].PreimageSha256);
            (await fixture.RunAsync(repair, "rollback")).Output.Should().Contain("status=idempotent");
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task ConcurrentStateChangeRejectsCasAndCreatesNoFinalReceipt()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            await fixture.ExecuteAdminAsync(
                "UPDATE public.ingestion_windows SET attempt_count=attempt_count+1 WHERE id=$1",
                repair.Files.WindowId);
            var result = await fixture.RunAsync(repair, "apply");
            result.Exit.Should().Be(RepairExitCodes.Rejected, result.Error);
            result.Error.Should().Contain("repair_preimage_rejected");
            Directory.EnumerateFileSystemEntries(repair.Files.ReceiptRoot).Should().BeEmpty();
            (await fixture.LoadSnapshotAsync(repair.Files.WindowId)).State.Should().Be("permanent_failed");
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task CommitAcknowledgementLossReconcilesExactDatabasePostimage()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            var result = await fixture.RunAsync(repair, "apply", new CommitThenThrow());
            result.Exit.Should().Be(RepairExitCodes.Success, result.Error);
            result.Output.Should().Contain("status=reconciled");
            (await fixture.LoadSnapshotAsync(repair.Files.WindowId)).State.Should().Be("retryable_failed");
            Directory.EnumerateDirectories(repair.Files.ReceiptRoot)
                .Should().ContainSingle(path => !Path.GetFileName(path).StartsWith(".pending-"));
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task LiveMigrationDriftWrongAuditIdentityAndMigratorLockFailClosed()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            var wrongAudit = await fixture.RunAsync(
                repair, "dry-run", auditLogin: $"{fixture.RolePrefix}_ingestion_login_v1");
            wrongAudit.Exit.Should().Be(RepairExitCodes.TargetRejected);
            wrongAudit.Error.Should().Contain("repair_audit_identity_mismatch");

            await fixture.ExecuteAdminAsync(
                "UPDATE public.schema_migrations SET checksum=repeat('0',64) WHERE version='001_initial'");
            try
            {
                var drift = await fixture.RunAsync(repair, "dry-run");
                drift.Exit.Should().Be(RepairExitCodes.TargetRejected);
                drift.Error.Should().Contain("repair_migration_checksum_or_state_mismatch");
            }
            finally
            {
                var expected = EmbeddedRepairMigrationTrust.Entries.Single(x => x.Version == "001_initial");
                await fixture.ExecuteAdminAsync(
                    "UPDATE public.schema_migrations SET checksum=$1 WHERE version='001_initial'",
                    expected.Sha256);
            }

            await using var lockConnection = await fixture.HoldTargetLockAsync();
            var locked = await fixture.RunAsync(repair, "dry-run");
            locked.Exit.Should().Be(RepairExitCodes.TargetRejected);
            locked.Error.Should().Contain("repair_target_lock_timeout");
            (await fixture.LoadSnapshotAsync(repair.Files.WindowId)).State.Should().Be("permanent_failed");
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    [Fact]
    public async Task RollbackRejectsLaterWindowMutationWithoutReceiptOrDatabaseWrite()
    {
        var repair = await fixture.CreateCaseAsync();
        try
        {
            var apply = await fixture.RunAsync(repair, "apply");
            apply.Exit.Should().Be(RepairExitCodes.Success, apply.Error);
            await fixture.ExecuteAdminAsync(
                "UPDATE public.ingestion_windows SET attempt_count=attempt_count+1 WHERE id=$1",
                repair.Files.WindowId);
            var before = RepairDatabase.SnapshotSha256(
                await fixture.LoadSnapshotAsync(repair.Files.WindowId));
            var rollback = await fixture.RunAsync(repair, "rollback");
            rollback.Exit.Should().Be(RepairExitCodes.Rejected);
            rollback.Error.Should().Contain("rollback_apply_postimage_changed");
            RepairDatabase.SnapshotSha256(await fixture.LoadSnapshotAsync(repair.Files.WindowId))
                .Should().Be(before);
            Directory.EnumerateDirectories(repair.Files.ReceiptRoot)
                .Should().NotContain(path => Path.GetFileName(path).EndsWith("-rollback"));
        }
        finally
        {
            await fixture.CleanupAsync(repair);
        }
    }

    private sealed class CommitThenThrow : ICommitBoundary
    {
        public async Task CommitAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken)
        {
            await transaction.CommitAsync(cancellationToken);
            throw new RepairCommitUncertainException();
        }
    }
}
