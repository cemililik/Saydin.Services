using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
        await db.ActivityLogs.AddRangeAsync(entries, ct);
        await db.SaveChangesAsync(ct);
    }
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
            if (postgres.SqlState.StartsWith("23", StringComparison.Ordinal))
                return ActivityLogWriteFailureKind.ToxicRow;
            if (postgres.SqlState is PostgresErrorCodes.SerializationFailure
                    or PostgresErrorCodes.DeadlockDetected
                || postgres.SqlState.StartsWith("08", StringComparison.Ordinal))
                return ActivityLogWriteFailureKind.TransientBatch;
            return ActivityLogWriteFailureKind.FatalHost;
        }

        if (Find<NpgsqlException>(exception) is not null
            || Find<SocketException>(exception) is not null
            || Find<IOException>(exception) is not null
            || Find<TimeoutException>(exception) is not null)
            return ActivityLogWriteFailureKind.TransientBatch;

        return ActivityLogWriteFailureKind.FatalHost;
    }

    private static T? Find<T>(Exception exception) where T : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is T match) return match;
        return null;
    }
}
