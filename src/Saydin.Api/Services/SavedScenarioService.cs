using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;
using Saydin.Api.Options;
using Saydin.Api.Repositories;

using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Services;

public sealed class SavedScenarioService(
    ISavedScenarioRepository repository,
    IOptions<PlanOptions> options,
    IStringLocalizer<ErrorMessages> localizer,
    ILogger<SavedScenarioService> logger) : ISavedScenarioService
{
    private static readonly HashSet<string> AllowedTypes = ["what_if", "comparison", "portfolio", "dca"];

    // DB kolon kapasiteleri ile uyumlu max length validasyonu.
    private const int MaxSymbolLength      = 64;
    private const int MaxDisplayNameLength = 200;
    private const int MaxLabelLength       = 200;

    public async Task<IReadOnlyList<ScenarioResponse>> GetScenariosAsync(string deviceId, CancellationToken ct)
    {
        var user = await GetOrCreateUserAsync(deviceId, ct);
        var scenarios = await repository.GetByUserIdAsync(user.Id, ct);

        logger.LogInformation(
            "Senaryo listesi alındı: {DeviceId} → {Count} senaryo",
            deviceId, scenarios.Count);

        return scenarios.Select(ToResponse).ToList();
    }

    public async Task<ScenarioResponse> SaveScenarioAsync(
        string deviceId, SaveScenarioRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await GetOrCreateUserAsync(deviceId, ct);

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

        if (!AllowedTypes.Contains(normalizedType))
            throw new ValidationException(
                string.Format(localizer["InvalidScenarioType"], request.Type, string.Join(", ", AllowedTypes)),
                field: nameof(request.Type));

        if (string.IsNullOrWhiteSpace(request.AssetSymbol))
            throw new ValidationException(
                string.Format(localizer["RequestPayloadMissing"], nameof(request.AssetSymbol)),
                field: nameof(request.AssetSymbol));

        var trimmedSymbol = request.AssetSymbol.Trim();
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

        // what_if / dca tipleri için asset FK kontrolü + sembol/display name canonicalization
        // (kullanıcı "btc" göndermiş olabilir; lookup case-insensitive ve kayıt asset'in
        // canonical değerleriyle yazılır → tutarsız casing veya XSS-yakın display name engellenir).
        Asset? asset = null;
        string canonicalSymbol      = trimmedSymbol.ToUpperInvariant();
        string? canonicalDisplayName = request.AssetDisplayName;

        if (normalizedType is "what_if" or "dca")
        {
            asset = await repository.GetActiveAssetBySymbolAsync(canonicalSymbol, ct)
                ?? throw new AssetNotFoundException(trimmedSymbol);
            canonicalSymbol      = asset.Symbol;
            canonicalDisplayName = asset.DisplayName;
        }

        var scenario = new SavedScenario
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            AssetId = asset?.Id,
            AssetSymbol = canonicalSymbol,
            AssetDisplayName = canonicalDisplayName ?? canonicalSymbol,
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
        var user = await GetOrCreateUserAsync(deviceId, ct);

        var scenario = await repository.GetByIdAndUserIdAsync(scenarioId, user.Id, ct)
            ?? throw new ScenarioNotFoundException(scenarioId);

        await repository.DeleteAsync(scenario, ct);

        logger.LogInformation(
            "Senaryo silindi: {DeviceId} → {ScenarioId}",
            deviceId, scenarioId);
    }

    private async Task<User> GetOrCreateUserAsync(string deviceId, CancellationToken ct)
    {
        var user = await repository.GetUserByDeviceIdAsync(deviceId, ct);
        if (user is not null)
        {
            await repository.UpdateUserLastSeenAsync(user, ct);
            return user;
        }

        logger.LogInformation("Yeni kullanıcı oluşturuluyor: {DeviceId}", deviceId);
        return await repository.CreateUserAsync(deviceId, ct);
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
