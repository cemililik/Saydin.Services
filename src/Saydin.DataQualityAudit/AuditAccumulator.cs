using System.Security.Cryptography;

namespace Saydin.DataQualityAudit;

internal sealed class AuditAccumulator(byte[] hmacKey, int sampleLimit)
{
    private readonly Dictionary<string, MutableCheck> _checks = new(StringComparer.Ordinal);

    public void Ensure(string checkId, AuditSeverity severity)
    {
        if (!_checks.ContainsKey(checkId))
            _checks.Add(checkId, new MutableCheck(severity));
    }

    public void Add(string checkId, AuditSeverity severity, string code, string businessKey)
    {
        Ensure(checkId, severity);
        var check = _checks[checkId];
        check.TotalCount++;
        AddSample(check, code, businessKey);
    }

    public void AddBatch(
        string checkId,
        AuditSeverity severity,
        long totalCount,
        IEnumerable<RawViolation> samples)
    {
        Ensure(checkId, severity);
        var check = _checks[checkId];
        check.TotalCount += totalCount;
        foreach (var sample in samples)
            AddSample(check, sample.ViolationCode, sample.BusinessKey);
    }

    public IReadOnlyList<AuditCheckResult> Build()
    {
        return _checks
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new AuditCheckResult(
                pair.Key,
                pair.Value.Severity,
                pair.Value.TotalCount == 0 ? "clean" : "violations",
                pair.Value.TotalCount,
                pair.Value.TotalCount > pair.Value.Samples.Count,
                pair.Value.Samples
                    .OrderBy(sample => sample.ViolationCode, StringComparer.Ordinal)
                    .ThenBy(sample => sample.BusinessKeyHmac, StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();
    }

    public IReadOnlyList<RepairRecommendation> BuildRecommendations()
    {
        return Build()
            .Where(check => check.TotalCount > 0)
            .SelectMany(check => check.Samples.Select(sample => new RepairRecommendation(
                check.CheckId,
                sample.BusinessKeyHmac,
                ActionFor(check.CheckId, sample.ViolationCode),
                null,
                null,
                RequiresProviderEvidence(check.CheckId))))
            .OrderBy(item => item.CheckId, StringComparer.Ordinal)
            .ThenBy(item => item.BusinessKeyHmac, StringComparer.Ordinal)
            .ToArray();
    }

    private void AddSample(MutableCheck check, string code, string businessKey)
    {
        if (check.Samples.Count >= sampleLimit)
            return;
        check.Samples.Add(new AuditSample(
            AuditCryptography.HmacBusinessKey(hmacKey, businessKey),
            code));
    }

    private static string ActionFor(string checkId, string code) => checkId switch
    {
        "DQ-001" or "DQ-002" or "DQ-007" => "requeue",
        "DQ-004" => "refetch",
        "DQ-005" or "DQ-008" => "manual_review",
        _ => "manual_review",
    };

    private static bool RequiresProviderEvidence(string checkId) =>
        checkId is "DQ-001" or "DQ-004" or "DQ-005" or "DQ-008";

    private sealed class MutableCheck(AuditSeverity severity)
    {
        public AuditSeverity Severity { get; } = severity;
        public long TotalCount { get; set; }
        public List<AuditSample> Samples { get; } = [];
    }
}
