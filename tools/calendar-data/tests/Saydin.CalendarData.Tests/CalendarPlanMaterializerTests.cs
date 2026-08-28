using System.Text.Json;

namespace Saydin.CalendarData.Tests;

public sealed class CalendarPlanMaterializerTests
{
    [Fact]
    public void TcmbPlan_IsDeterministicIdempotentAndUsesOfficialPublicationCutoff()
    {
        using var temp = new TempRoot();
        var path = Path.Combine(temp.Path, "plans", "tcmb.json");
        var beforePublication = new DateTimeOffset(2026, 8, 18, 13, 29, 59, TimeSpan.Zero);

        CalendarPlanMaterializer.MaterializeTcmb(
            CalendarDataTestRoot.DataRoot, path, beforePublication);
        var first = File.ReadAllBytes(path);
        CalendarPlanMaterializer.MaterializeTcmb(
            CalendarDataTestRoot.DataRoot, path, beforePublication);

        Assert.Equal(first, File.ReadAllBytes(path));
        var plan = JsonSerializer.Deserialize<CalendarAcquisitionPlan>(first,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("cal-tcmb-2026-08-17", plan.SnapshotSetId);
        Assert.Equal("2026-08-17", plan.Calendars.Single(calendar =>
            calendar.Code == CalendarDataGenerator.TcmbCode).CoverageThrough);
        Assert.Equal(["tcmb-annual-2026", "tcmb-month-202608"],
            plan.Sources.Select(source => source.Id));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));
    }

    [Fact]
    public void TcmbPlan_LaterCutoffAtomicallyReplacesPreviousPlan()
    {
        using var temp = new TempRoot();
        var path = Path.Combine(temp.Path, "plans", "tcmb.json");

        CalendarPlanMaterializer.MaterializeTcmb(
            CalendarDataTestRoot.DataRoot, path,
            new DateTimeOffset(2026, 8, 18, 13, 29, 59, TimeSpan.Zero));
        CalendarPlanMaterializer.MaterializeTcmb(
            CalendarDataTestRoot.DataRoot, path,
            new DateTimeOffset(2026, 8, 18, 13, 30, 0, TimeSpan.Zero));

        var plan = JsonSerializer.Deserialize<CalendarAcquisitionPlan>(
            File.ReadAllBytes(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("cal-tcmb-2026-08-18", plan.SnapshotSetId);
        Assert.Equal("2026-08-18", plan.Calendars.Single(calendar =>
            calendar.Code == CalendarDataGenerator.TcmbCode).CoverageThrough);
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(path)!, ".pending-plan-*"));
    }

    [Fact]
    public void TcmbPublicationResolver_UsesLatestEvidenceNotRequestedCalendarDate()
    {
        var manifest = CalendarDataTestRoot.ReadManifest();
        var latest = CalendarDataGenerator.ResolveLatestTcmbPublication(
            CalendarDataTestRoot.DataRoot, manifest, new DateOnly(2026, 8, 16));

        Assert.True(latest <= new DateOnly(2026, 8, 16));
        Assert.Contains(latest, TcmbArchiveParser.ParsePublicationDates(
            CalendarDataTestRoot.Raw("tcmb-month-202608"), 2026, 8));
        Assert.Equal(new DateOnly(2026, 8, 16),
            CalendarDataGenerator.ResolveTcmbCoverageThrough(
                CalendarDataTestRoot.DataRoot, manifest, new DateOnly(2026, 8, 16)));
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"saydin-calendar-plan-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
