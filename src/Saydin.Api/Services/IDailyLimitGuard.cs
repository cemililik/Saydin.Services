using Saydin.Shared.Entities;

namespace Saydin.Api.Services;

public interface IDailyLimitGuard
{
    /// <summary>
    /// Günlük limit kontrolü yapar. Limit aşıldıysa DailyLimitExceededException fırlatır.
    /// Redis erişilemezse sessizce geçer (fail-open) — calculation engellenmez.
    /// TOCTOU race riski içerir; tercih edilen: <see cref="TryAcquireAsync"/>.
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

    /// <summary>
    /// Atomik "reserve" — Lua script ile aynı anda INCR + limit check. Limit aşılırsa
    /// kotayı geri çevirir (DECR) ve <see cref="Saydin.Shared.Exceptions.DailyLimitExceededException"/>
    /// fırlatır. TOCTOU race'i kapatır; pahalı hesap işlemi öncesi tek round-trip ile çağrılır.
    /// Hesap başarısız olursa caller <see cref="ReleaseAsync"/> ile kotayı iade edebilir.
    /// </summary>
    Task TryAcquireAsync(
        User? user,
        string deviceId,
        string usageKeyPrefix,
        int? limitOverride = null,
        CancellationToken ct = default);

    /// <summary>
    /// Daha önce <see cref="TryAcquireAsync"/> ile alınmış kotayı geri verir
    /// (DECR — atomik, negatife düşmez). Yalnızca hesaplama başarısız olduğunda
    /// "başarısız hesap kotadan düşmesin" garantisi için çağrılır.
    /// </summary>
    Task ReleaseAsync(
        User? user,
        string deviceId,
        string usageKeyPrefix,
        int? limitOverride = null,
        CancellationToken ct = default);
}
