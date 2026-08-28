using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace Saydin.DataRepair.Tests;

public sealed class SignedRepairPlanTests
{
    [Fact]
    public async Task ValidCanonicalPlanAndBoundEvidence_AreAccepted()
    {
        using var files = new RepairTestFiles();
        var verified = SignedRepairPlan.LoadAndVerify(
            files.PlanFile, files.PlanSignatureFile, files.PlanPublicKeyFile,
            new FixedTimeProvider(files.Now));
        verified.Plan.Operations.Should().HaveCount(1);
        await DqaEvidenceVerifier.VerifyAsync(
            files.EvidenceDirectory, files.EvidencePublicKeyFile,
            verified.Plan.Evidence,
            VerifiedPhysicalRepairTarget.FromLiveTrust(verified.Plan.Target), default);
    }

    [Fact]
    public void TamperedPlanSignature_IsRejectedWithoutEchoingInput()
    {
        using var files = new RepairTestFiles();
        RepairTestFiles.WritePrivate(files.PlanSignatureFile, new byte[64]);
        var action = () => SignedRepairPlan.LoadAndVerify(
            files.PlanFile, files.PlanSignatureFile, files.PlanPublicKeyFile,
            new FixedTimeProvider(files.Now));
        action.Should().Throw<RepairRejectedException>()
            .Which.Code.Should().Be("plan_signature_invalid");
    }

    [Fact]
    public void DuplicateAndUnknownJsonProperties_AreRejected()
    {
        using var files = new RepairTestFiles();
        var canonical = File.ReadAllBytes(files.PlanFile);
        var duplicate = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":2," + Encoding.UTF8.GetString(canonical)[1..]);
        files.WriteSignedPlanBytes(duplicate);
        var duplicateAction = () => SignedRepairPlan.LoadAndVerify(
            files.PlanFile, files.PlanSignatureFile, files.PlanPublicKeyFile,
            new FixedTimeProvider(files.Now));
        duplicateAction.Should().Throw<RepairRejectedException>()
            .Which.Code.Should().Be("json_duplicate_property");

        files.WritePlan(files.Plan);
        var node = JsonNode.Parse(File.ReadAllBytes(files.PlanFile))!.AsObject();
        node["unknown"] = 1;
        var unknown = CanonicalJson.Canonicalize(
            Encoding.UTF8.GetBytes(node.ToJsonString()));
        files.WriteSignedPlanBytes(unknown);
        var unknownAction = () => SignedRepairPlan.LoadAndVerify(
            files.PlanFile, files.PlanSignatureFile, files.PlanPublicKeyFile,
            new FixedTimeProvider(files.Now));
        unknownAction.Should().Throw<RepairRejectedException>()
            .Which.Code.Should().Be("plan_contract_invalid");
    }

    [Fact]
    public void ExpiredPlanAndMigrationTrustDrift_AreRejected()
    {
        using var files = new RepairTestFiles();
        files.WritePlan(files.Plan with
        {
            IssuedAtUtc = files.Now.AddHours(-2),
            ExpiresAtUtc = files.Now.AddMinutes(-1),
        });
        var expired = () => SignedRepairPlan.LoadAndVerify(
            files.PlanFile, files.PlanSignatureFile, files.PlanPublicKeyFile,
            new FixedTimeProvider(files.Now));
        expired.Should().Throw<RepairRejectedException>()
            .Which.Code.Should().Be("plan_lifetime_invalid");

        var migrations = EmbeddedRepairMigrationTrust.Entries.ToArray();
        migrations[0] = migrations[0] with { Sha256 = new string('0', 64) };
        files.WritePlan(files.Plan with
        {
            IssuedAtUtc = files.Now.AddMinutes(-1),
            ExpiresAtUtc = files.Now.AddHours(1),
            MigrationTrust = files.Plan.MigrationTrust with { Migrations = migrations },
        });
        var drift = () => SignedRepairPlan.LoadAndVerify(
            files.PlanFile, files.PlanSignatureFile, files.PlanPublicKeyFile,
            new FixedTimeProvider(files.Now));
        drift.Should().Throw<RepairRejectedException>()
            .Which.Code.Should().Be("plan_migration_trust_invalid");
    }

    [Fact]
    public async Task TamperedEvidenceContent_IsRejected()
    {
        using var files = new RepairTestFiles();
        RepairTestFiles.WritePrivate(
            Path.Combine(files.EvidenceDirectory, "evidence-content.json"), "tampered");
        var action = () => DqaEvidenceVerifier.VerifyAsync(
            files.EvidenceDirectory, files.EvidencePublicKeyFile,
            files.Plan.Evidence,
            VerifiedPhysicalRepairTarget.FromLiveTrust(files.Plan.Target), default);
        (await action.Should().ThrowAsync<RepairRejectedException>())
            .Which.Code.Should().Be("evidence_file_invalid");
    }

    [Fact]
    public async Task ProductionRejectsLocalEvidenceEvenWhenSignerKeyIsBound()
    {
        using var files = new RepairTestFiles();
        var action = () => DqaEvidenceVerifier.VerifyAsync(
            files.EvidenceDirectory, files.EvidencePublicKeyFile,
            files.Plan.Evidence,
            VerifiedPhysicalRepairTarget.FromLiveTrust(
                files.Plan.Target with { Environment = "production" }), default);
        (await action.Should().ThrowAsync<RepairRejectedException>())
            .Which.Code.Should().Be("evidence_manifest_invalid");
    }

    [Fact]
    public async Task EvidenceInventoryCapPlusOneIsRejectedWithoutMaterializingTheInventory()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var files = new RepairTestFiles();
        for (var index = 0; index <= DqaEvidenceVerifier.MaximumInventoryDirectories; index++)
            Directory.CreateDirectory(Path.Combine(files.EvidenceDirectory, $"extra-{index:D4}"));

        var action = () => DqaEvidenceVerifier.VerifyAsync(
            files.EvidenceDirectory, files.EvidencePublicKeyFile,
            files.Plan.Evidence,
            VerifiedPhysicalRepairTarget.FromLiveTrust(files.Plan.Target), default);

        (await action.Should().ThrowAsync<RepairRejectedException>())
            .Which.Code.Should().Be("evidence_inventory_invalid");
    }

    [Fact]
    public async Task EvidenceVerificationHonorsCancellation()
    {
        using var files = new RepairTestFiles();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var action = () => DqaEvidenceVerifier.VerifyAsync(
            files.EvidenceDirectory, files.EvidencePublicKeyFile,
            files.Plan.Evidence,
            VerifiedPhysicalRepairTarget.FromLiveTrust(files.Plan.Target), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void PlanPathAndSecureFileSizeBoundaryFailClosed()
    {
        using var files = new RepairTestFiles();
        var relative = () => SignedRepairPlan.LoadAndVerify(
            "plan.json", files.PlanSignatureFile, files.PlanPublicKeyFile,
            new FixedTimeProvider(files.Now));
        relative.Should().Throw<RepairRejectedException>()
            .Which.Code.Should().Be("path_absolute_required");

        RepairTestFiles.WritePrivate(files.PlanFile, new byte[RepairFiles.PlanBytes + 1]);
        var oversized = () => SignedRepairPlan.LoadAndVerify(
            files.PlanFile, files.PlanSignatureFile, files.PlanPublicKeyFile,
            new FixedTimeProvider(files.Now));
        oversized.Should().Throw<RepairRejectedException>()
            .Which.Code.Should().Be("plan_file_invalid");
    }

    [Theory]
    [InlineData("keyId", "plan_contract_invalid")]
    [InlineData("changeTicket", "plan_contract_invalid")]
    [InlineData("nonce", "plan_contract_invalid")]
    public void MissingRequiredSignedPlanString_IsRejectedWithStableInvalidArgumentCode(
        string property,
        string expectedCode)
    {
        using var files = new RepairTestFiles();
        var node = JsonNode.Parse(File.ReadAllBytes(files.PlanFile))!.AsObject();
        node.Remove(property).Should().BeTrue();
        files.WriteSignedPlanBytes(CanonicalJson.Canonicalize(
            Encoding.UTF8.GetBytes(node.ToJsonString())));

        var action = () => SignedRepairPlan.LoadAndVerify(
            files.PlanFile, files.PlanSignatureFile, files.PlanPublicKeyFile,
            new FixedTimeProvider(files.Now));

        var rejected = action.Should().Throw<RepairRejectedException>().Which;
        rejected.Code.Should().Be(expectedCode);
        rejected.ExitCode.Should().Be(RepairExitCodes.InvalidArguments);
    }

    [Fact]
    public async Task MissingRequiredSignedManifestString_IsRejectedWithStableSignatureCode()
    {
        using var files = new RepairTestFiles();
        var manifestPath = Path.Combine(files.EvidenceDirectory, "manifest.json");
        var node = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        node.Remove("signingKeyIdentity").Should().BeTrue();
        files.WriteSignedEvidenceManifestBytes(CanonicalJson.Canonicalize(
            Encoding.UTF8.GetBytes(node.ToJsonString())));

        var action = () => DqaEvidenceVerifier.VerifyAsync(
            files.EvidenceDirectory, files.EvidencePublicKeyFile,
            files.Plan.Evidence,
            VerifiedPhysicalRepairTarget.FromLiveTrust(files.Plan.Target), default);

        var rejected = (await action.Should().ThrowAsync<RepairRejectedException>()).Which;
        rejected.Code.Should().Be("evidence_manifest_invalid");
        rejected.ExitCode.Should().Be(RepairExitCodes.SignatureFailure);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
