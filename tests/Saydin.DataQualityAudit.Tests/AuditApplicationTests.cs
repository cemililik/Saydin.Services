using FluentAssertions;

namespace Saydin.DataQualityAudit.Tests;

public sealed class AuditApplicationTests
{
    [Fact]
    public async Task MissingArguments_Returns64_WithoutUsageOrSecrets()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await AuditApplication.RunAsync([], output, error, new FixedTimeProvider(DateTimeOffset.UtcNow));

        exit.Should().Be(AuditExitCodes.InvalidArguments);
        error.ToString().Should().Contain("code=command_missing").And.NotContain("Password=");
    }

    [Fact]
    public async Task VerifyEvidence_TamperReturns6()
    {
        using var files = new TestFiles();
        var directory = Path.Combine(files.Root, "bundle");
        var content = new EvidenceContent(
            1, "test", new string('a', 64), new string('b', 64), new string('c', 64), [], []);
        await EvidenceBundle.WriteAsync(directory, content, files.EvidenceKeyId, files.PrivateKeyPath,
            1_000_000, DateTimeOffset.UtcNow, default);
        await File.AppendAllTextAsync(Path.Combine(directory, "evidence-content.json"), "tamper");
        var error = new StringWriter();

        var exit = await AuditApplication.RunAsync(
            ["verify-evidence", "--bundle", directory, "--public-key", files.PublicKeyPath],
            TextWriter.Null, error, new FixedTimeProvider(DateTimeOffset.UtcNow));

        exit.Should().Be(AuditExitCodes.EvidenceFailure);
        error.ToString().Should().Contain("code=evidence_file_integrity_invalid");
    }

    [Fact]
    public void ScanOptions_ExposeOnlySecretFileReferences_NotSecretValues()
    {
        var options = AuditOptions.Parse([
            "scan",
            "--input", "input.json",
            "--input-signature", "input.sig",
            "--input-public-key", "input.pem",
            "--evidence-private-key", "/run/secrets/signing.pem",
            "--hmac-key-file", "/run/secrets/hmac",
            "--output", "evidence",
        ]);

        options.Should().BeOfType<ScanOptions>();
        options.ToString().Should().NotContain("Host=").And.NotContain("Password=");
    }

    [Fact]
    public async Task ProductionLocalPemCli_Returns64BeforePrivateKeyOrDatabaseAccess_AndPublishesNothing()
    {
        using var files = new TestFiles();
        var baseline = files.ValidManifest();
        var productionManifest = baseline with
        {
            Target = baseline.Target with { Environment = "production" },
        };
        var signed = files.WriteSignedInput(productionManifest);
        var authority = files.WriteProductionTargetAuthority(productionManifest.Target);
        var bundle = Path.Combine(files.Root, "production-local-rejected");
        var error = new StringWriter();

        var exit = await AuditApplication.RunAsync([
            "scan",
            "--input", signed.Manifest,
            "--input-signature", signed.Signature,
            "--input-public-key", files.InputPublicKeyPath,
            "--evidence-private-key", "/must/not/be/read/private.pem",
            "--hmac-key-file", "/must/not/be/read/hmac",
            "--output", bundle,
            "--production-target-authority-file", authority,
        ], TextWriter.Null, error, new FixedTimeProvider(DateTimeOffset.UtcNow), environment: _ => null);

        exit.Should().Be(AuditExitCodes.InvalidArguments);
        error.ToString().Should().Contain("code=production_signer_mode_rejected");
        Directory.Exists(bundle).Should().BeFalse();
    }
}
