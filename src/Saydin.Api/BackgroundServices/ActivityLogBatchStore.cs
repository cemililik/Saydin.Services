using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;

namespace Saydin.Api.BackgroundServices;

public interface IActivityLogBatchStore
{
    Task SaveAsync(IReadOnlyList<ActivityLog> entries, CancellationToken ct);
}

internal sealed class EfActivityLogBatchStore(IServiceScopeFactory scopeFactory)
    : IActivityLogBatchStore
{
    public async Task SaveAsync(IReadOnlyList<ActivityLog> entries, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SaydinDbContext>();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var batch = new NpgsqlBatch(connection, transaction);
            foreach (var entry in entries)
            {
                var command = new NpgsqlBatchCommand("""
                    INSERT INTO public.activity_logs(
                        id,user_id,device_id,action,ip_address,country,city,
                        device_os,os_version,app_version,data,status_code,
                        duration_ms,error_code,created_at)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15)
                    ON CONFLICT (id,created_at) DO NOTHING
                    """);
                command.Parameters.Add(Parameter(NpgsqlDbType.Uuid, entry.Id));
                command.Parameters.Add(Parameter(NpgsqlDbType.Uuid, entry.UserId));
                command.Parameters.Add(Parameter(NpgsqlDbType.Varchar, entry.DeviceId));
                command.Parameters.Add(Parameter(NpgsqlDbType.Varchar, entry.Action));
                command.Parameters.Add(Parameter(NpgsqlDbType.Inet, entry.IpAddress));
                command.Parameters.Add(Parameter(NpgsqlDbType.Char, entry.Country));
                command.Parameters.Add(Parameter(NpgsqlDbType.Varchar, entry.City));
                command.Parameters.Add(Parameter(NpgsqlDbType.Varchar, entry.DeviceOs));
                command.Parameters.Add(Parameter(NpgsqlDbType.Varchar, entry.OsVersion));
                command.Parameters.Add(Parameter(NpgsqlDbType.Varchar, entry.AppVersion));
                command.Parameters.Add(Parameter(
                    NpgsqlDbType.Jsonb, entry.Data?.GetRawText()));
                command.Parameters.Add(Parameter(NpgsqlDbType.Smallint, entry.StatusCode));
                command.Parameters.Add(Parameter(NpgsqlDbType.Bigint, entry.DurationMs));
                command.Parameters.Add(Parameter(NpgsqlDbType.Varchar, entry.ErrorCode));
                command.Parameters.Add(Parameter(NpgsqlDbType.TimestampTz, entry.CreatedAt));
                batch.BatchCommands.Add(command);
            }
            await batch.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static NpgsqlParameter Parameter(NpgsqlDbType type, object? value) =>
        new()
        {
            NpgsqlDbType = type,
            Value = value ?? DBNull.Value,
        };
}

internal enum ActivityLogWriteFailureKind
{
    Cancelled,
    ToxicRow,
    TransientBatch,
    FatalHost,
}

internal static class ActivityLogWriteFailureClassifier
{
    public static ActivityLogWriteFailureKind Classify(Exception exception)
    {
        if (exception is OperationCanceledException)
            return ActivityLogWriteFailureKind.Cancelled;

        var postgres = Find<PostgresException>(exception);
        if (postgres is not null)
        {
            // A single malformed/out-of-contract row must not poison its healthy
            // siblings. Class 22 (data exception) and 23 (integrity violation) are
            // isolated by the writer's bisection path.
            if (postgres.SqlState.StartsWith("22", StringComparison.Ordinal)
                || postgres.SqlState.StartsWith("23", StringComparison.Ordinal))
                return ActivityLogWriteFailureKind.ToxicRow;

            // These states describe a batch-level condition which can recover
            // without changing the row: connection/failover, resource pressure,
            // operator restart, lock contention and an aborted transaction.
            if (postgres.SqlState is PostgresErrorCodes.SerializationFailure
                    or PostgresErrorCodes.DeadlockDetected
                    or "55P03"
                    or "25P02"
                || postgres.SqlState.StartsWith("08", StringComparison.Ordinal)
                || postgres.SqlState.StartsWith("53", StringComparison.Ordinal)
                || postgres.SqlState.StartsWith("57", StringComparison.Ordinal))
                return ActivityLogWriteFailureKind.TransientBatch;

            // Schema/catalog, privilege and authentication drift are deployment
            // contract violations. Keeping the host alive would silently discard
            // the audit trail forever, so these classes deliberately fail fast.
            if (postgres.SqlState.StartsWith("42", StringComparison.Ordinal)
                || postgres.SqlState is "3D000" or "3F000"
                || postgres.SqlState.StartsWith("28", StringComparison.Ordinal))
                return ActivityLogWriteFailureKind.FatalHost;

            // Unknown server-side conditions are not proof of an immutable
            // deployment-contract violation. Retry them within the writer's
            // bounded policy and drop the batch if the condition persists.
            return ActivityLogWriteFailureKind.TransientBatch;
        }

        var npgsql = Find<NpgsqlException>(exception);
        if (npgsql?.IsTransient == true
            || Find<SocketException>(exception) is not null
            || Find<IOException>(exception) is not null
            || Find<TimeoutException>(exception) is not null)
            return ActivityLogWriteFailureKind.TransientBatch;

        return ActivityLogWriteFailureKind.TransientBatch;
    }

    private static T? Find<T>(Exception exception) where T : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is T match) return match;
        return null;
    }
}
