using System.Security.Cryptography;

namespace Saydin.DatabaseSecurity;

/// <summary>
/// Produces the verifier format accepted by PostgreSQL's PASSWORD clause without
/// putting the plaintext password in a SQL command or server-observable query text.
/// </summary>
public static class PostgresScramSha256Verifier
{
    public const int Iterations = 4096;
    private const int SaltBytes = 16;
    private const int DerivedKeyBytes = 32;

    public static string Create(ReadOnlySpan<byte> password)
    {
        Span<byte> salt = stackalloc byte[SaltBytes];
        RandomNumberGenerator.Fill(salt);
        return Create(password, salt);
    }

    public static bool IsCanonical(string? verifier)
    {
        if (verifier is null || verifier.Length > 256)
            return false;
        var sections = verifier.Split('$');
        if (sections.Length != 3 || sections[0] != "SCRAM-SHA-256")
            return false;
        var iterationAndSalt = sections[1].Split(':');
        var keys = sections[2].Split(':');
        if (iterationAndSalt.Length != 2 || keys.Length != 2 ||
            iterationAndSalt[0] != Iterations.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            return false;
        return IsCanonicalBase64(iterationAndSalt[1], SaltBytes) &&
               IsCanonicalBase64(keys[0], DerivedKeyBytes) &&
               IsCanonicalBase64(keys[1], DerivedKeyBytes);
    }

    internal static string Create(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt)
    {
        ValidatePassword(password);
        if (salt.Length != SaltBytes)
            throw new DatabaseSecurityRejectedException(
                "scram_salt_invalid", DatabaseSecurityFailureKind.InvalidArguments);

        byte[]? saltedPassword = null;
        byte[]? clientKey = null;
        byte[]? storedKey = null;
        byte[]? serverKey = null;
        try
        {
            saltedPassword = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, Iterations, HashAlgorithmName.SHA256, DerivedKeyBytes);
            clientKey = HMACSHA256.HashData(saltedPassword, "Client Key"u8);
            storedKey = SHA256.HashData(clientKey);
            serverKey = HMACSHA256.HashData(saltedPassword, "Server Key"u8);
            return $"SCRAM-SHA-256${Iterations}:{Convert.ToBase64String(salt)}$" +
                   $"{Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
        }
        finally
        {
            Clear(saltedPassword);
            Clear(clientKey);
            Clear(storedKey);
            Clear(serverKey);
        }
    }

    private static void ValidatePassword(ReadOnlySpan<byte> password)
    {
        if (password.Length is < 24 or > 512)
            throw InvalidPassword();
        SecureSecretFile.ValidatePasswordMaterial(password, "login_password_secret_invalid");
    }

    private static void Clear(byte[]? value)
    {
        if (value is not null)
            CryptographicOperations.ZeroMemory(value);
    }

    private static bool IsCanonicalBase64(string value, int expectedBytes)
    {
        Span<byte> decoded = stackalloc byte[DerivedKeyBytes];
        return Convert.TryFromBase64String(value, decoded, out var written) &&
               written == expectedBytes &&
               string.Equals(
                   Convert.ToBase64String(decoded[..written]), value, StringComparison.Ordinal);
    }

    private static DatabaseSecurityRejectedException InvalidPassword() =>
        new("login_password_secret_invalid", DatabaseSecurityFailureKind.SecretRejected);
}
