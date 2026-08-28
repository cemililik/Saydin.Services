using System.Text.RegularExpressions;

namespace Saydin.DataRepair;

internal enum RepairMode
{
    DryRun,
    Apply,
    Rollback,
}

internal abstract record ReceiptSignerConfiguration;

internal sealed record LocalReceiptSignerConfiguration(string PrivateKeyFile)
    : ReceiptSignerConfiguration;

internal sealed record OciKmsReceiptSignerConfiguration(
    string KeyId,
    string KeyVersionId,
    string CryptoEndpoint,
    string Region,
    string PublicKeyFile,
    TimeSpan Timeout) : ReceiptSignerConfiguration;

internal sealed record RepairCommandOptions(
    RepairMode Mode,
    string PlanFile,
    string PlanSignatureFile,
    string PlanPublicKeyFile,
    string EvidenceBundleDirectory,
    string EvidencePublicKeyFile,
    string AuditLogin,
    string AuditPasswordFile,
    string? ApprovalTokenFile,
    string? ReceiptRoot,
    ReceiptSignerConfiguration? ReceiptSigner);

internal static partial class RepairOptions
{
    private static readonly string[] CommonKeys =
    [
        "--plan", "--plan-signature", "--plan-public-key",
        "--evidence-bundle", "--evidence-public-key",
        "--audit-login", "--audit-password-file",
    ];

    public static RepairCommandOptions Parse(string[] args)
    {
        if (args.Length == 0) throw Invalid("argument_required");
        RepairMode mode;
        var optionArgs = args;
        if (args[0].StartsWith("--", StringComparison.Ordinal))
        {
            mode = RepairMode.DryRun;
        }
        else
        {
            mode = args[0] switch
            {
                "dry-run" => RepairMode.DryRun,
                "apply" => RepairMode.Apply,
                "rollback" => RepairMode.Rollback,
                _ => throw Invalid("command_unknown"),
            };
            optionArgs = args[1..];
        }

        var allowed = new List<string>(CommonKeys);
        if (mode != RepairMode.DryRun)
        {
            allowed.AddRange([
                "--approval-token-file", "--receipt-root", "--receipt-signer-mode",
                "--receipt-private-key", "--kms-key-id", "--kms-key-version-id",
                "--kms-crypto-endpoint", "--oci-region", "--receipt-public-key",
                "--kms-timeout-seconds",
            ]);
        }
        var values = ParsePairs(optionArgs, allowed);
        foreach (var key in CommonKeys)
            if (!values.ContainsKey(key)) throw Invalid("argument_required");

        if (mode == RepairMode.DryRun)
        {
            if (values.Count != CommonKeys.Length) throw Invalid("argument_invalid");
            return Build(mode, values, null, null, null);
        }
        var approval = Required(values, "--approval-token-file");
        var receiptRoot = Required(values, "--receipt-root");
        var signerMode = Required(values, "--receipt-signer-mode");
        ReceiptSignerConfiguration signer;
        if (signerMode == "local-pem")
        {
            signer = new LocalReceiptSignerConfiguration(
                Required(values, "--receipt-private-key"));
            if (values.Keys.Any(key => key.StartsWith("--kms-", StringComparison.Ordinal) ||
                                       key is "--oci-region" or "--receipt-public-key"))
                throw Invalid("signer_argument_mismatch");
        }
        else if (signerMode == "oci-kms-instance-principal")
        {
            if (values.ContainsKey("--receipt-private-key"))
                throw Invalid("private_key_argument_rejected");
            var keyId = Required(values, "--kms-key-id");
            var keyVersion = Required(values, "--kms-key-version-id");
            var endpoint = Required(values, "--kms-crypto-endpoint");
            var region = Required(values, "--oci-region");
            var publicKey = Required(values, "--receipt-public-key");
            var timeout = ParseInteger(
                values.GetValueOrDefault("--kms-timeout-seconds") ?? "10", 1, 30);
            ValidateOci(keyId, keyVersion, endpoint, region);
            signer = new OciKmsReceiptSignerConfiguration(
                keyId, keyVersion, endpoint, region, publicKey, TimeSpan.FromSeconds(timeout));
        }
        else
        {
            throw Invalid("signer_mode_invalid");
        }
        return Build(mode, values, approval, receiptRoot, signer);
    }

    private static RepairCommandOptions Build(
        RepairMode mode,
        IReadOnlyDictionary<string, string> values,
        string? approval,
        string? receiptRoot,
        ReceiptSignerConfiguration? signer) =>
        new(mode,
            values["--plan"], values["--plan-signature"], values["--plan-public-key"],
            values["--evidence-bundle"], values["--evidence-public-key"],
            values["--audit-login"], values["--audit-password-file"],
            approval, receiptRoot, signer);

    private static Dictionary<string, string> ParsePairs(string[] args, IEnumerable<string> allowed)
    {
        if (args.Length == 0 || args.Length % 2 != 0) throw Invalid("argument_pair_invalid");
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

    private static int ParseInteger(string value, int minimum, int maximum) =>
        int.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
        parsed >= minimum && parsed <= maximum
            ? parsed
            : throw Invalid("kms_timeout_invalid");

    private static void ValidateOci(
        string keyId,
        string keyVersionId,
        string endpoint,
        string region)
    {
        if (!OcidPattern().IsMatch(keyId) || !KeyVersionOcidPattern().IsMatch(keyVersionId) ||
            region is not { Length: >= 3 and <= 63 } || !RegionPattern().IsMatch(region) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.Host.Contains($"-crypto.kms.{region}.", StringComparison.Ordinal) ||
            !(uri.Host.EndsWith(".oraclecloud.com", StringComparison.Ordinal) ||
              uri.Host.EndsWith(".oraclegovcloud.com", StringComparison.Ordinal) ||
              uri.Host.EndsWith(".oraclecloud.eu", StringComparison.Ordinal) ||
              uri.Host.EndsWith(".oraclecloud.uk", StringComparison.Ordinal)))
            throw Invalid("oci_kms_identity_invalid");
    }

    [GeneratedRegex("^ocid1\\.key\\.[a-z0-9]{3,8}\\.[a-z0-9-]{3,63}\\.[A-Za-z0-9_-]{1,128}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex OcidPattern();

    [GeneratedRegex("^ocid1\\.keyversion\\.[a-z0-9]{3,8}\\.[a-z0-9-]{3,63}\\.[A-Za-z0-9_-]{1,128}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyVersionOcidPattern();

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex RegionPattern();

    private static RepairRejectedException Invalid(string code) =>
        new(code, RepairExitCodes.InvalidArguments);
}
