namespace Saydin.CalendarData.Tests;

public sealed class OfficialSourceReplayTests
{
    [Fact]
    public void Manifest_ContainsTheCompletePinnedAuthoritySet()
    {
        var manifest = CalendarDataTestRoot.ReadManifest();

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("cal-001-2026-08-17", manifest.SnapshotSetId);
        Assert.Equal(274, manifest.Sources.Count);
        Assert.Equal(21, manifest.Sources.Count(source => source.Kind == "tcmbAnnualIndex"));
        Assert.Equal(248, manifest.Sources.Count(source => source.Kind == "tcmbMonthlyArchive"));
        Assert.Single(manifest.Sources, source => source.Kind == "tcmbPolicyFaq");
        Assert.Equal(3, manifest.Sources.Count(source => source.Kind == "bistPayHolidayPdf"));
        Assert.Single(manifest.Sources, source => source.Kind == "bistHolidayIndex");
        Assert.All(manifest.Sources, source => Assert.Equal(64, source.RawSha256.Length));
    }

    [Fact]
    public void TcmbAnnualIndexes_LinkAllRequiredMonths()
    {
        var manifest = CalendarDataTestRoot.ReadManifest();
        var store = new SourceSnapshotStore(CalendarDataTestRoot.DataRoot, manifest);

        foreach (var source in manifest.Sources.Where(source => source.Kind == "tcmbAnnualIndex"))
        {
            var months = TcmbArchiveParser.ParseAnnualMonthNumbers(store.Read(source), source.Year!.Value);
            Assert.Equal(source.Year == 2026 ? 8 : 12, months.Count);
        }
    }

    [Theory]
    [InlineData("tcmb-month-200601", 2006, 1, "2006-01-02", true)]
    [InlineData("tcmb-month-200601", 2006, 1, "2006-01-09", false)]
    [InlineData("tcmb-month-202506", 2025, 6, "2025-06-04", true)]
    [InlineData("tcmb-month-202506", 2025, 6, "2025-06-05", false)]
    [InlineData("tcmb-month-202506", 2025, 6, "2025-06-10", true)]
    [InlineData("tcmb-month-202603", 2026, 3, "2026-03-19", false)]
    [InlineData("tcmb-month-202603", 2026, 3, "2026-03-23", true)]
    public void TcmbMonthlyArchives_ReplayExactPublicationLinks(
        string sourceId, int year, int month, string date, bool expected)
    {
        var publications = TcmbArchiveParser.ParsePublicationDates(CalendarDataTestRoot.Raw(sourceId), year, month);

        Assert.Equal(expected, publications.Contains(DateOnly.Parse(date)));
    }

    [Theory]
    [InlineData("bist-pay-2024", 2024, "2024-04-09", BistHolidaySession.Partial)]
    [InlineData("bist-pay-2024", 2024, "2024-04-10", BistHolidaySession.Closed)]
    [InlineData("bist-pay-2025", 2025, "2025-03-29", BistHolidaySession.Closed)]
    [InlineData("bist-pay-2025", 2025, "2025-06-05", BistHolidaySession.Partial)]
    [InlineData("bist-pay-2025", 2025, "2025-06-06", BistHolidaySession.Closed)]
    [InlineData("bist-pay-2026", 2026, "2026-03-19", BistHolidaySession.Partial)]
    public void BistOfficialPdfs_UseOperationColumnSemantics(
        string sourceId, int year, string date, BistHolidaySession expected)
    {
        var sessions = BistPayCalendarParser.Parse(CalendarDataTestRoot.Raw(sourceId), year);

        Assert.Equal(expected, sessions[DateOnly.Parse(date)]);
    }
}
