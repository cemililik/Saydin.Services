using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Saydin.Api.BackgroundServices;
using Saydin.Api.Helpers;
using Saydin.Api.IntegrationTests.Fixtures;
using Saydin.Shared.Constants;
using Saydin.Shared.Diagnostics;
using Saydin.Shared.Entities;

namespace Saydin.Api.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class ActivityLogWriterIntegrationTests(DatabaseFixture db)
{
    [SkippableFact]
    public async Task ManagedApi_BatchStoreDuplicateRetry_IsIdempotent()
    {
        Skip.IfNot(db.Available, db.SkipReason);
        var entry = Entry(ActivityActions.ConfigFetch);
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddScoped(_ => db.CreateContext())
            .BuildServiceProvider();
        var store = new EfActivityLogBatchStore(
            services.GetRequiredService<IServiceScopeFactory>());

        await store.SaveAsync([entry], CancellationToken.None);
        await store.SaveAsync([entry], CancellationToken.None);

        await using var verify = db.CreateAdminContext();
        (await verify.ActivityLogs.CountAsync(log =>
            log.Id == entry.Id && log.CreatedAt == entry.CreatedAt)).Should().Be(1);
    }

    [SkippableFact]
    public async Task ManagedApi_ToxicConstraintRowIsBisected_ValidRowsPersistAndMetricIsExact()
    {
        Skip.IfNot(db.Available, db.SkipReason);
        var goodFirst = Entry(ActivityActions.WhatIfCalculate);
        var toxic = Entry("not_a_database_action");
        var goodLast = Entry(ActivityActions.AssetsList);
        long toxicMeasurements = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name == "saydin.activity_log.write.failures.total")
                    meterListener.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "outcome" && Equals(tag.Value, "toxic_row"))
                    toxicMeasurements += value;
        });
        listener.Start();

        await using var services = new ServiceCollection()
            .AddLogging()
            .AddScoped(_ => db.CreateContext())
            .BuildServiceProvider();
        var store = new EfActivityLogBatchStore(
            services.GetRequiredService<IServiceScopeFactory>());
        var channel = Channel.CreateUnbounded<ActivityLog>();
        var writer = new ActivityLogWriter(
            channel, store, NullLogger<ActivityLogWriter>.Instance);

        try
        {
            await writer.StartAsync(CancellationToken.None);
            await channel.Writer.WriteAsync(goodFirst);
            await channel.Writer.WriteAsync(toxic);
            await channel.Writer.WriteAsync(goodLast);
            channel.Writer.TryComplete();
            writer.ExecuteTask.Should().NotBeNull();
            await writer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15));

            await using var verify = db.CreateAdminContext();
            var persisted = await verify.ActivityLogs
                .Where(log => log.Id == goodFirst.Id
                              || log.Id == toxic.Id
                              || log.Id == goodLast.Id)
                .Select(log => log.Id)
                .ToListAsync();
            persisted.Should().BeEquivalentTo(new[] { goodFirst.Id, goodLast.Id });
            toxicMeasurements.Should().Be(1);
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
            await using var cleanup = db.CreateAdminContext();
            await cleanup.ActivityLogs
                .Where(log => log.Id == goodFirst.Id
                              || log.Id == toxic.Id
                              || log.Id == goodLast.Id)
                .ExecuteDeleteAsync();
            writer.Dispose();
        }
    }

    [SkippableFact]
    public async Task RealPostgreSqlConnectionTermination_IsRetried_AndWriterPersistsBatch()
    {
        Skip.IfNot(db.Available, db.SkipReason);
        var entry = Entry(ActivityActions.AssetsList);
        var store = new TerminateConnectionOnceStore(db);
        var channel = Channel.CreateUnbounded<ActivityLog>();
        var writer = new ActivityLogWriter(
            channel, store, NullLogger<ActivityLogWriter>.Instance);

        try
        {
            await writer.StartAsync(CancellationToken.None);
            await channel.Writer.WriteAsync(entry);
            channel.Writer.TryComplete();
            await writer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15));

            store.Calls.Should().Be(2);
            await using var verify = db.CreateAdminContext();
            (await verify.ActivityLogs.AnyAsync(log => log.Id == entry.Id)).Should().BeTrue();
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
            await using var cleanup = db.CreateAdminContext();
            await cleanup.ActivityLogs.Where(log => log.Id == entry.Id).ExecuteDeleteAsync();
            writer.Dispose();
        }
    }

    [SkippableFact]
    public async Task JsonbUpperBound_CoversRealPostgreSqlBinarySize_AtTextSizeBlindSpot()
    {
        Skip.IfNot(db.Available, db.SkipReason);
        var payload = Enumerable.Range(0, 700)
            .ToDictionary(index => $"k{index}", _ => 0);
        var json = JsonSerializer.Serialize(payload);
        Encoding.UTF8.GetByteCount(json).Should().BeLessThan(10_000);
        using var document = JsonDocument.Parse(json);

        await using var context = db.CreateContext();
        var postgresSize = await context.Database.SqlQuery<int>(
            $"""
            SELECT pg_catalog.pg_column_size({json}::jsonb) AS "Value"
            """)
            .SingleAsync();
        var upperBound = JsonbStorageSize.UpperBound(document.RootElement);

        const string exponentJson = "1e100000";
        using var exponentDocument = JsonDocument.Parse(exponentJson);
        var exponentPostgresSize = await context.Database.SqlQuery<int>(
            $"""
            SELECT pg_catalog.pg_column_size({exponentJson}::jsonb) AS "Value"
            """)
            .SingleAsync();
        var exponentUpperBound = JsonbStorageSize.UpperBound(
            exponentDocument.RootElement);

        postgresSize.Should().BeGreaterThan(10_000);
        upperBound.Should().BeGreaterThanOrEqualTo(postgresSize);
        exponentUpperBound.Should().BeGreaterThanOrEqualTo(exponentPostgresSize);
        exponentUpperBound.Should().BeGreaterThan(10_000);
    }

    private static ActivityLog Entry(string action) => new()
    {
        DeviceId = "api-writer-integration",
        Action = action,
        StatusCode = 200,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class TerminateConnectionOnceStore(DatabaseFixture database)
        : IActivityLogBatchStore
    {
        private int _first = 1;
        public int Calls { get; private set; }

        public async Task SaveAsync(IReadOnlyList<ActivityLog> entries, CancellationToken ct)
        {
            Calls++;
            await using var context = database.CreateContext();
            if (Interlocked.Exchange(ref _first, 0) == 1)
            {
                await context.Database.ExecuteSqlRawAsync(
                    "SELECT pg_catalog.pg_terminate_backend(pg_catalog.pg_backend_pid())", ct);
                throw new InvalidOperationException("PostgreSQL did not terminate the test connection.");
            }

            await context.ActivityLogs.AddRangeAsync(entries, ct);
            await context.SaveChangesAsync(ct);
        }
    }
}
