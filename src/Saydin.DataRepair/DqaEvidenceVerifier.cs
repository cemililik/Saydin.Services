using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Saydin.DataRepair;

internal static class DqaEvidenceVerifier
{
    internal const int MaximumFiles = 4_096;
    internal const int MaximumInventoryDepth = 16;
    internal const int MaximumInventoryDirectories = MaximumFiles;
    private const long MaxBundleBytes = 256L * 1024 * 1024;

    public static async Task VerifyAsync(
        string directory,
        string publicKeyFile,
        RepairEvidenceBinding expected,
        string targetEnvironment,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = ValidateRoot(directory);
            var publicSpki = RepairCryptography.ReadPublicSpki(publicKeyFile);
            var publicKeyId = RepairCryptography.Sha256Hex(publicSpki);
            if (!RepairCryptography.FixedEquals(publicKeyId, expected.SignerKeyId))
                throw Rejected("evidence_signer_mismatch");

            var manifestBytes = await ReadBoundedAsync(
                Path.Combine(root, "manifest.json"), 1024 * 1024, cancellationToken);
            var canonical = CanonicalJson.Canonicalize(manifestBytes);
            if (!canonical.AsSpan().SequenceEqual(manifestBytes))
                throw Rejected("evidence_manifest_not_canonical");
            var signature = await ReadBoundedAsync(
                Path.Combine(root, "manifest.sig"), 4 * 1024, cancellationToken);
            if (!RepairCryptography.Verify(canonical, signature, publicSpki))
                throw Rejected("evidence_signature_invalid");

            var manifest = JsonSerializer.Deserialize(
                    canonical, RepairJsonContext.Default.DqaEvidenceManifest)
                ?? throw Rejected("evidence_manifest_invalid");
            ValidateManifest(manifest, publicKeyId, expected, targetEnvironment);

            var manifestHash = Encoding.ASCII.GetString(await ReadBoundedAsync(
                Path.Combine(root, "manifest.sha256"), 128, cancellationToken)).TrimEnd('\n');
            if (!RepairCryptography.FixedEquals(
                    manifestHash, RepairCryptography.Sha256Hex(canonical)))
                throw Rejected("evidence_manifest_hash_invalid");

            var expectedFiles = manifest.Files.Select(file => file.Path)
                .Append("manifest.json").Append("manifest.sha256").Append("manifest.sig")
                .ToHashSet(StringComparer.Ordinal);
            if (!InventoryMatches(root, expectedFiles, cancellationToken))
                throw Rejected("evidence_inventory_invalid");

            long total = canonical.LongLength + signature.LongLength + manifestHash.Length + 1L;
            foreach (var file in manifest.Files)
            {
                if (file.Bytes < 0 || file.Bytes > MaxBundleBytes ||
                    total > MaxBundleBytes - file.Bytes)
                    throw Rejected("evidence_budget_invalid");
                total += file.Bytes;
                var path = ResolveContained(root, file.Path);
                var info = new FileInfo(path);
                if (!info.Exists || info.LinkTarget is not null || info.Length != file.Bytes ||
                    !RepairCryptography.FixedEquals(
                        await Sha256FileAsync(path, cancellationToken), file.Sha256))
                    throw Rejected("evidence_file_invalid");
            }
            if (!InventoryMatches(root, expectedFiles, cancellationToken))
                throw Rejected("evidence_inventory_invalid");
            var content = manifest.Files.SingleOrDefault(file => file.Path == "evidence-content.json");
            if (content is null || !RepairCryptography.FixedEquals(
                    content.Sha256, expected.ContentSha256))
                throw Rejected("evidence_content_binding_invalid");
        }
        catch (RepairRejectedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             JsonException or InvalidOperationException or ArgumentException)
        {
            throw Rejected("evidence_bundle_invalid");
        }
    }

    private static void ValidateManifest(
        DqaEvidenceManifest manifest,
        string publicKeyId,
        RepairEvidenceBinding expected,
        string targetEnvironment)
    {
        if (manifest.SchemaVersion != 2 ||
            manifest.SignatureAlgorithm != "ECDSA-SHA256-RFC3279-DER" ||
            manifest.SigningProvider is not ("local-pem" or "oci-kms-instance-principal") ||
            targetEnvironment == "production" &&
                manifest.SigningProvider != "oci-kms-instance-principal" ||
            !IsSigningIdentity(manifest) ||
            !RepairCryptography.IsSha256(manifest.KeyId) ||
            !RepairCryptography.IsSha256(manifest.ContentBundleSha256) ||
            !RepairCryptography.FixedEquals(manifest.KeyId, publicKeyId) ||
            !RepairCryptography.FixedEquals(manifest.KeyId, expected.SignerKeyId) ||
            !RepairCryptography.FixedEquals(manifest.ContentBundleSha256, expected.ContentSha256) ||
            manifest.Files is null || manifest.Files.Count is < 1 or > MaximumFiles ||
            manifest.Files.Any(file => file is null || file.Path is not { Length: >= 1 and <= 512 } ||
                file.Path.Contains('\\') || !RepairCryptography.IsSha256(file.Sha256)) ||
            manifest.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count()
                != manifest.Files.Count)
            throw Rejected("evidence_manifest_invalid");
    }

    private static bool IsSigningIdentity(DqaEvidenceManifest manifest)
    {
        if (manifest.SigningProvider == "local-pem")
            return manifest.SigningKeyIdentity == $"local-pem:{manifest.KeyId}";
        var identities = manifest.SigningKeyIdentity.Split(':', StringSplitOptions.None);
        return identities.Length == 2 &&
               IsKmsOcid(identities[0], "ocid1.key.") &&
               IsKmsOcid(identities[1], "ocid1.keyversion.");
    }

    private static bool IsKmsOcid(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) && value.Length <= 255 &&
        value[prefix.Length..] is { Length: >= 5 } suffix &&
        suffix.All(character => char.IsAsciiLetterOrDigit(character) ||
                                character is '.' or '-' or '_');

    private static string ValidateRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw Rejected("evidence_path_invalid");
        var full = Path.GetFullPath(path);
        var info = new DirectoryInfo(full);
        if (!info.Exists || info.LinkTarget is not null || HasLinkInPath(info))
            throw Rejected("evidence_path_invalid");
        if (!string.Equals(info.FullName, full, StringComparison.Ordinal))
            throw Rejected("evidence_path_invalid");
        return full;
    }

    private static bool InventoryMatches(
        string root,
        IReadOnlySet<string> expectedFiles,
        CancellationToken cancellationToken)
    {
        var remaining = new HashSet<string>(expectedFiles, StringComparer.Ordinal);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
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

                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!remaining.Remove(relative))
                    return false;
            }
        }
        return remaining.Count == 0;
    }

    private static string ResolveContained(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) ||
            relative.Contains("//", StringComparison.Ordinal) ||
            relative.Split('/').Any(part => part is "" or "." or ".."))
            throw Rejected("evidence_path_invalid");
        var prefix = root + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relative));
        if (!resolved.StartsWith(prefix, StringComparison.Ordinal))
            throw Rejected("evidence_path_invalid");
        return resolved;
    }

    private static bool HasLinkInPath(DirectoryInfo directory)
    {
        for (var current = directory; current is not null; current = current.Parent)
            if (current.LinkTarget is not null || current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return true;
        return false;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        int maximum,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            info.Length is < 1 || info.Length > maximum)
            throw Rejected("evidence_file_invalid");
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[checked((int)info.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.ReadByte() != -1 || new FileInfo(path).Length != info.Length)
            throw Rejected("evidence_file_changed");
        return bytes;
    }

    private static async Task<string> Sha256FileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static RepairRejectedException Rejected(string code) =>
        new(code, RepairExitCodes.SignatureFailure);
}
