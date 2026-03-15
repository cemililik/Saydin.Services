using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Saydin.Api.Models.Requests;
using Saydin.Api.Repositories;
using Saydin.Api.Services;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Tests.Services;

public class SavedScenarioServiceTests
{
    private readonly ISavedScenarioRepository _repository = Substitute.For<ISavedScenarioRepository>();
    private readonly SavedScenarioService _sut;

    private const string DeviceId = "test-device-001";

    private static readonly Asset BtcAsset = new()
    {
        Id          = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
        Symbol      = "BTC",
        DisplayName = "Bitcoin",
        Category    = AssetCategory.Crypto,
        Source      = "coingecko",
        IsActive    = true
    };

    private static readonly User FreeUser = new()
    {
        Id       = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"),
        DeviceId = DeviceId,
        Tier     = "free",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static readonly User PremiumUser = new()
    {
        Id       = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
        DeviceId = "premium-device",
        Tier     = "premium",
        CreatedAt = DateTimeOffset.UtcNow
    };

    public SavedScenarioServiceTests()
    {
        _sut = new SavedScenarioService(_repository, NullLogger<SavedScenarioService>.Instance);
    }

    // ── GetScenariosAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetScenariosAsync_ExistingUserWithScenarios_ReturnsMappedList()
    {
        var scenarioId = Guid.NewGuid();
        var scenarios = new List<SavedScenario>
        {
            new()
            {
                Id           = scenarioId,
                UserId       = FreeUser.Id,
                AssetId      = BtcAsset.Id,
                Asset        = BtcAsset,
                BuyDate      = new DateOnly(2020, 1, 1),
                SellDate     = new DateOnly(2021, 1, 1),
                Quantity     = 10000m,
                QuantityUnit = "try",
                Label        = "Test senaryosu",
                CreatedAt    = DateTimeOffset.UtcNow
            }
        };

        _repository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetByUserIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(scenarios.AsReadOnly());

        var result = await _sut.GetScenariosAsync(DeviceId, CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be(scenarioId);
        result[0].AssetSymbol.Should().Be("BTC");
        result[0].AssetDisplayName.Should().Be("Bitcoin");
        result[0].Amount.Should().Be(10000m);
        result[0].AmountType.Should().Be("try");
        result[0].Label.Should().Be("Test senaryosu");
    }

    [Fact]
    public async Task GetScenariosAsync_NewDevice_CreatesUserAndReturnsEmptyList()
    {
        _repository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>()).Returns((User?)null);
        _repository.CreateUserAsync(DeviceId, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetByUserIdAsync(FreeUser.Id, Arg.Any<CancellationToken>())
                   .Returns(new List<SavedScenario>().AsReadOnly());

        var result = await _sut.GetScenariosAsync(DeviceId, CancellationToken.None);

        result.Should().BeEmpty();
        await _repository.Received(1).CreateUserAsync(DeviceId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetScenariosAsync_ExistingUser_UpdatesLastSeen()
    {
        _repository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetByUserIdAsync(FreeUser.Id, Arg.Any<CancellationToken>())
                   .Returns(new List<SavedScenario>().AsReadOnly());

        await _sut.GetScenariosAsync(DeviceId, CancellationToken.None);

        await _repository.Received(1).UpdateUserLastSeenAsync(FreeUser, Arg.Any<CancellationToken>());
    }

    // ── SaveScenarioAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task SaveScenarioAsync_ValidRequest_ReturnsCreatedScenario()
    {
        var request = new SaveScenarioRequest("BTC", new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1),
            10000m, "try", "Bitcoin yatırımım");

        _repository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.CountByUserIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(0);
        _repository.GetActiveAssetBySymbolAsync("BTC", Arg.Any<CancellationToken>()).Returns(BtcAsset);
        _repository.CreateAsync(Arg.Any<SavedScenario>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo => callInfo.Arg<SavedScenario>());

        var result = await _sut.SaveScenarioAsync(DeviceId, request, CancellationToken.None);

        result.AssetSymbol.Should().Be("BTC");
        result.AssetDisplayName.Should().Be("Bitcoin");
        result.BuyDate.Should().Be(new DateOnly(2020, 1, 1));
        result.SellDate.Should().Be(new DateOnly(2021, 1, 1));
        result.Amount.Should().Be(10000m);
        result.AmountType.Should().Be("try");
        result.Label.Should().Be("Bitcoin yatırımım");
    }

    [Fact]
    public async Task SaveScenarioAsync_WithNullSellDate_SavesSuccessfully()
    {
        var request = new SaveScenarioRequest("BTC", new DateOnly(2020, 1, 1), null, 10000m, "try", null);

        _repository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.CountByUserIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(0);
        _repository.GetActiveAssetBySymbolAsync("BTC", Arg.Any<CancellationToken>()).Returns(BtcAsset);
        _repository.CreateAsync(Arg.Any<SavedScenario>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo => callInfo.Arg<SavedScenario>());

        var result = await _sut.SaveScenarioAsync(DeviceId, request, CancellationToken.None);

        result.SellDate.Should().BeNull();
        result.Label.Should().BeNull();
    }

    [Fact]
    public async Task SaveScenarioAsync_AssetNotFound_ThrowsAssetNotFoundException()
    {
        var request = new SaveScenarioRequest("YOKASSET", new DateOnly(2020, 1, 1), null, 100m, "try", null);

        _repository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.CountByUserIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(0);
        _repository.GetActiveAssetBySymbolAsync("YOKASSET", Arg.Any<CancellationToken>())
                   .Returns((Asset?)null);

        var act = () => _sut.SaveScenarioAsync(DeviceId, request, CancellationToken.None);

        await act.Should().ThrowAsync<AssetNotFoundException>()
                 .Where(ex => ex.Symbol == "YOKASSET");
    }

    [Fact]
    public async Task SaveScenarioAsync_FreeUserAtLimit_ThrowsScenarioLimitExceededException()
    {
        var request = new SaveScenarioRequest("BTC", new DateOnly(2020, 1, 1), null, 100m, "try", null);

        _repository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.CountByUserIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(5);

        var act = () => _sut.SaveScenarioAsync(DeviceId, request, CancellationToken.None);

        await act.Should().ThrowAsync<ScenarioLimitExceededException>()
                 .Where(ex => ex.Limit == 5);
    }

    [Fact]
    public async Task SaveScenarioAsync_FreeUserUnderLimit_SavesSuccessfully()
    {
        var request = new SaveScenarioRequest("BTC", new DateOnly(2020, 1, 1), null, 100m, "try", null);

        _repository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.CountByUserIdAsync(FreeUser.Id, Arg.Any<CancellationToken>()).Returns(4);
        _repository.GetActiveAssetBySymbolAsync("BTC", Arg.Any<CancellationToken>()).Returns(BtcAsset);
        _repository.CreateAsync(Arg.Any<SavedScenario>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo => callInfo.Arg<SavedScenario>());

        var act = () => _sut.SaveScenarioAsync(DeviceId, request, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SaveScenarioAsync_PremiumUser_DoesNotCheckLimit()
    {
        var request = new SaveScenarioRequest("BTC", new DateOnly(2020, 1, 1), null, 100m, "try", null);

        _repository.GetUserByDeviceIdAsync("premium-device", Arg.Any<CancellationToken>()).Returns(PremiumUser);
        _repository.GetActiveAssetBySymbolAsync("BTC", Arg.Any<CancellationToken>()).Returns(BtcAsset);
        _repository.CreateAsync(Arg.Any<SavedScenario>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo => callInfo.Arg<SavedScenario>());

        await _sut.SaveScenarioAsync("premium-device", request, CancellationToken.None);

        await _repository.DidNotReceive()
                         .CountByUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveScenarioAsync_NewDevice_CreatesUserBeforeSaving()
    {
        var request = new SaveScenarioRequest("BTC", new DateOnly(2020, 1, 1), null, 100m, "try", null);
        var newUser = new User { Id = Guid.NewGuid(), DeviceId = "new-device", Tier = "free" };

        _repository.GetUserByDeviceIdAsync("new-device", Arg.Any<CancellationToken>()).Returns((User?)null);
        _repository.CreateUserAsync("new-device", Arg.Any<CancellationToken>()).Returns(newUser);
        _repository.CountByUserIdAsync(newUser.Id, Arg.Any<CancellationToken>()).Returns(0);
        _repository.GetActiveAssetBySymbolAsync("BTC", Arg.Any<CancellationToken>()).Returns(BtcAsset);
        _repository.CreateAsync(Arg.Any<SavedScenario>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo => callInfo.Arg<SavedScenario>());

        await _sut.SaveScenarioAsync("new-device", request, CancellationToken.None);

        await _repository.Received(1).CreateUserAsync("new-device", Arg.Any<CancellationToken>());
    }

    // ── DeleteScenarioAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task DeleteScenarioAsync_OwnScenario_DeletesSuccessfully()
    {
        var scenarioId = Guid.NewGuid();
        var scenario = new SavedScenario
        {
            Id = scenarioId, UserId = FreeUser.Id, AssetId = BtcAsset.Id, Asset = BtcAsset,
            BuyDate = new DateOnly(2020, 1, 1), Quantity = 100m, QuantityUnit = "try",
            CreatedAt = DateTimeOffset.UtcNow
        };

        _repository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetByIdAndUserIdAsync(scenarioId, FreeUser.Id, Arg.Any<CancellationToken>())
                   .Returns(scenario);

        await _sut.DeleteScenarioAsync(DeviceId, scenarioId, CancellationToken.None);

        await _repository.Received(1).DeleteAsync(scenario, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteScenarioAsync_ScenarioNotFound_ThrowsScenarioNotFoundException()
    {
        var scenarioId = Guid.NewGuid();

        _repository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetByIdAndUserIdAsync(scenarioId, FreeUser.Id, Arg.Any<CancellationToken>())
                   .Returns((SavedScenario?)null);

        var act = () => _sut.DeleteScenarioAsync(DeviceId, scenarioId, CancellationToken.None);

        await act.Should().ThrowAsync<ScenarioNotFoundException>()
                 .Where(ex => ex.ScenarioId == scenarioId);
    }

    [Fact]
    public async Task DeleteScenarioAsync_ScenarioBelongsToOtherUser_ThrowsScenarioNotFoundException()
    {
        var scenarioId = Guid.NewGuid();

        // Başka kullanıcının senaryosu → repository null döner (ownership filtresi)
        _repository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>()).Returns(FreeUser);
        _repository.GetByIdAndUserIdAsync(scenarioId, FreeUser.Id, Arg.Any<CancellationToken>())
                   .Returns((SavedScenario?)null);

        var act = () => _sut.DeleteScenarioAsync(DeviceId, scenarioId, CancellationToken.None);

        await act.Should().ThrowAsync<ScenarioNotFoundException>();
        await _repository.DidNotReceive().DeleteAsync(Arg.Any<SavedScenario>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteScenarioAsync_NewDevice_CreatesUserAndThrowsNotFound()
    {
        var scenarioId = Guid.NewGuid();
        var newUser = new User { Id = Guid.NewGuid(), DeviceId = "ghost-device", Tier = "free" };

        _repository.GetUserByDeviceIdAsync("ghost-device", Arg.Any<CancellationToken>()).Returns((User?)null);
        _repository.CreateUserAsync("ghost-device", Arg.Any<CancellationToken>()).Returns(newUser);
        _repository.GetByIdAndUserIdAsync(scenarioId, newUser.Id, Arg.Any<CancellationToken>())
                   .Returns((SavedScenario?)null);

        var act = () => _sut.DeleteScenarioAsync("ghost-device", scenarioId, CancellationToken.None);

        await act.Should().ThrowAsync<ScenarioNotFoundException>();
    }
}
