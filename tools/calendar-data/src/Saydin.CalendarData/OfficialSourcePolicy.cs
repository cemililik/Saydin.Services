namespace Saydin.CalendarData;

internal static class OfficialSourcePolicy
{
    public const long HtmlMaximumBytes = 4L * 1024 * 1024;
    public const long PdfMaximumBytes = 16L * 1024 * 1024;

    public static long MaximumBytes(string mediaType) => mediaType switch
    {
        "text/html" => HtmlMaximumBytes,
        "application/pdf" => PdfMaximumBytes,
        _ => throw new CalendarDataException("source_media_type_invalid", mediaType),
    };
}
