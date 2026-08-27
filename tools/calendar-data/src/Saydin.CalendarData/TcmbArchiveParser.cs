using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Saydin.CalendarData;

public static partial class TcmbArchiveParser
{
    public static IReadOnlySet<int> ParseAnnualMonthNumbers(byte[] raw, int expectedYear)
    {
        var html = Encoding.Latin1.GetString(raw);
        var result = new HashSet<int>();
        var candidateCount = 0;
        foreach (Match match in HrefRegex().Matches(html))
        {
            var href = HrefValue(match);
            if (!href.Contains("_tr.html", StringComparison.OrdinalIgnoreCase) || !href.Any(char.IsDigit))
                continue;
            candidateCount++;
            var exact = AnnualMonthHrefRegex().Match(href);
            if (!exact.Success)
                throw new CalendarDataException("tcmb_annual_href_invalid", href);
            var year = int.Parse(exact.Groups["year"].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(exact.Groups["month"].Value, CultureInfo.InvariantCulture);
            var expectedCode = MonthCode(month);
            if (year != expectedYear || exact.Groups["code"].Value != expectedCode)
                throw new CalendarDataException("tcmb_annual_href_out_of_year_or_month", href);
            if (!result.Add(month))
                throw new CalendarDataException("tcmb_annual_month_duplicate", href);
        }
        if (candidateCount == 0 || result.Count == 0)
            throw new CalendarDataException("tcmb_annual_zero_month_links", expectedYear.ToString(CultureInfo.InvariantCulture));
        return result;
    }

    public static IReadOnlySet<DateOnly> ParsePublicationDates(byte[] raw, int expectedYear, int expectedMonth)
    {
        var html = Encoding.Latin1.GetString(raw);
        var result = new HashSet<DateOnly>();
        var candidateCount = 0;
        foreach (Match match in HrefRegex().Matches(html))
        {
            var href = HrefValue(match);
            if (!href.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                continue;
            candidateCount++;
            var exact = DailyXmlHrefRegex().Match(href);
            if (!exact.Success)
                throw new CalendarDataException("tcmb_daily_href_invalid", href);
            DateOnly date;
            try
            {
                date = DateOnly.ParseExact(exact.Groups["date"].Value, "ddMMyyyy", CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                throw new CalendarDataException("tcmb_daily_href_date_invalid", href);
            }
            if (date.Year != expectedYear || date.Month != expectedMonth)
                throw new CalendarDataException("tcmb_daily_href_out_of_month", href);
            if (exact.Groups["folder"].Success
                && exact.Groups["folder"].Value != $"{expectedYear}{expectedMonth:00}")
                throw new CalendarDataException("tcmb_daily_href_path_conflict", href);
            if (!result.Add(date))
                throw new CalendarDataException("tcmb_daily_date_duplicate", href);
        }
        if (candidateCount == 0 || result.Count == 0)
            throw new CalendarDataException("tcmb_month_zero_publication_links", $"{expectedYear}-{expectedMonth:00}");
        return result;
    }

    private static string HrefValue(Match match)
    {
        foreach (var name in new[] { "double", "single", "bare" })
            if (match.Groups[name].Success)
                return match.Groups[name].Value;
        throw new CalendarDataException("tcmb_href_unreadable");
    }

    private static string MonthCode(int month) => month switch
    {
        1 => "Jan",
        2 => "Feb",
        3 => "Mar",
        4 => "Apr",
        5 => "May",
        6 => "Jun",
        7 => "Jul",
        8 => "Aug",
        9 => "Sep",
        10 => "Oct",
        11 => "Nov",
        12 => "Dec",
        _ => throw new CalendarDataException("tcmb_month_invalid", month.ToString(CultureInfo.InvariantCulture)),
    };

    [GeneratedRegex("\\bhref\\s*=\\s*(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)'|(?<bare>[^\\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HrefRegex();

    [GeneratedRegex("^(?<year>[0-9]{4})(?<month>[0-9]{2})/(?<code>[A-Z][a-z]{2})_tr\\.html$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AnnualMonthHrefRegex();

    [GeneratedRegex("^(?:/kurlar/(?<folder>[0-9]{6})/)?(?<date>[0-9]{8})\\.xml$", RegexOptions.CultureInvariant)]
    private static partial Regex DailyXmlHrefRegex();
}
