using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Saydin.DataQualityAudit.Tests;

internal sealed class TestFiles : IDisposable
{
    public TestFiles()
    {
        Root = Path.Combine(Path.GetTempPath(), $"saydin-audit-unit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        using var inputKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        InputPrivateKeyPath = Write("input-private.pem", inputKey.ExportECPrivateKeyPem());
        InputPublicKeyPath = Write("input-public.pem", inputKey.ExportSubjectPublicKeyInfoPem());
        using var evidenceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        EvidencePrivateKeyPath = Write("evidence-private.pem", evidenceKey.ExportECPrivateKeyPem());
        EvidencePublicKeyPath = Write("evidence-public.pem", evidenceKey.ExportSubjectPublicKeyInfoPem());
        HmacKeyPath = Path.Combine(Root, "hmac.key");
        File.WriteAllBytes(HmacKeyPath, RandomNumberGenerator.GetBytes(32));
    }

    public string Root { get; }
    public string InputPrivateKeyPath { get; }
    public string InputPublicKeyPath { get; }
    public string EvidencePrivateKeyPath { get; }
    public string EvidencePublicKeyPath { get; }
    public string PrivateKeyPath => EvidencePrivateKeyPath;
    public string PublicKeyPath => EvidencePublicKeyPath;
    public string EvidenceKeyId => AuditCryptography.PublicKeyId(EvidencePublicKeyPath);
    public string HmacKeyPath { get; }

    public string Write(string name, string content)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    public (string Manifest, string Signature) WriteSignedInput(AuditInputManifest manifest)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(manifest, AuditJsonContext.Default.AuditInputManifest);
        var canonical = CanonicalJson.Canonicalize(raw);
        var manifestPath = Path.Combine(Root, $"input-{Guid.NewGuid():N}.json");
        var signaturePath = Path.Combine(Root, $"input-{Guid.NewGuid():N}.sig");
        File.WriteAllBytes(manifestPath, canonical);
        File.WriteAllBytes(signaturePath, AuditCryptography.Sign(canonical, InputPrivateKeyPath));
        return (manifestPath, signaturePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }

    public AuditInputManifest ValidManifest(DateTimeOffset? now = null)
    {
        var instant = now ?? DateTimeOffset.UtcNow;
        return new AuditInputManifest(
            1,
            AuditCryptography.PublicKeyId(InputPublicKeyPath),
            AuditCryptography.PublicKeyId(EvidencePublicKeyPath),
            instant.AddMinutes(-1),
            instant.AddHours(1),
            new AuditTarget(
                "saydin_ingestion_test_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                new string('a', 64),
                "integration",
                "headroom-test-attestation"),
            new AuditBudget(
                10_000_000_000,
                5_000_000_000,
                100_000_000,
                366,
                100,
                2,
                1_000_000,
                30_000,
                1_000,
                60),
            new AuditScope(
                instant,
                instant.AddMinutes(-1),
                [new AuditLane(
                    "coingecko",
                    Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    "historical_backfill",
                    1,
                    new DateOnly(2024, 1, 1),
                    new DateOnly(2024, 1, 2),
                    "day")]));
    }
}
