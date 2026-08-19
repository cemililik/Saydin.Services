using System.Security.Cryptography;
using System.Text.Json;
using Saydin.DatabaseSecurity;

namespace Saydin.DataRepair;

internal sealed class ReceiptStore
{
    private const string ReceiptFile = "receipt.json";
    private const string SignatureFile = "receipt.sig";
    private readonly string root;

    public ReceiptStore(string root)
    {
        this.root = ValidateRoot(root);
    }

    public string FinalPath(string nonceSha256, string mode) =>
        Path.Combine(root, $"{nonceSha256}-{mode}");

    public string PendingPath(string nonceSha256, string mode) =>
        Path.Combine(root, $".pending-{nonceSha256}-{mode}");

    public bool FinalExists(string nonceSha256, string mode) =>
        Directory.Exists(FinalPath(nonceSha256, mode));

    public bool PendingExists(string nonceSha256, string mode) =>
        Directory.Exists(PendingPath(nonceSha256, mode));

    public async Task<VerifiedRepairReceipt> StageAsync(
        RepairReceipt receipt,
        IReceiptSigner signer,
        CancellationToken cancellationToken)
    {
        var pending = PendingPath(receipt.NonceSha256, receipt.Mode);
        var final = FinalPath(receipt.NonceSha256, receipt.Mode);
        if (Directory.Exists(pending) || File.Exists(pending) ||
            Directory.Exists(final) || File.Exists(final))
            throw Rejected("receipt_path_exists");
        CreatePrivateDirectory(pending);
        try
        {
            var bytes = CanonicalJson.Serialize(receipt, RepairJsonContext.Default.RepairReceipt);
            var signature = await signer.SignAsync(bytes, cancellationToken);
            await WriteNewPrivateFileAsync(
                Path.Combine(pending, ReceiptFile), bytes, cancellationToken);
            await WriteNewPrivateFileAsync(
                Path.Combine(pending, SignatureFile), signature, cancellationToken);
            var verified = await VerifyAsync(
                pending, signer.Identity.PublicSubjectPublicKeyInfo, cancellationToken);
            if (!RepairCryptography.FixedEquals(
                    verified.Receipt.KeyId, signer.Identity.KeyId))
                throw Rejected("receipt_key_id_mismatch");
            return verified;
        }
        catch
        {
            DeletePending(receipt.NonceSha256, receipt.Mode);
            throw;
        }
    }

    public async Task<VerifiedRepairReceipt> ReadFinalAsync(
        string nonceSha256,
        string mode,
        ReadOnlyMemory<byte> publicSpki,
        CancellationToken cancellationToken) =>
        await VerifyAsync(FinalPath(nonceSha256, mode), publicSpki, cancellationToken);

    public async Task<VerifiedRepairReceipt> ReadPendingAsync(
        string nonceSha256,
        string mode,
        ReadOnlyMemory<byte> publicSpki,
        CancellationToken cancellationToken) =>
        await VerifyAsync(PendingPath(nonceSha256, mode), publicSpki, cancellationToken);

    public void Promote(string nonceSha256, string mode)
    {
        var pending = PendingPath(nonceSha256, mode);
        var final = FinalPath(nonceSha256, mode);
        ValidateReceiptDirectory(pending);
        ValidateReceiptInventory(pending, requireComplete: true, CancellationToken.None);
        if (Directory.Exists(final) || File.Exists(final))
            throw Rejected("receipt_final_exists");
        Directory.Move(pending, final);
    }

    public void DeletePending(string nonceSha256, string mode)
    {
        var pending = PendingPath(nonceSha256, mode);
        if (!Directory.Exists(pending)) return;
        if (!string.Equals(
            Path.GetDirectoryName(pending), root, StringComparison.Ordinal) ||
            Path.GetFileName(pending) != $".pending-{nonceSha256}-{mode}" ||
            !ReceiptInventoryMatches(pending, requireComplete: false, CancellationToken.None))
            throw Rejected("receipt_pending_unsafe");
        foreach (var fileName in new[] { ReceiptFile, SignatureFile })
        {
            var file = Path.Combine(pending, fileName);
            if (File.Exists(file)) File.Delete(file);
        }
        Directory.Delete(pending, recursive: false);
    }

    private static async Task<VerifiedRepairReceipt> VerifyAsync(
        string directory,
        ReadOnlyMemory<byte> publicSpki,
        CancellationToken cancellationToken)
    {
        ValidateReceiptDirectory(directory);
        ValidateReceiptInventory(directory, requireComplete: true, cancellationToken);
        var receiptBytes = await ReadPrivateReceiptFileAsync(
            Path.Combine(directory, ReceiptFile), 4 * 1024 * 1024, cancellationToken);
        var canonical = CanonicalJson.Canonicalize(receiptBytes);
        if (!canonical.AsSpan().SequenceEqual(receiptBytes))
            throw Rejected("receipt_not_canonical");
        var signature = await ReadPrivateReceiptFileAsync(
            Path.Combine(directory, SignatureFile), 4 * 1024, cancellationToken);
        if (!RepairCryptography.Verify(canonical, signature, publicSpki.Span))
            throw Rejected("receipt_signature_invalid");
        RepairReceipt receipt;
        try
        {
            receipt = JsonSerializer.Deserialize(canonical, RepairJsonContext.Default.RepairReceipt)
                ?? throw Rejected("receipt_contract_invalid");
        }
        catch (JsonException)
        {
            throw Rejected("receipt_contract_invalid");
        }
        var publicKeyId = RepairCryptography.Sha256Hex(publicSpki.Span);
        ValidateReceipt(receipt, publicKeyId);
        ValidateReceiptInventory(directory, requireComplete: true, cancellationToken);
        return new VerifiedRepairReceipt(
            receipt, canonical, RepairCryptography.Sha256Hex(canonical), directory);
    }

    private static void ValidateReceipt(RepairReceipt receipt, string publicKeyId)
    {
        if (receipt.SchemaVersion != 1 ||
            receipt.SignatureAlgorithm != "ECDSA-SHA256-RFC3279-DER" ||
            receipt.SigningProvider is not ("local-pem" or "oci-kms-instance-principal") ||
            receipt.Mode is not ("apply" or "rollback") ||
            !RepairCryptography.IsSha256(receipt.KeyId) ||
            !RepairCryptography.FixedEquals(receipt.KeyId, publicKeyId) ||
            !RepairCryptography.IsSha256(receipt.PlanSha256) ||
            !RepairCryptography.IsSha256(receipt.TargetSha256) ||
            !RepairCryptography.IsSha256(receipt.NonceSha256) ||
            !RepairCryptography.IsSha256(receipt.MigrationManifestSha256) ||
            !RepairCryptography.IsSha256(receipt.EvidenceContentSha256) ||
            !RepairCryptography.IsSha256(receipt.EvidenceSignerKeyId) ||
            receipt.PriorReceiptSha256 is not null &&
                !RepairCryptography.IsSha256(receipt.PriorReceiptSha256) ||
            receipt.DatabaseTransactionId <= 0 || receipt.CreatedAtUtc.Offset != TimeSpan.Zero ||
            receipt.Operations is null || receipt.Operations.Count is < 1 or > 1_000 ||
            !receipt.Operations.Select(operation => operation.Index).Order()
                .SequenceEqual(Enumerable.Range(0, receipt.Operations.Count)) ||
            receipt.Operations.Any(operation => operation.Index < 0 ||
                operation.Result is not ("requeued" or "rolled_back" or
                    "work_order_refetch" or "work_order_manual_review") ||
                operation.PreimageSha256 is not null &&
                    !RepairCryptography.IsSha256(operation.PreimageSha256) ||
                operation.PostimageSha256 is not null &&
                    !RepairCryptography.IsSha256(operation.PostimageSha256) ||
                operation.GuardSha256 is not null &&
                    !RepairCryptography.IsSha256(operation.GuardSha256)))
            throw Rejected("receipt_contract_invalid");
        if (receipt.SigningProvider == "local-pem" &&
            receipt.SigningKeyIdentity != $"local-pem:{receipt.KeyId}" ||
            receipt.SigningProvider == "oci-kms-instance-principal" &&
            !IsKmsIdentity(receipt.SigningKeyIdentity))
            throw Rejected("receipt_signing_identity_invalid");
    }

    private static bool IsKmsIdentity(string identity)
    {
        var values = identity.Split(':', StringSplitOptions.None);
        return values.Length == 2 && IsKmsOcid(values[0], "ocid1.key.") &&
               IsKmsOcid(values[1], "ocid1.keyversion.");
    }

    private static bool IsKmsOcid(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) && value.Length <= 255 &&
        value[prefix.Length..] is { Length: >= 5 } suffix &&
        suffix.All(character => char.IsAsciiLetterOrDigit(character) ||
                                character is '.' or '-' or '_');

    private static string ValidateRoot(string path)
    {
        if (!OperatingSystem.IsLinux())
            throw Rejected("receipt_root_invalid");
        if (!Path.IsPathFullyQualified(path))
            throw Rejected("receipt_root_invalid");
        var full = Path.GetFullPath(path);
        var info = new DirectoryInfo(full);
        if (!info.Exists || info.LinkTarget is not null || HasLinkInPath(info) ||
            File.GetUnixFileMode(full) !=
                (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
            throw Rejected("receipt_root_invalid");

        var probe = Path.Combine(full, $".owner-probe-{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(probe, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            {
                stream.WriteByte(1);
                stream.Flush(flushToDisk: true);
            }
            _ = SecureSecretFile.ReadBytes(probe, 1, 1, "receipt_root_invalid");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             DatabaseSecurityRejectedException)
        {
            throw Rejected("receipt_root_invalid");
        }
        finally
        {
            if (File.Exists(probe) && new FileInfo(probe).LinkTarget is null) File.Delete(probe);
        }
        return full;
    }

    private static void ValidateReceiptDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) throw Rejected("receipt_path_invalid");
        var info = new DirectoryInfo(path);
        if (!info.Exists || info.LinkTarget is not null || HasLinkInPath(info) ||
            File.GetUnixFileMode(path) !=
                (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
            throw Rejected("receipt_path_invalid");
    }

    private static bool HasLinkInPath(DirectoryInfo directory)
    {
        for (var current = directory; current is not null; current = current.Parent)
            if (current.LinkTarget is not null || current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return true;
        return false;
    }

    private static void ValidateReceiptInventory(
        string directory,
        bool requireComplete,
        CancellationToken cancellationToken)
    {
        if (!ReceiptInventoryMatches(directory, requireComplete, cancellationToken))
            throw Rejected("receipt_inventory_invalid");
    }

    private static bool ReceiptInventoryMatches(
        string directory,
        bool requireComplete,
        CancellationToken cancellationToken)
    {
        var remaining = new HashSet<string>([ReceiptFile, SignatureFile], StringComparer.Ordinal);
        var entryCount = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     directory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entryCount > 2)
                return false;
            var attributes = File.GetAttributes(path);
            var info = new FileInfo(path);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint) ||
                info.LinkTarget is not null || !remaining.Remove(info.Name))
                return false;
        }
        return !requireComplete || remaining.Count == 0;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) throw Rejected("receipt_path_invalid");
        Directory.CreateDirectory(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task WriteNewPrivateFileAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw Rejected("receipt_path_invalid");
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<byte[]> ReadPrivateReceiptFileAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw Rejected("receipt_file_invalid");
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length is < 1 ||
            info.Length > maximumBytes || File.GetUnixFileMode(path) is not
                (UnixFileMode.UserRead or (UnixFileMode.UserRead | UnixFileMode.UserWrite)))
            throw Rejected("receipt_file_invalid");
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[checked((int)info.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.ReadByte() != -1 || new FileInfo(path).Length != info.Length)
            throw Rejected("receipt_file_changed");
        return bytes;
    }

    private static RepairRejectedException Rejected(string code) =>
        new(code, RepairExitCodes.ReceiptFailure);
}
