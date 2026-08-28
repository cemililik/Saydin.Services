using System.Net;
using Npgsql;
using StackExchange.Redis;

namespace Saydin.Api.IntegrationTests.Fixtures;

/// <summary>
/// CI'daki gerçek-altyapı entegrasyon testlerini yanlış bir veritabanına karşı
/// çalıştırmayı engelleyen fail-fast sınırı. Yerel koşular varsayılan olarak
/// optional'dır; <c>SAYDIN_INTEGRATION_REQUIRED=true</c> yalnız required CI job'ında
/// etkinleştirilir.
/// </summary>
internal static class IntegrationTestEnvironment
{
    internal const string RequiredVariable = "SAYDIN_INTEGRATION_REQUIRED";
    internal const string RunIdVariable = "SAYDIN_INTEGRATION_RUN_ID";

    internal static bool IsRequired =>
        string.Equals(
            Environment.GetEnvironmentVariable(RequiredVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);

    internal static void ValidateRequiredDatabase(string? connectionString)
    {
        if (!IsRequired)
            return;

        ValidateDatabase(connectionString, Environment.GetEnvironmentVariable(RunIdVariable));
    }

    internal static void ValidateRequiredDatabase(string host, string database)
    {
        if (!IsRequired) return;
        var normalizedRunId = ParseRunId(Environment.GetEnvironmentVariable(RunIdVariable));
        if (!string.Equals(host, "postgres", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(database, $"saydin_test_{normalizedRunId}", StringComparison.Ordinal) ||
            HasProtectedEnvironmentMarker(host) || HasProtectedEnvironmentMarker(database))
            throw new InvalidOperationException("Required integration managed PostgreSQL hedefi güvenli değil.");
    }

    internal static void ValidateRequiredRedis(string? connectionString)
    {
        if (!IsRequired)
            return;

        ValidateRedis(connectionString, Environment.GetEnvironmentVariable(RunIdVariable));
    }

    internal static void ValidateRedis(string? connectionString, string? runId)
    {
        _ = ParseRunId(runId);

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "Required integration modunda ConnectionStrings__Redis zorunludur.");

        ConfigurationOptions options;
        try
        {
            options = ConfigurationOptions.Parse(connectionString);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Required integration Redis connection string'i parse edilemedi.", ex);
        }

        if (options.EndPoints.Count != 1)
            throw new InvalidOperationException(
                "Required integration Redis connection string'i tam olarak bir endpoint içermelidir.");

        var endpoints = string.Join(',', options.EndPoints.Select(endpoint => endpoint.ToString()));
        if (HasProtectedEnvironmentMarker(endpoints))
            throw new InvalidOperationException(
                "Required integration Redis endpoint'i production/staging işareti içeremez.");

        if (options.EndPoints[0] is not DnsEndPoint endpoint
            || !string.Equals(endpoint.Host, "redis", StringComparison.OrdinalIgnoreCase)
            || endpoint.Port != 6379)
            throw new InvalidOperationException(
                "Required integration Redis endpoint'i yalnız disposable Compose redis:6379 olabilir.");
    }

    internal static void EnsureRequiredRedisConnected(bool required, bool available)
    {
        if (required && !available)
            throw new InvalidOperationException(
                "Required integration Redis bağlantısı kurulamadı; testler skip edilemez.");
    }

    /// <summary>
    /// Saf doğrulama yüzeyi: herhangi bir bağlantı açmadan önce çağrılır ve testlerle
    /// doğrudan sınanabilir.
    /// </summary>
    internal static void ValidateDatabase(string? connectionString, string? runId)
    {
        var normalizedRunId = ParseRunId(runId);

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "Required integration modunda PostgreSQL hedefi zorunludur.");

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Required integration PostgreSQL connection string'i parse edilemedi.", ex);
        }

        if (HasProtectedEnvironmentMarker(builder.Host) || HasProtectedEnvironmentMarker(builder.Database))
            throw new InvalidOperationException(
                "Required integration PostgreSQL hedefi production/staging işareti içeremez.");

        if (!string.Equals(builder.Host, "postgres", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Required integration PostgreSQL host'u yalnız disposable Compose postgres servisi olabilir.");

        var expectedDatabase = $"saydin_test_{normalizedRunId}";
        if (!string.Equals(builder.Database, expectedDatabase, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Required integration DB adı bu run ile eşleşmiyor. Beklenen: {expectedDatabase}.");
    }

    private static string ParseRunId(string? runId)
    {
        if (!Guid.TryParseExact(runId, "N", out var parsed) || parsed == Guid.Empty)
            throw new InvalidOperationException(
                $"{RunIdVariable} boş olmayan UUID 'N' formatında (32 hex karakter) olmalıdır.");

        return parsed.ToString("N");
    }

    private static bool HasProtectedEnvironmentMarker(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value.Contains("production", StringComparison.OrdinalIgnoreCase)
            || value.Contains("prod", StringComparison.OrdinalIgnoreCase)
            || value.Contains("staging", StringComparison.OrdinalIgnoreCase)
            || value.Contains("stage", StringComparison.OrdinalIgnoreCase));
}
