using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Saydin.CalendarData;

public enum BistHolidaySession
{
    Partial,
    Closed,
}

public static partial class BistPayCalendarParser
{
    private const double DateColumnLeft = 160;
    private const double OperationColumnLeft = 285;
    private const double OperationColumnRight = 445;

    public static IReadOnlyDictionary<DateOnly, BistHolidaySession> Parse(byte[] raw, int expectedYear)
    {
        try
        {
            using var stream = new MemoryStream(raw, writable: false);
            using var document = PdfDocument.Open(stream);
            if (document.NumberOfPages != 1)
                throw new CalendarDataException("bist_pdf_page_count_invalid", document.NumberOfPages.ToString(CultureInfo.InvariantCulture));
            var page = document.GetPage(1);
            ValidateTitleAndGeometry(page, expectedYear);

            var headerBottom = FindWord(page, "Tarih").BoundingBox.Bottom;
            var dateText = ExtractColumnText(page, DateColumnLeft, OperationColumnLeft, headerBottom);
            var operationText = ExtractColumnText(page, OperationColumnLeft, OperationColumnRight, headerBottom);
            var declaredDates = ExtractDates(dateText, expectedYear, rejectDuplicates: false).ToHashSet();
            var sessions = ParseOperationSemantics(operationText, expectedYear);
            if (!declaredDates.SetEquals(sessions.Keys))
                throw new CalendarDataException("bist_pdf_date_operation_mismatch",
                    $"declared={declaredDates.Count}, operation={sessions.Count}");
            return sessions;
        }
        catch (CalendarDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CalendarDataException("bist_pdf_parse_failed", ex.Message);
        }
    }

    public static IReadOnlyDictionary<DateOnly, BistHolidaySession> ParseOperationSemantics(
        string operationText,
        int expectedYear)
    {
        var result = new Dictionary<DateOnly, BistHolidaySession>();
        var cursor = 0;
        foreach (Match match in OperationStatementRegex().Matches(operationText))
        {
            if (match.Index != cursor && !string.IsNullOrWhiteSpace(operationText[cursor..match.Index]))
                throw new CalendarDataException("bist_operation_unparsed_text", operationText[cursor..match.Index]);
            var statement = match.Value;
            var state = match.Groups["partial"].Success ? BistHolidaySession.Partial : BistHolidaySession.Closed;
            var dates = ExtractDates(statement, expectedYear, rejectDuplicates: true);
            if (dates.Count == 0)
                throw new CalendarDataException("bist_operation_statement_without_date", statement);
            foreach (var date in dates)
            {
                if (result.TryGetValue(date, out var existing))
                    throw new CalendarDataException(existing == state
                        ? "bist_operation_date_duplicate"
                        : "bist_operation_date_conflict", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                result.Add(date, state);
            }
            cursor = match.Index + match.Length;
        }
        if (cursor != operationText.Length && !string.IsNullOrWhiteSpace(operationText[cursor..]))
            throw new CalendarDataException("bist_operation_unparsed_text", operationText[cursor..]);
        if (result.Count == 0)
            throw new CalendarDataException("bist_operation_zero_sessions", expectedYear.ToString(CultureInfo.InvariantCulture));
        return result;
    }

    private static void ValidateTitleAndGeometry(Page page, int expectedYear)
    {
        var text = ContentOrderTextExtractor.GetText(page);
        if (!text.Contains($"PAY PİYASASI {expectedYear} YILI TATİL TABLOSU", StringComparison.Ordinal))
            throw new CalendarDataException("bist_pdf_title_invalid", expectedYear.ToString(CultureInfo.InvariantCulture));

        RequireWordX(page, "Tarih", 200, 240);
        RequireWordX(page, "İşlem", 320, 355);
        RequireWordX(page, "Bilgileri", 350, 390);
        RequireWordX(page, "Takas", 560, 610);
    }

    private static void RequireWordX(Page page, string text, double min, double max)
    {
        var word = FindWord(page, text);
        if (word.BoundingBox.Left < min || word.BoundingBox.Left > max)
            throw new CalendarDataException("bist_pdf_column_geometry_changed", text);
    }

    private static Word FindWord(Page page, string text)
    {
        var matches = page.GetWords().Where(word => word.Text == text).ToArray();
        if (matches.Length != 1)
            throw new CalendarDataException("bist_pdf_header_word_invalid", text);
        return matches[0];
    }

    private static string ExtractColumnText(Page page, double left, double right, double headerBottom)
    {
        var words = page.GetWords()
            .Where(word => word.BoundingBox.Bottom < headerBottom - 1
                           && word.BoundingBox.Left >= left
                           && word.BoundingBox.Left < right)
            .OrderByDescending(word => word.BoundingBox.Bottom)
            .ThenBy(word => word.BoundingBox.Left)
            .Select(word => word.Text)
            .ToArray();
        if (words.Length == 0)
            throw new CalendarDataException("bist_pdf_column_empty", $"{left}-{right}");
        return string.Join(' ', words);
    }

    private static IReadOnlyList<DateOnly> ExtractDates(string text, int expectedYear, bool rejectDuplicates)
    {
        var result = new List<DateOnly>();
        var seen = new HashSet<DateOnly>();
        foreach (Match match in TurkishDateRegex().Matches(text))
        {
            var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
            var month = TurkishMonth(match.Groups["month"].Value);
            var year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
            if (year != expectedYear)
                throw new CalendarDataException("bist_pdf_date_out_of_year", match.Value);
            var date = new DateOnly(year, month, day);
            if (TurkishWeekday(date.DayOfWeek) != match.Groups["weekday"].Value)
                throw new CalendarDataException("bist_pdf_weekday_conflict", match.Value);
            if (rejectDuplicates && !seen.Add(date))
                throw new CalendarDataException("bist_pdf_date_duplicate", match.Value);
            seen.Add(date);
            result.Add(date);
        }
        return result;
    }

    private static int TurkishMonth(string value) => value switch
    {
        "Ocak" => 1,
        "Şubat" => 2,
        "Mart" => 3,
        "Nisan" => 4,
        "Mayıs" => 5,
        "Haziran" => 6,
        "Temmuz" => 7,
        "Ağustos" => 8,
        "Eylül" => 9,
        "Ekim" => 10,
        "Kasım" => 11,
        "Aralık" => 12,
        _ => throw new CalendarDataException("bist_pdf_month_invalid", value),
    };

    private static string TurkishWeekday(DayOfWeek value) => value switch
    {
        DayOfWeek.Monday => "Pazartesi",
        DayOfWeek.Tuesday => "Salı",
        DayOfWeek.Wednesday => "Çarşamba",
        DayOfWeek.Thursday => "Perşembe",
        DayOfWeek.Friday => "Cuma",
        DayOfWeek.Saturday => "Cumartesi",
        DayOfWeek.Sunday => "Pazar",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    [GeneratedRegex(
        "(?<statement>.*?(?:(?<partial>yarım\\s+gün\\s+seans\\s+yapılacaktır\\.)|(?<closed>seans\\s+yapılmayacaktır\\.)))",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex OperationStatementRegex();

    [GeneratedRegex(
        "(?<day>[0-9]{1,2})\\s+(?<month>Ocak|Şubat|Mart|Nisan|Mayıs|Haziran|Temmuz|Ağustos|Eylül|Ekim|Kasım|Aralık)\\s+(?<year>[0-9]{4})\\s+(?<weekday>Pazartesi|Salı|Çarşamba|Perşembe|Cumartesi|Cuma|Pazar)(?!\\p{L})",
        RegexOptions.CultureInvariant)]
    private static partial Regex TurkishDateRegex();
}
