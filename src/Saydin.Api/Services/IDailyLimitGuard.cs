using Saydin.Shared.Entities;

namespace Saydin.Api.Services;

public interface IDailyLimitGuard
{
    /// <summary>
    /// Günlük limit kontrolü yapar. Limit aşıldıysa DailyLimitExceededException fırlatır.
    /// Redis erişilemezse sessizce geçer (fail-open) — calculation engellenmez.
    /// </summary>
    /// <param name="user">Kayıtlı kullanıcı (anonim → null).</param>
    /// <param name="deviceId">Anonim user için fallback key.</param>
    /// <param name="usageKeyPrefix">Redis key prefix'i (ör. "usage:whatif:", "usage:assets:").</param>
    /// <param name="limitOverride">
    /// Null ise tier'ın <c>DailyCalculationLimit</c>'i kullanılır.
    /// Belirtilirse o değer geçerlidir (örn. asset query'leri için farklı limit).
    /// </param>
    /// <param name="ct">HTTP cancellation propagation.</param>
    Task CheckAsync(
        User? user,
        string deviceId,
        string usageKeyPrefix,
        int? limitOverride = null,
        CancellationToken ct = default);

    /// <summary>
    /// Atomik olarak günlük sayacı artırır. Limit aşıldıysa DailyLimitExceededException fırlatır.
    /// Redis erişilemezse sessizce geçer (fail-open).
    /// </summary>
    /// <inheritdoc cref="CheckAsync"/>
    Task IncrementAsync(
        User? user,
        string deviceId,
        string usageKeyPrefix,
        int? limitOverride = null,
        CancellationToken ct = default);
}
