using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Saydin.DatabaseSecurity;

namespace Saydin.DataRepair.Tests;

internal sealed class RepairTestFiles : IDisposable
{
    private readonly ECDsa planKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa evidenceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa receiptKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public RepairTestFiles()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        Root = Path.Combine(Path.GetTempPath(), $"saydin-repair-unit-{Guid.NewGuid():N}");
        CreateDirectory(Root);
        InputDirectory = Path.Combine(Root, "input");
        EvidenceDirectory = Path.Combine(Root, "evidence");
        ReceiptRoot = Path.Combine(Root, "receipts");
        CreateDirectory(InputDirectory);
        CreateDirectory(EvidenceDirectory);
        CreateDirectory(ReceiptRoot);
        PlanFile = Path.Combine(InputDirectory, "plan.json");
        PlanSignatureFile = Path.Combine(InputDirectory, "plan.sig");
        PlanPublicKeyFile = Path.Combine(InputDirectory, "plan-public.pem");
        EvidencePublicKeyFile = Path.Combine(InputDirectory, "evidence-public.pem");
        ReceiptPrivateKeyFile = Path.Combine(InputDirectory, "receipt-private.pem");
        ApprovalTokenFile = Path.Combine(InputDirectory, "approval-token");
        AuditPasswordFile = Path.Combine(InputDirectory, "audit-password");
        WritePrivate(PlanPublicKeyFile, planKey.ExportSubjectPublicKeyInfoPem());
        WritePrivate(EvidencePublicKeyFile, evidenceKey.ExportSubjectPublicKeyInfoPem());
        WritePrivate(ReceiptPrivateKeyFile, receiptKey.ExportPkcs8PrivateKeyPem());
        ApprovalToken = RandomNumberGenerator.GetBytes(48);
        WritePrivate(ApprovalTokenFile, ApprovalToken);
        WritePrivate(AuditPasswordFile, RandomNumberGenerator.GetBytes(48));
        EvidenceContentSha256 = WriteEvidence();
        Plan = ValidPlan();
        WritePlan(Plan);
    }

    public string Root { get; }
    public string InputDirectory { get; }
    public string EvidenceDirectory { get; }
    public string ReceiptRoot { get; }
    public string PlanFile { get; }
    public string PlanSignatureFile { get; }
    public string PlanPublicKeyFile { get; }
    public string EvidencePublicKeyFile { get; }
    public string ReceiptPrivateKeyFile { get; }
    public string ApprovalTokenFile { get; }
    public string AuditPasswordFile { get; }
    public byte[] ApprovalToken { get; }
    public string EvidenceContentSha256 { get; }
    public RepairPlan Plan { get; private set; }
    public DateTimeOffset Now { get; } = new(2026, 8, 19, 8, 0, 0, TimeSpan.Zero);

    public string ReceiptKeyId => RepairCryptography.Sha256Hex(
        receiptKey.ExportSubjectPublicKeyInfo());

    public void WritePlan(RepairPlan plan)
    {
        Plan = plan;
        var bytes = CanonicalJson.Serialize(plan, RepairJsonContext.Default.RepairPlan);
        WriteSignedPlanBytes(bytes);
    }

    public void WriteSignedPlanBytes(byte[] bytes)
    {
        WritePrivate(PlanFile, bytes);
        WritePrivate(PlanSignatureFile, planKey.SignData(
            bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    public void WriteSignedEvidenceManifestBytes(byte[] bytes)
    {
        WritePrivate(Path.Combine(EvidenceDirectory, "manifest.json"), bytes);
        WritePrivate(Path.Combine(EvidenceDirectory, "manifest.sha256"),
            Encoding.ASCII.GetBytes(RepairCryptography.Sha256Hex(bytes) + "\n"));
        WritePrivate(Path.Combine(EvidenceDirectory, "manifest.sig"), evidenceKey.SignData(
            bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    public string[] CommonArguments(params string[] prefix) =>
    [
        .. prefix,
        "--plan", PlanFile,
        "--plan-signature", PlanSignatureFile,
        "--plan-public-key", PlanPublicKeyFile,
        "--evidence-bundle", EvidenceDirectory,
        "--evidence-public-key", EvidencePublicKeyFile,
        "--audit-login", $"{Plan.Target.RolePrefix}_audit_login_v1",
        "--audit-password-file", AuditPasswordFile,
    ];

    public void Dispose()
    {
        planKey.Dispose();
        evidenceKey.Dispose();
        receiptKey.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    private RepairPlan ValidPlan()
    {
        var systemHash = new string('a', 64);
        const string deployment = "dev-repair";
        const string database = "saydin_repair_test";
        var prefix = RoleContract.DerivePrefix(deployment, database, systemHash);
        return new RepairPlan(
            2,
            RepairCryptography.Sha256Hex(planKey.ExportSubjectPublicKeyInfo()),
            ReceiptKeyId,
            Now.AddMinutes(-1),
            Now.AddHours(1),
            "CHG-12345",
            "nonce-0123456789abcdef0123456789abcdef",
            RepairCryptography.Sha256Hex(ApprovalToken),
            new RepairTarget("development", database, systemHash, deployment, prefix),
            new RepairEvidenceBinding(
                EvidenceContentSha256,
                RepairCryptography.Sha256Hex(evidenceKey.ExportSubjectPublicKeyInfo())),
            new RepairMigrationTrust(
                EmbeddedRepairMigrationTrust.ManifestSha256,
                EmbeddedRepairMigrationTrust.Entries),
            [new RepairOperation(
                "requeue_permanent_window", Guid.Parse("018f6f10-1234-7abc-8def-1234567890ab"),
                new string('b', 64), Now.AddMinutes(5), null, null)]);
    }

    private string WriteEvidence()
    {
        var content = CanonicalJson.Canonicalize(
            Encoding.UTF8.GetBytes("{\"schemaVersion\":2}"));
        var contentHash = RepairCryptography.Sha256Hex(content);
        WritePrivate(Path.Combine(EvidenceDirectory, "evidence-content.json"), content);
        var keyId = RepairCryptography.Sha256Hex(evidenceKey.ExportSubjectPublicKeyInfo());
        var manifest = new DqaEvidenceManifest(
            2, "ECDSA-SHA256-RFC3279-DER", "local-pem", $"local-pem:{keyId}", keyId,
            Now.AddMinutes(-2), contentHash,
            [new DqaEvidenceFile("evidence-content.json", content.LongLength, contentHash)]);
        var manifestBytes = CanonicalJson.Serialize(
            manifest, RepairJsonContext.Default.DqaEvidenceManifest);
        WriteSignedEvidenceManifestBytes(manifestBytes);
        return contentHash;
    }

    private static void CreateDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static void WritePrivate(string path, string value) =>
        WritePrivate(path, Encoding.UTF8.GetBytes(value));

    internal static void WritePrivate(string path, byte[] value)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (File.Exists(path)) File.Delete(path);
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        stream.Write(value);
        stream.Flush(flushToDisk: true);
    }
}
