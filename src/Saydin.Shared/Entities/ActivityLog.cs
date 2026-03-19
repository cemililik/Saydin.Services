using System.Net;
using System.Text.Json;

namespace Saydin.Shared.Entities;

public sealed class ActivityLog
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid? UserId { get; init; }
    public string DeviceId { get; init; } = default!;
    public string Action { get; init; } = default!;
    public IPAddress? IpAddress { get; init; }
    public string? DeviceOs { get; init; }
    public string? OsVersion { get; init; }
    public string? AppVersion { get; init; }
    public JsonElement? Data { get; init; }
    public short StatusCode { get; init; }
    public int? DurationMs { get; init; }
    public string? ErrorCode { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // Navigation
    public User? User { get; init; }
}
