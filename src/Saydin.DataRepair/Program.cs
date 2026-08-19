using Npgsql;
using Saydin.DatabaseSecurity;

namespace Saydin.DataRepair;

internal static class Program
{
    public static Task<int> Main(string[] args) =>
        RepairApplication.RunAsync(
            args, Console.Out, Console.Error, TimeProvider.System,
            Environment.GetEnvironmentVariable);
}

internal static class RepairApplication
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        TimeProvider timeProvider,
        Func<string, string?> environment,
        CancellationToken cancellationToken = default,
        Func<OciKmsReceiptSignerConfiguration, IKmsSigningClient>? kmsFactory = null,
        ICommitBoundary? commitBoundary = null)
    {
        try
        {
            var options = RepairOptions.Parse(args);
            var plan = SignedRepairPlan.LoadAndVerify(
                options.PlanFile, options.PlanSignatureFile,
                options.PlanPublicKeyFile, timeProvider);
            await DqaEvidenceVerifier.VerifyAsync(
                options.EvidenceBundleDirectory, options.EvidencePublicKeyFile,
                plan.Plan.Evidence, plan.Plan.Target.Environment, cancellationToken);
            var runtime = RuntimeDatabaseOptions.FromEnvironment(
                LoginPurpose.Ingestion, RuntimeDatabasePooling.Disabled, environment);
            ValidateTarget(plan.Plan.Target, runtime, environment);
            await using var dataSource = await RuntimeDatabase.OpenVerifiedDataSourceAsync(
                runtime, cancellationToken: cancellationToken);
            var audit = BuildAuditRuntime(options, runtime);
            await using var trustLease = await RepairTrustLease.AcquireAsync(
                dataSource, runtime.Contract, cancellationToken);
            await using var auditDataSource = await RuntimeDatabase.OpenVerifiedDataSourceAsync(
                audit, cancellationToken: cancellationToken);
            await trustLease.VerifyLiveTrustAsync(
                auditDataSource, plan, runtime.Contract, cancellationToken);
            var database = new RepairDatabase(dataSource);
            await database.VerifyPreflightAsync(cancellationToken);

            if (options.Mode == RepairMode.DryRun)
            {
                var ready = await database.DryRunAsync(plan, cancellationToken);
                var workOrders = plan.Plan.Operations.Count(operation =>
                    operation.Type is "refetch" or "manual_review");
                await output.WriteLineAsync(
                    $"repair dry-run complete: requeue_ready={ready}; work_orders={workOrders}; database_writes=0");
                return RepairExitCodes.Success;
            }

            RepairFiles.ValidateApprovalToken(
                options.ApprovalTokenFile!, plan.Plan.ApprovalTokenSha256);
            await using var signer = ReceiptSignerFactory.Create(
                options.ReceiptSigner!, plan.Plan.Target, plan.Plan.ReceiptKeyId,
                environment, kmsFactory);
            var store = new ReceiptStore(options.ReceiptRoot!);
            var executor = new RepairExecutor(
                database, store, signer, commitBoundary ?? new DefaultCommitBoundary());
            var result = options.Mode == RepairMode.Apply
                ? await executor.ApplyAsync(plan, cancellationToken)
                : await executor.RollbackAsync(plan, cancellationToken);
            await output.WriteLineAsync(
                $"repair complete: status={result.Status}; receipt_sha256={result.ReceiptSha256}; " +
                $"mutated_operations={result.MutatedOperations}; work_orders={result.WorkOrders}");
            return RepairExitCodes.Success;
        }
        catch (RepairRejectedException exception)
        {
            await error.WriteLineAsync($"repair rejected: code={exception.Code}");
            return exception.ExitCode;
        }
        catch (DatabaseSecurityRejectedException exception)
        {
            await error.WriteLineAsync($"repair rejected: code={exception.Code}");
            return RepairExitCodes.TargetRejected;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("repair failed: code=cancelled");
            return RepairExitCodes.DatabaseFailure;
        }
        catch (PostgresException exception)
        {
            await error.WriteLineAsync($"repair failed: code=postgres_{exception.SqlState}");
            return RepairExitCodes.DatabaseFailure;
        }
        catch (NpgsqlException)
        {
            await error.WriteLineAsync("repair failed: code=database_transport");
            return RepairExitCodes.DatabaseFailure;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await error.WriteLineAsync($"repair failed: code={SafeCode(exception)}");
            return RepairExitCodes.DatabaseFailure;
        }
    }

    private static void ValidateTarget(
        RepairTarget target,
        RuntimeDatabaseOptions runtime,
        Func<string, string?> environment)
    {
        var actualEnvironment = environment("SAYDIN_ENVIRONMENT");
        if (actualEnvironment != target.Environment || runtime.Purpose != LoginPurpose.Ingestion ||
            runtime.Database != target.Database ||
            runtime.Contract.Database != target.Database ||
            runtime.Contract.DeploymentId != target.DeploymentId ||
            runtime.Contract.Prefix != target.RolePrefix ||
            !RepairCryptography.FixedEquals(
                runtime.Contract.SystemIdentifierSha256, target.SystemIdentifierSha256) ||
            runtime.Login.Name != runtime.Contract.Login(LoginPurpose.Ingestion, 1).Name)
            throw new RepairRejectedException(
                "repair_target_mismatch", RepairExitCodes.TargetRejected);
    }

    private static RuntimeDatabaseOptions BuildAuditRuntime(
        RepairCommandOptions options,
        RuntimeDatabaseOptions ingestion)
    {
        var expected = ingestion.Contract.Login(LoginPurpose.Audit, 1);
        if (options.AuditLogin != expected.Name ||
            !Path.IsPathFullyQualified(options.AuditPasswordFile))
            throw new RepairRejectedException(
                "repair_audit_identity_mismatch", RepairExitCodes.TargetRejected);
        return new RuntimeDatabaseOptions(
            LoginPurpose.Audit,
            ingestion.Contract,
            expected,
            ingestion.Host,
            ingestion.Port,
            ingestion.Database,
            ingestion.SslMode,
            options.AuditPasswordFile,
            RuntimeDatabasePooling.Disabled);
    }

    private static string SafeCode(Exception exception) => exception switch
    {
        IOException => "io_failure",
        UnauthorizedAccessException => "access_denied",
        System.Text.Json.JsonException => "json_invalid",
        _ => exception.GetType().Name,
    };
}
