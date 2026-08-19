using System.Security.Cryptography;
using Oci.Common;
using Oci.Common.Auth;
using Oci.KeymanagementService;
using Oci.KeymanagementService.Models;
using Oci.KeymanagementService.Requests;

namespace Saydin.DataQualityAudit;

internal sealed record EvidenceSigningIdentity(
    string Provider,
    string KeyIdentity,
    string EvidenceKeyId,
    byte[] PublicSubjectPublicKeyInfo);

internal interface IEvidenceSigner : IAsyncDisposable
{
    EvidenceSigningIdentity Identity { get; }

    Task<byte[]> SignAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);
}

internal sealed class LocalPemEvidenceSigner : IEvidenceSigner
{
    private readonly string privateKeyPath;

    public LocalPemEvidenceSigner(string privateKeyPath)
    {
        this.privateKeyPath = privateKeyPath;
        var publicKey = AuditCryptography.ReadPrivateP256PublicKey(privateKeyPath);
        var keyId = AuditCryptography.Sha256Hex(publicKey);
        Identity = new EvidenceSigningIdentity(
            "local-pem", $"local-pem:{keyId}", keyId, publicKey);
    }

    public EvidenceSigningIdentity Identity { get; }

    public Task<byte[]> SignAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var signature = AuditCryptography.Sign(payload.Span, privateKeyPath);
        if (!AuditCryptography.VerifyWithSubjectPublicKeyInfo(
                payload.Span, signature, Identity.PublicSubjectPublicKeyInfo))
            throw EvidenceFailure("local_signature_verification_failed");
        return Task.FromResult(signature);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static AuditRejectedException EvidenceFailure(string code) =>
        new(code, AuditExitCodes.EvidenceFailure);
}

internal sealed record OciKmsSignatureResponse(
    string KeyId,
    string KeyVersionId,
    string Algorithm,
    string Base64Signature);

internal interface IOciKmsSigningClient : IDisposable
{
    Task<OciKmsSignatureResponse> SignDigestAsync(
        string keyId,
        string keyVersionId,
        ReadOnlyMemory<byte> sha256Digest,
        CancellationToken cancellationToken);
}

internal sealed class OciSdkKmsSigningClient : IOciKmsSigningClient
{
    private readonly KmsCryptoClient client;

    public OciSdkKmsSigningClient(OciKmsSignerConfiguration options)
    {
        // Instance principal obtains short-lived request-signing material from OCI
        // metadata/federation. No customer KMS private key enters this process.
        IBasicAuthenticationDetailsProvider authentication =
            new InstancePrincipalsAuthenticationDetailsProvider();
        client = new KmsCryptoClient(
            authentication,
            new ClientConfiguration
            {
                TimeoutMillis = checked((int)options.Timeout.TotalMilliseconds),
                ResponseContentBufferBytes = 16 * 1024,
                ClientUserAgent = "Saydin-DataQualityAudit/1",
                RetryConfiguration = null,
            },
            options.CryptoEndpoint);
    }

    public async Task<OciKmsSignatureResponse> SignDigestAsync(
        string keyId,
        string keyVersionId,
        ReadOnlyMemory<byte> sha256Digest,
        CancellationToken cancellationToken)
    {
        var response = await client.Sign(new SignRequest
        {
            SignDataDetails = new SignDataDetails
            {
                KeyId = keyId,
                KeyVersionId = keyVersionId,
                Message = Convert.ToBase64String(sha256Digest.Span),
                MessageType = SignDataDetails.MessageTypeEnum.Digest,
                SigningAlgorithm = SignDataDetails.SigningAlgorithmEnum.EcdsaSha256,
                LoggingContext = new Dictionary<string, string>
                {
                    ["component"] = "saydin-data-quality-audit",
                },
            },
        }, retryConfiguration: null, cancellationToken).ConfigureAwait(false);
        var signed = response.SignedData;
        return signed is null
            ? throw EvidenceFailure("kms_signature_response_invalid")
            : new OciKmsSignatureResponse(
                signed.KeyId,
                signed.KeyVersionId,
                signed.SigningAlgorithm?.ToString() ?? string.Empty,
                signed.Signature);
    }

    public void Dispose() => client.Dispose();

    private static AuditRejectedException EvidenceFailure(string code) =>
        new(code, AuditExitCodes.EvidenceFailure);
}

internal sealed class OciKmsEvidenceSigner : IEvidenceSigner
{
    private readonly OciKmsSignerConfiguration options;
    private readonly IOciKmsSigningClient client;

    public OciKmsEvidenceSigner(
        OciKmsSignerConfiguration options,
        IOciKmsSigningClient client)
    {
        this.options = options;
        this.client = client;
        var publicKey = AuditCryptography.ReadPublicP256Key(
            options.PublicKeyFile, AuditExitCodes.EvidenceFailure);
        var evidenceKeyId = AuditCryptography.Sha256Hex(publicKey);
        if (!options.AllowedEvidenceKeyIds.Contains(evidenceKeyId))
            throw EvidenceFailure("evidence_key_not_allowed");
        Identity = new EvidenceSigningIdentity(
            "oci-kms-instance-principal",
            $"{options.KeyId}:{options.KeyVersionId}",
            evidenceKeyId,
            publicKey);
    }

    public EvidenceSigningIdentity Identity { get; }

    public async Task<byte[]> SignAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        var digest = SHA256.HashData(payload.Span);
        try
        {
            var response = await client.SignDigestAsync(
                options.KeyId, options.KeyVersionId, digest, timeout.Token).ConfigureAwait(false);
            if (!string.Equals(response.KeyId, options.KeyId, StringComparison.Ordinal) ||
                !string.Equals(response.KeyVersionId, options.KeyVersionId, StringComparison.Ordinal) ||
                !string.Equals(response.Algorithm, "EcdsaSha256", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(response.Base64Signature) ||
                response.Base64Signature.Length > 512)
                throw EvidenceFailure("kms_signature_response_invalid");

            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(response.Base64Signature);
            }
            catch (FormatException)
            {
                throw EvidenceFailure("kms_signature_response_invalid");
            }
            if (!string.Equals(
                    Convert.ToBase64String(raw), response.Base64Signature, StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(raw);
                throw EvidenceFailure("kms_signature_response_invalid");
            }
            var signature = AuditCryptography.NormalizeP256Signature(raw);
            CryptographicOperations.ZeroMemory(raw);
            if (!AuditCryptography.VerifyHashWithSubjectPublicKeyInfo(
                    digest, signature, Identity.PublicSubjectPublicKeyInfo))
                throw EvidenceFailure("kms_signature_verification_failed");
            return signature;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw EvidenceFailure("kms_sign_timeout");
        }
        catch (AuditRejectedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw EvidenceFailure("kms_sign_denied");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    private static AuditRejectedException EvidenceFailure(string code) =>
        new(code, AuditExitCodes.EvidenceFailure);
}

internal static class EvidenceSignerFactory
{
    public static IEvidenceSigner Create(
        ScanOptions scan,
        VerifiedAuditInput input,
        Func<string, string?> environment,
        Func<OciKmsSignerConfiguration, IOciKmsSigningClient>? kmsClientFactory = null)
    {
        var configured = scan.EvidenceSigner ??
            (scan.EvidencePrivateKeyFile is { } path
                ? new LocalPemSignerConfiguration(path)
                : throw Invalid("signer_configuration_missing"));
        var production = string.Equals(
            input.Manifest.Target.Environment, "production", StringComparison.OrdinalIgnoreCase);
        if (production)
        {
            RejectProductionRawKeyEnvironment(environment);
            if (configured is not OciKmsSignerConfiguration || scan.EvidencePrivateKeyFile is not null)
                throw Invalid("production_signer_mode_rejected");
        }

        IEvidenceSigner signer;
        if (configured is LocalPemSignerConfiguration local)
        {
            signer = new LocalPemEvidenceSigner(local.PrivateKeyFile);
        }
        else if (configured is OciKmsSignerConfiguration kms)
        {
            var client = (kmsClientFactory ?? (options => new OciSdkKmsSigningClient(options)))(kms);
            try
            {
                signer = new OciKmsEvidenceSigner(kms, client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        else
        {
            throw Invalid("signer_configuration_invalid");
        }
        if (!CryptographicEquals(
                input.Manifest.EvidenceKeyId, signer.Identity.EvidenceKeyId))
        {
            signer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw Invalid("evidence_key_id_mismatch");
        }
        return signer;
    }

    private static void RejectProductionRawKeyEnvironment(Func<string, string?> environment)
    {
        var forbidden = new[]
        {
            "SAYDIN_DQA_EVIDENCE_PRIVATE_KEY",
            "SAYDIN_DQA_EVIDENCE_PRIVATE_KEY_FILE",
            "EVIDENCE_PRIVATE_KEY",
            "OCI_CONFIG_FILE",
            "OCI_CLI_KEY_FILE",
            "OCI_CLI_PROFILE",
            "OCI_CLI_AUTH",
            "OCI_RESOURCE_PRINCIPAL_PRIVATE_PEM",
            "OCI_RESOURCE_PRINCIPAL_PRIVATE_PEM_PASSPHRASE",
            "OCI_SDK_DEFAULT_RETRY_ENABLED",
        };
        if (forbidden.Any(name => environment(name) is not null))
            throw Invalid("production_private_key_environment_rejected");
    }

    private static bool CryptographicEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.ASCII.GetBytes(left);
        var rightBytes = System.Text.Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static AuditRejectedException Invalid(string code) =>
        new(code, AuditExitCodes.InvalidArguments);
}
