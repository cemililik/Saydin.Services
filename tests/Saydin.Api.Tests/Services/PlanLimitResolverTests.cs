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
    private readonly ISavedScenarioRepository _repository = Substitute.For<ISavedScenarioRepository>();
    private readonly IInstallationPrincipalContext _principal = Substitute.For<IInstallationPrincipalContext>();
    private readonly PlanLimitResolver _sut;
    private readonly Guid _principalId = Guid.NewGuid();

    private static readonly PlanOptions Plans = new()
    {
        Free    = new TierOptions { DailyAssetQueryLimit = 500 },
        Premium = new TierOptions { DailyAssetQueryLimit = 5000 },
    };

    public PlanLimitResolverTests()
    {
        _principal.PrincipalId.Returns(_principalId);
        _sut = new PlanLimitResolver(
            _repository,
            _principal,
            Microsoft.Extensions.Options.Options.Create(Plans));
    }

    [Fact]
    public async Task ResolveDailyAssetQueryLimitAsync_FreeUser_ReturnsFreeLimit()
    {
        _repository.GetUserByIdAsync(_principalId, Arg.Any<CancellationToken>())
                   .Returns(new User { Id = _principalId, Tier = "free" });

        var limit = await _sut.ResolveDailyAssetQueryLimitAsync(CancellationToken.None);

        limit.Should().Be(500);
    }

    [Fact]
    public async Task ResolveDailyAssetQueryLimitAsync_PremiumUser_ReturnsPremiumLimit()
    {
        _repository.GetUserByIdAsync(_principalId, Arg.Any<CancellationToken>())
                   .Returns(new User { Id = _principalId, Tier = "premium" });

        var limit = await _sut.ResolveDailyAssetQueryLimitAsync(CancellationToken.None);

        limit.Should().Be(5000);
    }

    [Fact]
    public async Task ResolveDailyAssetQueryLimitAsync_MissingAuthenticatedPrincipal_FailsClosed()
    {
        _repository.GetUserByIdAsync(_principalId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var act = () => _sut.ResolveDailyAssetQueryLimitAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
