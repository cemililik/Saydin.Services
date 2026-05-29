using FluentAssertions;
using NSubstitute;
using Saydin.Api.Options;
using Saydin.Api.Repositories;
using Saydin.Api.Services;
using Saydin.Shared.Entities;

namespace Saydin.Api.Tests.Services;

/// <summary>
/// TSTR-010: PlanLimitResolver.ResolveDailyAssetQueryLimitAsync için unit test. Asset-query
/// günlük kotasını (DailyLimitGuard limitOverride kaynağı) tier başına çözer; null-user → Free.
/// </summary>
public class PlanLimitResolverTests
{
    private const string DeviceId = "test-device-001";

    private readonly ISavedScenarioRepository _repository = Substitute.For<ISavedScenarioRepository>();
    private readonly PlanLimitResolver _sut;

    private static readonly PlanOptions Plans = new()
    {
        Free    = new TierOptions { DailyAssetQueryLimit = 500 },
        Premium = new TierOptions { DailyAssetQueryLimit = 5000 },
    };

    public PlanLimitResolverTests()
    {
        _sut = new PlanLimitResolver(_repository, Microsoft.Extensions.Options.Options.Create(Plans));
    }

    [Fact]
    public async Task ResolveDailyAssetQueryLimitAsync_FreeUser_ReturnsFreeLimit()
    {
        _repository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>())
                   .Returns(new User { Id = Guid.NewGuid(), DeviceId = DeviceId, Tier = "free" });

        var limit = await _sut.ResolveDailyAssetQueryLimitAsync(DeviceId, CancellationToken.None);

        limit.Should().Be(500);
    }

    [Fact]
    public async Task ResolveDailyAssetQueryLimitAsync_PremiumUser_ReturnsPremiumLimit()
    {
        _repository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>())
                   .Returns(new User { Id = Guid.NewGuid(), DeviceId = DeviceId, Tier = "premium" });

        var limit = await _sut.ResolveDailyAssetQueryLimitAsync(DeviceId, CancellationToken.None);

        limit.Should().Be(5000);
    }

    [Fact]
    public async Task ResolveDailyAssetQueryLimitAsync_UnknownDevice_ReturnsFreeLimit()
    {
        _repository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var limit = await _sut.ResolveDailyAssetQueryLimitAsync(DeviceId, CancellationToken.None);

        limit.Should().Be(500);
    }
}
