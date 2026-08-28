using System.Text;
using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseRoleBootstrap.Tests;

public sealed class PostgresScramSha256VerifierTests
{
    [Fact]
    public void Deterministic_postgresql_scram_vector_is_exact_and_contains_no_plaintext()
    {
        var password = Encoding.UTF8.GetBytes("Correct-Horse-Battery-Staple-123!");
        var salt = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();

        var verifier = PostgresScramSha256Verifier.Create(password, salt);

        Assert.Equal(
            "SCRAM-SHA-256$4096:AAECAwQFBgcICQoLDA0ODw==$" +
            "e/5u63kN/qBNeWgz+4ahFLysYFtpndd/Rx8s9mcW32o=:" +
            "/c+6H+HEABD/d1QBvKKBICzfSRt5Aw5HmKkIxPL9urE=",
            verifier);
        Assert.DoesNotContain("Correct-Horse", verifier, StringComparison.Ordinal);
        Assert.True(PostgresScramSha256Verifier.IsCanonical(verifier));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Correct-Horse-Battery-Staple-123!")]
    [InlineData("SCRAM-SHA-256$4095:AAECAwQFBgcICQoLDA0ODw==$bad:bad")]
    public void Noncanonical_or_plaintext_material_is_never_accepted_as_a_verifier(string? value)
    {
        Assert.False(PostgresScramSha256Verifier.IsCanonical(value));
    }

    [Theory]
    [InlineData("too-short")]
    [InlineData(" Correct-Horse-Battery-Staple-123!")]
    [InlineData("Correct-Horse-Battery-Staple-123!\n")]
    public void Invalid_password_material_is_rejected_without_echo(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);

        var exception = Assert.Throws<DatabaseSecurityRejectedException>(() =>
            PostgresScramSha256Verifier.Create(bytes));

        Assert.Equal("login_password_secret_invalid", exception.Code);
        Assert.DoesNotContain(value, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_utf8_password_material_is_rejected_fail_closed()
    {
        var bytes = Enumerable.Repeat((byte)'A', 24).Append((byte)0xff).ToArray();

        var exception = Assert.Throws<DatabaseSecurityRejectedException>(() =>
            PostgresScramSha256Verifier.Create(bytes));

        Assert.Equal("login_password_secret_invalid", exception.Code);
    }
}
