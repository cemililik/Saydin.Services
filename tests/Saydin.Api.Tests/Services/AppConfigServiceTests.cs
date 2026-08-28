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
/// için unit test zorunlu"). Doğrulanmış installation principal'inin tier →
/// feature-flag çözümünü doğrular.
/// </summary>
public class AppConfigServiceTests
{
    private static readonly Guid PrincipalId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    private readonly ISavedScenarioRepository _repository = Substitute.For<ISavedScenarioRepository>();
    private readonly IInstallationPrincipalContext _principalContext =
        Substitute.For<IInstallationPrincipalContext>();
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
        _principalContext.PrincipalId.Returns(PrincipalId);
        _sut = new AppConfigService(
            _repository, _principalContext, Microsoft.Extensions.Options.Options.Create(Plans));
    }

    [Fact]
    public async Task GetConfigAsync_FreeUser_ReturnsFreeTierConfig()
    {
        _repository.GetUserByIdAsync(PrincipalId, Arg.Any<CancellationToken>())
                   .Returns(new User { Id = PrincipalId, DeviceId = null, Tier = "free" });

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
        _repository.GetUserByIdAsync(PrincipalId, Arg.Any<CancellationToken>())
                   .Returns(new User { Id = PrincipalId, DeviceId = null, Tier = "premium" });

        var config = await _sut.GetConfigAsync(CancellationToken.None);

        config.Tier.Should().Be("premium");
        config.DailyCalculationLimit.Should().Be(0);
        config.MaxSavedScenarios.Should().Be(100,
            "plan 0 olsa da API sistem hard cap'ini effective contract olarak dönmeli");
        config.Features.Dca.Should().BeTrue();
        config.Features.PriceHistoryMonths.Should().Be(0);
    }

    [Fact]
    public async Task GetConfigAsync_MissingAuthenticatedPrincipal_Throws()
    {
        _repository.GetUserByIdAsync(PrincipalId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var act = () => _sut.GetConfigAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Authenticated installation principal is missing.");
    }
}
