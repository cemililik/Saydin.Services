using System.Collections;
using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseMigrator;

internal static class Program
{
    public static Task<int> Main(string[] args)
    {
        var environment = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .ToDictionary(
                entry => (string)entry.Key,
                entry => entry.Value?.ToString(),
                StringComparer.Ordinal);
        return MigratorApplication.RunAsync(args, environment, Console.Out, Console.Error);
    }
}

internal static class MigratorApplication
{
    public static async Task<int> RunAsync(
        string[] args,
        IReadOnlyDictionary<string, string?> environment,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = MigratorOptions.Parse(args, environment);
            using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            totalTimeout.CancelAfter(options.Timeouts.Total);
            var result = await new MigrationRunner(options, output).RunAsync(totalTimeout.Token);
            await output.WriteLineAsync(
                $"migration complete: applied={result.Applied}; already_applied={result.AlreadyApplied}; " +
                $"skipped_optional={result.SkippedOptional}; " +
                $"backup_postbootstrap_required={result.BackupPostBootstrapRequired.ToString().ToLowerInvariant()}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("migration cancelled");
            return 130;
        }
        catch (MigratorRejectedException ex)
        {
            var fingerprint = ex.Code == "schema_fingerprint_mismatch" && ex.Detail is not null
                ? $"; fingerprint={ex.Detail}"
                : string.Empty;
            await error.WriteLineAsync($"migration rejected: code={ex.Code}{fingerprint}");
            return 3;
        }
        catch (DatabaseSecurityRejectedException ex)
        {
            await error.WriteLineAsync($"migration rejected: code={ex.Code}");
            return 3;
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"migration failed: code={ex.GetType().Name}");
            return 4;
        }
    }
}
