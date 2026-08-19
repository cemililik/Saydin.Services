using System.Text.Json;

namespace Saydin.CalendarData.Tests;

internal static class CalendarDataTestRoot
{
    public static string DataRoot { get; } = FindDataRoot();

    public static SourceManifest ReadManifest() =>
        JsonSerializer.Deserialize<SourceManifest>(
            File.ReadAllBytes(Path.Combine(DataRoot, "source-manifest.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    public static SourceDefinition Source(string id) =>
        ReadManifest().Sources.Single(source => source.Id == id);

    public static byte[] Raw(string id)
    {
        var manifest = ReadManifest();
        var source = manifest.Sources.Single(item => item.Id == id);
        return new SourceSnapshotStore(DataRoot, manifest).Read(source);
    }

    private static string FindDataRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "calendar-data", "data");
            if (File.Exists(Path.Combine(candidate, "source-manifest.json")))
                return candidate;
            candidate = Path.Combine(directory.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "source-manifest.json")))
                return candidate;
        }
        throw new InvalidOperationException("tools/calendar-data/data bulunamadı.");
    }
}
