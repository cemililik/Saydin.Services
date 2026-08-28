namespace Saydin.Api.Helpers;

internal static class BusinessClock
{
    private static readonly TimeZoneInfo Istanbul =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    public static DateOnly TodayInIstanbul(TimeProvider timeProvider) =>
        DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), Istanbul).DateTime);
}
