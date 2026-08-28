using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Saydin.PriceIngestion.Adapters;

namespace Saydin.PriceIngestion.Tests.Adapters;

public sealed class ProviderStartupValidatorTests
{
    [Theory]
    [InlineData("CoinGecko", "provider_secret_missing_coingecko")]
    [InlineData("OpenExchangeRates", "provider_secret_missing_openexchangerates")]
    [InlineData("TwelveData", "provider_secret_missing_twelvedata")]
    [InlineData("EvdsInflation", "provider_secret_missing_evds")]
    public void EnabledProviderWithBlankSecret_FailsClosed(string worker, string code)
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            [$"IngestionWorkers:{worker}:Enabled"] = "true",
        });

        var act = () => ProviderStartupValidator.Validate(configuration);

        act.Should().Throw<ProviderStartupRejectedException>()
            .Which.Code.Should().Be(code);
    }

    [Fact]
    public void DisabledProvidersMayHaveBlankSecrets()
    {
        var configuration = Configuration([]);
        var act = () => ProviderStartupValidator.Validate(configuration);
        act.Should().NotThrow();
    }

    [Fact]
    public void Classifier_OwnedTimeoutAndOpenCircuitAreRetryable_ContractAndOverflowArePermanent()
    {
        ProviderFailureClassifier.IsRetryable(new TimeoutRejectedException()).Should().BeTrue();
        ProviderFailureClassifier.IsRetryable(new BrokenCircuitException()).Should().BeTrue();
        ProviderFailureClassifier.IsRetryable(new ProviderContractException("schema")).Should().BeFalse();
        ProviderFailureClassifier.IsRetryable(new ProviderPayloadTooLargeException()).Should().BeFalse();
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
