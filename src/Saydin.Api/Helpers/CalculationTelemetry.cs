using System.Diagnostics;

using Saydin.Shared.Diagnostics;

namespace Saydin.Api.Helpers;

internal static class CalculationTelemetry
{
    internal static async Task<T> ObserveWhatIfAsync<T>(
        string operation,
        Func<Task<T>> action)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "success";
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        catch
        {
            outcome = "error";
            throw;
        }
        finally
        {
            var tags = new TagList
            {
                { "operation", operation },
                { "outcome", outcome },
            };
            SaydinMetrics.WhatIfCalculations.Add(1, tags);
            SaydinMetrics.CalculationDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        }
    }
}
