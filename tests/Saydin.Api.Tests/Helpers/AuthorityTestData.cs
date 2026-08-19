using Saydin.Api.Repositories;
using Saydin.Shared.Entities;

namespace Saydin.Api.Tests.Helpers;

internal static class AuthorityTestData
{
    internal static readonly Guid DefaultAssetId =
        Guid.Parse("aaaaaaaa-0000-0000-0000-000000000099");

    internal static PricePoint FinalPrice(
        DateOnly date,
        decimal close,
        Guid? assetId = null,
        decimal? open = null,
        decimal? high = null,
        decimal? low = null,
        decimal? volume = null) => new()
    {
        AssetId = assetId ?? DefaultAssetId,
        PriceDate = date,
        Close = close,
        Open = open,
        High = high,
        Low = low,
        Volume = volume,
        ProviderSource = ProviderSources.Tcmb,
        SourceObservationId = $"tcmb:USD:{date:yyyy-MM-dd}:forex_buying",
        AsOfAt = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        PriceKind = ObservationPriceKinds.OfficialReference,
        IsFinal = true,
        ObservationSha256 = Enumerable.Repeat((byte)0x42, 32).ToArray(),
        AuthorityContractVersion = 1,
        SourceRaw = "{}",
    };

    internal static InflationIndexObservation FinalCpi(DateOnly date, decimal value) =>
        new(date, value, new ObservationAuthorityValue(
            ProviderSources.Evds,
            ObservationPriceKinds.CpiIndex,
            new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            1));

    internal static IReadOnlyDictionary<DateOnly, InflationIndexObservation> FinalCpi(
        IReadOnlyDictionary<DateOnly, decimal> values) =>
        values.ToDictionary(item => item.Key, item => FinalCpi(item.Key, item.Value));
}
