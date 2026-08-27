namespace Saydin.PriceIngestion.Adapters;

internal static class ProviderStartupValidator
{
    private static readonly (string Worker, string Secret, string Code)[] RequiredSecrets =
    [
        ("CoinGecko", "ExternalApis:CoinGecko:ApiKey", "provider_secret_missing_coingecko"),
        ("OpenExchangeRates", "ExternalApis:OpenExchangeRates:AppId", "provider_secret_missing_openexchangerates"),
        ("TwelveData", "ExternalApis:TwelveData:ApiKey", "provider_secret_missing_twelvedata"),
        ("EvdsInflation", "ExternalApis:Evds:ApiKey", "provider_secret_missing_evds"),
    ];

    public static void Validate(IConfiguration configuration)
    {
        foreach (var (worker, secret, code) in RequiredSecrets)
        {
            if (configuration.GetValue<bool>($"IngestionWorkers:{worker}:Enabled")
                && string.IsNullOrWhiteSpace(configuration[secret]))
                throw new ProviderStartupRejectedException(code);
        }
    }
}

public sealed class ProviderStartupRejectedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
