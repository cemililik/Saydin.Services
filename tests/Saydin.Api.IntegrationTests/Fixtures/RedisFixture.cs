using StackExchange.Redis;

namespace Saydin.Api.IntegrationTests.Fixtures;

/// <summary>
/// F1.9-7 / F2.6-21: docker-compose ağındaki gerçek Redis'e bağlanır
/// (<c>ConnectionStrings__Redis</c> env). Yerel optional modda erişilemezse testler
/// SkippableFact ile atlanır; required CI modunda aynı durum failure'dır.
/// </summary>
public sealed class RedisFixture : IDisposable
{
    public IConnectionMultiplexer? Multiplexer { get; }
    public bool Available { get; }
    public string SkipReason { get; } = string.Empty;

    public RedisFixture()
    {
        var required = IntegrationTestEnvironment.IsRequired;
        var connStr = Environment.GetEnvironmentVariable("ConnectionStrings__Redis");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            if (required)
                throw new InvalidOperationException(
                    "Required integration modunda ConnectionStrings__Redis env zorunludur.");

            SkipReason = "ConnectionStrings__Redis env yok (entegrasyon Redis'i erişilemez).";
            return;
        }

        IntegrationTestEnvironment.ValidateRequiredRedis(connStr);

        try
        {
            var opts = ConfigurationOptions.Parse(connStr);
            opts.AbortOnConnectFail = false;
            opts.ConnectTimeout = 3000;
            Multiplexer = ConnectionMultiplexer.Connect(opts);
            Available = Multiplexer.IsConnected;
            if (!Available)
            {
                if (required)
                    Multiplexer.Dispose();
                IntegrationTestEnvironment.EnsureRequiredRedisConnected(required, Available);
                SkipReason = "Redis bağlantısı kurulamadı (IsConnected=false).";
            }
        }
        catch (Exception ex)
        {
            if (required)
                throw new InvalidOperationException(
                    "Required integration Redis hazırlığı başarısız oldu; testler skip edilemez.", ex);

            SkipReason = $"Redis erişilemez: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public void Dispose() => Multiplexer?.Dispose();
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "redis";
}
