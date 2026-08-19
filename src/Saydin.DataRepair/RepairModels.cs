using System.Text.Json.Serialization;

namespace Saydin.DataRepair;

internal static class RepairExitCodes
{
    public const int Success = 0;
    public const int Rejected = 3;
    public const int TargetRejected = 4;
    public const int SignatureFailure = 5;
    public const int DatabaseFailure = 6;
    public const int ReceiptFailure = 7;
    public const int InvalidArguments = 64;
}

internal sealed class RepairRejectedException(string code, int exitCode) : Exception(code)
{
    public string Code { get; } = code;
    public int ExitCode { get; } = exitCode;
}

internal sealed record RepairPlan(
    int SchemaVersion,
    string KeyId,
    string ReceiptKeyId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string ChangeTicket,
    string Nonce,
    string ApprovalTokenSha256,
    RepairTarget Target,
    RepairEvidenceBinding Evidence,
    RepairMigrationTrust MigrationTrust,
    IReadOnlyList<RepairOperation> Operations);

internal sealed record RepairTarget(
    string Environment,
    string Database,
    string SystemIdentifierSha256,
    string DeploymentId,
    string RolePrefix);

internal sealed record RepairEvidenceBinding(
    string ContentSha256,
    string SignerKeyId);

internal sealed record RepairMigrationTrust(
    string ManifestSha256,
    IReadOnlyList<RepairMigrationEntry> Migrations);

internal sealed record RepairMigrationEntry(string Version, string Sha256);

internal sealed record RepairOperation(
    string Type,
    Guid? WindowId,
    string? PreimageSha256,
    DateTimeOffset? NextAttemptAtUtc,
    string? ReferenceSha256,
    string? ReasonCode);

internal sealed record VerifiedRepairPlan(
    RepairPlan Plan,
    byte[] CanonicalBytes,
    string PlanSha256,
    string TargetSha256,
    string NonceSha256);

internal sealed record DqaEvidenceManifest(
    int SchemaVersion,
    string SignatureAlgorithm,
    string SigningProvider,
    string SigningKeyIdentity,
    string KeyId,
    DateTimeOffset CreatedAtUtc,
    string ContentBundleSha256,
    IReadOnlyList<DqaEvidenceFile> Files);

internal sealed record DqaEvidenceFile(string Path, long Bytes, string Sha256);

internal sealed record WindowSnapshot(
    Guid Id,
    string Source,
    Guid? AssetId,
    string JobType,
    DateOnly RangeStart,
    DateOnly RangeEnd,
    int ContractVersion,
    string State,
    string? LeaseOwner,
    Guid? LeaseToken,
    DateTimeOffset? LeaseUntil,
    int AttemptCount,
    DateTimeOffset NextAttemptAt,
    int RequestedCalendarCount,
    int ExpectedObservationCount,
    int RawItemCount,
    int AcceptedDistinctCount,
    int RejectedCount,
    int ExpectedNoDataCount,
    string? OutcomeCode,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    Guid? CalendarReleaseId);

internal sealed record RollbackState(
    string State,
    DateTimeOffset NextAttemptAt,
    string? OutcomeCode,
    string? ErrorCode,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

internal sealed record RepairOperationReceipt(
    int Index,
    string Result,
    string? PreimageSha256,
    string? PostimageSha256,
    string? GuardSha256,
    RollbackState? RollbackState);

internal sealed record RepairReceipt(
    int SchemaVersion,
    string SignatureAlgorithm,
    string SigningProvider,
    string SigningKeyIdentity,
    string KeyId,
    string Mode,
    string PlanSha256,
    string TargetSha256,
    string NonceSha256,
    string MigrationManifestSha256,
    string EvidenceContentSha256,
    string EvidenceSignerKeyId,
    string? PriorReceiptSha256,
    long DatabaseTransactionId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<RepairOperationReceipt> Operations);

internal sealed record VerifiedRepairReceipt(
    RepairReceipt Receipt,
    byte[] CanonicalBytes,
    string ReceiptSha256,
    string Directory);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RepairPlan))]
[JsonSerializable(typeof(RepairTarget))]
[JsonSerializable(typeof(DqaEvidenceManifest))]
[JsonSerializable(typeof(WindowSnapshot))]
[JsonSerializable(typeof(RepairReceipt))]
internal partial class RepairJsonContext : JsonSerializerContext;
