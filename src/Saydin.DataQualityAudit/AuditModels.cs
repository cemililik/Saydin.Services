using System.Text.Json.Serialization;

namespace Saydin.DataQualityAudit;

internal static class AuditExitCodes
{
    public const int Clean = 0;
    public const int Violations = 2;
    public const int PreflightRejected = 3;
    public const int BudgetRejected = 4;
    public const int RuntimeFailure = 5;
    public const int EvidenceFailure = 6;
    public const int InvalidArguments = 64;
}

internal sealed record AuditInputManifest(
    int SchemaVersion,
    string KeyId,
    string EvidenceKeyId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    AuditTarget Target,
    AuditBudget Budget,
    AuditScope Scope);

internal sealed record AuditTarget(
    string Database,
    string SystemIdentifierSha256,
    string Environment,
    string HeadroomAttestationId);

internal sealed record AuditBudget(
    long MaxDatabaseBytes,
    long MaxRelationBytes,
    long AttestedHeadroomBytes,
    int MaxScopeDays,
    int MaxWindows,
    int MaxEvidencePerCheck,
    long MaxEvidenceBytes,
    int StatementTimeoutMilliseconds,
    int LockTimeoutMilliseconds,
    int TotalTimeoutSeconds,
    int MaxGlobalRows,
    int MaxCalendarReleases);

internal sealed record AuditScope(
    DateTimeOffset AsOfUtc,
    DateTimeOffset LegacyGraceEndedAtUtc,
    IReadOnlyList<AuditLane> Lanes);

internal sealed record AuditLane(
    string Source,
    Guid? AssetId,
    string JobType,
    int ContractVersion,
    DateOnly From,
    DateOnly Through,
    string Cadence);

internal enum AuditSeverity
{
    Info,
    Medium,
    High,
    Critical,
}

internal sealed record AuditSample(
    string BusinessKeyHmac,
    string ViolationCode);

internal sealed record AuditCheckResult(
    string CheckId,
    AuditSeverity Severity,
    string Status,
    long TotalCount,
    bool Truncated,
    IReadOnlyList<AuditSample> Samples);

internal sealed record RepairRecommendation(
    string CheckId,
    string BusinessKeyHmac,
    string Action,
    string? PreimageSha256,
    string? PostimageSha256,
    bool RequiresProviderEvidence);

internal enum RepairAction
{
    Requeue,
    Refetch,
    RestoreSchemaContract,
    RestoreCalendarRelease,
    ReconcileAuthorityEvidence,
    ManualReview,
}

internal sealed record EvidenceContent(
    int SchemaVersion,
    string RulesetVersion,
    string EmbeddedMigrationManifestSha256,
    string InputManifestSha256,
    string TargetIdentitySha256,
    IReadOnlyList<AuditCheckResult> Checks,
    IReadOnlyList<RepairRecommendation> RepairRecommendations);

internal sealed record EvidenceFileHash(string Path, long Bytes, string Sha256);

internal sealed record EvidenceManifest(
    int SchemaVersion,
    string SignatureAlgorithm,
    string SigningProvider,
    string SigningKeyIdentity,
    string KeyId,
    DateTimeOffset CreatedAtUtc,
    string ContentBundleSha256,
    IReadOnlyList<EvidenceFileHash> Files);

internal sealed record EvidenceVerificationResult(bool IsValid, string Code);

internal sealed record EmbeddedMigration(string Version, string FileName, string Checksum);

internal sealed record EmbeddedMigrationManifest(
    IReadOnlyList<EmbeddedMigration> Migrations,
    string Checksum);

internal sealed record DatabaseWindow(
    Guid Id,
    string Source,
    Guid? AssetId,
    string JobType,
    DateOnly RangeStart,
    DateOnly RangeEnd,
    int ContractVersion,
    string State,
    int RequestedCalendarCount,
    int ExpectedObservationCount,
    int AcceptedDistinctCount,
    int RejectedCount,
    int ExpectedNoDataCount,
    Guid? CalendarReleaseId);

internal sealed record RawViolation(string ViolationCode, string BusinessKey);

internal sealed class AuditRejectedException(string code, int exitCode) : Exception(code)
{
    public string Code { get; } = code;
    public int ExitCode { get; } = exitCode;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AuditInputManifest))]
[JsonSerializable(typeof(EvidenceContent))]
[JsonSerializable(typeof(EvidenceManifest))]
[JsonSerializable(typeof(RepairRecommendation[]))]
[JsonSerializable(typeof(IReadOnlyList<AuditCheckResult>))]
internal partial class AuditJsonContext : JsonSerializerContext;
