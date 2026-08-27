using System.Security.Cryptography;
using System.Text;

namespace Saydin.CalendarData.Tests;

public sealed class FailClosedParserTests
{
    [Fact]
    public void TcmbMonth_RejectsZeroLinks()
    {
        var error = Assert.Throws<CalendarDataException>(() =>
            TcmbArchiveParser.ParsePublicationDates("<html><a href='style.css'>x</a></html>"u8.ToArray(), 2025, 6));

        Assert.Equal("tcmb_month_zero_publication_links", error.Code);
    }

    [Fact]
    public void TcmbMonth_RejectsDuplicateDates()
    {
        const string html = "<a href='04062025.xml'>a</a><a href=04062025.xml>b</a>";

        var error = Assert.Throws<CalendarDataException>(() =>
            TcmbArchiveParser.ParsePublicationDates(Encoding.ASCII.GetBytes(html), 2025, 6));

        Assert.Equal("tcmb_daily_date_duplicate", error.Code);
    }

    [Theory]
    [InlineData("<a href='04072025.xml'>x</a>", "tcmb_daily_href_out_of_month")]
    [InlineData("<a href='/kurlar/202507/04062025.xml'>x</a>", "tcmb_daily_href_path_conflict")]
    [InlineData("<a href='https://www.tcmb.gov.tr/kurlar/202506/04062025.xml'>x</a>", "tcmb_daily_href_invalid")]
    [InlineData("<a href='../04062025.xml'>x</a>", "tcmb_daily_href_invalid")]
    [InlineData("<a href='04062025.XML'>x</a>", "tcmb_daily_href_invalid")]
    public void TcmbMonth_RejectsNonAllowlistedOrConflictingPaths(string html, string code)
    {
        var error = Assert.Throws<CalendarDataException>(() =>
            TcmbArchiveParser.ParsePublicationDates(Encoding.ASCII.GetBytes(html), 2025, 6));

        Assert.Equal(code, error.Code);
    }

    [Theory]
    [InlineData("<html></html>", "tcmb_annual_zero_month_links")]
    [InlineData("<a href='202501/Feb_tr.html'>x</a>", "tcmb_annual_href_out_of_year_or_month")]
    [InlineData("<a href='202601/Jan_tr.html'>x</a>", "tcmb_annual_href_out_of_year_or_month")]
    [InlineData("<a href='https://www.tcmb.gov.tr/kurlar/202501/Jan_tr.html'>x</a>", "tcmb_annual_href_invalid")]
    public void TcmbAnnual_RejectsZeroOrNonExactLinks(string html, string code)
    {
        var error = Assert.Throws<CalendarDataException>(() =>
            TcmbArchiveParser.ParseAnnualMonthNumbers(Encoding.ASCII.GetBytes(html), 2025));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void BistOperation_RejectsConflictingSessionSemantics()
    {
        const string text = "5 Haziran 2025 Perşembe yarım gün seans yapılacaktır. "
                            + "5 Haziran 2025 Perşembe seans yapılmayacaktır.";

        var error = Assert.Throws<CalendarDataException>(() =>
            BistPayCalendarParser.ParseOperationSemantics(text, 2025));

        Assert.Equal("bist_operation_date_conflict", error.Code);
    }

    [Fact]
    public void BistOperation_RejectsDuplicateDates()
    {
        const string text = "6 Haziran 2025 Cuma 6 Haziran 2025 Cuma seans yapılmayacaktır.";

        var error = Assert.Throws<CalendarDataException>(() =>
            BistPayCalendarParser.ParseOperationSemantics(text, 2025));

        Assert.Equal("bist_pdf_date_duplicate", error.Code);
    }

    [Theory]
    [InlineData("", "bist_operation_zero_sessions")]
    [InlineData("5 Haziran 2025 Perşembe", "bist_operation_unparsed_text")]
    [InlineData("5 Haziran 2025 Cuma seans yapılmayacaktır.", "bist_pdf_weekday_conflict")]
    [InlineData("5 Haziran 2024 Çarşamba seans yapılmayacaktır.", "bist_pdf_date_out_of_year")]
    [InlineData("yarım gün seans yapılacaktır.", "bist_operation_statement_without_date")]
    public void BistOperation_RejectsMissingOrMalformedSemantics(string text, string code)
    {
        var error = Assert.Throws<CalendarDataException>(() =>
            BistPayCalendarParser.ParseOperationSemantics(text, 2025));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void SnapshotStore_RejectsRawHashMismatch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"saydin-calendar-hash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "snapshots", "sha256"));
        var expectedHash = new string('0', 64);
        var path = $"snapshots/sha256/{expectedHash}.html";
        File.WriteAllBytes(Path.Combine(root, path), "changed"u8.ToArray());
        var source = ValidTcmbMonthlySource(expectedHash, path);
        var manifest = Manifest(source);
        var store = new SourceSnapshotStore(root, manifest);

        var error = Assert.Throws<CalendarDataException>(() => store.Read(source));

        Assert.Equal("snapshot_hash_mismatch", error.Code);
    }

    [Theory]
    [InlineData("https://evil.example/kurlar/202506/Jun_tr.html")]
    [InlineData("http://www.tcmb.gov.tr/kurlar/202506/Jun_tr.html")]
    [InlineData("https://www.tcmb.gov.tr/kurlar/202506/Jun_tr.html?changed=1")]
    [InlineData("https://www.tcmb.gov.tr/kurlar/202505/May_tr.html")]
    public void SnapshotStore_RejectsSourceUrisOutsideExactAllowlist(string uri)
    {
        var raw = "fixture"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(raw));
        var source = ValidTcmbMonthlySource(hash, $"snapshots/sha256/{hash}.html", uri);

        var error = Assert.Throws<CalendarDataException>(() => new SourceSnapshotStore(".", Manifest(source)));

        Assert.Contains(error.Code, new[] { "source_uri_invalid", "source_uri_not_allowlisted" });
    }

    [Theory]
    [InlineData("https://www.tcmb.gov.tr/wps/wcm/connect/TR/TCMB+TR/Main+Menu/Banka+Hakkinda/Sikca%2FSorulan+Sorular")]
    [InlineData("https://www.tcmb.gov.tr/wps/wcm/connect/TR/TCMB+TR/Main+Menu/Banka+Hakkinda/Sikca%252FSorulan+Sorular")]
    public void SnapshotStore_RejectsEncodedSeparatorsInExactPathAllowlist(string uri)
    {
        var raw = "fixture"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(raw));
        var source = new SourceDefinition
        {
            Id = "tcmb-policy-faq",
            CalendarCode = CalendarDataGenerator.TcmbCode,
            Kind = "tcmbPolicyFaq",
            Role = "policy",
            Uri = uri,
            MediaType = "text/html",
            RetrievedAt = "2026-08-18T14:08:37Z",
            RawSha256 = hash,
            SnapshotPath = $"snapshots/sha256/{hash}.html",
        };

        var error = Assert.Throws<CalendarDataException>(() =>
            new SourceSnapshotStore(".", Manifest(source)));

        Assert.Equal("source_uri_not_allowlisted", error.Code);
    }

    private static SourceManifest Manifest(SourceDefinition source) => new()
    {
        SchemaVersion = 1,
        SnapshotSetId = "test",
        Calendars =
        [
            new CalendarDefinition
            {
                Code = CalendarDataGenerator.TcmbCode,
                CoverageFrom = "2025-06-01",
                CoverageThrough = "2025-06-30",
                OutputPath = "normalized/test.csv",
            },
        ],
        Sources = [source],
    };

    private static SourceDefinition ValidTcmbMonthlySource(
        string hash,
        string path,
        string uri = "https://www.tcmb.gov.tr/kurlar/202506/Jun_tr.html") => new()
        {
            Id = "tcmb-month-202506",
            CalendarCode = CalendarDataGenerator.TcmbCode,
            Kind = "tcmbMonthlyArchive",
            Role = "authority",
            Uri = uri,
            MediaType = "text/html",
            RetrievedAt = "2026-08-18T14:08:37Z",
            RawSha256 = hash,
            SnapshotPath = path,
            Year = 2025,
            Month = 6,
        };
}
