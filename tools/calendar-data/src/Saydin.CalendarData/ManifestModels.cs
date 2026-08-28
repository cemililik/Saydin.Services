using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saydin.CalendarData;

public sealed class SourceManifest
{
    public required int SchemaVersion { get; init; }
    public required string SnapshotSetId { get; init; }
    public required IReadOnlyList<CalendarDefinition> Calendars { get; init; }
    public required IReadOnlyList<SourceDefinition> Sources { get; init; }
}

public sealed class CalendarDefinition
{
    public required string Code { get; init; }
    public required string CoverageFrom { get; init; }
    public required string CoverageThrough { get; init; }
    public required string OutputPath { get; init; }
}

public sealed class SourceDefinition
{
    public required string Id { get; init; }
    public required string CalendarCode { get; init; }
    public required string Kind { get; init; }
    public required string Role { get; init; }
    public required string Uri { get; init; }
    public required string MediaType { get; init; }
    public required string RetrievedAt { get; init; }
    public required string RawSha256 { get; init; }
    public required string SnapshotPath { get; init; }
    public int? Year { get; init; }
    public int? Month { get; init; }
}

public sealed class ExpectedOutputSet
{
    public required int SchemaVersion { get; init; }
    public required string SnapshotSetId { get; init; }
    public required IReadOnlyList<ExpectedOutput> Outputs { get; init; }
}

public sealed class ExpectedOutput
{
    public required string CalendarCode { get; init; }
    public required string OutputPath { get; init; }
    public required int RowCount { get; init; }
    public required string NormalizedSha256 { get; init; }
    public required string SourceBundleSha256 { get; init; }
}

internal static class ManifestJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static T Read<T>(string path) where T : class
    {
        try
        {
            return Read<T>(File.ReadAllBytes(path), path);
        }
        catch (CalendarDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new CalendarDataException("manifest_invalid", $"{path}: {ex.Message}");
        }
    }

    public static T Read<T>(ReadOnlySpan<byte> content, string identity) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(content, Options)
                   ?? throw new CalendarDataException("manifest_null", identity);
        }
        catch (CalendarDataException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new CalendarDataException("manifest_invalid", $"{identity}: {ex.Message}");
        }
    }

    public static byte[] Write<T>(T value) where T : class =>
        JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(Options)
        {
            WriteIndented = true,
        });
}
