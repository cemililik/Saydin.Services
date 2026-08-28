using Saydin.Shared.Entities;
using System.Text;

namespace Saydin.PriceIngestion.Adapters;

internal static class ProviderAuthority
{
    public static PricePoint Price(
        PricePoint point,
        string providerSource,
        string observationId,
        DateTimeOffset asOfAt,
        string priceKind,
        byte[] payloadSha256,
        int payloadByteLength,
        ObservationEvidenceValue evidence)
    {
        Validate(providerSource, observationId, priceKind,
            payloadSha256, payloadByteLength, evidence);
        point.ProviderSource = providerSource;
        point.SourceObservationId = observationId;
        point.AsOfAt = asOfAt;
        point.PriceKind = priceKind;
        point.IsFinal = true;
        point.PayloadSha256 = payloadSha256.ToArray();
        point.PayloadByteLength = payloadByteLength;
        point.ObservationSha256 = null; // database-canonical authority, populated only after persistence
        point.SourceRaw = evidence.Json;
        return point;
    }

    public static InflationRate Inflation(
        InflationRate rate,
        string providerSource,
        string observationId,
        DateTimeOffset asOfAt,
        byte[] payloadSha256,
        int payloadByteLength,
        ObservationEvidenceValue evidence)
    {
        Validate(providerSource, observationId, ObservationPriceKinds.CpiIndex,
            payloadSha256, payloadByteLength, evidence);
        rate.ProviderSource = providerSource;
        rate.SourceObservationId = observationId;
        rate.AsOfAt = asOfAt;
        rate.PriceKind = ObservationPriceKinds.CpiIndex;
        rate.IsFinal = true;
        rate.PayloadSha256 = payloadSha256.ToArray();
        rate.PayloadByteLength = payloadByteLength;
        rate.ObservationSha256 = null; // database-canonical authority, populated only after persistence
        rate.SourceRaw = evidence.Json;
        return rate;
    }

    private static void Validate(
        string providerSource,
        string observationId,
        string priceKind,
        byte[] payloadSha256,
        int payloadByteLength,
        ObservationEvidenceValue evidence)
    {
        if (string.IsNullOrWhiteSpace(providerSource)
            || string.IsNullOrWhiteSpace(observationId)
            || string.IsNullOrWhiteSpace(priceKind)
            || payloadSha256.Length != ObservationAuthorityLimits.Sha256Bytes
            || payloadByteLength is < 1 or > ObservationAuthorityLimits.SourceRawBytes
            || string.IsNullOrWhiteSpace(evidence.Json)
            || Encoding.UTF8.GetByteCount(evidence.Json) > ObservationAuthorityLimits.SourceRawBytes)
            throw new ProviderContractException("authority_contract_invalid");
    }
}
