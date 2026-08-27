namespace Saydin.DataQualityAudit;

internal static class LedgerContinuity
{
    public static IReadOnlyList<RawViolation> Analyze(
        AuditLane lane,
        IReadOnlyList<DatabaseWindow> windows)
    {
        var violations = new List<RawViolation>();
        var laneKey = $"{lane.Source}|{lane.AssetId?.ToString("D") ?? "global"}|" +
                      $"{lane.JobType}|{lane.ContractVersion}";
        if (windows.Count == 0)
        {
            violations.Add(new RawViolation("scope_has_no_windows", laneKey));
            return violations;
        }

        var expected = lane.From;
        foreach (var window in windows)
        {
            var clippedStart = window.RangeStart < lane.From ? lane.From : window.RangeStart;
            var clippedEnd = window.RangeEnd > lane.Through ? lane.Through : window.RangeEnd;
            if (clippedStart > expected)
                violations.Add(new RawViolation("interior_or_edge_gap",
                    $"{laneKey}|{expected:yyyy-MM-dd}|{clippedStart:yyyy-MM-dd}"));
            else if (clippedStart < expected)
                violations.Add(new RawViolation("overlapping_windows", $"{laneKey}|{window.Id:D}"));

            var candidate = lane.Cadence == "month"
                ? clippedEnd.AddMonths(1)
                : clippedEnd.AddDays(1);
            if (candidate > expected)
                expected = candidate;
        }

        if (expected <= lane.Through)
            violations.Add(new RawViolation("trailing_gap",
                $"{laneKey}|{expected:yyyy-MM-dd}|{lane.Through:yyyy-MM-dd}"));
        return violations;
    }
}
