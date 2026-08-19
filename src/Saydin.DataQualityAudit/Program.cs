namespace Saydin.DataQualityAudit;

using Saydin.DatabaseSecurity;

internal static class Program
{
    public static Task<int> Main(string[] args) =>
        AuditApplication.RunAsync(args, Console.Out, Console.Error, TimeProvider.System);
}

internal static class AuditApplication
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default,
        Func<string, string?>? environment = null,
        Func<OciKmsSignerConfiguration, IOciKmsSigningClient>? kmsClientFactory = null)
    {
        try
        {
            var options = AuditOptions.Parse(args);
            return options switch
            {
                ScanOptions scan => await RunScanAsync(
                    scan, output, timeProvider, environment ?? Environment.GetEnvironmentVariable,
                    cancellationToken, kmsClientFactory),
                VerifyEvidenceOptions verify => await RunVerifyAsync(verify, output, cancellationToken),
                _ => AuditExitCodes.InvalidArguments,
            };
        }
        catch (AuditRejectedException exception)
        {
            await error.WriteLineAsync($"audit rejected: code={exception.Code}");
            return exception.ExitCode;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("audit failed: code=cancelled");
            return AuditExitCodes.RuntimeFailure;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await error.WriteLineAsync($"audit failed: code={SafeFailureCode(exception)}");
            return AuditExitCodes.RuntimeFailure;
        }
    }

    private static async Task<int> RunScanAsync(
        ScanOptions options,
        TextWriter output,
        TimeProvider timeProvider,
        Func<string, string?> environment,
        CancellationToken cancellationToken,
        Func<OciKmsSignerConfiguration, IOciKmsSigningClient>? kmsClientFactory)
    {
        var input = SignedAuditInput.LoadAndVerify(options, timeProvider);
        await using var signer = EvidenceSignerFactory.Create(
            options, input, environment, kmsClientFactory);
        var hmacKey = AuditCryptography.ReadHmacKey(options.HmacKeyFile);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(input.Manifest.Budget.TotalTimeoutSeconds));

        var embedded = EmbeddedMigrations.Load();
        var runtimeDatabase = RuntimeDatabaseOptions.FromEnvironment(
            LoginPurpose.Audit, RuntimeDatabasePooling.Disabled, environment);
        var backupValidUntilText = environment("SAYDIN_BACKUP_V1_VALID_UNTIL");
        if (backupValidUntilText is null ||
            !RoleContract.TryParseBackupValidUntil(
                backupValidUntilText, out var backupV1ValidUntilUtc))
            throw new AuditRejectedException(
                "backup_valid_until_invalid", AuditExitCodes.InvalidArguments);
        await using var dataSource = await RuntimeDatabase.OpenVerifiedDataSourceAsync(
            runtimeDatabase, cancellationToken: timeout.Token);
        var runner = new AuditRunner(
            dataSource, input, embedded, hmacKey,
            runtimeDatabase.Contract, backupV1ValidUntilUtc);
        var content = await runner.RunAsync(timeout.Token);
        var bundle = await EvidenceBundle.WriteAsync(
            options.OutputDirectory,
            content,
            input.Manifest.EvidenceKeyId,
            signer,
            input.Manifest.Budget.MaxEvidenceBytes,
            timeProvider.GetUtcNow(),
            timeout.Token);
        await output.WriteLineAsync(
            $"audit complete: content_sha256={bundle.ContentBundleSha256}; violations={content.Checks.Count(check => check.Severity >= AuditSeverity.High && check.TotalCount > 0)}");
        return content.Checks.Any(check => check.Severity >= AuditSeverity.High && check.TotalCount > 0)
            ? AuditExitCodes.Violations
            : AuditExitCodes.Clean;
    }

    private static async Task<int> RunVerifyAsync(
        VerifyEvidenceOptions options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var result = await EvidenceBundle.VerifyAsync(
            options.BundleDirectory,
            options.PublicKeyFile,
            cancellationToken);
        if (!result)
            throw new AuditRejectedException("evidence_verification_failed", AuditExitCodes.EvidenceFailure);
        await output.WriteLineAsync("evidence verified");
        return AuditExitCodes.Clean;
    }

    private static string SafeFailureCode(Exception exception) => exception switch
    {
        DatabaseSecurityRejectedException rejected => rejected.Code,
        Npgsql.PostgresException postgres => $"postgres_{postgres.SqlState}",
        Npgsql.NpgsqlException => "database_transport",
        IOException => "io_failure",
        UnauthorizedAccessException => "access_denied",
        _ => exception.GetType().Name,
    };

}
