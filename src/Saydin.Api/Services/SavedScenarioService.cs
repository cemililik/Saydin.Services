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
    private static readonly HashSet<string> AllowedTypes = ["what_if", "comparison", "portfolio"];

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
        var user = await GetOrCreateUserAsync(deviceId, ct);

        var scenarioLimit = options.Value.GetTierOptions(user.Tier).MaxSavedScenarios;
        if (scenarioLimit > 0)
        {
            var count = await repository.CountByUserIdAsync(user.Id, ct);
            if (count >= scenarioLimit)
                throw new ScenarioLimitExceededException(scenarioLimit);
        }

        if (!AllowedTypes.Contains(request.Type))
            throw new ArgumentException(
                string.Format(localizer["InvalidScenarioType"], request.Type, string.Join(", ", AllowedTypes)));

        // what_if tipinde asset FK kontrolü yap; diğer tipler için atla
        Asset? asset = null;
        if (request.Type is "what_if")
        {
            asset = await repository.GetActiveAssetBySymbolAsync(request.AssetSymbol, ct)
                ?? throw new AssetNotFoundException(request.AssetSymbol);
        }

        var scenario = new SavedScenario
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            AssetId = asset?.Id,
            AssetSymbol = request.AssetSymbol,
            AssetDisplayName = request.AssetDisplayName,
            Type = request.Type,
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
            deviceId, request.Type, request.AssetSymbol, request.BuyDate);

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
