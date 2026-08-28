namespace Saydin.DataQualityAudit;

internal abstract record AuditCommandOptions;

internal sealed record ScanOptions(
    string InputManifestFile,
    string InputSignatureFile,
    string InputPublicKeyFile,
    string? EvidencePrivateKeyFile,
    string HmacKeyFile,
    string OutputDirectory,
    EvidenceSignerConfiguration? EvidenceSigner = null,
    string? ProductionTargetAuthorityFile = null) : AuditCommandOptions;

internal abstract record EvidenceSignerConfiguration;

internal sealed record LocalPemSignerConfiguration(string PrivateKeyFile)
    : EvidenceSignerConfiguration;

internal sealed record OciKmsSignerConfiguration(
    string KeyId,
    string KeyVersionId,
    string CryptoEndpoint,
    string Region,
    string PublicKeyFile,
    IReadOnlySet<string> AllowedEvidenceKeyIds,
    TimeSpan Timeout) : EvidenceSignerConfiguration;

internal sealed record VerifyEvidenceOptions(
    string BundleDirectory,
    string PublicKeyFile) : AuditCommandOptions;

internal static class AuditOptions
{
    public static AuditCommandOptions Parse(string[] args)
    {
        if (args.Length == 0)
            throw Invalid("command_missing");

        return args[0] switch
        {
            "scan" => ParseScan(args[1..]),
            "verify-evidence" => ParseVerify(args[1..]),
            _ => throw Invalid("command_unknown"),
        };
    }

    private static ScanOptions ParseScan(string[] args)
    {
        var allowed = new[]
        {
            "--input",
            "--input-signature",
            "--input-public-key",
            "--evidence-private-key",
            "--hmac-key-file",
            "--output",
            "--signer-mode",
            "--kms-key-id",
            "--kms-key-version-id",
            "--kms-crypto-endpoint",
            "--oci-region",
            "--evidence-public-key",
            "--allowed-evidence-key-ids",
            "--kms-timeout-seconds",
            "--production-target-authority-file",
        };
        var values = ParseOptionalPairs(args, allowed);
        foreach (var required in new[]
                 {
                     "--input", "--input-signature", "--input-public-key",
                     "--hmac-key-file", "--output",
                 })
            if (!values.ContainsKey(required))
                throw Invalid("argument_required");

        var mode = values.GetValueOrDefault("--signer-mode") ?? "local-pem";
        EvidenceSignerConfiguration signer;
        string? privateKey = null;
        if (string.Equals(mode, "local-pem", StringComparison.Ordinal))
        {
            privateKey = Required(values, "--evidence-private-key");
            if (values.Keys.Any(key => key.StartsWith("--kms-", StringComparison.Ordinal) ||
                                      key is "--oci-region" or "--evidence-public-key" or
                                          "--allowed-evidence-key-ids"))
                throw Invalid("signer_argument_mismatch");
            signer = new LocalPemSignerConfiguration(privateKey);
        }
        else if (string.Equals(mode, "oci-kms-instance-principal", StringComparison.Ordinal))
        {
            if (values.ContainsKey("--evidence-private-key"))
                throw Invalid("production_private_key_argument_rejected");
            var timeoutSeconds = ParseInteger(
                values.GetValueOrDefault("--kms-timeout-seconds") ?? "10", 1, 30,
                "kms_timeout_invalid");
            var keyId = Required(values, "--kms-key-id");
            var keyVersionId = Required(values, "--kms-key-version-id");
            var endpoint = Required(values, "--kms-crypto-endpoint");
            var region = Required(values, "--oci-region");
            var publicKey = Required(values, "--evidence-public-key");
            var allowlist = ParseEvidenceKeyAllowlist(
                Required(values, "--allowed-evidence-key-ids"));
            ValidateOciIdentity(keyId, keyVersionId, endpoint, region);
            signer = new OciKmsSignerConfiguration(
                keyId, keyVersionId, endpoint, region, publicKey, allowlist,
                TimeSpan.FromSeconds(timeoutSeconds));
        }
        else
        {
            throw Invalid("signer_mode_invalid");
        }
        return new ScanOptions(
            values["--input"],
            values["--input-signature"],
            values["--input-public-key"],
            privateKey,
            values["--hmac-key-file"],
            values["--output"],
            signer,
            values.GetValueOrDefault("--production-target-authority-file"));
    }

    private static VerifyEvidenceOptions ParseVerify(string[] args)
    {
        var values = ParsePairs(args, ["--bundle", "--public-key"]);
        return new VerifyEvidenceOptions(values["--bundle"], values["--public-key"]);
    }

    private static Dictionary<string, string> ParsePairs(string[] args, string[] allowed)
    {
        if (args.Length == 0 || args.Length % 2 != 0)
            throw Invalid("argument_pair_invalid");
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var key = args[index];
            if (!allowedSet.Contains(key) || !result.TryAdd(key, args[index + 1]) ||
                string.IsNullOrWhiteSpace(args[index + 1]))
                throw Invalid("argument_invalid");
        }

        if (result.Count != allowed.Length || allowed.Any(key => !result.ContainsKey(key)))
            throw Invalid("argument_required");
        return result;
    }

    private static Dictionary<string, string> ParseOptionalPairs(string[] args, string[] allowed)
    {
        if (args.Length == 0 || args.Length % 2 != 0)
            throw Invalid("argument_pair_invalid");
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var key = args[index];
            var value = args[index + 1];
            if (!allowedSet.Contains(key) || !result.TryAdd(key, value) ||
                string.IsNullOrWhiteSpace(value) || value != value.Trim() ||
                value.IndexOfAny(['\0', '\r', '\n']) >= 0)
                throw Invalid("argument_invalid");
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : throw Invalid("argument_required");

    private static int ParseInteger(string value, int minimum, int maximum, string code) =>
        int.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
        parsed >= minimum && parsed <= maximum
            ? parsed
            : throw Invalid(code);

    private static IReadOnlySet<string> ParseEvidenceKeyAllowlist(string value)
    {
        var entries = value.Split(',', StringSplitOptions.None);
        if (entries.Length is < 1 or > 3 || entries.Distinct(StringComparer.Ordinal).Count() != entries.Length ||
            entries.Any(entry => entry.Length != 64 || entry.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw Invalid("evidence_key_allowlist_invalid");
        return entries.ToHashSet(StringComparer.Ordinal);
    }

    private static void ValidateOciIdentity(
        string keyId,
        string keyVersionId,
        string endpoint,
        string region)
    {
        if (keyId.Length > 255 || keyVersionId.Length > 255 ||
            !IsCanonicalKeyOcid(keyId, "key", region) ||
            !IsCanonicalKeyOcid(keyVersionId, "keyversion", region) ||
            region.Length is < 3 or > 63 || region.Any(character =>
                !(char.IsAsciiDigit(character) || character is >= 'a' and <= 'z' || character == '-')) ||
            region[0] == '-' || region[^1] == '-' ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/" ||
            !IsCanonicalCryptoEndpointHost(uri.Host, region))
            throw Invalid("oci_kms_identity_invalid");
    }

    private static bool IsCanonicalKeyOcid(string value, string resource, string region)
    {
        var parts = value.Split('.', StringSplitOptions.None);
        return parts.Length == 5 && parts[0] == "ocid1" && parts[1] == resource &&
               parts[2] is { Length: >= 3 and <= 8 } realm &&
               realm.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9') &&
               parts[3] == region && parts[4] is { Length: >= 1 and <= 128 } unique &&
               unique.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static bool IsCanonicalCryptoEndpointHost(string host, string region)
    {
        foreach (var suffix in new[]
                 {
                     ".oraclecloud.com", ".oraclegovcloud.com",
                     ".oraclecloud.eu", ".oraclecloud.uk",
                 })
        {
            var serviceSuffix = $"-crypto.kms.{region}{suffix}";
            if (!host.EndsWith(serviceSuffix, StringComparison.Ordinal)) continue;
            var keyNamespace = host[..^serviceSuffix.Length];
            return keyNamespace is { Length: >= 1 and <= 63 } &&
                   keyNamespace[0] != '-' && keyNamespace[^1] != '-' &&
                   keyNamespace.All(character =>
                       character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
        }
        return false;
    }

    private static AuditRejectedException Invalid(string code) =>
        new(code, AuditExitCodes.InvalidArguments);
}
