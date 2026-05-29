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
    public string? Country { get; init; }
    public string? City { get; init; }
    public string? DeviceOs { get; init; }
    public string? OsVersion { get; init; }
    public string? AppVersion { get; init; }
    public JsonElement? Data { get; init; }
    public short StatusCode { get; init; }
    /// <summary>
    /// F2.1-9 ([C-A-29]): <c>long</c> — int kullanılırsa 24.8 günden uzun süreli
    /// işlemler taşma yapar. Pratikte HTTP istekleri saniyeler içinde tamamlanır
    /// ancak shutdown drain veya hatalı bekleyen task'lerin gerçek süresini görmek
    /// için sınır int'in altında tutulmamalı.
    /// </summary>
    public long? DurationMs { get; init; }
    public string? ErrorCode { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // Navigation
    public User? User { get; init; }
}
