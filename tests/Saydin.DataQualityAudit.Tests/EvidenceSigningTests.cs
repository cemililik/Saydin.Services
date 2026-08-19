using System.Security.Cryptography;
using FluentAssertions;

namespace Saydin.DataQualityAudit.Tests;

public sealed class EvidenceSigningTests
{
    private const string KeyId = "ocid1.key.oc1.eu-frankfurt-1.test-key";
    private const string KeyVersionId = "ocid1.keyversion.oc1.eu-frankfurt-1.test-version";
    private const string Endpoint =
        "https://test-vault-crypto.kms.eu-frankfurt-1.oraclecloud.com/";

    [Fact]
    public void OciKmsOptions_PinExactIdentityTimeoutAndBoundedRotationAllowlist()
    {
        using var files = new TestFiles();
        var oldKeyId = new string('a', 64);
        var options = AuditOptions.Parse([
            "scan",
            "--input", "input.json",
            "--input-signature", "input.sig",
            "--input-public-key", "input.pem",
            "--hmac-key-file", "/run/secrets/hmac",
            "--output", "evidence",
            "--signer-mode", "oci-kms-instance-principal",
            "--kms-key-id", KeyId,
            "--kms-key-version-id", KeyVersionId,
            "--kms-crypto-endpoint", Endpoint,
            "--oci-region", "eu-frankfurt-1",
            "--evidence-public-key", files.EvidencePublicKeyPath,
            "--allowed-evidence-key-ids", $"{oldKeyId},{files.EvidenceKeyId}",
            "--kms-timeout-seconds", "7",
        ]).Should().BeOfType<ScanOptions>().Subject;

        options.EvidencePrivateKeyFile.Should().BeNull();
        var kms = options.EvidenceSigner.Should().BeOfType<OciKmsSignerConfiguration>().Subject;
        kms.KeyId.Should().Be(KeyId);
        kms.KeyVersionId.Should().Be(KeyVersionId);
        kms.CryptoEndpoint.Should().Be(Endpoint);
        kms.Region.Should().Be("eu-frankfurt-1");
        kms.Timeout.Should().Be(TimeSpan.FromSeconds(7));
        kms.AllowedEvidenceKeyIds.Should().BeEquivalentTo(oldKeyId, files.EvidenceKeyId);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("four")]
    [InlineData("uppercase")]
    public void OciKmsOptions_RejectUnboundedOrNonCanonicalRotationAllowlist(string shape)
    {
        using var files = new TestFiles();
        var first = new string('a', 64);
        var allowlist = shape switch
        {
            "duplicate" => $"{first},{first}",
            "four" => string.Join(',', Enumerable.Range(0, 4).Select(index =>
                new string((char)('a' + index), 64))),
            "uppercase" => new string('A', 64),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var action = () => AuditOptions.Parse([
            "scan",
            "--input", "input.json",
            "--input-signature", "input.sig",
            "--input-public-key", "input.pem",
            "--hmac-key-file", "/run/secrets/hmac",
            "--output", "evidence",
            "--signer-mode", "oci-kms-instance-principal",
            "--kms-key-id", KeyId,
            "--kms-key-version-id", KeyVersionId,
            "--kms-crypto-endpoint", Endpoint,
            "--oci-region", "eu-frankfurt-1",
            "--evidence-public-key", files.EvidencePublicKeyPath,
            "--allowed-evidence-key-ids", allowlist,
        ]);

        action.Should().Throw<AuditRejectedException>().Which.Code
            .Should().Be("evidence_key_allowlist_invalid");
    }

    [Theory]
    [InlineData("key_whitespace")]
    [InlineData("version_region_mismatch")]
    [InlineData("generic_kms_host")]
    [InlineData("uppercase_region")]
    public void OciKmsOptions_RejectNoncanonicalOcidRegionOrCryptoEndpoint(string shape)
    {
        using var files = new TestFiles();
        var keyId = KeyId;
        var keyVersionId = KeyVersionId;
        var endpoint = Endpoint;
        var region = "eu-frankfurt-1";
        switch (shape)
        {
            case "key_whitespace":
                keyId = "ocid1.key.oc1.eu-frankfurt-1.test key";
                break;
            case "version_region_mismatch":
                keyVersionId = "ocid1.keyversion.oc1.us-ashburn-1.test-version";
                break;
            case "generic_kms_host":
                endpoint = "https://test-vault.kms.eu-frankfurt-1.oraclecloud.com/";
                break;
            case "uppercase_region":
                region = "EU-frankfurt-1";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
        var action = () => AuditOptions.Parse([
            "scan",
            "--input", "input.json",
            "--input-signature", "input.sig",
            "--input-public-key", "input.pem",
            "--hmac-key-file", "/run/secrets/hmac",
            "--output", "evidence",
            "--signer-mode", "oci-kms-instance-principal",
            "--kms-key-id", keyId,
            "--kms-key-version-id", keyVersionId,
            "--kms-crypto-endpoint", endpoint,
            "--oci-region", region,
            "--evidence-public-key", files.EvidencePublicKeyPath,
            "--allowed-evidence-key-ids", files.EvidenceKeyId,
        ]);

        action.Should().Throw<AuditRejectedException>().Which.Code
            .Should().Be("oci_kms_identity_invalid");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OciKmsSigner_NormalizesDerAndRawP256Signatures_AndPinsManifestIdentity(bool raw)
    {
        using var files = new TestFiles();
        var client = new SigningFakeClient(files.EvidencePrivateKeyPath, raw);
        await using var signer = new OciKmsEvidenceSigner(Options(files), client);
        var directory = Path.Combine(files.Root, "kms-bundle");
        var content = new EvidenceContent(
            1, "test", new string('a', 64), new string('b', 64), new string('c', 64), [], []);

        var manifest = await EvidenceBundle.WriteAsync(
            directory, content, files.EvidenceKeyId, signer, 1_000_000,
            DateTimeOffset.Parse("2026-08-19T00:00:00Z"), default);

        manifest.SchemaVersion.Should().Be(2);
        manifest.SigningProvider.Should().Be("oci-kms-instance-principal");
        manifest.SigningKeyIdentity.Should().Be($"{KeyId}:{KeyVersionId}");
        manifest.KeyId.Should().Be(files.EvidenceKeyId);
        (await EvidenceBundle.VerifyAsync(directory, files.EvidencePublicKeyPath, default))
            .Should().BeTrue();
    }

    [Fact]
    public async Task OciKmsSigner_FailsClosedForResponseTimeoutDenyWrongKeyAndInvalidSignature()
    {
        using var files = new TestFiles();
        await AssertFailureAsync(files, "kms_signature_response_invalid", (_, _, _, _) =>
            Task.FromResult(new OciKmsSignatureResponse(
                KeyId + "-wrong", KeyVersionId, "EcdsaSha256", Convert.ToBase64String(new byte[64]))));
        await AssertFailureAsync(files, "kms_signature_response_invalid", (_, _, _, _) =>
            Task.FromResult(new OciKmsSignatureResponse(
                KeyId, KeyVersionId + "-wrong", "EcdsaSha256", Convert.ToBase64String(new byte[64]))));
        await AssertFailureAsync(files, "kms_signature_response_invalid", (_, _, _, _) =>
            Task.FromResult(new OciKmsSignatureResponse(
                KeyId, KeyVersionId, "RsaPkcs1Sha256", Convert.ToBase64String(new byte[64]))));
        await AssertFailureAsync(files, "kms_signature_response_invalid", (_, _, _, _) =>
            Task.FromResult(new OciKmsSignatureResponse(
                KeyId, KeyVersionId, "EcdsaSha256", "not-base64!")));
        await AssertFailureAsync(files, "kms_signature_response_invalid", (_, _, _, _) =>
            Task.FromResult(new OciKmsSignatureResponse(
                KeyId, KeyVersionId, "EcdsaSha256",
                Convert.ToBase64String(new byte[64]) + "\n")));
        await AssertFailureAsync(files, "kms_signature_encoding_invalid", (_, _, _, _) =>
            Task.FromResult(new OciKmsSignatureResponse(
                KeyId, KeyVersionId, "EcdsaSha256", Convert.ToBase64String([1, 2, 3]))));
        using (var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            await AssertFailureAsync(files, "kms_signature_verification_failed", (_, _, digest, _) =>
                Task.FromResult(new OciKmsSignatureResponse(
                    KeyId, KeyVersionId, "EcdsaSha256",
                    Convert.ToBase64String(wrongKey.SignHash(
                        digest.Span, DSASignatureFormat.Rfc3279DerSequence)))));
        }
        await AssertFailureAsync(files, "kms_sign_denied", (_, _, _, _) =>
            Task.FromException<OciKmsSignatureResponse>(new UnauthorizedAccessException()));
        await AssertFailureAsync(files, "kms_sign_timeout", async (_, _, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }, TimeSpan.FromMilliseconds(25));
    }

    [Fact]
    public async Task KmsFailure_PublishesNeitherFinalNorStagingBundle()
    {
        using var files = new TestFiles();
        var client = new DelegateFakeClient((_, _, _, _) =>
            Task.FromException<OciKmsSignatureResponse>(new UnauthorizedAccessException()));
        await using var signer = new OciKmsEvidenceSigner(Options(files), client);
        var directory = Path.Combine(files.Root, "failed-kms-bundle");
        var content = new EvidenceContent(
            1, "test", new string('a', 64), new string('b', 64), new string('c', 64), [], []);

        var action = async () => await EvidenceBundle.WriteAsync(
            directory, content, files.EvidenceKeyId, signer, 1_000_000,
            DateTimeOffset.Parse("2026-08-19T00:00:00Z"), default);

        (await action.Should().ThrowAsync<AuditRejectedException>()).Which
            .Should().Match<AuditRejectedException>(exception =>
                exception.Code == "kms_sign_denied" &&
                exception.ExitCode == AuditExitCodes.EvidenceFailure);
        Directory.Exists(directory).Should().BeFalse();
        Directory.EnumerateFileSystemEntries(files.Root, ".failed-kms-bundle.staging-*")
            .Should().BeEmpty();
    }

    [Fact]
    public void Production_RejectsLocalSignerBeforeReadingPrivateKey()
    {
        using var files = new TestFiles();
        var scan = LocalScan(files) with
        {
            EvidencePrivateKeyFile = "/must/not/be/read/private.pem",
            EvidenceSigner = new LocalPemSignerConfiguration("/must/not/be/read/private.pem"),
        };

        var action = () => EvidenceSignerFactory.Create(
            scan, Input(files, "production"), _ => null);

        action.Should().Throw<AuditRejectedException>().Which
            .Should().Match<AuditRejectedException>(exception =>
                exception.Code == "production_signer_mode_rejected" &&
                exception.ExitCode == AuditExitCodes.InvalidArguments);
    }

    [Theory]
    [InlineData("SAYDIN_DQA_EVIDENCE_PRIVATE_KEY")]
    [InlineData("SAYDIN_DQA_EVIDENCE_PRIVATE_KEY_FILE")]
    [InlineData("EVIDENCE_PRIVATE_KEY")]
    [InlineData("OCI_CONFIG_FILE")]
    [InlineData("OCI_CLI_KEY_FILE")]
    [InlineData("OCI_CLI_PROFILE")]
    [InlineData("OCI_CLI_AUTH")]
    [InlineData("OCI_RESOURCE_PRINCIPAL_PRIVATE_PEM")]
    [InlineData("OCI_RESOURCE_PRINCIPAL_PRIVATE_PEM_PASSPHRASE")]
    [InlineData("OCI_SDK_DEFAULT_RETRY_ENABLED")]
    public void Production_RejectsRawKeyOrAlternateCredentialEnvironmentBeforeClientCreation(
        string variable)
    {
        using var files = new TestFiles();
        var created = false;
        var action = () => EvidenceSignerFactory.Create(
            KmsScan(files), Input(files, "production"),
            name => name == variable ? "configured-even-if-empty-or-indirect" : null,
            _ =>
            {
                created = true;
                return new DelegateFakeClient((_, _, _, _) => throw new InvalidOperationException());
            });

        action.Should().Throw<AuditRejectedException>().Which.Code
            .Should().Be("production_private_key_environment_rejected");
        created.Should().BeFalse();
    }

    [Fact]
    public void Production_RejectsPrivatePemMasqueradingAsPublicKey_AndDisposesClient()
    {
        using var files = new TestFiles();
        var privateKeyId = AuditCryptography.PrivateKeyId(files.EvidencePrivateKeyPath);
        var configured = Options(files) with
        {
            PublicKeyFile = files.EvidencePrivateKeyPath,
            AllowedEvidenceKeyIds = new HashSet<string>(StringComparer.Ordinal) { privateKeyId },
        };
        var client = new DelegateFakeClient((_, _, _, _) => throw new InvalidOperationException());
        var scan = KmsScan(files) with { EvidenceSigner = configured };

        var action = () => EvidenceSignerFactory.Create(
            scan, Input(files, "production"), _ => null, _ => client);

        action.Should().Throw<AuditRejectedException>().Which
            .Should().Match<AuditRejectedException>(exception =>
                exception.Code == "evidence_public_key_invalid" &&
                exception.ExitCode == AuditExitCodes.EvidenceFailure);
        client.Disposed.Should().BeTrue();
    }

    private static async Task AssertFailureAsync(
        TestFiles files,
        string code,
        Func<string, string, ReadOnlyMemory<byte>, CancellationToken,
            Task<OciKmsSignatureResponse>> handler,
        TimeSpan? timeout = null)
    {
        var client = new DelegateFakeClient(handler);
        await using var signer = new OciKmsEvidenceSigner(
            Options(files, timeout ?? TimeSpan.FromSeconds(1)), client);

        var action = async () => await signer.SignAsync("manifest"u8.ToArray(), default);

        (await action.Should().ThrowAsync<AuditRejectedException>()).Which
            .Should().Match<AuditRejectedException>(exception =>
                exception.Code == code && exception.ExitCode == AuditExitCodes.EvidenceFailure);
    }

    private static OciKmsSignerConfiguration Options(
        TestFiles files,
        TimeSpan? timeout = null) => new(
        KeyId,
        KeyVersionId,
        Endpoint,
        "eu-frankfurt-1",
        files.EvidencePublicKeyPath,
        new HashSet<string>(StringComparer.Ordinal) { files.EvidenceKeyId },
        timeout ?? TimeSpan.FromSeconds(1));

    private static ScanOptions LocalScan(TestFiles files) => new(
        "input", "signature", files.InputPublicKeyPath,
        files.EvidencePrivateKeyPath, files.HmacKeyPath, "output",
        new LocalPemSignerConfiguration(files.EvidencePrivateKeyPath));

    private static ScanOptions KmsScan(TestFiles files) => LocalScan(files) with
    {
        EvidencePrivateKeyFile = null,
        EvidenceSigner = Options(files),
    };

    private static VerifiedAuditInput Input(TestFiles files, string environment) => new(
        files.ValidManifest() with
        {
            EvidenceKeyId = files.EvidenceKeyId,
            Target = files.ValidManifest().Target with { Environment = environment },
        },
        new string('b', 64));

    private sealed class DelegateFakeClient(
        Func<string, string, ReadOnlyMemory<byte>, CancellationToken,
            Task<OciKmsSignatureResponse>> handler) : IOciKmsSigningClient
    {
        public bool Disposed { get; private set; }

        public Task<OciKmsSignatureResponse> SignDigestAsync(
            string keyId,
            string keyVersionId,
            ReadOnlyMemory<byte> sha256Digest,
            CancellationToken cancellationToken) =>
            handler(keyId, keyVersionId, sha256Digest, cancellationToken);

        public void Dispose() => Disposed = true;
    }

    private sealed class SigningFakeClient : IOciKmsSigningClient
    {
        private readonly ECDsa key = ECDsa.Create();
        private readonly bool raw;

        public SigningFakeClient(string privateKeyFile, bool raw)
        {
            key.ImportFromPem(File.ReadAllText(privateKeyFile));
            this.raw = raw;
        }

        public Task<OciKmsSignatureResponse> SignDigestAsync(
            string keyId,
            string keyVersionId,
            ReadOnlyMemory<byte> sha256Digest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = key.SignHash(
                sha256Digest.Span,
                raw
                    ? DSASignatureFormat.IeeeP1363FixedFieldConcatenation
                    : DSASignatureFormat.Rfc3279DerSequence);
            return Task.FromResult(new OciKmsSignatureResponse(
                keyId, keyVersionId, "EcdsaSha256", Convert.ToBase64String(signature)));
        }

        public void Dispose() => key.Dispose();
    }
}
