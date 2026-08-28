namespace Saydin.Api.Tests;

/// <summary>
/// SaydinMetrics instruments are process-global. A MeterListener observes writes
/// from every concurrently running test, so listener-owning classes must neither
/// overlap each other nor ordinary tests which exercise metric-producing code.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MetricsTestCollection
{
    public const string Name = "process-global-saydin-metrics";
}
