using System.Text.Json;
using System.Text.RegularExpressions;
using Saydin.DatabaseSecurity;

namespace Saydin.DataRepair;

internal static partial class SignedRepairPlan
{
    public static VerifiedRepairPlan LoadAndVerify(
        string planFile,
        string signatureFile,
        string publicKeyFile,
        TimeProvider timeProvider)
    {
        var raw = RepairFiles.ReadPrivateInput(planFile, RepairFiles.PlanBytes, "plan_file_invalid");
        var signature = RepairFiles.ReadPrivateInput(
            signatureFile, RepairFiles.SignatureBytes, "plan_signature_invalid");
        try
        {
            byte[] canonical;
            RepairPlan plan;
            try
            {
                canonical = CanonicalJson.Canonicalize(raw);
                if (!canonical.AsSpan().SequenceEqual(raw)) throw Invalid("plan_not_canonical");
                plan = JsonSerializer.Deserialize(canonical, RepairJsonContext.Default.RepairPlan)
                    ?? throw Invalid("plan_contract_invalid");
            }
            catch (JsonException)
            {
                throw Invalid("plan_contract_invalid");
            }

            var publicSpki = RepairCryptography.ReadPublicSpki(publicKeyFile);
            if (!RepairCryptography.Verify(canonical, signature, publicSpki))
                throw new RepairRejectedException(
                    "plan_signature_invalid", RepairExitCodes.SignatureFailure);
            var keyId = RepairCryptography.Sha256Hex(publicSpki);
            if (!RepairCryptography.IsSha256(plan.KeyId))
                throw Invalid("plan_contract_invalid");
            if (!RepairCryptography.FixedEquals(keyId, plan.KeyId))
                throw new RepairRejectedException(
                    "plan_key_id_mismatch", RepairExitCodes.SignatureFailure);
            Validate(plan, timeProvider.GetUtcNow());
            var targetBytes = CanonicalJson.Serialize(
                plan.Target, RepairJsonContext.Default.RepairTarget);
            return new VerifiedRepairPlan(
                plan,
                canonical,
                RepairCryptography.Sha256Hex(canonical),
                RepairCryptography.Sha256Hex(targetBytes),
                RepairCryptography.Sha256Hex(plan.Nonce));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static void Validate(RepairPlan plan, DateTimeOffset now)
    {
        if (plan.SchemaVersion is not (1 or 2) || !RepairCryptography.IsSha256(plan.KeyId) ||
            !RepairCryptography.IsSha256(plan.ReceiptKeyId) ||
            !RepairCryptography.IsSha256(plan.ApprovalTokenSha256) ||
            plan.Target is null || plan.Evidence is null || plan.MigrationTrust is null ||
            plan.Operations is null || string.IsNullOrEmpty(plan.ChangeTicket) ||
            string.IsNullOrEmpty(plan.Nonce))
            throw Invalid("plan_contract_invalid");

        if (!IsUtc(plan.IssuedAtUtc) || !IsUtc(plan.ExpiresAtUtc) ||
            plan.IssuedAtUtc > now.AddMinutes(5) || plan.ExpiresAtUtc <= now ||
            plan.ExpiresAtUtc <= plan.IssuedAtUtc ||
            plan.ExpiresAtUtc - plan.IssuedAtUtc > TimeSpan.FromHours(24))
            throw Invalid("plan_lifetime_invalid");
        if (!ChangeTicketPattern().IsMatch(plan.ChangeTicket) ||
            !NoncePattern().IsMatch(plan.Nonce))
            throw Invalid("plan_approval_identity_invalid");

        ValidateTarget(plan.Target);
        if (!RepairCryptography.IsSha256(plan.Evidence.ContentSha256) ||
            !RepairCryptography.IsSha256(plan.Evidence.SignerKeyId))
            throw Invalid("plan_evidence_binding_invalid");
        ValidateMigrationTrust(plan.MigrationTrust);

        if (plan.Operations.Count is < 1 or > 1_000 ||
            plan.Operations.Any(operation => operation is null))
            throw Invalid("plan_operation_count_invalid");
        var windows = new HashSet<Guid>();
        var workOrders = new HashSet<(string Type, string Reference)>(
            EqualityComparer<(string Type, string Reference)>.Default);
        foreach (var operation in plan.Operations)
        {
            if (operation.Type == "requeue_permanent_window")
            {
                if (operation.WindowId is not { } id || id == Guid.Empty || !windows.Add(id) ||
                    !RepairCryptography.IsSha256(operation.PreimageSha256) ||
                    operation.NextAttemptAtUtc is not { } next || !IsUtc(next) ||
                    next < plan.IssuedAtUtc || next > plan.ExpiresAtUtc ||
                    operation.ReferenceSha256 is not null || operation.ReasonCode is not null)
                    throw Invalid("plan_requeue_operation_invalid");
            }
            else if (operation.Type is "refetch" or "manual_review")
            {
                if (operation.WindowId is not null || operation.PreimageSha256 is not null ||
                    operation.NextAttemptAtUtc is not null ||
                    !RepairCryptography.IsSha256(operation.ReferenceSha256) ||
                    operation.ReasonCode is null || !ReasonCodePattern().IsMatch(operation.ReasonCode) ||
                    !workOrders.Add((operation.Type, operation.ReferenceSha256!)))
                    throw Invalid("plan_work_order_invalid");
            }
            else
            {
                throw Invalid("plan_operation_type_invalid");
            }
        }
    }

    private static void ValidateTarget(RepairTarget target)
    {
        if (target.Environment is not ("development" or "staging" or "production") ||
            string.IsNullOrEmpty(target.Database) || string.IsNullOrEmpty(target.DeploymentId) ||
            string.IsNullOrEmpty(target.RolePrefix) ||
            !RepairCryptography.IsSha256(target.SystemIdentifierSha256))
            throw Invalid("plan_target_invalid");
        try
        {
            _ = RoleContract.Create(
                target.DeploymentId,
                target.Database,
                target.SystemIdentifierSha256,
                target.RolePrefix);
        }
        catch (DatabaseSecurityRejectedException)
        {
            throw Invalid("plan_target_invalid");
        }
    }

    private static void ValidateMigrationTrust(RepairMigrationTrust trust)
    {
        if (!RepairCryptography.IsSha256(trust.ManifestSha256) ||
            trust.Migrations is null || trust.Migrations.Count != EmbeddedRepairMigrationTrust.Entries.Count ||
            !RepairCryptography.FixedEquals(
                trust.ManifestSha256, EmbeddedRepairMigrationTrust.ManifestSha256))
            throw Invalid("plan_migration_trust_invalid");
        for (var index = 0; index < trust.Migrations.Count; index++)
        {
            var supplied = trust.Migrations[index];
            var expected = EmbeddedRepairMigrationTrust.Entries[index];
            if (supplied is null || supplied.Version != expected.Version ||
                !RepairCryptography.FixedEquals(supplied.Sha256, expected.Sha256))
                throw Invalid("plan_migration_trust_invalid");
        }
        if (!RepairCryptography.FixedEquals(
                EmbeddedRepairMigrationTrust.ComputeManifestSha256(trust.Migrations), trust.ManifestSha256))
            throw Invalid("plan_migration_trust_invalid");
    }

    private static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    [GeneratedRegex("^[A-Z][A-Z0-9-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChangeTicketPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{32,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex NoncePattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonCodePattern();

    private static RepairRejectedException Invalid(string code) =>
        new(code, RepairExitCodes.InvalidArguments);
}
