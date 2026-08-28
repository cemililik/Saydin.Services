using System.Security.Cryptography;
using Oci.Common;
using Oci.Common.Auth;
using Oci.KeymanagementService;
using Oci.KeymanagementService.Models;
using Oci.KeymanagementService.Requests;

namespace Saydin.DataRepair;

internal sealed record ReceiptSigningIdentity(
    string Provider,
    string KeyIdentity,
    string KeyId,
    byte[] PublicSubjectPublicKeyInfo);

internal interface IReceiptSigner : IAsyncDisposable
{
    ReceiptSigningIdentity Identity { get; }
    Task<byte[]> SignAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
}

internal sealed class LocalReceiptSigner : IReceiptSigner
{
    private readonly string privateKeyFile;

    public LocalReceiptSigner(string privateKeyFile)
    {
        this.privateKeyFile = privateKeyFile;
        var spki = RepairCryptography.ReadPrivatePublicSpki(privateKeyFile);
        var keyId = RepairCryptography.Sha256Hex(spki);
        Identity = new ReceiptSigningIdentity(
            "local-pem", $"local-pem:{keyId}", keyId, spki);
    }

    public ReceiptSigningIdentity Identity { get; }

    public Task<byte[]> SignAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var signature = RepairCryptography.Sign(payload.Span, privateKeyFile);
        if (!RepairCryptography.Verify(payload.Span, signature, Identity.PublicSubjectPublicKeyInfo))
            throw Rejected("receipt_signature_self_check_failed");
        return Task.FromResult(signature);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static RepairRejectedException Rejected(string code) =>
        new(code, RepairExitCodes.ReceiptFailure);
}

internal sealed record KmsSignatureResponse(
    string KeyId,
    string KeyVersionId,
    string Algorithm,
    string Base64Signature);

internal interface IKmsSigningClient : IDisposable
{
    Task<KmsSignatureResponse> SignDigestAsync(
        string keyId,
        string keyVersionId,
        ReadOnlyMemory<byte> digest,
        CancellationToken cancellationToken);
}

internal sealed class OciSdkKmsSigningClient : IKmsSigningClient
{
    private readonly KmsCryptoClient client;

    public OciSdkKmsSigningClient(OciKmsReceiptSignerConfiguration options)
    {
        IBasicAuthenticationDetailsProvider authentication =
            new InstancePrincipalsAuthenticationDetailsProvider();
        client = new KmsCryptoClient(authentication, new ClientConfiguration
        {
            TimeoutMillis = checked((int)options.Timeout.TotalMilliseconds),
            ResponseContentBufferBytes = 16 * 1024,
            ClientUserAgent = "Saydin-DataRepair/1",
            RetryConfiguration = null,
        }, options.CryptoEndpoint);
    }

    public async Task<KmsSignatureResponse> SignDigestAsync(
        string keyId,
        string keyVersionId,
        ReadOnlyMemory<byte> digest,
        CancellationToken cancellationToken)
    {
        var response = await client.Sign(new SignRequest
        {
            SignDataDetails = new SignDataDetails
            {
                KeyId = keyId,
                KeyVersionId = keyVersionId,
                Message = Convert.ToBase64String(digest.Span),
                MessageType = SignDataDetails.MessageTypeEnum.Digest,
                SigningAlgorithm = SignDataDetails.SigningAlgorithmEnum.EcdsaSha256,
                LoggingContext = new Dictionary<string, string>
                {
                    ["component"] = "saydin-data-repair",
                },
            },
        }, retryConfiguration: null, cancellationToken).ConfigureAwait(false);
        var signed = response.SignedData ?? throw Rejected("kms_signature_response_invalid");
        return new KmsSignatureResponse(
            signed.KeyId,
            signed.KeyVersionId,
            signed.SigningAlgorithm?.ToString() ?? string.Empty,
            signed.Signature);
    }

    public void Dispose() => client.Dispose();

    private static RepairRejectedException Rejected(string code) =>
        new(code, RepairExitCodes.ReceiptFailure);
}

internal sealed class OciKmsReceiptSigner : IReceiptSigner
{
    private readonly OciKmsReceiptSignerConfiguration options;
    private readonly IKmsSigningClient client;

    public OciKmsReceiptSigner(
        OciKmsReceiptSignerConfiguration options,
        IKmsSigningClient client)
    {
        this.options = options;
        this.client = client;
        var spki = RepairCryptography.ReadPublicSpki(options.PublicKeyFile);
        var keyId = RepairCryptography.Sha256Hex(spki);
        Identity = new ReceiptSigningIdentity(
            "oci-kms-instance-principal",
            $"{options.KeyId}:{options.KeyVersionId}", keyId, spki);
    }

    public ReceiptSigningIdentity Identity { get; }

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
                options.KeyId, options.KeyVersionId, digest, timeout.Token);
            if (response.KeyId != options.KeyId || response.KeyVersionId != options.KeyVersionId ||
                response.Algorithm != "EcdsaSha256" || response.Base64Signature.Length > 512)
                throw Rejected("kms_signature_response_invalid");
            byte[] signature;
            try
            {
                var raw = Convert.FromBase64String(response.Base64Signature);
                try
                {
                    signature = RepairCryptography.NormalizeP256Signature(raw);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(raw);
                }
            }
            catch (FormatException)
            {
                throw Rejected("kms_signature_response_invalid");
            }
            if (!RepairCryptography.Verify(payload.Span, signature, Identity.PublicSubjectPublicKeyInfo))
            {
                CryptographicOperations.ZeroMemory(signature);
                throw Rejected("kms_signature_verification_failed");
            }
            return signature;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Rejected("kms_signature_timeout");
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

    private static RepairRejectedException Rejected(string code) =>
        new(code, RepairExitCodes.ReceiptFailure);
}

internal static class ReceiptSignerFactory
{
    public static IReceiptSigner Create(
        ReceiptSignerConfiguration configuration,
        VerifiedPhysicalRepairTarget target,
        string expectedKeyId,
        Func<string, string?> environment,
        Func<OciKmsReceiptSignerConfiguration, IKmsSigningClient>? kmsFactory = null)
    {
        if (target.IsProduction)
        {
            RejectProductionKeyEnvironment(environment);
            if (configuration is not OciKmsReceiptSignerConfiguration)
                throw Rejected("production_signer_mode_rejected");
        }

        IReceiptSigner signer = configuration switch
        {
            LocalReceiptSignerConfiguration local => new LocalReceiptSigner(local.PrivateKeyFile),
            OciKmsReceiptSignerConfiguration kms => CreateKms(kms, kmsFactory),
            _ => throw Rejected("receipt_signer_invalid"),
        };
        if (!RepairCryptography.FixedEquals(signer.Identity.KeyId, expectedKeyId))
        {
            signer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw Rejected("receipt_key_id_mismatch");
        }
        return signer;
    }

    private static IReceiptSigner CreateKms(
        OciKmsReceiptSignerConfiguration options,
        Func<OciKmsReceiptSignerConfiguration, IKmsSigningClient>? factory)
    {
        var client = (factory ?? (value => new OciSdkKmsSigningClient(value)))(options);
        try
        {
            return new OciKmsReceiptSigner(options, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static void RejectProductionKeyEnvironment(Func<string, string?> environment)
    {
        foreach (var name in new[]
                 {
                     "SAYDIN_REPAIR_RECEIPT_PRIVATE_KEY", "SAYDIN_REPAIR_RECEIPT_PRIVATE_KEY_FILE",
                     "OCI_CONFIG_FILE", "OCI_CLI_KEY_FILE", "OCI_CLI_PROFILE", "OCI_CLI_AUTH",
                     "OCI_RESOURCE_PRINCIPAL_PRIVATE_PEM",
                     "OCI_RESOURCE_PRINCIPAL_PRIVATE_PEM_PASSPHRASE",
                 })
            if (environment(name) is not null)
                throw Rejected("production_private_key_environment_rejected");
    }

    private static RepairRejectedException Rejected(string code) =>
        new(code, RepairExitCodes.ReceiptFailure);
}
