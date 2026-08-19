using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;

namespace Saydin.DataQualityAudit;

internal static class AuditCryptography
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string HmacBusinessKey(ReadOnlySpan<byte> key, string businessKey)
    {
        using var hmac = new HMACSHA256(key.ToArray());
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(businessKey)));
    }

    public static byte[] Sign(ReadOnlySpan<byte> bytes, string privateKeyPath)
    {
        var pem = AuditFileLimits.ReadText(
            privateKeyPath,
            AuditFileLimits.PemKeyBytes,
            "evidence_private_key_unreadable",
            "evidence_private_key_too_large",
            AuditExitCodes.EvidenceFailure);
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(pem);
            EnsureP256(ecdsa);
            return ecdsa.SignData(bytes, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw new AuditRejectedException("evidence_private_key_invalid", AuditExitCodes.EvidenceFailure);
        }
    }

    public static bool Verify(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> signature, string publicKeyPath)
    {
        string pem;
        try
        {
            pem = AuditFileLimits.ReadText(
                publicKeyPath,
                AuditFileLimits.PemKeyBytes,
                "public_key_unreadable",
                "public_key_too_large",
                AuditExitCodes.InvalidArguments);
        }
        catch (AuditRejectedException)
        {
            return false;
        }
        using var ecdsa = ECDsa.Create();
        try
        {
            ImportPublicP256Pem(ecdsa, pem);
            return ecdsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    public static string PublicKeyId(string publicKeyPath)
    {
        var pem = AuditFileLimits.ReadText(
            publicKeyPath, AuditFileLimits.PemKeyBytes,
            "public_key_unreadable", "public_key_too_large", AuditExitCodes.InvalidArguments);
        using var ecdsa = ECDsa.Create();
        try
        {
            ImportPublicP256Pem(ecdsa, pem);
            return Sha256Hex(ecdsa.ExportSubjectPublicKeyInfo());
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw new AuditRejectedException("public_key_invalid", AuditExitCodes.InvalidArguments);
        }
    }

    public static string PrivateKeyId(string privateKeyPath)
    {
        var pem = AuditFileLimits.ReadText(
            privateKeyPath, AuditFileLimits.PemKeyBytes,
            "evidence_private_key_unreadable", "evidence_private_key_too_large",
            AuditExitCodes.EvidenceFailure);
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(pem);
            EnsureP256(ecdsa);
            return Sha256Hex(ecdsa.ExportSubjectPublicKeyInfo());
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw new AuditRejectedException("evidence_private_key_invalid", AuditExitCodes.EvidenceFailure);
        }
    }

    public static byte[] ReadPrivateP256PublicKey(string privateKeyPath)
    {
        var pem = AuditFileLimits.ReadText(
            privateKeyPath, AuditFileLimits.PemKeyBytes,
            "evidence_private_key_unreadable", "evidence_private_key_too_large",
            AuditExitCodes.EvidenceFailure);
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(pem);
            EnsureP256(ecdsa);
            return ecdsa.ExportSubjectPublicKeyInfo();
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw new AuditRejectedException(
                "evidence_private_key_invalid", AuditExitCodes.EvidenceFailure);
        }
    }

    public static byte[] ReadPublicP256Key(string publicKeyPath, int exitCode)
    {
        var pem = AuditFileLimits.ReadText(
            publicKeyPath, AuditFileLimits.PemKeyBytes,
            "evidence_public_key_unreadable", "evidence_public_key_too_large", exitCode);
        using var ecdsa = ECDsa.Create();
        try
        {
            ImportPublicP256Pem(ecdsa, pem);
            return ecdsa.ExportSubjectPublicKeyInfo();
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw new AuditRejectedException("evidence_public_key_invalid", exitCode);
        }
    }

    public static bool VerifyWithSubjectPublicKeyInfo(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> publicSubjectPublicKeyInfo)
    {
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(publicSubjectPublicKeyInfo, out var read);
            EnsureP256(ecdsa);
            return read == publicSubjectPublicKeyInfo.Length &&
                   ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                       DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static bool VerifyHashWithSubjectPublicKeyInfo(
        ReadOnlySpan<byte> sha256Digest,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> publicSubjectPublicKeyInfo)
    {
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(publicSubjectPublicKeyInfo, out var read);
            EnsureP256(ecdsa);
            return read == publicSubjectPublicKeyInfo.Length && sha256Digest.Length == 32 &&
                   ecdsa.VerifyHash(sha256Digest, signature,
                       DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

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
            if (signature.Length is < 8 or > 72)
                throw new CryptographicException();
            var reader = new AsnReader(signature.ToArray(), AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            var r = sequence.ReadIntegerBytes();
            var s = sequence.ReadIntegerBytes();
            if (sequence.HasData || reader.HasData || r.Length is < 1 or > 33 || s.Length is < 1 or > 33)
                throw new CryptographicException();
            var derWriter = new AsnWriter(AsnEncodingRules.DER);
            derWriter.PushSequence();
            derWriter.WriteInteger(r.Span);
            derWriter.WriteInteger(s.Span);
            derWriter.PopSequence();
            var canonical = derWriter.Encode();
            if (!signature.SequenceEqual(canonical))
                throw new CryptographicException();
            return canonical;
        }
        catch (Exception exception) when (exception is CryptographicException or AsnContentException or ArgumentException)
        {
            throw new AuditRejectedException(
                "kms_signature_encoding_invalid", AuditExitCodes.EvidenceFailure);
        }
    }

    public static byte[] ReadHmacKey(string path)
    {
        byte[] bytes;
        try
        {
            bytes = AuditFileLimits.ReadBytes(
                path,
                AuditFileLimits.HmacKeyBytes,
                "hmac_key_unreadable",
                "hmac_key_too_large",
                AuditExitCodes.InvalidArguments);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AuditRejectedException("hmac_key_unreadable", AuditExitCodes.InvalidArguments);
        }
        if (bytes.Length < 32)
            throw new AuditRejectedException("hmac_key_too_short", AuditExitCodes.InvalidArguments);
        return bytes;
    }

    private static void EnsureP256(ECDsa ecdsa)
    {
        const string nistP256Oid = "1.2.840.10045.3.1.7";
        var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
        if (ecdsa.KeySize != 256 || parameters.Curve.Oid.Value != nistP256Oid)
            throw new CryptographicException("Only NIST P-256 keys are accepted.");
    }

    private static void ImportPublicP256Pem(ECDsa ecdsa, string pem)
    {
        var characters = pem.AsSpan();
        if (!PemEncoding.TryFind(characters, out var fields) ||
            !characters[fields.Label].SequenceEqual("PUBLIC KEY"))
            throw new CryptographicException("Only SubjectPublicKeyInfo PUBLIC KEY PEM is accepted.");
        var start = fields.Location.Start.GetOffset(characters.Length);
        var end = fields.Location.End.GetOffset(characters.Length);
        if (ContainsNonWhitespace(characters[..start]) ||
            ContainsNonWhitespace(characters[end..]))
            throw new CryptographicException("Unexpected data outside the public key PEM block.");
        byte[] subjectPublicKeyInfo;
        try
        {
            subjectPublicKeyInfo = Convert.FromBase64String(characters[fields.Base64Data].ToString());
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("Public key PEM encoding is invalid.", exception);
        }
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
            if (bytesRead != subjectPublicKeyInfo.Length)
                throw new CryptographicException("Trailing public key bytes are rejected.");
            EnsureP256(ecdsa);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(subjectPublicKeyInfo);
        }
    }

    private static bool ContainsNonWhitespace(ReadOnlySpan<char> characters)
    {
        foreach (var character in characters)
            if (!char.IsWhiteSpace(character))
                return true;
        return false;
    }

}
