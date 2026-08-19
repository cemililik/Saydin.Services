using System.Security.Cryptography;

namespace Saydin.CalendarData.Tests;

public sealed class NormalizedCalendarReplayTests
{
    [Fact]
    public void VerifiedBundle_RejectsCoordinatedManifestAndExpectedContractReplacement()
    {
        var target = Path.Combine(Path.GetTempPath(),
            $"saydin-calendar-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        try
        {
            foreach (var file in Directory.EnumerateFiles(
                         CalendarDataTestRoot.DataRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(CalendarDataTestRoot.DataRoot, file);
                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            var bundle = CalendarDataGenerator.LoadVerified(target);
            foreach (var name in new[] { "source-manifest.json", "expected-output.json" })
            {
                var path = Path.Combine(target, name);
                File.WriteAllText(path, File.ReadAllText(path)
                    .Replace("cal-001-2026-08-17", "cal-001-raced-contract",
                        StringComparison.Ordinal));
            }

            var exception = Assert.Throws<CalendarDataException>(bundle.EnsureInputsUnchanged);
            Assert.StartsWith("verified_input_changed", exception.Message);
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void OfflineGenerator_IsDeterministicAndMatchesCommittedContracts()
    {
        var first = CalendarDataGenerator.Generate(CalendarDataTestRoot.DataRoot);
        var second = CalendarDataGenerator.Generate(CalendarDataTestRoot.DataRoot);

        var verified = CalendarDataGenerator.LoadVerified(CalendarDataTestRoot.DataRoot);
        verified.EnsureInputsUnchanged();
        Assert.Equal(first.Select(item => item.NormalizedSha256),
            verified.Calendars.Select(item => item.NormalizedSha256));
        Assert.Equal(first.Select(item => item.NormalizedSha256), second.Select(item => item.NormalizedSha256));
        Assert.All(first.Zip(second), pair => Assert.Equal(pair.First.Content, pair.Second.Content));

        var tcmb = first.Single(item => item.CalendarCode == CalendarDataGenerator.TcmbCode);
        Assert.Equal(7_534, tcmb.RowCount);
        Assert.Equal("de8f0ff7654ae4972d081f1d2a225de6997986cd8297736715b3e71bfda1b1da", tcmb.NormalizedSha256);
        Assert.Equal(tcmb.NormalizedSha256, Convert.ToHexStringLower(SHA256.HashData(tcmb.Content)));

        var bist = first.Single(item => item.CalendarCode == CalendarDataGenerator.BistCode);
        Assert.Equal(1_096, bist.RowCount);
        Assert.Equal("82c463fec5abf9663b689d863da9e7efcd93b976747e869c5aaeccfe7a4feed0", bist.NormalizedSha256);
        Assert.Equal(bist.NormalizedSha256, Convert.ToHexStringLower(SHA256.HashData(bist.Content)));
    }

    [Fact]
    public void NormalizedOutputs_HaveOneOrderedRowPerCoveredCalendarDay()
    {
        var calendars = CalendarDataGenerator.Generate(CalendarDataTestRoot.DataRoot);

        AssertCoverage(calendars.Single(item => item.CalendarCode == CalendarDataGenerator.TcmbCode),
            new DateOnly(2006, 1, 1), new DateOnly(2026, 8, 17));
        AssertCoverage(calendars.Single(item => item.CalendarCode == CalendarDataGenerator.BistCode),
            new DateOnly(2024, 1, 1), new DateOnly(2026, 12, 31));
    }

    [Theory]
    [InlineData("tcmb_indicative_fx", "2025-06-04", true, "publication")]
    [InlineData("tcmb_indicative_fx", "2025-06-05", false, "no_publication")]
    [InlineData("tcmb_indicative_fx", "2025-06-09", false, "no_publication")]
    [InlineData("tcmb_indicative_fx", "2025-06-10", true, "publication")]
    [InlineData("tcmb_indicative_fx", "2026-03-19", false, "no_publication")]
    [InlineData("tcmb_indicative_fx", "2026-03-22", false, "no_publication")]
    [InlineData("tcmb_indicative_fx", "2026-03-23", true, "publication")]
    [InlineData("bist_pay_xist", "2024-04-09", true, "partial_session")]
    [InlineData("bist_pay_xist", "2024-04-10", false, "closed")]
    [InlineData("bist_pay_xist", "2025-03-29", false, "closed")]
    [InlineData("bist_pay_xist", "2025-06-05", true, "partial_session")]
    [InlineData("bist_pay_xist", "2025-06-09", false, "closed")]
    [InlineData("bist_pay_xist", "2026-03-19", true, "partial_session")]
    [InlineData("bist_pay_xist", "2026-03-20", false, "closed")]
    public void NormalizedOutputs_PreserveAnchorSemantics(
        string calendarCode, string date, bool expectedObservation, string expectedState)
    {
        var calendar = CalendarDataGenerator.Generate(CalendarDataTestRoot.DataRoot)
            .Single(item => item.CalendarCode == calendarCode);
        var row = ReadRows(calendar).Single(item => item.Date == DateOnly.Parse(date));

        Assert.Equal(expectedObservation, row.ObservationExpected);
        Assert.Equal(expectedState, row.MarketState);
    }

    private static void AssertCoverage(NormalizedCalendar calendar, DateOnly from, DateOnly through)
    {
        var rows = ReadRows(calendar);
        Assert.Equal(calendar.RowCount, rows.Count);
        Assert.Equal(from, rows[0].Date);
        Assert.Equal(through, rows[^1].Date);
        for (var index = 1; index < rows.Count; index++)
            Assert.Equal(rows[index - 1].Date.AddDays(1), rows[index].Date);
    }

    private static IReadOnlyList<Row> ReadRows(NormalizedCalendar calendar)
    {
        using var reader = new StringReader(System.Text.Encoding.UTF8.GetString(calendar.Content));
        Assert.Equal("calendar_code,date,observation_expected,market_state,reason_code,evidence_raw_sha256", reader.ReadLine());
        var rows = new List<Row>();
        while (reader.ReadLine() is { } line)
        {
            var columns = line.Split(',');
            Assert.Equal(6, columns.Length);
            Assert.Equal(calendar.CalendarCode, columns[0]);
            Assert.Equal(64, columns[5].Length);
            rows.Add(new Row(DateOnly.Parse(columns[1]), bool.Parse(columns[2]), columns[3]));
        }
        return rows;
    }

    private sealed record Row(DateOnly Date, bool ObservationExpected, string MarketState);
}
