using System.Text;

namespace Saydin.DatabaseSecurity;

public static class SecureSecretFile
{
    private const int MaxConnectionBytes = 8 * 1024;
    private const int MaxPasswordBytes = 512;
    private const int MinPasswordBytes = 24;

    public static string ReadConnectionString(string path) =>
        Read(path, MaxConnectionBytes, minimumBytes: 1, "admin_connection_secret_invalid");

    public static string ReadPassword(string path) =>
        Read(path, MaxPasswordBytes, MinPasswordBytes, "login_password_secret_invalid");

    /// <summary>
    /// Reads and validates a database password without creating an immutable managed string.
    /// The caller owns the returned buffer and must clear it after use.
    /// </summary>
    public static byte[] ReadPasswordBytes(string path)
    {
        byte[]? bytes = null;
        try
        {
            bytes = ReadBytes(
                path, MinPasswordBytes, MaxPasswordBytes, "login_password_secret_invalid");
            ValidatePasswordMaterial(bytes, "login_password_secret_invalid");
            var result = bytes;
            bytes = null;
            return result;
        }
        finally
        {
            if (bytes is not null)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Reads an opaque secret through the same Linux openat2/statx identity contract used
    /// by database credentials. The caller owns the returned buffer and must clear it.
    /// </summary>
    public static byte[] ReadBytes(
        string path,
        int minimumBytes,
        int maximumBytes,
        string rejectionCode)
    {
        var code = IsSafeRejectionCode(rejectionCode)
            ? rejectionCode
            : "secret_file_invalid";

        try
        {
            if (minimumBytes < 1 || maximumBytes < minimumBytes || maximumBytes > 64 * 1024)
                throw Rejected(code);
            if (!Path.IsPathFullyQualified(path) || path.Length > 1024)
                throw Rejected(code);
            var fullPath = Path.GetFullPath(path);
            RejectReparseTraversal(fullPath, code);
            if (!OperatingSystem.IsLinux()) throw Rejected(code);
            return LinuxSecretFile.Read(fullPath, minimumBytes, maximumBytes, code);
        }
        catch (DatabaseSecurityRejectedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             ArgumentException)
        {
            throw Rejected(code);
        }
    }

    private static string Read(string path, int maximumBytes, int minimumBytes, string code)
    {
        byte[]? bytes = null;
        try
        {
            bytes = ReadBytes(path, minimumBytes, maximumBytes, code);
            return DecodeAndValidate(bytes, code);
        }
        catch (DatabaseSecurityRejectedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             DecoderFallbackException or ArgumentException)
        {
            throw Rejected(code);
        }
        finally
        {
            if (bytes is not null)
                Array.Clear(bytes);
        }
    }

    private static string DecodeAndValidate(byte[] bytes, string code)
    {
        try
        {
            ValidatePasswordMaterial(bytes, code);
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static void ValidatePasswordMaterial(ReadOnlySpan<byte> bytes, string code)
    {
        if (bytes.IsEmpty || bytes.Contains((byte)0) ||
            bytes.Contains((byte)'\n') || bytes.Contains((byte)'\r'))
            throw Rejected(code);
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw Rejected(code);
        }
        if (Rune.DecodeFromUtf8(bytes, out var first, out _) !=
                System.Buffers.OperationStatus.Done ||
            Rune.DecodeLastFromUtf8(bytes, out var last, out _) !=
                System.Buffers.OperationStatus.Done ||
            Rune.IsWhiteSpace(first) || Rune.IsWhiteSpace(last))
            throw Rejected(code);
    }

    private static void RejectReparseTraversal(string fullPath, string code)
    {
        for (var directory = Directory.GetParent(fullPath); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw Rejected(code);
    }

    private static DatabaseSecurityRejectedException Rejected(string code) =>
        new(code, DatabaseSecurityFailureKind.SecretRejected);

    private static bool IsSafeRejectionCode(string? code) =>
        !string.IsNullOrWhiteSpace(code)
        && code.Length <= 128
        && code.All(static value => value is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}
