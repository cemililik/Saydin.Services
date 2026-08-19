using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;

namespace Saydin.DataRepair;

internal static class RepairCryptography
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sha256Hex(string value) =>
        Sha256Hex(Encoding.UTF8.GetBytes(value));

    public static byte[] ReadPublicSpki(string path)
    {
        var pem = RepairFiles.ReadPrivateInput(path, 64 * 1024, "public_key_invalid");
        using var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(Encoding.UTF8.GetString(pem));
            EnsureP256(key);
            return key.ExportSubjectPublicKeyInfo();
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw Rejected("public_key_invalid", RepairExitCodes.SignatureFailure);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }
    }

    public static byte[] ReadPrivatePublicSpki(string path)
    {
        var pem = RepairFiles.ReadPrivateInput(path, 64 * 1024, "private_key_invalid");
        using var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(Encoding.UTF8.GetString(pem));
            EnsureP256(key);
            return key.ExportSubjectPublicKeyInfo();
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw Rejected("private_key_invalid", RepairExitCodes.ReceiptFailure);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }
    }

    public static byte[] Sign(ReadOnlySpan<byte> payload, string path)
    {
        var pem = RepairFiles.ReadPrivateInput(path, 64 * 1024, "private_key_invalid");
        using var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(Encoding.UTF8.GetString(pem));
            EnsureP256(key);
            return key.SignData(payload, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw Rejected("private_key_invalid", RepairExitCodes.ReceiptFailure);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }
    }

    public static bool Verify(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> spki)
    {
        using var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            EnsureP256(key);
            return read == spki.Length && IsCanonicalDerSignature(signature) &&
                   key.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                       DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    public static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static byte[] NormalizeP256Signature(ReadOnlySpan<byte> signature)
    {
        try
        {
            if (signature.Length == 64)
            {
                var rawWriter = new AsnWriter(AsnEncodingRules.DER);
                rawWriter.PushSequence();
                rawWriter.WriteIntegerUnsigned(signature[..32]);
                rawWriter.WriteIntegerUnsigned(signature[32..]);
                rawWriter.PopSequence();
                return rawWriter.Encode();
            }
            if (!IsCanonicalDerSignature(signature)) throw new CryptographicException();
            return signature.ToArray();
        }
        catch (Exception exception) when (exception is CryptographicException or
                                              AsnContentException or ArgumentException)
        {
            throw Rejected("kms_signature_encoding_invalid", RepairExitCodes.ReceiptFailure);
        }
    }

    private static bool IsCanonicalDerSignature(ReadOnlySpan<byte> signature)
    {
        try
        {
            var reader = new AsnReader(signature.ToArray(), AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            var r = sequence.ReadIntegerBytes().ToArray();
            var s = sequence.ReadIntegerBytes().ToArray();
            sequence.ThrowIfNotEmpty();
            reader.ThrowIfNotEmpty();
            var writer = new AsnWriter(AsnEncodingRules.DER);
            writer.PushSequence();
            writer.WriteInteger(r);
            writer.WriteInteger(s);
            writer.PopSequence();
            return writer.Encode().AsSpan().SequenceEqual(signature);
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static void EnsureP256(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        if (parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new CryptographicException("P-256 required.");
    }

    private static RepairRejectedException Rejected(string code, int exitCode) =>
        new(code, exitCode);
}
