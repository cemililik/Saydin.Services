using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace Saydin.DataQualityAudit.Tests;

public sealed class CanonicalAndSignatureTests
{
    [Fact]
    public void P1363Signature_WithLeadingZeroComponents_NormalizesToCanonicalDer()
    {
        var raw = new byte[64];
        raw[31] = 0x80;
        raw[63] = 0x80;

        var normalized = AuditCryptography.NormalizeP256Signature(raw);

        normalized.Should().Equal(Convert.FromHexString("30080202008002020080"));
    }

    [Fact]
    public void Canonicalize_SortsObjects_AndPreservesArrayOrder()
    {
        var first = CanonicalJson.Canonicalize("""{"z":2,"a":{"y":true,"b":[2,1]}}"""u8);
        var second = CanonicalJson.Canonicalize("""{"a":{"b":[2,1],"y":true},"z":2}"""u8);

        first.Should().Equal(second);
        Encoding.UTF8.GetString(first).Should().Be("{\"a\":{\"b\":[2,1],\"y\":true},\"z\":2}");
    }

    [Fact]
    public void Canonicalize_RejectsNonIntegerNumbers()
    {
        var action = () => CanonicalJson.Canonicalize("""{"n":1.5}"""u8);

        action.Should().Throw<AuditRejectedException>()
            .Where(exception => exception.Code == "manifest_number_not_integer");
    }

    [Fact]
    public void Canonicalize_RejectsDuplicateObjectProperties()
    {
        var action = () => CanonicalJson.Canonicalize("""{"target":1,"target":2}"""u8);

        action.Should().Throw<AuditRejectedException>()
            .Where(exception => exception.Code == "manifest_duplicate_property");
    }

    [Fact]
    public void SignedInput_VerifiesCanonicalPayload_AndRejectsTamper()
    {
        using var files = new TestFiles();
        var manifest = files.ValidManifest();
        var signed = files.WriteSignedInput(manifest);
        var options = new ScanOptions(signed.Manifest, signed.Signature,
            files.InputPublicKeyPath, files.EvidencePrivateKeyPath, files.HmacKeyPath, "unused");

        var verified = SignedAuditInput.LoadAndVerify(options, new FixedTimeProvider(DateTimeOffset.UtcNow));
        verified.Manifest.Should().BeEquivalentTo(manifest);

        var text = File.ReadAllText(signed.Manifest);
        File.WriteAllText(signed.Manifest, text.Replace(
            "integration", "integratioN", StringComparison.Ordinal));
        var action = () => SignedAuditInput.LoadAndVerify(options, new FixedTimeProvider(DateTimeOffset.UtcNow));
        action.Should().Throw<AuditRejectedException>()
            .Where(exception => exception.Code == "input_manifest_signature_invalid");
    }

    [Fact]
    public void SignedInput_RejectsExpiredAndUnsafeScope()
    {
        using var files = new TestFiles();
        var now = DateTimeOffset.UtcNow;
        var baseline = files.ValidManifest(now);
        var unsupported = files.WriteSignedInput(baseline with { SchemaVersion = 1 });
        var unsupportedOptions = new ScanOptions(
            unsupported.Manifest, unsupported.Signature, files.InputPublicKeyPath,
            files.EvidencePrivateKeyPath, files.HmacKeyPath, "unused");
        var unsupportedAction = () => SignedAuditInput.LoadAndVerify(
            unsupportedOptions, new FixedTimeProvider(now));
        unsupportedAction.Should().Throw<AuditRejectedException>()
            .Which.Code.Should().Be("input_manifest_schema_unsupported");

        var manifest = baseline with
        {
            ExpiresAtUtc = now.AddSeconds(-1),
        };
        var signed = files.WriteSignedInput(manifest);
        var options = new ScanOptions(signed.Manifest, signed.Signature,
            files.InputPublicKeyPath, files.EvidencePrivateKeyPath, files.HmacKeyPath, "unused");

        var action = () => SignedAuditInput.LoadAndVerify(options, new FixedTimeProvider(DateTimeOffset.UtcNow));
        action.Should().Throw<AuditRejectedException>()
            .Where(exception => exception.Code == "input_manifest_lifetime_invalid");
    }

    [Fact]
    public void SignedInput_RejectsDuplicateLane()
    {
        using var files = new TestFiles();
        var baseline = files.ValidManifest();
        var manifest = baseline with
        {
            Scope = baseline.Scope with
            {
                Lanes = [baseline.Scope.Lanes[0], baseline.Scope.Lanes[0]],
            },
        };
        var signed = files.WriteSignedInput(manifest);
        var options = new ScanOptions(signed.Manifest, signed.Signature,
            files.InputPublicKeyPath, files.EvidencePrivateKeyPath, files.HmacKeyPath, "unused");

        var action = () => SignedAuditInput.LoadAndVerify(options, new FixedTimeProvider(DateTimeOffset.UtcNow));
        action.Should().Throw<AuditRejectedException>()
            .Where(exception => exception.Code == "input_scope_duplicate_lane");
    }

    [Fact]
    public void SignedInput_RejectsOverlappingSameDimensionLanes()
    {
        using var files = new TestFiles();
        var baseline = files.ValidManifest();
        var first = baseline.Scope.Lanes[0] with { Through = new DateOnly(2024, 1, 5) };
        var second = first with { From = new DateOnly(2024, 1, 5), Through = new DateOnly(2024, 1, 6) };
        var signed = files.WriteSignedInput(baseline with
        {
            Scope = baseline.Scope with { Lanes = [first, second] },
        });
        var options = new ScanOptions(signed.Manifest, signed.Signature,
            files.InputPublicKeyPath, files.EvidencePrivateKeyPath, files.HmacKeyPath, "unused");

        var action = () => SignedAuditInput.LoadAndVerify(options, new FixedTimeProvider(DateTimeOffset.UtcNow));

        action.Should().Throw<AuditRejectedException>()
            .Which.Code.Should().Be("input_scope_overlapping_lane");
    }

    [Theory]
    [InlineData("evds", "day", false)]
    [InlineData("coingecko", "month", true)]
    public void SignedInput_RejectsSourceCadenceMismatch(string source, string cadence, bool assetRequired)
    {
        using var files = new TestFiles();
        var baseline = files.ValidManifest();
        var lane = baseline.Scope.Lanes[0] with
        {
            Source = source,
            AssetId = assetRequired ? baseline.Scope.Lanes[0].AssetId : null,
            Cadence = cadence,
        };
        var signed = files.WriteSignedInput(baseline with
        {
            Scope = baseline.Scope with { Lanes = [lane] },
        });
        var options = new ScanOptions(signed.Manifest, signed.Signature,
            files.InputPublicKeyPath, files.EvidencePrivateKeyPath, files.HmacKeyPath, "unused");

        var action = () => SignedAuditInput.LoadAndVerify(options, new FixedTimeProvider(DateTimeOffset.UtcNow));

        action.Should().Throw<AuditRejectedException>().Which.Code.Should().Be("input_lane_invalid");
    }

    [Fact]
    public void SignedInput_RejectsSignerWhoseSpkiFingerprintDoesNotMatchKeyId()
    {
        using var files = new TestFiles();
        var manifest = files.ValidManifest() with
        {
            KeyId = AuditCryptography.PublicKeyId(files.EvidencePublicKeyPath),
        };
        var signed = files.WriteSignedInput(manifest);
        var options = new ScanOptions(signed.Manifest, signed.Signature,
            files.InputPublicKeyPath, files.EvidencePrivateKeyPath, files.HmacKeyPath, "unused");

        var action = () => SignedAuditInput.LoadAndVerify(options, new FixedTimeProvider(DateTimeOffset.UtcNow));

        action.Should().Throw<AuditRejectedException>().Which.Code.Should().Be("input_key_id_mismatch");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignedInput_RejectsUnknownRootOrNestedMember(bool nested)
    {
        using var files = new TestFiles();
        var node = JsonNode.Parse(JsonSerializer.Serialize(
            files.ValidManifest(), AuditJsonContext.Default.AuditInputManifest))!.AsObject();
        if (nested)
            node["target"]!.AsObject()["databaseTypo"] = "must-not-be-ignored";
        else
            node["unknownRoot"] = "must-not-be-ignored";
        var canonical = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(node.ToJsonString()));
        var manifestPath = Path.Combine(files.Root, $"unknown-{nested}.json");
        var signaturePath = Path.Combine(files.Root, $"unknown-{nested}.sig");
        File.WriteAllBytes(manifestPath, canonical);
        File.WriteAllBytes(signaturePath, AuditCryptography.Sign(canonical, files.InputPrivateKeyPath));
        var options = new ScanOptions(manifestPath, signaturePath,
            files.InputPublicKeyPath, files.EvidencePrivateKeyPath, files.HmacKeyPath, "unused");

        var action = () => SignedAuditInput.LoadAndVerify(options, new FixedTimeProvider(DateTimeOffset.UtcNow));

        action.Should().Throw<AuditRejectedException>()
            .Where(exception => exception.Code == "input_manifest_contract_invalid" &&
                                exception.ExitCode == AuditExitCodes.InvalidArguments);
    }

    [Fact]
    public void SignedInput_RejectsOversizedManifestBeforeParsingOrSignatureVerification()
    {
        using var files = new TestFiles();
        var manifestPath = Path.Combine(files.Root, "oversized-input.json");
        var signaturePath = Path.Combine(files.Root, "small.sig");
        File.WriteAllBytes(manifestPath, new byte[AuditFileLimits.InputManifestBytes + 1]);
        File.WriteAllBytes(signaturePath, [1]);
        var options = new ScanOptions(manifestPath, signaturePath,
            files.InputPublicKeyPath, files.EvidencePrivateKeyPath, files.HmacKeyPath, "unused");

        var action = () => SignedAuditInput.LoadAndVerify(options, new FixedTimeProvider(DateTimeOffset.UtcNow));

        action.Should().Throw<AuditRejectedException>()
            .Where(exception => exception.Code == "input_manifest_too_large" &&
                                exception.ExitCode == AuditExitCodes.InvalidArguments);
    }

    [Theory]
    [InlineData("target-null")]
    [InlineData("scope-null")]
    [InlineData("lanes-null")]
    [InlineData("lane-null")]
    [InlineData("target-hash-null")]
    public void SignedInput_RejectsSignedNullShapesDeterministically(string shape)
    {
        using var files = new TestFiles();
        var node = JsonNode.Parse(JsonSerializer.Serialize(
            files.ValidManifest(), AuditJsonContext.Default.AuditInputManifest))!.AsObject();
        switch (shape)
        {
            case "target-null":
                node["target"] = null;
                break;
            case "scope-null":
                node["scope"] = null;
                break;
            case "lanes-null":
                node["scope"]!.AsObject()["lanes"] = null;
                break;
            case "lane-null":
                node["scope"]!.AsObject()["lanes"]!.AsArray()[0] = null;
                break;
            case "target-hash-null":
                node["target"]!.AsObject()["systemIdentifierSha256"] = null;
                break;
            default:
                throw new InvalidOperationException(shape);
        }
        var canonical = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(node.ToJsonString()));
        var manifestPath = Path.Combine(files.Root, $"null-{shape}.json");
        var signaturePath = Path.Combine(files.Root, $"null-{shape}.sig");
        File.WriteAllBytes(manifestPath, canonical);
        File.WriteAllBytes(signaturePath, AuditCryptography.Sign(canonical, files.InputPrivateKeyPath));
        var options = new ScanOptions(manifestPath, signaturePath,
            files.InputPublicKeyPath, files.EvidencePrivateKeyPath, files.HmacKeyPath, "unused");

        var action = () => SignedAuditInput.LoadAndVerify(options, new FixedTimeProvider(DateTimeOffset.UtcNow));

        action.Should().Throw<AuditRejectedException>()
            .Where(exception => exception.ExitCode == AuditExitCodes.InvalidArguments);
    }

    [Fact]
    public void HmacBusinessKey_IsStable_AndDoesNotExposeInput()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        const string secretCanary = "asset|2024-01-01|SECRET-CANARY";

        var first = AuditCryptography.HmacBusinessKey(key, secretCanary);
        var second = AuditCryptography.HmacBusinessKey(key, secretCanary);

        first.Should().Be(second).And.HaveLength(64).And.NotContain("CANARY");
    }

    [Fact]
    public void KeyIdentity_IsSha256OfExactNistP256Spki()
    {
        using var files = new TestFiles();
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(files.InputPublicKeyPath));

        key.ExportParameters(false).Curve.Oid.Value.Should().Be("1.2.840.10045.3.1.7");
        AuditCryptography.PublicKeyId(files.InputPublicKeyPath).Should().Be(
            AuditCryptography.Sha256Hex(key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void P384Key_IsRejectedForSigningAndIdentity()
    {
        using var files = new TestFiles();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var privatePath = files.Write("p384-private.pem", key.ExportECPrivateKeyPem());
        var publicPath = files.Write("p384-public.pem", key.ExportSubjectPublicKeyInfoPem());

        var sign = () => AuditCryptography.Sign("payload"u8, privatePath);
        sign.Should().Throw<AuditRejectedException>()
            .Which.Code.Should().Be("evidence_private_key_invalid");
        var identify = () => AuditCryptography.PublicKeyId(publicPath);
        identify.Should().Throw<AuditRejectedException>().Which.Code.Should().Be("public_key_invalid");
    }
}
