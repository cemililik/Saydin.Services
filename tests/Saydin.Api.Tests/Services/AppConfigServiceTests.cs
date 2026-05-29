using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Saydin.Api.Options;
using Saydin.Api.Repositories;
using Saydin.Api.Services;
using Saydin.Shared.Entities;

namespace Saydin.Api.Tests.Services;

/// <summary>
/// TSTR-010: AppConfigService.GetConfigAsync için unit test (CLAUDE.md "her public method
/// için unit test zorunlu"). Tier → feature-flag çözümü ve null-user "free" fallback'i doğrular.
/// </summary>
public class AppConfigServiceTests
{
    private const string DeviceId = "test-device-001";

    private readonly ISavedScenarioRepository _repository = Substitute.For<ISavedScenarioRepository>();
    private readonly IDeviceContext _deviceContext = Substitute.For<IDeviceContext>();
    private readonly AppConfigService _sut;

    private static readonly PlanOptions Plans = new()
    {
        Free = new TierOptions
        {
            DailyCalculationLimit = 20, MaxSavedScenarios = 10,
            Features = new FeatureOptions { Dca = false, PriceHistoryMonths = 12 },
        },
        Premium = new TierOptions
        {
            DailyCalculationLimit = 0, MaxSavedScenarios = 0,
            Features = new FeatureOptions { Dca = true, PriceHistoryMonths = 0 },
        },
    };

    public AppConfigServiceTests()
    {
        _deviceContext.DeviceId.Returns(DeviceId);
        _sut = new AppConfigService(_repository, _deviceContext, Microsoft.Extensions.Options.Options.Create(Plans));
    }

    [Fact]
    public async Task GetConfigAsync_FreeUser_ReturnsFreeTierConfig()
    {
        _repository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>())
                   .Returns(new User { Id = Guid.NewGuid(), DeviceId = DeviceId, Tier = "free" });

        var config = await _sut.GetConfigAsync(CancellationToken.None);

        config.Tier.Should().Be("free");
        config.DailyCalculationLimit.Should().Be(20);
        config.MaxSavedScenarios.Should().Be(10);
        config.Features.Dca.Should().BeFalse();
        config.Features.PriceHistoryMonths.Should().Be(12);
    }

    [Fact]
    public async Task GetConfigAsync_PremiumUser_ReturnsPremiumTierConfig()
    {
        _repository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>())
                   .Returns(new User { Id = Guid.NewGuid(), DeviceId = DeviceId, Tier = "premium" });

        var config = await _sut.GetConfigAsync(CancellationToken.None);

        config.Tier.Should().Be("premium");
        config.DailyCalculationLimit.Should().Be(0);
        config.Features.Dca.Should().BeTrue();
        config.Features.PriceHistoryMonths.Should().Be(0);
    }

    [Fact]
    public async Task GetConfigAsync_UnknownDevice_FallsBackToFreeTier()
    {
        _repository.GetUserByDeviceIdAsync(DeviceId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var config = await _sut.GetConfigAsync(CancellationToken.None);

        config.Tier.Should().Be("free");
        config.DailyCalculationLimit.Should().Be(20);
    }
}
