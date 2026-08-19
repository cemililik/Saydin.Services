using System.Security.Cryptography;
using FluentAssertions;

namespace Saydin.DataRepair.Tests;

public sealed class OptionsAndReceiptTests
{
    [Fact]
    public void OptionOnlyInvocation_DefaultsToDryRunAndRequiresAuditIdentity()
    {
        using var files = new RepairTestFiles();
        var options = RepairOptions.Parse(files.CommonArguments());
        options.Mode.Should().Be(RepairMode.DryRun);
        options.AuditLogin.Should().EndWith("_audit_login_v1");

        var missingAudit = files.CommonArguments()[..^4];
        var action = () => RepairOptions.Parse(missingAudit);
        action.Should().Throw<RepairRejectedException>();
    }

    [Fact]
    public void ApprovalTokenMustMatchSignedHash()
    {
        using var files = new RepairTestFiles();
        RepairFiles.ValidateApprovalToken(
            files.ApprovalTokenFile, files.Plan.ApprovalTokenSha256);
        RepairTestFiles.WritePrivate(files.ApprovalTokenFile, new byte[48]);
        var action = () => RepairFiles.ValidateApprovalToken(
            files.ApprovalTokenFile, files.Plan.ApprovalTokenSha256);
        action.Should().Throw<RepairRejectedException>()
            .Which.Code.Should().Be("approval_token_invalid");
    }

    [Fact]
    public async Task ReceiptIsPrivateCanonicalSignedAndAtomic()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var files = new RepairTestFiles();
        await using var signer = new LocalReceiptSigner(files.ReceiptPrivateKeyFile);
        var store = new ReceiptStore(files.ReceiptRoot);
        var receipt = new RepairReceipt(
            1, "ECDSA-SHA256-RFC3279-DER", signer.Identity.Provider,
            signer.Identity.KeyIdentity, signer.Identity.KeyId, "apply",
            new string('1', 64), new string('2', 64), new string('3', 64),
            EmbeddedRepairMigrationTrust.ManifestSha256,
            files.EvidenceContentSha256, files.Plan.Evidence.SignerKeyId,
            null, 42, files.Now,
            [new RepairOperationReceipt(
                0, "work_order_manual_review", null, null, null, null)]);
        var staged = await store.StageAsync(receipt, signer, default);
        Directory.Exists(staged.Directory).Should().BeTrue();
        Directory.Exists(store.FinalPath(receipt.NonceSha256, "apply")).Should().BeFalse();
        store.Promote(receipt.NonceSha256, "apply");
        var verified = await store.ReadFinalAsync(
            receipt.NonceSha256, "apply", signer.Identity.PublicSubjectPublicKeyInfo, default);
        verified.ReceiptSha256.Should().Be(staged.ReceiptSha256);
        File.GetUnixFileMode(verified.Directory).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.EnumerateFiles(verified.Directory).Should().OnlyContain(path =>
            IsPrivateFile(path));
    }

    [Fact]
    public async Task ReceiptInventoryRejectsTheFirstEntryBeyondItsExactCap()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var files = new RepairTestFiles();
        await using var signer = new LocalReceiptSigner(files.ReceiptPrivateKeyFile);
        var store = new ReceiptStore(files.ReceiptRoot);
        var receipt = new RepairReceipt(
            1, "ECDSA-SHA256-RFC3279-DER", signer.Identity.Provider,
            signer.Identity.KeyIdentity, signer.Identity.KeyId, "apply",
            new string('1', 64), new string('2', 64), new string('3', 64),
            EmbeddedRepairMigrationTrust.ManifestSha256,
            files.EvidenceContentSha256, files.Plan.Evidence.SignerKeyId,
            null, 42, files.Now,
            [new RepairOperationReceipt(
                0, "work_order_manual_review", null, null, null, null)]);
        var staged = await store.StageAsync(receipt, signer, default);
        RepairTestFiles.WritePrivate(Path.Combine(staged.Directory, "third-entry"), "unexpected");

        var action = () => store.Promote(receipt.NonceSha256, receipt.Mode);

        action.Should().Throw<RepairRejectedException>()
            .Which.Code.Should().Be("receipt_inventory_invalid");
    }

    [Fact]
    public async Task ApplicationErrorOutputDoesNotEchoPathsOrSecretMaterial()
    {
        using var files = new RepairTestFiles();
        RepairTestFiles.WritePrivate(files.PlanSignatureFile, new byte[64]);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await RepairApplication.RunAsync(
            files.CommonArguments(), output, error,
            new FixedTimeProvider(files.Now), _ => null);
        exit.Should().Be(RepairExitCodes.SignatureFailure);
        error.ToString().Should().Be("repair rejected: code=plan_signature_invalid\n");
        error.ToString().Should().NotContain(files.Root)
            .And.NotContain(Convert.ToHexString(files.ApprovalToken));
    }

    [Fact]
    public async Task ProductionReceiptSigningIsKmsOnlyAndNormalizesRawP256Signatures()
    {
        using var files = new RepairTestFiles();
        var target = files.Plan.Target with { Environment = "production" };
        var local = () => ReceiptSignerFactory.Create(
            new LocalReceiptSignerConfiguration(files.ReceiptPrivateKeyFile),
            target, files.ReceiptKeyId, _ => null);
        local.Should().Throw<RepairRejectedException>()
            .Which.Code.Should().Be("production_signer_mode_rejected");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Path.Combine(files.InputDirectory, "kms-public.pem");
        RepairTestFiles.WritePrivate(publicKey, key.ExportSubjectPublicKeyInfoPem());
        var keyId = RepairCryptography.Sha256Hex(key.ExportSubjectPublicKeyInfo());
        var configuration = new OciKmsReceiptSignerConfiguration(
            "ocid1.key.oc1.eu-frankfurt-1.repairtest",
            "ocid1.keyversion.oc1.eu-frankfurt-1.repairtest",
            "https://repairtest-crypto.kms.eu-frankfurt-1.oraclecloud.com/",
            "eu-frankfurt-1", publicKey, TimeSpan.FromSeconds(1));
        await using var signer = ReceiptSignerFactory.Create(
            configuration, target, keyId, _ => null, _ => new RawP256KmsClient(key));
        var payload = "signed-repair-receipt"u8.ToArray();
        var signature = await signer.SignAsync(payload, default);
        signature.Should().NotHaveCount(64);
        RepairCryptography.Verify(payload, signature, key.ExportSubjectPublicKeyInfo())
            .Should().BeTrue();

        var leakedEnvironment = () => ReceiptSignerFactory.Create(
            configuration, target, keyId,
            name => name == "SAYDIN_REPAIR_RECEIPT_PRIVATE_KEY_FILE" ? "/secret" : null,
            _ => new RawP256KmsClient(key));
        leakedEnvironment.Should().Throw<RepairRejectedException>()
            .Which.Code.Should().Be("production_private_key_environment_rejected");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static bool IsPrivateFile(string path)
    {
        if (!OperatingSystem.IsLinux()) return false;
        return File.GetUnixFileMode(path) ==
               (UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed class RawP256KmsClient(ECDsa key) : IKmsSigningClient
    {
        public Task<KmsSignatureResponse> SignDigestAsync(
            string keyId,
            string keyVersionId,
            ReadOnlyMemory<byte> digest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = key.SignHash(
                digest.Span, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return Task.FromResult(new KmsSignatureResponse(
                keyId, keyVersionId, "EcdsaSha256", Convert.ToBase64String(signature)));
        }

        public void Dispose()
        {
        }
    }
}
