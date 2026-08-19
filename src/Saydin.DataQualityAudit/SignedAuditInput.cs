using System.Text.Json;

namespace Saydin.DataQualityAudit;

internal sealed record VerifiedAuditInput(
    AuditInputManifest Manifest,
    string CanonicalSha256);

internal static class SignedAuditInput
{
    public static VerifiedAuditInput LoadAndVerify(
        ScanOptions options,
        TimeProvider timeProvider)
    {
        byte[] raw;
        byte[] signature;
        try
        {
            raw = AuditFileLimits.ReadBytes(
                options.InputManifestFile,
                AuditFileLimits.InputManifestBytes,
                "input_manifest_unreadable",
                "input_manifest_too_large",
                AuditExitCodes.InvalidArguments);
            signature = AuditFileLimits.ReadBytes(
                options.InputSignatureFile,
                AuditFileLimits.DetachedSignatureBytes,
                "input_signature_unreadable",
                "input_signature_too_large",
                AuditExitCodes.InvalidArguments);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AuditRejectedException("input_manifest_unreadable", AuditExitCodes.InvalidArguments);
        }

        byte[] canonical;
        try
        {
            canonical = CanonicalJson.Canonicalize(raw);
        }
        catch (JsonException)
        {
            throw new AuditRejectedException(
                "input_manifest_contract_invalid", AuditExitCodes.InvalidArguments);
        }
        if (!AuditCryptography.Verify(canonical, signature, options.InputPublicKeyFile))
            throw new AuditRejectedException("input_manifest_signature_invalid", AuditExitCodes.InvalidArguments);

        AuditInputManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(canonical, AuditJsonContext.Default.AuditInputManifest)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new AuditRejectedException("input_manifest_contract_invalid", AuditExitCodes.InvalidArguments);
        }

        if (!CryptographicEquals(manifest.KeyId, AuditCryptography.PublicKeyId(options.InputPublicKeyFile)))
            throw new AuditRejectedException("input_key_id_mismatch", AuditExitCodes.InvalidArguments);
        Validate(manifest, timeProvider.GetUtcNow());
        return new VerifiedAuditInput(manifest, AuditCryptography.Sha256Hex(canonical));
    }

    private static void Validate(AuditInputManifest manifest, DateTimeOffset now)
    {
        if (manifest.Target is null || manifest.Budget is null || manifest.Scope is null)
            throw Invalid("input_manifest_contract_invalid");

        if (manifest.SchemaVersion != 1 || !IsKeyId(manifest.KeyId) ||
            !IsKeyId(manifest.EvidenceKeyId) ||
            manifest.IssuedAtUtc > now.AddMinutes(5) || manifest.ExpiresAtUtc <= now ||
            manifest.ExpiresAtUtc <= manifest.IssuedAtUtc)
            throw Invalid("input_manifest_lifetime_invalid");

        if (string.IsNullOrWhiteSpace(manifest.Target.Database) ||
            string.IsNullOrWhiteSpace(manifest.Target.Environment) ||
            string.IsNullOrWhiteSpace(manifest.Target.HeadroomAttestationId) ||
            !IsSha256(manifest.Target.SystemIdentifierSha256))
            throw Invalid("input_target_invalid");

        var budget = manifest.Budget;
        if (budget.MaxDatabaseBytes <= 0 || budget.MaxRelationBytes <= 0 ||
            budget.AttestedHeadroomBytes < budget.MaxEvidenceBytes ||
            budget.MaxScopeDays is < 1 or > 3660 ||
            budget.MaxWindows is < 1 or > 100_000 ||
            budget.MaxEvidencePerCheck is < 1 or > 1_000 ||
            budget.MaxEvidenceBytes is < 1_024 or > AuditFileLimits.EvidenceBundleBytes ||
            budget.StatementTimeoutMilliseconds is < 10 or > 300_000 ||
            budget.LockTimeoutMilliseconds is < 1 or > 30_000 ||
            budget.TotalTimeoutSeconds is < 1 or > 3_600)
            throw Invalid("input_budget_invalid");

        if (manifest.Scope.Lanes is null ||
            manifest.Scope.Lanes.Count is 0 or > 1_000 ||
            manifest.Scope.AsOfUtc > now.AddMinutes(5) ||
            manifest.Scope.LegacyGraceEndedAtUtc > manifest.Scope.AsOfUtc)
            throw Invalid("input_scope_invalid");

        if (manifest.Scope.Lanes.Any(lane => lane is null))
            throw Invalid("input_lane_invalid");

        if (manifest.Scope.Lanes.Distinct().Count() != manifest.Scope.Lanes.Count)
            throw Invalid("input_scope_duplicate_lane");

        foreach (var lane in manifest.Scope.Lanes)
        {
            if (lane.From > lane.Through ||
                lane.Through.DayNumber - lane.From.DayNumber + 1 > budget.MaxScopeDays ||
                lane.ContractVersion <= 0 ||
                lane.Cadence is not ("day" or "month") ||
                lane.Source is not ("tcmb" or "coingecko" or "openexchangerates" or "twelvedata" or "evds") ||
                string.IsNullOrWhiteSpace(lane.JobType) ||
                (lane.Source == "evds" ? lane.AssetId is not null : lane.AssetId is null) ||
                (lane.Source == "evds" ? lane.Cadence != "month" : lane.Cadence != "day") ||
                (lane.Cadence == "month" && (lane.From.Day != 1 || lane.Through.Day != 1)))
                throw Invalid("input_lane_invalid");
        }

        foreach (var group in manifest.Scope.Lanes.GroupBy(lane =>
                     (lane.Source, lane.AssetId, lane.JobType, lane.ContractVersion)))
        {
            DateOnly? coveredThrough = null;
            foreach (var lane in group.OrderBy(lane => lane.From).ThenBy(lane => lane.Through))
            {
                if (coveredThrough is not null && lane.From <= coveredThrough.Value)
                    throw Invalid("input_scope_overlapping_lane");
                if (coveredThrough is null || lane.Through > coveredThrough.Value)
                    coveredThrough = lane.Through;
            }
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsKeyId(string? value) => IsSha256(value);

    private static bool CryptographicEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.ASCII.GetBytes(left);
        var rightBytes = System.Text.Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static AuditRejectedException Invalid(string code) =>
        new(code, AuditExitCodes.InvalidArguments);
}
