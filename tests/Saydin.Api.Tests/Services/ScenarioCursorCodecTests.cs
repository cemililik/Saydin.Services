using System.Buffers.Binary;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Saydin.Api.Models;
using Saydin.Api.Services;

namespace Saydin.Api.Tests.Services;

public sealed class ScenarioCursorCodecTests
{
    [Fact]
    public void EncodeDecode_ValidTuple_RoundTripsCanonicalUtcValue()
    {
        var cursor = new ScenarioCursor(
            new DateTimeOffset(2026, 8, 18, 15, 30, 12, TimeSpan.FromHours(3)).AddTicks(4560),
            Guid.Parse("0198beef-0000-7000-8000-000000000001"));

        var token = ScenarioCursorCodec.Encode(cursor);
        var valid = ScenarioCursorCodec.TryDecode(token, out var decoded);

        token.Should().HaveLength(ScenarioCursorCodec.EncodedLength);
        valid.Should().BeTrue();
        decoded.CreatedAt.Should().Be(cursor.CreatedAt.ToUniversalTime());
        decoded.Id.Should().Be(cursor.Id);
        ScenarioCursorCodec.Encode(decoded).Should().Be(token, "cursor encoding must be canonical");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void TryDecode_InvalidLengthAlphabetOrPadding_Rejects(string token)
    {
        ScenarioCursorCodec.TryDecode(token, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_VersionByteTampering_Rejects()
    {
        var token = ScenarioCursorCodec.Encode(new ScenarioCursor(
            DateTimeOffset.Parse("2026-08-18T12:00:00Z"),
            Guid.Parse("0198beef-0000-7000-8000-000000000001")));
        var replacement = token[0] == 'A' ? 'B' : 'A';
        var tampered = replacement + token[1..];

        ScenarioCursorCodec.TryDecode(tampered, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_StructurallyValidForgedTuple_DecodesBecauseCursorIsNotAuthority()
    {
        // Cursor unsigned by design: a caller can manufacture a structurally valid tuple.
        // Authorization is instead enforced by the mandatory userId predicate in every
        // repository page query (SavedScenarioRepositoryQueryTests locks that contract).
        var forged = ScenarioCursorCodec.Encode(new ScenarioCursor(
            DateTimeOffset.Parse("2025-01-01T00:00:00Z"),
            Guid.Parse("11111111-1111-1111-1111-111111111111")));

        ScenarioCursorCodec.TryDecode(forged, out var decoded).Should().BeTrue();
        decoded.Id.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    }

    [Theory]
    [InlineData(2, 638911152000000000L, "0198beef-0000-7000-8000-000000000001")]
    [InlineData(1, 0L, "0198beef-0000-7000-8000-000000000001")]
    [InlineData(1, 638911152000000000L, "00000000-0000-0000-0000-000000000000")]
    public void TryDecode_InvalidVersionDateOrGuid_Rejects(byte version, long ticks, string id)
    {
        Span<byte> payload = stackalloc byte[25];
        payload[0] = version;
        BinaryPrimitives.WriteInt64BigEndian(payload[1..9], ticks);
        Guid.Parse(id).TryWriteBytes(payload[9..]);
        var token = WebEncoders.Base64UrlEncode(payload);

        ScenarioCursorCodec.TryDecode(token, out _).Should().BeFalse();
    }
}
