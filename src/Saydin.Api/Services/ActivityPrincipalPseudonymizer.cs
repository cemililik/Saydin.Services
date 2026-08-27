using System.Security.Cryptography;
using System.Text;
using Saydin.Api.Options;
using Saydin.DatabaseSecurity;

namespace Saydin.Api.Services;

public interface IActivityPrincipalPseudonymizer
{
    string Pseudonymize(Guid principalId);
}

public interface IQuotaSubjectPseudonymizer
{
    string PseudonymizeQuotaSubject(string subject);
}

/// <summary>
/// Stable activity-log correlation authority. This key has an independent lifecycle
/// from installation bearer-verifier keys, so credential keyring rotation cannot split
/// one principal's audit trail. Only a domain-separated truncated HMAC is emitted.
/// </summary>
public sealed class ActivityPrincipalPseudonymizer :
    IActivityPrincipalPseudonymizer,
    IQuotaSubjectPseudonymizer,
    IDisposable
{
    private const int KeyBytes = 32;
    private readonly byte[] key;

    private ActivityPrincipalPseudonymizer(byte[] key) => this.key = key;

    public static ActivityPrincipalPseudonymizer Load(ActivityPrincipalPseudonymOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.SecretFile)
            || !Path.IsPathFullyQualified(options.SecretFile))
            throw InvalidSecret();

        byte[]? loaded = null;
        try
        {
            loaded = SecureSecretFile.ReadBytes(
                options.SecretFile,
                minimumBytes: KeyBytes,
                maximumBytes: KeyBytes,
                rejectionCode: "activity_principal_pseudonym_secret_invalid");
            var result = new ActivityPrincipalPseudonymizer(loaded);
            loaded = null;
            return result;
        }
        catch (DatabaseSecurityRejectedException)
        {
            throw InvalidSecret();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw InvalidSecret();
        }
        finally
        {
            if (loaded is not null)
                CryptographicOperations.ZeroMemory(loaded);
        }
    }

    public string Pseudonymize(Guid principalId)
    {
        Span<byte> payload = stackalloc byte[48];
        payload.Clear();
        Encoding.ASCII.GetBytes("saydin.activity.principal.v1", payload);
        principalId.TryWriteBytes(payload[32..]);
        var digest = HMACSHA256.HashData(key, payload);
        try
        {
            return $"p1:{Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant()}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public string PseudonymizeQuotaSubject(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (subject.Length > 256 || subject.Any(char.IsControl))
            throw new ArgumentException("Invalid quota subject.", nameof(subject));

        var subjectBytes = Encoding.UTF8.GetBytes(subject);
        var domainBytes = Encoding.ASCII.GetBytes("saydin.quota.subject.v1");
        var payload = new byte[checked(domainBytes.Length + 1 + subjectBytes.Length)];
        domainBytes.CopyTo(payload, 0);
        subjectBytes.CopyTo(payload, domainBytes.Length + 1);
        var digest = HMACSHA256.HashData(key, payload);
        try
        {
            return $"q1:{Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant()}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(subjectBytes);
            CryptographicOperations.ZeroMemory(domainBytes);
        }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(key);

    private static InvalidOperationException InvalidSecret() =>
        new("Activity principal pseudonym secret file is invalid.");
}
