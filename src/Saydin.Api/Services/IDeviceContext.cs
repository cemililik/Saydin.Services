namespace Saydin.Api.Services;

/// <summary>
/// F2.2-3 ([C-B-IWhatIfCalculator-1], [C-B-IDcaCalculator-1], [C-B-ISaveScenarioService-1],
/// [C-B-CC-5]): İstek-kapsamlı (scoped) cihaz kimliği soyutlaması.
///
/// Önceden <c>IWhatIfCalculator</c>, <c>IDcaCalculator</c>, <c>ISavedScenarioService</c>
/// ve <c>IAppConfigService</c>'in her metodu bir <c>string deviceId</c> parametresi
/// taşıyordu — bu, HTTP/altyapı endişesinin (header'dan okunan device id) iş mantığı
/// arayüzlerine sızmasıydı. Bunun yerine doğrulanmış device id, <c>RequireDeviceId</c>
/// endpoint filter'ı tarafından bu scoped context'e yazılır; service'ler constructor
/// injection ile okur. Böylece iş arayüzleri yalnızca domain parametrelerini taşır.
/// </summary>
public interface IDeviceContext
{
    /// <summary>
    /// Doğrulanmış cihaz kimliği. <c>RequireDeviceId</c> filter'ı set etmemişse
    /// (filter atlanmış/yanlış sırada) <see cref="InvalidOperationException"/> fırlatır —
    /// bu, sessiz bir null/empty device id ile devam etmekten daha güvenlidir.
    /// </summary>
    string DeviceId { get; }

    /// <summary>Device id set edildi mi (filter çalıştı mı)? Defensive kontrol için.</summary>
    bool IsResolved { get; }
}

/// <summary>
/// <see cref="IDeviceContext"/>'in scoped implementasyonu. <c>RequireDeviceId</c> filter'ı
/// istek başına bir kez <see cref="SetDeviceId"/> ile doldurur. DI kaydı scoped'tır —
/// her HTTP isteği kendi örneğini alır, cihazlar arası sızıntı olmaz.
/// </summary>
public sealed class DeviceContext : IDeviceContext
{
    private string? _deviceId;

    public bool IsResolved => _deviceId is not null;

    public string DeviceId => _deviceId
        ?? throw new InvalidOperationException(
            "DeviceContext doldurulmadı — RequireDeviceId filter'ı atlanmış olabilir.");

    /// <summary>RequireDeviceId filter'ı tarafından (doğrulama sonrası) bir kez set edilir.</summary>
    public void SetDeviceId(string deviceId) => _deviceId = deviceId;
}
