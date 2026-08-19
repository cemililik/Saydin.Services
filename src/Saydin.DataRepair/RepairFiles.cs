using Saydin.DatabaseSecurity;

namespace Saydin.DataRepair;

internal static class RepairFiles
{
    // SecureSecretFile deliberately caps opaque reads at 64 KiB. Keep the signed
    // plan inside that same hardened openat2/statx boundary.
    public const int PlanBytes = 64 * 1024;
    public const int SignatureBytes = 4 * 1024;
    public const int ApprovalTokenBytes = 4 * 1024;

    public static byte[] ReadPrivateInput(string path, int maximumBytes, string code)
    {
        if (!Path.IsPathFullyQualified(path)) throw Rejected("path_absolute_required");
        try
        {
            return SecureSecretFile.ReadBytes(path, 1, maximumBytes, code);
        }
        catch (DatabaseSecurityRejectedException)
        {
            throw Rejected(code);
        }
    }

    public static void ValidateApprovalToken(string path, string expectedSha256)
    {
        var bytes = ReadPrivateInput(path, ApprovalTokenBytes, "approval_token_invalid");
        try
        {
            if (bytes.Length < 32 || !RepairCryptography.FixedEquals(
                    RepairCryptography.Sha256Hex(bytes), expectedSha256))
                throw Rejected("approval_token_invalid");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static RepairRejectedException Rejected(string code) =>
        new(code, RepairExitCodes.InvalidArguments);
}
