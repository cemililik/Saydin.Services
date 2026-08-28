using Npgsql;

namespace Saydin.Api.Services;

/// <summary>
/// Narrow taxonomy for optional chart/CPI degradation. Authorization, SQL contract,
/// EF state and programmer failures deliberately do not match and must propagate.
/// </summary>
internal static class OptionalDataFailure
{
    internal static bool IsExpected(Exception exception) => exception switch
    {
        TimeoutException => true,
        HttpRequestException => true,
        NpgsqlException { IsTransient: true } => true,
        _ => false,
    };
}
