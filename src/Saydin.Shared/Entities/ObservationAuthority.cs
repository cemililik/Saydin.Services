namespace Saydin.Shared.Entities;

public static class ProviderSources
{
    public const string Tcmb = "tcmb";
    public const string CoinGecko = "coingecko";
    public const string OpenExchangeRates = "openexchangerates";
    public const string TwelveData = "twelvedata";
    public const string Evds = "evds";
}

public static class ObservationPriceKinds
{
    public const string OfficialReference = "official_reference";
    public const string DailyUtcReference = "daily_utc_reference";
    public const string DailyReference = "daily_reference";
    public const string DailyClose = "daily_close";
    public const string CpiIndex = "cpi_index";
}

public static class ObservationAuthorityLimits
{
    public const int Sha256Bytes = 32;
    public const int SourceObservationIdBytes = 256;
    public const int SourceRawBytes = 65_536;
    public const int ProviderSourceBytes = 32;
    public const int PriceKindBytes = 32;
}
