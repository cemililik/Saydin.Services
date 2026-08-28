using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace Saydin.DataQualityAudit.Tests;

public sealed class EvidenceBundleTests
{
    [Fact]
    public async Task Bundle_IsSigned_Verifiable_AndContentHashIsDeterministic()
    {
        using var files = new TestFiles();
        var content = ContentWithCanary("TOP-SECRET-CANARY");
        var firstDirectory = Path.Combine(files.Root, "bundle-1");
        var secondDirectory = Path.Combine(files.Root, "bundle-2");

        var keyId = AuditCryptography.PublicKeyId(files.EvidencePublicKeyPath);
        var first = await EvidenceBundle.WriteAsync(firstDirectory, content, keyId,
            files.EvidencePrivateKeyPath, 1_000_000, DateTimeOffset.Parse("2026-08-18T00:00:00Z"), default);
        var second = await EvidenceBundle.WriteAsync(secondDirectory, content, keyId,
            files.EvidencePrivateKeyPath, 1_000_000, DateTimeOffset.Parse("2026-08-18T00:01:00Z"), default);

        first.ContentBundleSha256.Should().Be(second.ContentBundleSha256);
        (await EvidenceBundle.VerifyAsync(firstDirectory, files.EvidencePublicKeyPath, default)).Should().BeTrue();
        Directory.EnumerateFiles(firstDirectory, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Should().OnlyContain(text => !text.Contains("TOP-SECRET-CANARY", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(firstDirectory).Should().Be(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task Bundle_VerificationRejectsTamperedEvidenceAndSignature()
    {
        using var files = new TestFiles();
        var directory = Path.Combine(files.Root, "bundle");
        await EvidenceBundle.WriteAsync(directory, ContentWithCanary("private"),
            AuditCryptography.PublicKeyId(files.EvidencePublicKeyPath),
            files.EvidencePrivateKeyPath, 1_000_000, DateTimeOffset.UtcNow, default);
        await File.AppendAllTextAsync(Path.Combine(directory, "checks", "dq-001.csv"), "tamper");

        var verification = await EvidenceBundle.VerifyDetailedAsync(
            directory, files.EvidencePublicKeyPath, default);
        verification.Should().Be(new EvidenceVerificationResult(
            false, "evidence_file_integrity_invalid"));
    }

    [Fact]
    public async Task Bundle_IncompleteMarkerIsRealDuringStaging_AndRemovedOnlyAtAtomicPublish()
    {
        using var files = new TestFiles();
        var directory = Path.Combine(files.Root, "marker-lifecycle");
        EvidenceVerificationResult? stagingResult = null;

        await EvidenceBundle.WriteAsync(
            directory, ContentWithCanary("private"), files.EvidenceKeyId,
            files.EvidencePrivateKeyPath, 1_000_000, DateTimeOffset.UtcNow, default,
            async (staging, _, cancellationToken) =>
            {
                File.Exists(Path.Combine(staging, ".incomplete")).Should().BeTrue();
                stagingResult = await EvidenceBundle.VerifyDetailedAsync(
                    staging, files.EvidencePublicKeyPath, cancellationToken);
            });

        stagingResult.Should().Be(new EvidenceVerificationResult(
            false, "evidence_bundle_incomplete"));
        File.Exists(Path.Combine(directory, ".incomplete")).Should().BeFalse();
        (await EvidenceBundle.VerifyDetailedAsync(
            directory, files.EvidencePublicKeyPath, default)).Should()
            .Be(new EvidenceVerificationResult(true, "evidence_verified"));
    }

    [Theory]
    [InlineData("manifest", "evidence_manifest_unreadable")]
    [InlineData("signature", "evidence_signature_unreadable")]
    [InlineData("inventory", "evidence_inventory_invalid")]
    public async Task Bundle_VerificationReturnsDistinctExactPhaseCode(
        string mutation,
        string expectedCode)
    {
        using var files = new TestFiles();
        var directory = Path.Combine(files.Root, $"phase-{mutation}");
        await EvidenceBundle.WriteAsync(
            directory, ContentWithCanary("private"), files.EvidenceKeyId,
            files.EvidencePrivateKeyPath, 1_000_000, DateTimeOffset.UtcNow, default);
        if (mutation == "manifest") File.Delete(Path.Combine(directory, "manifest.json"));
        else if (mutation == "signature") File.Delete(Path.Combine(directory, "manifest.sig"));
        else await File.WriteAllTextAsync(Path.Combine(directory, "unexpected"), "extra");

        var result = await EvidenceBundle.VerifyDetailedAsync(
            directory, files.EvidencePublicKeyPath, default);

        result.Should().Be(new EvidenceVerificationResult(false, expectedCode));
    }

    [Fact]
    public async Task Bundle_KeyIdIsBoundToSigningAndVerificationSpki()
    {
        using var signer = new TestFiles();
        using var other = new TestFiles();
        var directory = Path.Combine(signer.Root, "key-binding");
        var wrongId = AuditCryptography.PublicKeyId(other.EvidencePublicKeyPath);

        var write = () => EvidenceBundle.WriteAsync(directory, ContentWithCanary("private"), wrongId,
            signer.EvidencePrivateKeyPath, 1_000_000, DateTimeOffset.UtcNow, default);
        await write.Should().ThrowAsync<AuditRejectedException>()
            .Where(error => error.Code == "evidence_key_id_mismatch");

        await EvidenceBundle.WriteAsync(directory, ContentWithCanary("private"), signer.EvidenceKeyId,
            signer.EvidencePrivateKeyPath, 1_000_000, DateTimeOffset.UtcNow, default);
        (await EvidenceBundle.VerifyAsync(directory, other.EvidencePublicKeyPath, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Bundle_VerificationRejectsSymlinkAndSigningFailurePublishesNothing()
    {
        using var files = new TestFiles();
        var directory = Path.Combine(files.Root, "bundle");
        await EvidenceBundle.WriteAsync(directory, ContentWithCanary("private"), files.EvidenceKeyId,
            files.PrivateKeyPath, 1_000_000, DateTimeOffset.UtcNow, default);
        File.CreateSymbolicLink(Path.Combine(directory, "linked-evidence"),
            Path.Combine(directory, "evidence-content.json"));
        (await EvidenceBundle.VerifyAsync(directory, files.PublicKeyPath, default)).Should().BeFalse();

        var incomplete = Path.Combine(files.Root, "incomplete");
        var action = () => EvidenceBundle.WriteAsync(incomplete, ContentWithCanary("private"), files.EvidenceKeyId,
            Path.Combine(files.Root, "missing-private.pem"), 1_000_000, DateTimeOffset.UtcNow, default);
        await action.Should().ThrowAsync<AuditRejectedException>()
            .Where(exception => exception.ExitCode == AuditExitCodes.EvidenceFailure);
        Directory.Exists(incomplete).Should().BeFalse();
    }

    [Fact]
    public async Task Bundle_RejectsNonEmptyOutputAndSizeOverflow()
    {
        using var files = new TestFiles();
        var directory = Path.Combine(files.Root, "bundle");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "existing"), "keep");

        var action = () => EvidenceBundle.WriteAsync(directory, ContentWithCanary("private"), files.EvidenceKeyId,
            files.PrivateKeyPath, 1_000_000, DateTimeOffset.UtcNow, default);
        await action.Should().ThrowAsync<AuditRejectedException>()
            .Where(exception => exception.Code == "evidence_output_must_be_absent");

        var tiny = Path.Combine(files.Root, "tiny");
        var sizeAction = () => EvidenceBundle.WriteAsync(tiny, ContentWithCanary("private"), files.EvidenceKeyId,
            files.PrivateKeyPath, 10, DateTimeOffset.UtcNow, default);
        await sizeAction.Should().ThrowAsync<AuditRejectedException>()
            .Where(exception => exception.Code == "evidence_size_budget_exceeded");
        Directory.Exists(tiny).Should().BeFalse();
        Directory.EnumerateDirectories(files.Root, ".tiny.staging-*", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Bundle_WriterRejectsRootAndAncestorSymlinks_WithoutOutsideWrites()
    {
        using var files = new TestFiles();
        var outsideRoot = Path.Combine(files.Root, "outside-root");
        Directory.CreateDirectory(outsideRoot);
        var linkedRoot = Path.Combine(files.Root, "linked-root");
        Directory.CreateSymbolicLink(linkedRoot, outsideRoot);

        var rootAction = () => EvidenceBundle.WriteAsync(
            linkedRoot, ContentWithCanary("private"), files.EvidenceKeyId,
            files.PrivateKeyPath, 1_000_000, DateTimeOffset.UtcNow, default);
        await rootAction.Should().ThrowAsync<AuditRejectedException>()
            .Where(exception => exception.Code == "evidence_output_link_traversal" &&
                                exception.ExitCode == AuditExitCodes.InvalidArguments);
        Directory.EnumerateFileSystemEntries(outsideRoot).Should().BeEmpty();

        var outsideParent = Path.Combine(files.Root, "outside-parent");
        Directory.CreateDirectory(outsideParent);
        var linkedParent = Path.Combine(files.Root, "linked-parent");
        Directory.CreateSymbolicLink(linkedParent, outsideParent);
        var child = Path.Combine(linkedParent, "absent-child");

        var parentAction = () => EvidenceBundle.WriteAsync(
            child, ContentWithCanary("private"), files.EvidenceKeyId,
            files.PrivateKeyPath, 1_000_000, DateTimeOffset.UtcNow, default);
        await parentAction.Should().ThrowAsync<AuditRejectedException>()
            .Where(exception => exception.Code == "evidence_output_link_traversal" &&
                                exception.ExitCode == AuditExitCodes.InvalidArguments);
        Directory.EnumerateFileSystemEntries(outsideParent).Should().BeEmpty();
    }

    [Fact]
    public async Task Bundle_AtomicPublishRejectsFinalPathSymlinkSwapWithoutOutsideWrite()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var files = new TestFiles();
        var outside = Path.Combine(files.Root, "outside-publish");
        Directory.CreateDirectory(outside);
        var final = Path.Combine(files.Root, "atomic-final");

        var action = () => EvidenceBundle.WriteAsync(
            final, ContentWithCanary("private"), files.EvidenceKeyId,
            files.EvidencePrivateKeyPath, 1_000_000, DateTimeOffset.UtcNow, default,
            (_, output, _) =>
            {
                Directory.CreateSymbolicLink(output, outside);
                return Task.CompletedTask;
            });

        await action.Should().ThrowAsync<AuditRejectedException>()
            .Where(error => error.Code == "evidence_output_link_traversal");
        Directory.EnumerateFileSystemEntries(outside).Should().BeEmpty();
        Directory.EnumerateDirectories(files.Root, ".atomic-final.staging-*", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();
        Directory.Delete(final);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bundle_VerificationRejectsValidlySignedUnknownRootOrNestedMember(bool nested)
    {
        using var files = new TestFiles();
        var directory = Path.Combine(files.Root, $"unknown-{nested}");
        await EvidenceBundle.WriteAsync(directory, ContentWithCanary("private"), files.EvidenceKeyId,
            files.PrivateKeyPath, 1_000_000, DateTimeOffset.UtcNow, default);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory, "manifest.json")))!.AsObject();
        if (nested)
            node["files"]!.AsArray()[0]!.AsObject()["bytesTypo"] = 1;
        else
            node["unknownRoot"] = true;
        await RewriteSignedManifestAsync(directory, node, files.PrivateKeyPath);

        (await EvidenceBundle.VerifyAsync(directory, files.PublicKeyPath, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Bundle_VerificationRejectsDeclaredFileBeyondHardCapBeforeReadingPayload()
    {
        using var files = new TestFiles();
        var directory = Path.Combine(files.Root, "oversized-declaration");
        await EvidenceBundle.WriteAsync(directory, ContentWithCanary("private"), files.EvidenceKeyId,
            files.PrivateKeyPath, 1_000_000, DateTimeOffset.UtcNow, default);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory, "manifest.json")))!.AsObject();
        node["files"]!.AsArray()[0]!.AsObject()["bytes"] = AuditFileLimits.EvidenceBundleBytes + 1;
        await RewriteSignedManifestAsync(directory, node, files.PrivateKeyPath);

        (await EvidenceBundle.VerifyAsync(directory, files.PublicKeyPath, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Bundle_VerificationRejectsInventoryDirectoryCapPlusOne()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var files = new TestFiles();
        var directory = Path.Combine(files.Root, "inventory-cap");
        await EvidenceBundle.WriteAsync(directory, ContentWithCanary("private"), files.EvidenceKeyId,
            files.PrivateKeyPath, 1_000_000, DateTimeOffset.UtcNow, default);

        // The valid bundle already contains its checks directory, so these additions are cap + 1.
        for (var index = 0; index < EvidenceBundle.MaximumInventoryDirectories; index++)
            Directory.CreateDirectory(Path.Combine(directory, $"extra-{index:D4}"));

        (await EvidenceBundle.VerifyAsync(directory, files.PublicKeyPath, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Bundle_VerificationHonorsCancellation()
    {
        using var files = new TestFiles();
        var directory = Path.Combine(files.Root, "inventory-cancel");
        await EvidenceBundle.WriteAsync(directory, ContentWithCanary("private"), files.EvidenceKeyId,
            files.PrivateKeyPath, 1_000_000, DateTimeOffset.UtcNow, default);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var action = () => EvidenceBundle.VerifyAsync(
            directory, files.PublicKeyPath, cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("files-null")]
    [InlineData("file-null")]
    [InlineData("path-null")]
    [InlineData("hash-null")]
    [InlineData("key-null")]
    [InlineData("content-hash-null")]
    public async Task Bundle_VerificationRejectsSignedNullContractShapes(string shape)
    {
        using var files = new TestFiles();
        var directory = Path.Combine(files.Root, $"null-{shape}");
        await EvidenceBundle.WriteAsync(directory, ContentWithCanary("private"), files.EvidenceKeyId,
            files.PrivateKeyPath, 1_000_000, DateTimeOffset.UtcNow, default);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory, "manifest.json")))!.AsObject();
        switch (shape)
        {
            case "files-null":
                node["files"] = null;
                break;
            case "file-null":
                node["files"]!.AsArray()[0] = null;
                break;
            case "path-null":
                node["files"]!.AsArray()[0]!.AsObject()["path"] = null;
                break;
            case "hash-null":
                node["files"]!.AsArray()[0]!.AsObject()["sha256"] = null;
                break;
            case "key-null":
                node["keyId"] = null;
                break;
            case "content-hash-null":
                node["contentBundleSha256"] = null;
                break;
            default:
                throw new InvalidOperationException(shape);
        }
        await RewriteSignedManifestAsync(directory, node, files.PrivateKeyPath);

        (await EvidenceBundle.VerifyAsync(directory, files.PublicKeyPath, default)).Should().BeFalse();
    }

    [Fact]
    public void Accumulator_TruncatesSamples_ButPreservesTotal_AndAllowlistedRepairOnly()
    {
        var accumulator = new AuditAccumulator(Encoding.UTF8.GetBytes(new string('k', 32)), 2);
        accumulator.Add("DQ-001", AuditSeverity.Critical, "missing", "one");
        accumulator.Add("DQ-001", AuditSeverity.Critical, "missing", "two");
        accumulator.Add("DQ-001", AuditSeverity.Critical, "missing", "three");

        var check = accumulator.Build().Single();
        check.TotalCount.Should().Be(3);
        check.Samples.Should().HaveCount(2);
        check.Truncated.Should().BeTrue();
        accumulator.BuildRecommendations().Should().OnlyContain(item => item.Action == "requeue");
        accumulator.BuildRecommendations().Should().OnlyContain(item => item.PreimageSha256 == null);
    }

    private static EvidenceContent ContentWithCanary(string canary)
    {
        var accumulator = new AuditAccumulator(Encoding.UTF8.GetBytes(new string('h', 32)), 2);
        accumulator.Add("DQ-001", AuditSeverity.Critical, "missing_expected_observation", canary);
        return new EvidenceContent(
            1, "test", new string('a', 64), new string('b', 64), new string('c', 64),
            accumulator.Build(), accumulator.BuildRecommendations());
    }

    private static async Task RewriteSignedManifestAsync(
        string directory,
        JsonObject manifest,
        string privateKeyPath)
    {
        var canonical = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), canonical);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "manifest.sha256"),
            AuditCryptography.Sha256Hex(canonical) + "\n",
            Encoding.ASCII);
        await File.WriteAllBytesAsync(
            Path.Combine(directory, "manifest.sig"),
            AuditCryptography.Sign(canonical, privateKeyPath));
    }
}
