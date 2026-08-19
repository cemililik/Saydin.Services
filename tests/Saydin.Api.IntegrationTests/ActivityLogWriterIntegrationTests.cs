using System.Diagnostics.Metrics;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Saydin.Api.BackgroundServices;
using Saydin.Api.IntegrationTests.Fixtures;
using Saydin.Shared.Constants;
using Saydin.Shared.Diagnostics;
using Saydin.Shared.Entities;

namespace Saydin.Api.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class ActivityLogWriterIntegrationTests(DatabaseFixture db)
{
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

    private static ActivityLog Entry(string action) => new()
    {
        DeviceId = "api-writer-integration",
        Action = action,
        StatusCode = 200,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
