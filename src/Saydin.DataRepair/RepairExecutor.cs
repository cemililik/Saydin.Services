using Npgsql;

namespace Saydin.DataRepair;

internal interface ICommitBoundary
{
    Task CommitAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken);
}

internal sealed class DefaultCommitBoundary : ICommitBoundary
{
    public Task CommitAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        transaction.CommitAsync(cancellationToken);
}

internal sealed class RepairCommitUncertainException : Exception;

internal sealed record RepairExecutionResult(
    string Status,
    string ReceiptSha256,
    int MutatedOperations,
    int WorkOrders);

internal sealed class RepairExecutor(
    RepairDatabase database,
    ReceiptStore receipts,
    IReceiptSigner signer,
    ICommitBoundary commitBoundary)
{
    public async Task<RepairExecutionResult> ApplyAsync(
        VerifiedRepairPlan plan,
        CancellationToken cancellationToken)
    {
        if (receipts.FinalExists(plan.NonceSha256, "rollback"))
            throw Rejected("repair_plan_already_rolled_back");
        var existing = await RecoverOrReadExistingAsync(
            plan, "apply", priorReceiptSha256: null, cancellationToken);
        if (existing is not null) return Result("idempotent", existing);

        var prepared = await database.PrepareApplyAsync(plan, cancellationToken);
        await using var connection = prepared.Connection;
        await using var transaction = prepared.Transaction;
        var receipt = BuildReceipt(plan, "apply", null, prepared.Prepared);
        var staged = await receipts.StageAsync(receipt, signer, cancellationToken);
        return await CommitPublishOrReconcileAsync(
            plan, staged, transaction, "apply", cancellationToken);
    }

    public async Task<RepairExecutionResult> RollbackAsync(
        VerifiedRepairPlan plan,
        CancellationToken cancellationToken)
    {
        if (!receipts.FinalExists(plan.NonceSha256, "apply"))
            throw Rejected("rollback_apply_receipt_missing");
        var apply = await receipts.ReadFinalAsync(
            plan.NonceSha256, "apply", signer.Identity.PublicSubjectPublicKeyInfo,
            cancellationToken);
        ValidateBinding(plan, apply, "apply", null);
        if (receipts.FinalExists(plan.NonceSha256, "rollback"))
        {
            var final = await receipts.ReadFinalAsync(
                plan.NonceSha256, "rollback", signer.Identity.PublicSubjectPublicKeyInfo,
                cancellationToken);
            ValidateBinding(plan, final, "rollback", apply.ReceiptSha256);
            if (!await database.MatchesReceiptStateAsync(
                    plan, final, postState: true, cancellationToken))
                throw Rejected("receipt_database_postimage_mismatch");
            return Result("idempotent", final);
        }
        if (!await database.MatchesReceiptStateAsync(
                plan, apply, postState: true, cancellationToken))
            throw Rejected("rollback_apply_postimage_changed");

        // A pending rollback can only be interpreted after the apply receipt is known.
        if (receipts.PendingExists(plan.NonceSha256, "rollback"))
        {
            var recovered = await RecoverPendingAsync(
                plan, "rollback", apply.ReceiptSha256, cancellationToken);
            if (recovered is not null) return Result("reconciled", recovered);
        }

        var prepared = await database.PrepareRollbackAsync(plan, apply, cancellationToken);
        await using var connection = prepared.Connection;
        await using var transaction = prepared.Transaction;
        var receipt = BuildReceipt(
            plan, "rollback", apply.ReceiptSha256, prepared.Prepared);
        var staged = await receipts.StageAsync(receipt, signer, cancellationToken);
        return await CommitPublishOrReconcileAsync(
            plan, staged, transaction, "rollback", cancellationToken);
    }

    private async Task<RepairExecutionResult> CommitPublishOrReconcileAsync(
        VerifiedRepairPlan plan,
        VerifiedRepairReceipt staged,
        NpgsqlTransaction transaction,
        string mode,
        CancellationToken cancellationToken)
    {
        try
        {
            await commitBoundary.CommitAsync(transaction, cancellationToken);
        }
        catch (PostgresException)
        {
            receipts.DeletePending(plan.NonceSha256, mode);
            throw;
        }
        catch (Exception exception) when (exception is NpgsqlException or RepairCommitUncertainException)
        {
            var reconciled = await ReconcileAsync(plan, staged, mode, cancellationToken);
            if (reconciled is not null) return Result("reconciled", reconciled);
            throw Rejected("repair_commit_not_applied");
        }
        catch
        {
            receipts.DeletePending(plan.NonceSha256, mode);
            throw;
        }

        try
        {
            receipts.Promote(plan.NonceSha256, mode);
            var final = await receipts.ReadFinalAsync(
                plan.NonceSha256, mode, signer.Identity.PublicSubjectPublicKeyInfo,
                cancellationToken);
            return Result(mode == "apply" ? "applied" : "rolled_back", final);
        }
        catch
        {
            // The committed DB state and signed pending receipt deliberately remain
            // available for a later exact postimage reconciliation.
            throw Rejected("receipt_publish_after_commit_failed");
        }
    }

    private async Task<VerifiedRepairReceipt?> RecoverOrReadExistingAsync(
        VerifiedRepairPlan plan,
        string mode,
        string? priorReceiptSha256,
        CancellationToken cancellationToken,
        bool allowMissing = false)
    {
        if (receipts.FinalExists(plan.NonceSha256, mode))
        {
            var final = await receipts.ReadFinalAsync(
                plan.NonceSha256, mode, signer.Identity.PublicSubjectPublicKeyInfo,
                cancellationToken);
            ValidateBinding(plan, final, mode, priorReceiptSha256);
            if (!await database.MatchesReceiptStateAsync(
                    plan, final, postState: true, cancellationToken))
                throw Rejected("receipt_database_postimage_mismatch");
            return final;
        }
        if (receipts.PendingExists(plan.NonceSha256, mode))
        {
            var recovered = await RecoverPendingAsync(
                plan, mode, priorReceiptSha256, cancellationToken);
            if (recovered is not null) return recovered;
        }
        if (!allowMissing && mode == "rollback")
            throw Rejected("rollback_receipt_missing");
        return null;
    }

    private async Task<VerifiedRepairReceipt?> RecoverPendingAsync(
        VerifiedRepairPlan plan,
        string mode,
        string? priorReceiptSha256,
        CancellationToken cancellationToken)
    {
        var pending = await receipts.ReadPendingAsync(
            plan.NonceSha256, mode, signer.Identity.PublicSubjectPublicKeyInfo,
            cancellationToken);
        ValidateBinding(plan, pending, mode, priorReceiptSha256);
        if (await database.MatchesReceiptStateAsync(
                plan, pending, postState: true, cancellationToken))
        {
            receipts.Promote(plan.NonceSha256, mode);
            return await receipts.ReadFinalAsync(
                plan.NonceSha256, mode, signer.Identity.PublicSubjectPublicKeyInfo,
                cancellationToken);
        }
        if (await database.MatchesReceiptStateAsync(
                plan, pending, postState: false, cancellationToken))
        {
            receipts.DeletePending(plan.NonceSha256, mode);
            return null;
        }
        throw Rejected("repair_commit_state_uncertain");
    }

    private async Task<VerifiedRepairReceipt?> ReconcileAsync(
        VerifiedRepairPlan plan,
        VerifiedRepairReceipt staged,
        string mode,
        CancellationToken cancellationToken)
    {
        if (await database.MatchesReceiptStateAsync(
                plan, staged, postState: true, cancellationToken))
        {
            receipts.Promote(plan.NonceSha256, mode);
            return await receipts.ReadFinalAsync(
                plan.NonceSha256, mode, signer.Identity.PublicSubjectPublicKeyInfo,
                cancellationToken);
        }
        if (await database.MatchesReceiptStateAsync(
                plan, staged, postState: false, cancellationToken))
        {
            receipts.DeletePending(plan.NonceSha256, mode);
            return null;
        }
        throw Rejected("repair_commit_state_uncertain");
    }

    private RepairReceipt BuildReceipt(
        VerifiedRepairPlan plan,
        string mode,
        string? priorReceiptSha256,
        PreparedRepair prepared) =>
        new(1,
            "ECDSA-SHA256-RFC3279-DER",
            signer.Identity.Provider,
            signer.Identity.KeyIdentity,
            signer.Identity.KeyId,
            mode,
            plan.PlanSha256,
            plan.TargetSha256,
            plan.NonceSha256,
            plan.Plan.MigrationTrust.ManifestSha256,
            plan.Plan.Evidence.ContentSha256,
            plan.Plan.Evidence.SignerKeyId,
            priorReceiptSha256,
            prepared.TransactionId,
            prepared.CreatedAtUtc,
            prepared.Operations);

    private static void ValidateBinding(
        VerifiedRepairPlan plan,
        VerifiedRepairReceipt receipt,
        string mode,
        string? priorReceiptSha256)
    {
        var value = receipt.Receipt;
        if (value.Mode != mode ||
            !RepairCryptography.FixedEquals(value.PlanSha256, plan.PlanSha256) ||
            !RepairCryptography.FixedEquals(value.TargetSha256, plan.TargetSha256) ||
            !RepairCryptography.FixedEquals(value.NonceSha256, plan.NonceSha256) ||
            !RepairCryptography.FixedEquals(value.KeyId, plan.Plan.ReceiptKeyId) ||
            !RepairCryptography.FixedEquals(
                value.MigrationManifestSha256, plan.Plan.MigrationTrust.ManifestSha256) ||
            !RepairCryptography.FixedEquals(
                value.EvidenceContentSha256, plan.Plan.Evidence.ContentSha256) ||
            !RepairCryptography.FixedEquals(
                value.EvidenceSignerKeyId, plan.Plan.Evidence.SignerKeyId) ||
            value.Operations.Count != plan.Plan.Operations.Count ||
            (priorReceiptSha256 is null
                ? value.PriorReceiptSha256 is not null
                : value.PriorReceiptSha256 is null ||
                  !RepairCryptography.FixedEquals(value.PriorReceiptSha256, priorReceiptSha256)))
            throw Rejected("receipt_plan_binding_mismatch");
    }

    private static RepairExecutionResult Result(
        string status,
        VerifiedRepairReceipt receipt) =>
        new(status,
            receipt.ReceiptSha256,
            receipt.Receipt.Operations.Count(item => item.Result is "requeued" or "rolled_back"),
            receipt.Receipt.Operations.Count(item => item.Result.StartsWith(
                "work_order_", StringComparison.Ordinal)));

    private static RepairRejectedException Rejected(string code) =>
        new(code, RepairExitCodes.Rejected);
}
