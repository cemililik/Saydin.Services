using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Saydin.DataQualityAudit.IntegrationTests;

public sealed class KmsFakeServiceIntegrationTests
{
    private const string KeyId = "ocid1.key.oc1.eu-frankfurt-1.fake-service";
    private const string KeyVersionId =
        "ocid1.keyversion.oc1.eu-frankfurt-1.fake-service-version";

    [Fact]
    public async Task DisposableFakeKmsService_SignsDigestWithoutExportingPrivateKeyToAuditClient()
    {
        var root = Path.Combine(Path.GetTempPath(), $"saydin-kms-fake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKeyFile = Path.Combine(root, "public.pem");
            await File.WriteAllTextAsync(publicKeyFile, key.ExportSubjectPublicKeyInfoPem());
            var evidenceKeyId = AuditCryptography.PublicKeyId(publicKeyFile);
            await using var service = await FakeKmsService.StartAsync(key);
            using var client = new FakeHttpKmsSigningClient(service.Endpoint);
            var options = new OciKmsSignerConfiguration(
                KeyId,
                KeyVersionId,
                "https://fake-crypto.kms.eu-frankfurt-1.oraclecloud.com/",
                "eu-frankfurt-1",
                publicKeyFile,
                new HashSet<string>(StringComparer.Ordinal) { evidenceKeyId },
                TimeSpan.FromSeconds(2));
            await using var signer = new OciKmsEvidenceSigner(options, client);
            var payload = "canonical fake-service manifest"u8.ToArray();

            var signature = await signer.SignAsync(payload, default);

            AuditCryptography.VerifyWithSubjectPublicKeyInfo(
                    payload, signature, signer.Identity.PublicSubjectPublicKeyInfo)
                .Should().BeTrue();
            service.ObservedRequests.Should().Be(1);
            service.ObservedKeyId.Should().Be(KeyId);
            service.ObservedKeyVersionId.Should().Be(KeyVersionId);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeHttpKmsSigningClient(Uri endpoint) : IOciKmsSigningClient
    {
        private readonly HttpClient client = new() { BaseAddress = endpoint };

        public async Task<OciKmsSignatureResponse> SignDigestAsync(
            string keyId,
            string keyVersionId,
            ReadOnlyMemory<byte> sha256Digest,
            CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new FakeSignRequest(
                keyId, keyVersionId, Convert.ToBase64String(sha256Digest.Span)));
            using var requestBody = new ByteArrayContent(payload);
            requestBody.Headers.ContentType = new("application/json");
            using var response = await client.PostAsync("sign", requestBody, cancellationToken);
            response.EnsureSuccessStatusCode();
            return JsonSerializer.Deserialize<OciKmsSignatureResponse>(
                       await response.Content.ReadAsByteArrayAsync(cancellationToken)) ??
                   throw new InvalidOperationException("fake KMS response missing");
        }

        public void Dispose() => client.Dispose();
    }

    private sealed class FakeKmsService : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly ECDsa key;
        private readonly CancellationTokenSource stop = new();
        private readonly Task serverTask;

        private FakeKmsService(TcpListener listener, ECDsa key)
        {
            this.listener = listener;
            this.key = key;
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/", UriKind.Absolute);
            serverTask = ServeOnceAsync();
        }

        public Uri Endpoint { get; }
        public int ObservedRequests { get; private set; }
        public string? ObservedKeyId { get; private set; }
        public string? ObservedKeyVersionId { get; private set; }

        public static Task<FakeKmsService> StartAsync(ECDsa key)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            return Task.FromResult(new FakeKmsService(listener, key));
        }

        private async Task ServeOnceAsync()
        {
            try
            {
                using var connection = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = connection.GetStream();
                var headerBytes = new List<byte>();
                var suffix = new byte[] { 13, 10, 13, 10 };
                while (!EndsWith(headerBytes, suffix))
                {
                    var next = stream.ReadByte();
                    if (next < 0 || headerBytes.Count >= 16 * 1024)
                        throw new InvalidOperationException("fake KMS request headers invalid");
                    headerBytes.Add((byte)next);
                }
                var headers = Encoding.ASCII.GetString(headerBytes.ToArray());
                var contentLengthLine = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                    .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var contentLength = int.Parse(
                    contentLengthLine[(contentLengthLine.IndexOf(':') + 1)..],
                    System.Globalization.CultureInfo.InvariantCulture);
                if (contentLength is < 1 or > 4_096)
                    throw new InvalidOperationException("fake KMS request length invalid");
                var body = new byte[contentLength];
                await stream.ReadExactlyAsync(body, stop.Token);
                var request = JsonSerializer.Deserialize<FakeSignRequest>(body) ??
                    throw new InvalidOperationException("fake KMS request body invalid");
                ObservedRequests++;
                ObservedKeyId = request.KeyId;
                ObservedKeyVersionId = request.KeyVersionId;
                var digest = Convert.FromBase64String(request.Base64Digest);
                if (digest.Length != 32)
                    throw new InvalidOperationException("fake KMS digest invalid");
                var rawSignature = key.SignHash(
                    digest, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                var payload = JsonSerializer.SerializeToUtf8Bytes(new OciKmsSignatureResponse(
                    request.KeyId,
                    request.KeyVersionId,
                    "EcdsaSha256",
                    Convert.ToBase64String(rawSignature)));
                var responseHeaders = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(responseHeaders, stop.Token);
                await stream.WriteAsync(payload, stop.Token);
                await stream.FlushAsync(stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            try
            {
                await serverTask;
            }
            catch (SocketException) when (stop.IsCancellationRequested)
            {
            }
            stop.Dispose();
        }

        private static bool EndsWith(IReadOnlyList<byte> bytes, IReadOnlyList<byte> suffix)
        {
            if (bytes.Count < suffix.Count) return false;
            for (var index = 0; index < suffix.Count; index++)
                if (bytes[bytes.Count - suffix.Count + index] != suffix[index])
                    return false;
            return true;
        }
    }

    private sealed record FakeSignRequest(
        string KeyId,
        string KeyVersionId,
        string Base64Digest);
}
