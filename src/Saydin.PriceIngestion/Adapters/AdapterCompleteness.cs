using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.Adapters;

internal static class AdapterCompleteness
{
    public static AdapterOutcome<PricePoint> Price(
        PriceFetchRequest request,
        IReadOnlyList<PricePoint> points,
        int rawItemCount,
        int rejectedCount = 0,
        IReadOnlySet<DateOnly>? providerNoDataDates = null,
        string noDataCode = "calendar_closed")
    {
        var noData = request.CalendarClosedDates
            .Concat(providerNoDataDates ?? new HashSet<DateOnly>())
            .ToHashSet();
        var requested = Dates(request.From, request.To).ToHashSet();

        if (!noData.IsSubsetOf(requested))
            return AdapterOutcome<PricePoint>.PermanentFailure(
                "provider_no_data_out_of_range", rawItemCount: rawItemCount);

        var acceptedDates = points.Select(point => point.PriceDate).ToArray();
        var acceptedSet = acceptedDates.ToHashSet();
        var expected = requested.Except(noData).ToHashSet();
        var duplicateCount = acceptedDates.Length - acceptedSet.Count;
        var outOfRangeCount = acceptedSet.Count(date => !expected.Contains(date));
        var totalRejected = rejectedCount + duplicateCount + outOfRangeCount;

        if (acceptedSet.SetEquals(expected) && totalRejected == 0)
        {
            return points.Count == 0
                ? AdapterOutcome<PricePoint>.ExpectedNoData(noData, noDataCode, rawItemCount)
                : AdapterOutcome<PricePoint>.Data(points, rawItemCount, noData);
        }

        var missing = expected.Count(date => !acceptedSet.Contains(date));
        return AdapterOutcome<PricePoint>.PartialRejected(
            points, rawItemCount, Math.Max(1, totalRejected + missing),
            "incomplete_observation_set",
            $"missing={missing};duplicate={duplicateCount};out_of_range={outOfRangeCount}", noData);
    }

    public static IReadOnlyList<DateOnly> Dates(DateOnly from, DateOnly to)
    {
        var dates = new List<DateOnly>();
        for (var date = from; date <= to; date = date.AddDays(1))
            dates.Add(date);
        return dates;
    }
}
