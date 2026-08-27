using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Saydin.Api.Options;
using Saydin.DatabaseSecurity;

namespace Saydin.Api.Services;

public sealed record CredentialHashCandidate(short KeyVersion, byte[] SecretHash);

public sealed record GeneratedInstallationCredential(string Token, byte[] Secret) : IDisposable
{
    public void Dispose() => CryptographicOperations.ZeroMemory(Secret);

    public override string ToString() =>
        "GeneratedInstallationCredential { Token = [REDACTED], Secret = [REDACTED] }";
}

public interface IInstallationCredentialKeyring
{
    short ActiveKeyVersion { get; }
    GeneratedInstallationCredential Generate();
    bool TryDecode(string token, out byte[] secret);
    CredentialHashCandidate HashActive(ReadOnlySpan<byte> secret);
    IReadOnlyList<CredentialHashCandidate> HashAccepted(ReadOnlySpan<byte> secret);
}

/// <summary>
/// Installation bearer secrets are generated in-process and immediately reduced to
/// keyed hashes before crossing the database boundary. The key file is strict JSON:
/// an object whose property names are positive Int16 versions and whose values are
/// base64-encoded 32-byte HMAC keys. At most three versions are accepted during rotation.
/// </summary>
public sealed class InstallationCredentialKeyring : IInstallationCredentialKeyring, IDisposable
{
    internal const int CredentialByteLength = 32;
    internal const int CredentialTextLength = 43;
    private const int MaxAcceptedKeys = 3;
    private const int MaxSecretFileBytes = 4096;

    private readonly IReadOnlyDictionary<short, byte[]> _keys;

    private InstallationCredentialKeyring(
        short activeKeyVersion,
        IReadOnlyDictionary<short, byte[]> keys)
    {
        ActiveKeyVersion = activeKeyVersion;
        _keys = keys;
    }

    public short ActiveKeyVersion { get; }

    public static InstallationCredentialKeyring Load(InstallationCredentialOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.SecretFile)
            || !Path.IsPathFullyQualified(options.SecretFile))
        {
            throw InvalidKeyring();
        }

        Dictionary<short, byte[]>? keys = null;
        byte[]? serializedKeyring = null;
        try
        {
            serializedKeyring = SecureSecretFile.ReadBytes(
                options.SecretFile,
                minimumBytes: 2,
                maximumBytes: MaxSecretFileBytes,
                rejectionCode: "installation_keyring_secret_invalid");
            using var document = JsonDocument.Parse(serializedKeyring, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 2,
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw InvalidKeyring();

            keys = new Dictionary<short, byte[]>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!short.TryParse(
                        property.Name,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var version)
                    || version <= 0
                    || property.Name != version.ToString(CultureInfo.InvariantCulture)
                    || property.Value.ValueKind != JsonValueKind.String
                    || keys.ContainsKey(version))
                {
                    throw InvalidKeyring();
                }

                byte[] key;
                try
                {
                    // Decode directly from JsonDocument's UTF-8 payload. Calling
                    // GetString()+Convert would leave an immutable managed string
                    // containing the HMAC key until a later GC.
                    key = property.Value.GetBytesFromBase64();
                }
                catch (FormatException)
                {
                    throw InvalidKeyring();
                }

                if (key.Length != CredentialByteLength)
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw InvalidKeyring();
                }

                keys.Add(version, key);
                if (keys.Count > MaxAcceptedKeys)
                    throw InvalidKeyring();
            }

            if (keys.Count == 0
                || !keys.ContainsKey(options.ActiveKeyVersion)
                || options.ActiveKeyVersion != keys.Keys.Max())
                throw InvalidKeyring();

            var loaded = new InstallationCredentialKeyring(options.ActiveKeyVersion, keys);
            keys = null;
            return loaded;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (DatabaseSecurityRejectedException)
        {
            throw InvalidKeyring();
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or FormatException)
        {
            throw InvalidKeyring();
        }
        finally
        {
            if (serializedKeyring is not null)
                CryptographicOperations.ZeroMemory(serializedKeyring);
            if (keys is not null)
            {
                foreach (var key in keys.Values)
                    CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    public GeneratedInstallationCredential Generate()
    {
        var secret = RandomNumberGenerator.GetBytes(CredentialByteLength);
        return new GeneratedInstallationCredential(ToBase64Url(secret), secret);
    }

    public bool TryDecode(string token, out byte[] secret)
    {
        secret = [];
        if (token.Length != CredentialTextLength
            || token.Any(static character => !IsBase64Url(character))
            || (Base64UrlValue(token[^1]) & 0b11) != 0)
            return false;

        try
        {
            secret = Convert.FromBase64String(
                string.Concat(token.Replace('-', '+').Replace('_', '/'), "="));
            if (secret.Length == CredentialByteLength)
                return true;

            CryptographicOperations.ZeroMemory(secret);
            secret = [];
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public CredentialHashCandidate HashActive(ReadOnlySpan<byte> secret)
    {
        EnsureCredentialLength(secret);
        return new CredentialHashCandidate(ActiveKeyVersion, Hash(ActiveKeyVersion, secret));
    }

    public IReadOnlyList<CredentialHashCandidate> HashAccepted(ReadOnlySpan<byte> secret)
    {
        EnsureCredentialLength(secret);
        var candidates = new List<CredentialHashCandidate>(_keys.Count);
        try
        {
            foreach (var entry in _keys.OrderByDescending(static item => item.Key))
                candidates.Add(new CredentialHashCandidate(
                    entry.Key,
                    HMACSHA256.HashData(entry.Value, secret)));
            return candidates;
        }
        catch
        {
            foreach (var candidate in candidates)
                CryptographicOperations.ZeroMemory(candidate.SecretHash);
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values)
            CryptographicOperations.ZeroMemory(key);
    }

    private byte[] Hash(short version, ReadOnlySpan<byte> secret)
        => HMACSHA256.HashData(_keys[version], secret);

    private static void EnsureCredentialLength(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != CredentialByteLength)
            throw new ArgumentException("Installation credential length is invalid.", nameof(secret));
    }

    private static string ToBase64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsBase64Url(char value)
        => value is >= 'A' and <= 'Z'
           or >= 'a' and <= 'z'
           or >= '0' and <= '9'
           or '-' or '_';

    private static int Base64UrlValue(char value) => value switch
    {
        >= 'A' and <= 'Z' => value - 'A',
        >= 'a' and <= 'z' => value - 'a' + 26,
        >= '0' and <= '9' => value - '0' + 52,
        '-' => 62,
        '_' => 63,
        _ => -1,
    };

    private static InvalidOperationException InvalidKeyring(Exception? inner = null)
        => new("Installation credential keyring secret file is invalid.", inner);
}
