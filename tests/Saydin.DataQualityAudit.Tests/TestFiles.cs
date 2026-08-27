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
        SetPrivateDirectoryMode(Root);
        using var inputKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        InputPrivateKeyPath = Write("input-private.pem", inputKey.ExportECPrivateKeyPem());
        InputPublicKeyPath = Write("input-public.pem", inputKey.ExportSubjectPublicKeyInfoPem());
        using var evidenceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        EvidencePrivateKeyPath = Write("evidence-private.pem", evidenceKey.ExportECPrivateKeyPem());
        EvidencePublicKeyPath = Write("evidence-public.pem", evidenceKey.ExportSubjectPublicKeyInfoPem());
        HmacKeyPath = Path.Combine(Root, "hmac.key");
        File.WriteAllBytes(HmacKeyPath, RandomNumberGenerator.GetBytes(32));
        SetSecretFileMode(HmacKeyPath);
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
        if (name.Contains("private", StringComparison.Ordinal))
            SetSecretFileMode(path);
        return path;
    }

    private static void SetPrivateDirectoryMode(string path)
    {
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static void SetSecretFileMode(string path)
    {
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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

    public string WriteProductionTargetAuthority(AuditTarget target)
    {
        var path = Path.Combine(Root, $"production-target-{Guid.NewGuid():N}.authority");
        File.WriteAllBytes(path, SHA256.HashData(Encoding.UTF8.GetBytes(
            $"saydin-dqa-production-target/v1\0{target.Database}\0{target.SystemIdentifierSha256}")));
        SetSecretFileMode(path);
        return path;
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
            2,
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
                60,
                10_000,
                1_000),
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

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
