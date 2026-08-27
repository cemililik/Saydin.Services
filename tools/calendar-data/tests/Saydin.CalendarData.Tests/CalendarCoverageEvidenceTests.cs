using System.Text.Json;

namespace Saydin.CalendarData.Tests;

public sealed class CalendarCoverageEvidenceTests
{
    [Fact]
    public void TcmbWeekdayCoverageWithoutPublicationEvidenceFailsClosed()
    {
        using var temp = new TempRoot();
        CopyTree(CalendarDataTestRoot.DataRoot, temp.Path);
        var manifest = CalendarDataTestRoot.ReadManifest();
        var changed = new SourceManifest
        {
            SchemaVersion = manifest.SchemaVersion,
            SnapshotSetId = manifest.SnapshotSetId,
            Calendars = manifest.Calendars.Select(calendar =>
                calendar.Code == CalendarDataGenerator.TcmbCode
                    ? new CalendarDefinition
                    {
                        Code = calendar.Code,
                        CoverageFrom = calendar.CoverageFrom,
                        CoverageThrough = "2026-08-19",
                        OutputPath = calendar.OutputPath,
                    }
                    : calendar).ToArray(),
            Sources = manifest.Sources,
        };
        File.WriteAllBytes(Path.Combine(temp.Path, "source-manifest.json"),
            JsonSerializer.SerializeToUtf8Bytes(changed,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        var error = Assert.Throws<CalendarDataException>(() =>
            CalendarDataGenerator.Generate(temp.Path));

        Assert.Equal("tcmb_coverage_beyond_last_publication", error.Code);
    }

    [Fact]
    public void TcmbWeekendCoverageWithStalePublicationEvidenceFailsClosed()
    {
        using var temp = new TempRoot();
        CopyTree(CalendarDataTestRoot.DataRoot, temp.Path);
        var manifest = CalendarDataTestRoot.ReadManifest();
        var changed = new SourceManifest
        {
            SchemaVersion = manifest.SchemaVersion,
            SnapshotSetId = manifest.SnapshotSetId,
            Calendars = manifest.Calendars.Select(calendar =>
                calendar.Code == CalendarDataGenerator.TcmbCode
                    ? new CalendarDefinition
                    {
                        Code = calendar.Code,
                        CoverageFrom = calendar.CoverageFrom,
                        CoverageThrough = "2026-08-30",
                        OutputPath = calendar.OutputPath,
                    }
                    : calendar).ToArray(),
            Sources = manifest.Sources,
        };
        File.WriteAllBytes(Path.Combine(temp.Path, "source-manifest.json"),
            JsonSerializer.SerializeToUtf8Bytes(changed,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        var error = Assert.Throws<CalendarDataException>(() =>
            CalendarDataGenerator.Generate(temp.Path));

        Assert.Equal("tcmb_coverage_beyond_last_publication", error.Code);
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == ".DS_Store") continue;
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"saydin-calendar-coverage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
