namespace Saydin.CalendarData;

public sealed class CalendarDataException(string code, string? detail = null)
    : Exception(detail is null ? code : $"{code}: {detail}")
{
    public string Code { get; } = code;
}
