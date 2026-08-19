using System.Buffers.Binary;
using Microsoft.AspNetCore.WebUtilities;
using Saydin.Api.Models;

namespace Saydin.Api.Services;

/// <summary>
/// Cursor wire format v1: one version byte, eight UTC <see cref="DateTime.Ticks"/>
/// bytes and sixteen GUID bytes, encoded as unpadded Base64Url.
///
/// The value is intentionally opaque to API consumers but is not an authorization
/// token. Every repository query is independently scoped to the current user's ID.
/// Strict fixed-length and canonical re-encoding checks prevent ambiguous or
/// amplification-oriented inputs without introducing a deployment secret/key ring.
/// </summary>
internal static class ScenarioCursorCodec
{
    internal const int EncodedLength = 34;
    private const byte CurrentVersion = 1;
    private const int PayloadLength = 25;

    public static string Encode(ScenarioCursor cursor)
    {
        Span<byte> payload = stackalloc byte[PayloadLength];
        payload[0] = CurrentVersion;
        BinaryPrimitives.WriteInt64BigEndian(payload[1..9], cursor.CreatedAt.UtcDateTime.Ticks);
        cursor.Id.TryWriteBytes(payload[9..]);
        return WebEncoders.Base64UrlEncode(payload);
    }

    public static bool TryDecode(string? token, out ScenarioCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(token) || token.Length != EncodedLength)
            return false;

        byte[] payload;
        try
        {
            payload = WebEncoders.Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            return false;
        }

        if (payload.Length != PayloadLength || payload[0] != CurrentVersion)
            return false;

        var ticks = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(1, 8));
        if (ticks < DateTimeOffset.UnixEpoch.UtcDateTime.Ticks
            || ticks > DateTimeOffset.MaxValue.UtcDateTime.Ticks)
            return false;

        var id = new Guid(payload.AsSpan(9, 16));
        if (id == Guid.Empty)
            return false;

        var decoded = new ScenarioCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        if (!string.Equals(Encode(decoded), token, StringComparison.Ordinal))
            return false;

        cursor = decoded;
        return true;
    }
}
