using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;
using Saydin.Api.Options;
using Saydin.Api.Repositories;

using Saydin.Shared.Constants;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Services;

public sealed class SavedScenarioService(
    ISavedScenarioRepository repository,
    ILastSeenThrottle lastSeenThrottle,
    IOptions<PlanOptions> options,
    IStringLocalizer<ErrorMessages> localizer,
    ILogger<SavedScenarioService> logger) : ISavedScenarioService
{
    // DB kolon kapasiteleri ile uyumlu max length validasyonu.
    private const int MaxSymbolLength      = 64;
    private const int MaxDisplayNameLength = 200;
    private const int MaxLabelLength       = 200;

    public async Task<IReadOnlyList<ScenarioResponse>> GetScenariosAsync(string deviceId, CancellationToken ct)
    {
        // F2.2-13 ([C-B-SavedScenario-4]): GET path'inde user yaratma side-effect'i yok.
        // Cihaz hiç POST atmadan listele çağırdıysa boş liste döner ve users tablosu
        // gereksiz kayıt biriktirmez.
        var user = await repository.GetUserByDeviceIdAsync(deviceId, ct);
        if (user is null)
        {
            logger.LogInformation("Senaryo listesi: {DeviceId} için kullanıcı kaydı yok — boş döndü", deviceId);
            return Array.Empty<ScenarioResponse>();
        }

        await TryTouchLastSeenAsync(user, ct);
        var scenarios = await repository.GetByUserIdAsync(user.Id, ct);

        logger.LogInformation(
            "Senaryo listesi alındı: {DeviceId} → {Count} senaryo",
            deviceId, scenarios.Count);

        return scenarios.Select(ToResponse).ToList();
    }

    public async Task<ScenarioResponse> SaveScenarioAsync(
        string deviceId, SaveScenarioRequest request, CancellationToken ct)
    {
        // P1R-003: domain ValidationException ile guard — altyapı kaynaklı ArgumentException
        // (Redis/EF Core) artık ValidationExceptionHandler tarafından 400'e dönmez.
        if (request is null)
            throw new ValidationException(
                string.Format(localizer["RequestPayloadMissing"], "request"), field: "request");

        // F2.3-8: Save path'inde user kesinlikle yaratılır — atomik upsert + select.
        var user = await repository.GetOrCreateUserAsync(deviceId, ct);
        await TryTouchLastSeenAsync(user, ct);

        var scenarioLimit = options.Value.GetTierOptions(user.Tier).MaxSavedScenarios;
        if (scenarioLimit > 0)
        {
            var count = await repository.CountByUserIdAsync(user.Id, ct);
            if (count >= scenarioLimit)
                throw new ScenarioLimitExceededException(scenarioLimit);
        }

        if (string.IsNullOrWhiteSpace(request.Type))
            throw new ValidationException(
                string.Format(localizer["RequestPayloadMissing"], nameof(request.Type)),
                field: nameof(request.Type));

        // Trim before normalize: " what_if " ve " btc " gibi inputlar lookup'ta gereksiz fail etmemeli.
        var normalizedType = request.Type.Trim().ToLowerInvariant();

        // F2.5-2 sync: kabul edilen tipler tek noktadan (ScenarioTypes.All) gelir.
        if (!ScenarioTypes.All.Contains(normalizedType))
            throw new ValidationException(
                string.Format(localizer["InvalidScenarioType"], request.Type, string.Join(", ", ScenarioTypes.All)),
                field: nameof(request.Type));

        // F2.2-11 ([C-B-SavedScenario-2]): asset sembolü what_if/dca için zorunlu,
        // comparison/portfolio için opsiyonel (portföy "PORTFOLIO" gibi sentinel
        // sembol veya boş gönderebilir). Tip-bağımlı validation aşağıda yapılır.
        var requiresAssetSymbol = normalizedType is ScenarioTypes.WhatIf or ScenarioTypes.Dca;
        if (requiresAssetSymbol && string.IsNullOrWhiteSpace(request.AssetSymbol))
            throw new ValidationException(
                string.Format(localizer["RequestPayloadMissing"], nameof(request.AssetSymbol)),
                field: nameof(request.AssetSymbol));

        // Boş sembol gelirse (comparison/portfolio için) sentinel kullan.
        var trimmedSymbol = string.IsNullOrWhiteSpace(request.AssetSymbol)
            ? "PORTFOLIO"
            : request.AssetSymbol.Trim();
        if (trimmedSymbol.Length > MaxSymbolLength)
            throw new ValidationException(
                string.Format(localizer["FieldTooLong"], nameof(request.AssetSymbol), MaxSymbolLength),
                field: nameof(request.AssetSymbol));
        if (!string.IsNullOrEmpty(request.AssetDisplayName)
            && request.AssetDisplayName.Length > MaxDisplayNameLength)
            throw new ValidationException(
                string.Format(localizer["FieldTooLong"], nameof(request.AssetDisplayName), MaxDisplayNameLength),
                field: nameof(request.AssetDisplayName));
        if (!string.IsNullOrEmpty(request.Label) && request.Label.Length > MaxLabelLength)
            throw new ValidationException(
                string.Format(localizer["FieldTooLong"], nameof(request.Label), MaxLabelLength),
                field: nameof(request.Label));

        // F2.3-6 ([C-C-29]): client-tarafı AssetDisplayName artık güven kaynağı değil —
        // server-side resolve. what_if/dca için Asset tablosundan kanonik isim okunur;
        // comparison/portfolio için label varsa label, yoksa sembolün kendisi kullanılır.
        Asset? asset = null;
        string canonicalSymbol       = trimmedSymbol.ToUpperInvariant();
        string  canonicalDisplayName = canonicalSymbol;

        if (requiresAssetSymbol)
        {
            asset = await repository.GetActiveAssetBySymbolAsync(canonicalSymbol, ct)
                ?? throw new AssetNotFoundException(trimmedSymbol);
            canonicalSymbol      = asset.Symbol;
            canonicalDisplayName = asset.DisplayName;
        }

        var scenario = new SavedScenario
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            AssetId = asset?.Id,
            AssetSymbol = canonicalSymbol,
            AssetDisplayName = canonicalDisplayName,
            Type = normalizedType,
            ExtraData = request.ExtraData,
            BuyDate = request.BuyDate,
            SellDate = request.SellDate,
            Quantity = request.Amount,
            QuantityUnit = request.AmountType,
            Label = request.Label,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await repository.CreateAsync(scenario, ct);

        logger.LogInformation(
            "Senaryo kaydedildi: {DeviceId} → {Type} {AssetSymbol} {BuyDate}",
            deviceId, normalizedType, canonicalSymbol, request.BuyDate);

        return ToResponse(scenario);
    }

    public async Task DeleteScenarioAsync(string deviceId, Guid scenarioId, CancellationToken ct)
    {
        // F2.2-13: DELETE path'inde user yaratma side-effect'i yok. Kullanıcı kaydı
        // hiç yoksa senaryo da yok → 404. (ScenarioNotFound semantik olarak doğru.)
        var user = await repository.GetUserByDeviceIdAsync(deviceId, ct)
            ?? throw new ScenarioNotFoundException(scenarioId);
        await TryTouchLastSeenAsync(user, ct);

        var scenario = await repository.GetByIdAndUserIdAsync(scenarioId, user.Id, ct)
            ?? throw new ScenarioNotFoundException(scenarioId);

        await repository.DeleteAsync(scenario, ct);

        logger.LogInformation(
            "Senaryo silindi: {DeviceId} → {ScenarioId}",
            deviceId, scenarioId);
    }

    /// <summary>
    /// F2.2-12: last_seen_at UPDATE'lerini throttling yapar. Hata kullanıcı yoluna sızmaz.
    /// </summary>
    private async Task TryTouchLastSeenAsync(User user, CancellationToken ct)
    {
        if (!lastSeenThrottle.ShouldUpdate(user.Id))
            return;

        try
        {
            await repository.UpdateUserLastSeenAsync(user, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // last_seen telemetri bilgisidir — DB hatası kullanıcıyı engelelmemeli.
            logger.LogWarning(ex, "users.last_seen_at güncellemesi başarısız: {UserId}", user.Id);
        }
    }

    private static ScenarioResponse ToResponse(SavedScenario s) => new(
        s.Id,
        s.AssetSymbol,
        s.AssetDisplayName,
        s.BuyDate,
        s.SellDate,
        s.Quantity,
        s.QuantityUnit,
        s.Label,
        s.CreatedAt,
        s.Type,
        s.ExtraData
    );
}
