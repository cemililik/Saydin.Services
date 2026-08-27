using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseRoleBootstrap;

internal static class Program
{
    public static Task<int> Main(string[] args) =>
        BootstrapApplication.RunAsync(args, Console.Out, Console.Error);
}

internal static class BootstrapApplication
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = BootstrapOptions.Parse(args);
            using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            totalTimeout.CancelAfter(options.Timeouts.Total);

            var runner = new RoleBootstrapRunner(options, output);
            await runner.RunAsync(totalTimeout.Token);
            return BootstrapExitCodes.Success;
        }
        catch (BootstrapRejectedException exception)
        {
            await error.WriteLineAsync($"role-bootstrap failed: code={exception.Code}");
            return exception.ExitCode;
        }
        catch (DatabaseSecurityRejectedException exception)
        {
            await error.WriteLineAsync($"role-bootstrap failed: code={exception.Code}");
            return exception.Kind switch
            {
                DatabaseSecurityFailureKind.InvalidArguments => BootstrapExitCodes.InvalidArguments,
                DatabaseSecurityFailureKind.SecretRejected => BootstrapExitCodes.SecretRejected,
                DatabaseSecurityFailureKind.TargetRejected => BootstrapExitCodes.TargetRejected,
                _ => BootstrapExitCodes.DatabaseFailure,
            };
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("role-bootstrap failed: code=operation_timeout");
            return BootstrapExitCodes.Timeout;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Do not emit exception messages: Npgsql/IO errors may contain a target,
            // username, path or server-provided text. The stable code is sufficient.
            await error.WriteLineAsync("role-bootstrap failed: code=unexpected_failure");
            return BootstrapExitCodes.DatabaseFailure;
        }
    }
}
