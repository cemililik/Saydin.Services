using Saydin.Shared.Entities;

namespace Saydin.Api.Services;

public interface IDailyLimitGuard
{
    /// <summary>
    /// Günlük hesaplama limiti kontrolü yapar. Limit aşıldıysa DailyLimitExceededException fırlatır.
    /// Redis erişilemezse sessizce geçer (fail-open).
    /// </summary>
    Task CheckAsync(User? user, string deviceId, string usageKeyPrefix);

    /// <summary>
    /// Atomik olarak sayacı artırır. Limit aşıldıysa DailyLimitExceededException fırlatır.
    /// Redis erişilemezse sessizce geçer (fail-open).
    /// </summary>
    Task IncrementAsync(User? user, string deviceId, string usageKeyPrefix);
}
