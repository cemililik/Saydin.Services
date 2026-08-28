using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Saydin.DataQualityAudit;

internal static class EvidenceBundle
{
    internal const int MaximumInventoryDepth = 16;
    internal const int MaximumInventoryDirectories = AuditFileLimits.EvidenceFileCount;
    private const string ContentFile = "evidence-content.json";
    private const string RepairFile = "repair-recommendations.json";
    private const string ManifestFile = "manifest.json";
    private const string ManifestHashFile = "manifest.sha256";
    private const string SignatureFile = "manifest.sig";
    private const string IncompleteFile = ".incomplete";

    public static async Task<EvidenceManifest> WriteAsync(
        string directory,
        EvidenceContent content,
        string keyId,
        string privateKeyPath,
        long maxBytes,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken,
        Func<string, string, CancellationToken, Task>? beforePublish = null)
    {
        await using var signer = new LocalPemEvidenceSigner(privateKeyPath);
        return await WriteAsync(
            directory, content, keyId, signer, maxBytes, createdAtUtc,
            cancellationToken, beforePublish);
    }

    public static async Task<EvidenceManifest> WriteAsync(
        string directory,
        EvidenceContent content,
        string keyId,
        IEvidenceSigner signer,
        long maxBytes,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken,
        Func<string, string, CancellationToken, Task>? beforePublish = null)
    {
        if (!string.Equals(keyId, signer.Identity.EvidenceKeyId, StringComparison.Ordinal))
            throw new AuditRejectedException("evidence_key_id_mismatch", AuditExitCodes.EvidenceFailure);
        EnsureOutputAbsentAndParentSafe(directory);
        var fileHashes = new List<EvidenceFileHash>();
        var payloads = new List<(string Path, byte[] Bytes)>();

        var contentBytes = CanonicalJson.SerializeCanonical(
            content, AuditJsonContext.Default.EvidenceContent);
        AddEvidenceFile(ContentFile, contentBytes);

        var orderedRecommendations = content.RepairRecommendations
            .OrderBy(item => item.CheckId, StringComparer.Ordinal)
            .ThenBy(item => item.BusinessKeyHmac, StringComparer.Ordinal)
            .ToArray();
        var repairJson = JsonSerializer.SerializeToUtf8Bytes(
            orderedRecommendations, AuditJsonContext.Default.RepairRecommendationArray);
        var repairBytes = CanonicalJson.Canonicalize(repairJson);
        AddEvidenceFile(RepairFile, repairBytes);

        foreach (var check in content.Checks.OrderBy(check => check.CheckId, StringComparer.Ordinal))
        {
            var csv = BuildCsv(check);
            AddEvidenceFile($"checks/{check.CheckId.ToLowerInvariant()}.csv",
                Encoding.UTF8.GetBytes(csv));
        }

        var manifest = new EvidenceManifest(
            2,
            "ECDSA-SHA256-RFC3279-DER",
            signer.Identity.Provider,
            signer.Identity.KeyIdentity,
            keyId,
            createdAtUtc.ToUniversalTime(),
            AuditCryptography.Sha256Hex(contentBytes),
            fileHashes.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray());
        var manifestBytes = CanonicalJson.SerializeCanonical(
            manifest, AuditJsonContext.Default.EvidenceManifest);
        var signature = await signer.SignAsync(manifestBytes, cancellationToken);
        var manifestHashBytes = Encoding.ASCII.GetBytes(
            AuditCryptography.Sha256Hex(manifestBytes) + "\n");
        payloads.Add((ManifestFile, manifestBytes));
        payloads.Add((ManifestHashFile, manifestHashBytes));
        payloads.Add((SignatureFile, signature));
        long totalBytes = 0;
        foreach (var payload in payloads)
        {
            if (payload.Bytes.LongLength > maxBytes - totalBytes)
                throw new AuditRejectedException(
                    "evidence_size_budget_exceeded", AuditExitCodes.BudgetRejected);
            totalBytes += payload.Bytes.LongLength;
        }

        var fullOutput = Path.GetFullPath(directory);
        var parent = Path.GetDirectoryName(fullOutput)
            ?? throw new AuditRejectedException("evidence_output_parent_invalid", AuditExitCodes.InvalidArguments);
        var staging = Path.Combine(parent,
            $".{Path.GetFileName(fullOutput)}.staging-{Guid.NewGuid():N}");
        try
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(staging);
            else
                Directory.CreateDirectory(staging,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            RejectLinkTraversal(staging);
            await WriteNewFileAsync(
                Path.Combine(staging, IncompleteFile), "incomplete\n"u8.ToArray(), cancellationToken);
            foreach (var payload in payloads)
            {
                var path = ResolveContainedPath(staging, payload.Path);
                var payloadDirectory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(payloadDirectory);
                RejectLinkTraversal(payloadDirectory);
                await WriteNewFileAsync(path, payload.Bytes, cancellationToken);
            }
            if (beforePublish is not null)
                await beforePublish(staging, fullOutput, cancellationToken);
            File.Delete(Path.Combine(staging, IncompleteFile));
            EnsureOutputAbsentAndParentSafe(fullOutput);
            Directory.Move(staging, fullOutput);
        }
        catch
        {
            if (Directory.Exists(staging) && !HasLinkInPath(staging))
                Directory.Delete(staging, recursive: true);
            throw;
        }
        return manifest;

        void AddEvidenceFile(string path, byte[] bytes)
        {
            payloads.Add((path, bytes));
            fileHashes.Add(new EvidenceFileHash(
                path.Replace('\\', '/'), bytes.LongLength, AuditCryptography.Sha256Hex(bytes)));
        }
    }

    public static async Task<bool> VerifyAsync(
        string directory,
        string publicKeyPath,
        CancellationToken cancellationToken) =>
        (await VerifyDetailedAsync(directory, publicKeyPath, cancellationToken)).IsValid;

    public static async Task<EvidenceVerificationResult> VerifyDetailedAsync(
        string directory,
        string publicKeyPath,
        CancellationToken cancellationToken)
    {
        var phase = "evidence_bundle_unreadable";
        try
        {
            if (File.Exists(Path.Combine(directory, IncompleteFile)))
                return Invalid("evidence_bundle_incomplete");
            if (HasLinkInPath(directory))
                return Invalid("evidence_link_traversal");
            phase = "evidence_manifest_unreadable";
            var manifestPath = Path.Combine(directory, ManifestFile);
            var manifestBytes = await ReadBoundedFileAsync(
                manifestPath, AuditFileLimits.EvidenceManifestBytes, cancellationToken);
            var canonical = CanonicalJson.Canonicalize(manifestBytes);
            if (!canonical.AsSpan().SequenceEqual(manifestBytes))
                return Invalid("evidence_manifest_noncanonical");
            phase = "evidence_signature_unreadable";
            var signature = await ReadBoundedFileAsync(
                Path.Combine(directory, SignatureFile),
                AuditFileLimits.DetachedSignatureBytes,
                cancellationToken);
            if (!AuditCryptography.Verify(canonical, signature, publicKeyPath))
                return Invalid("evidence_signature_invalid");
            phase = "evidence_manifest_hash_unreadable";
            var expectedManifestHash = Encoding.ASCII.GetString(await ReadBoundedFileAsync(
                Path.Combine(directory, ManifestHashFile),
                AuditFileLimits.EvidenceManifestHashBytes,
                cancellationToken)).Trim();
            var manifestHashLength = new FileInfo(Path.Combine(directory, ManifestHashFile)).Length;
            if (!string.Equals(expectedManifestHash, AuditCryptography.Sha256Hex(canonical),
                    StringComparison.Ordinal))
                return Invalid("evidence_manifest_hash_invalid");

            phase = "evidence_manifest_contract_invalid";
            var manifest = JsonSerializer.Deserialize(
                canonical, AuditJsonContext.Default.EvidenceManifest);
            if (manifest is null || manifest.SchemaVersion != 2 ||
                manifest.SignatureAlgorithm != "ECDSA-SHA256-RFC3279-DER" ||
                manifest.SigningProvider is not ("local-pem" or "oci-kms-instance-principal") ||
                manifest.SigningKeyIdentity is not { Length: >= 16 and <= 768 } ||
                manifest.SigningKeyIdentity.Any(char.IsWhiteSpace) ||
                !IsKeyId(manifest.KeyId) || !IsSha256(manifest.ContentBundleSha256) ||
                !IsSigningIdentity(manifest) ||
                manifest.Files is null ||
                manifest.Files.Count > AuditFileLimits.EvidenceFileCount ||
                manifest.Files.Any(file => file is null ||
                    file.Path is not { Length: >= 1 and <= 512 } ||
                    file.Path.Contains('\\', StringComparison.Ordinal) ||
                    !IsSha256(file.Sha256)) ||
                manifest.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count()
                    != manifest.Files.Count)
                return Invalid("evidence_manifest_contract_invalid");
            if (!string.Equals(manifest.KeyId, AuditCryptography.PublicKeyId(publicKeyPath),
                    StringComparison.Ordinal))
                return Invalid("evidence_key_identity_mismatch");
            var expectedFiles = manifest.Files.Select(file => file.Path)
                .Append(ManifestFile).Append(ManifestHashFile).Append(SignatureFile)
                .ToHashSet(StringComparer.Ordinal);
            if (!InventoryMatches(directory, expectedFiles, cancellationToken))
                return Invalid("evidence_inventory_invalid");
            long declaredBytes = 0;
            foreach (var file in manifest.Files)
            {
                if (file.Bytes < 0 || file.Bytes > AuditFileLimits.EvidenceBundleBytes ||
                    file.Sha256.Length != 64 || !file.Sha256.All(character =>
                        character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                    declaredBytes > AuditFileLimits.EvidenceBundleBytes - file.Bytes)
                    return Invalid("evidence_size_contract_invalid");
                declaredBytes += file.Bytes;
                phase = "evidence_file_unreadable";
                var path = ResolveContainedPath(directory, file.Path);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != file.Bytes ||
                    !string.Equals(await Sha256FileAsync(path, cancellationToken), file.Sha256,
                        StringComparison.Ordinal))
                    return Invalid("evidence_file_integrity_invalid");
            }

            if (declaredBytes > AuditFileLimits.EvidenceBundleBytes - manifestBytes.LongLength ||
                declaredBytes + manifestBytes.LongLength >
                    AuditFileLimits.EvidenceBundleBytes - signature.LongLength ||
                declaredBytes + manifestBytes.LongLength + signature.LongLength >
                    AuditFileLimits.EvidenceBundleBytes - manifestHashLength)
                return Invalid("evidence_size_contract_invalid");

            var contentEntry = manifest.Files.SingleOrDefault(file => file.Path == ContentFile);
            return InventoryMatches(directory, expectedFiles, cancellationToken) &&
                   contentEntry is not null &&
                   string.Equals(contentEntry.Sha256, manifest.ContentBundleSha256,
                       StringComparison.Ordinal)
                ? Valid()
                : Invalid("evidence_content_binding_invalid");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or JsonException or InvalidOperationException
                                               or AuditRejectedException or ArgumentException)
        {
            return Invalid(phase);
        }
    }

    private static EvidenceVerificationResult Valid() => new(true, "evidence_verified");

    private static EvidenceVerificationResult Invalid(string code) => new(false, code);

    private static void EnsureOutputAbsentAndParentSafe(string directory)
    {
        RejectLinkTraversal(directory);
        if (File.Exists(directory) || Directory.Exists(directory))
            throw new AuditRejectedException(
                "evidence_output_must_be_absent", AuditExitCodes.InvalidArguments);
        var parent = Path.GetDirectoryName(Path.GetFullPath(directory));
        if (parent is null || !Directory.Exists(parent))
            throw new AuditRejectedException(
                "evidence_output_parent_missing", AuditExitCodes.InvalidArguments);
        RejectLinkTraversal(parent);
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("evidence path must be relative");
        var resolvedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!resolved.StartsWith(resolvedRoot, StringComparison.Ordinal))
            throw new InvalidOperationException("evidence path escaped root");
        return resolved;
    }

    private static bool InventoryMatches(
        string directory,
        IReadOnlySet<string> expectedFiles,
        CancellationToken cancellationToken)
    {
        var remaining = new HashSet<string>(expectedFiles, StringComparer.Ordinal);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((Path.GetFullPath(directory), 0));
        var directoryCount = 0;
        var entryCount = 0;
        var maximumEntries = checked(expectedFiles.Count + MaximumInventoryDirectories);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         current.Path, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entryCount > maximumEntries)
                    return false;
                var attributes = File.GetAttributes(path);
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                FileSystemInfo info = isDirectory
                    ? new DirectoryInfo(path)
                    : new FileInfo(path);
                if (info.LinkTarget is not null ||
                    attributes.HasFlag(FileAttributes.ReparsePoint))
                    return false;
                if (isDirectory)
                {
                    if (current.Depth >= MaximumInventoryDepth ||
                        ++directoryCount > MaximumInventoryDirectories)
                        return false;
                    pending.Push((path, current.Depth + 1));
                    continue;
                }

                var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                if (!remaining.Remove(relative))
                    return false;
            }
        }
        return remaining.Count == 0;
    }

    private static bool HasLinkInPath(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null;
             current = current.Parent)
        {
            if (current.LinkTarget is not null ||
                current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return true;
        }
        return false;
    }

    private static void RejectLinkTraversal(string path)
    {
        if (HasLinkInPath(path))
            throw new AuditRejectedException(
                "evidence_output_link_traversal", AuditExitCodes.InvalidArguments);
    }

    private static async Task WriteNewFileAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        RejectLinkTraversal(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 16_384, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            info.Length < 1 || info.Length > maxBytes)
            throw new IOException("bounded evidence file rejected");
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 16_384, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != info.Length || stream.Length > maxBytes)
            throw new IOException("bounded evidence file limit exceeded");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.ReadByte() != -1)
            throw new IOException("bounded evidence file changed while reading");
        return bytes;
    }

    private static async Task<string> Sha256FileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsKeyId(string? value) => IsSha256(value);

    private static bool IsSigningIdentity(EvidenceManifest manifest)
    {
        if (manifest.SigningProvider == "local-pem")
            return string.Equals(
                manifest.SigningKeyIdentity, $"local-pem:{manifest.KeyId}", StringComparison.Ordinal);
        var identities = manifest.SigningKeyIdentity.Split(':', StringSplitOptions.None);
        return identities.Length == 2 &&
               IsKmsOcid(identities[0], "ocid1.key.") &&
               IsKmsOcid(identities[1], "ocid1.keyversion.");
    }

    private static bool IsKmsOcid(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.Length <= 255 &&
        value[prefix.Length..] is { Length: >= 5 } suffix &&
        suffix.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static string BuildCsv(AuditCheckResult check)
    {
        var builder = new StringBuilder("check_id,severity,status,total_count,truncated,business_key_hmac,violation_code\n");
        if (check.Samples.Count == 0)
        {
            builder.Append(check.CheckId).Append(',')
                .Append(check.Severity.ToString().ToLowerInvariant()).Append(',')
                .Append(check.Status).Append(',')
                .Append(check.TotalCount).Append(',')
                .Append(check.Truncated.ToString().ToLowerInvariant()).Append(",,\n");
            return builder.ToString();
        }

        foreach (var sample in check.Samples)
        {
            builder.Append(check.CheckId).Append(',')
                .Append(check.Severity.ToString().ToLowerInvariant()).Append(',')
                .Append(check.Status).Append(',')
                .Append(check.TotalCount).Append(',')
                .Append(check.Truncated.ToString().ToLowerInvariant()).Append(',')
                .Append(sample.BusinessKeyHmac).Append(',')
                .Append(sample.ViolationCode).Append('\n');
        }
        return builder.ToString();
    }
}
