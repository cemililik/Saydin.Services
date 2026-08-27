using System.Text;

namespace Saydin.DataQualityAudit;

internal static class AuditFileLimits
{
    internal const int InputManifestBytes = 1 * 1024 * 1024;
    internal const int DetachedSignatureBytes = 4 * 1024;
    internal const int PemKeyBytes = 64 * 1024;
    internal const int HmacKeyBytes = 4 * 1024;
    internal const int ConnectionFileBytes = 64 * 1024;
    internal const int EvidenceManifestBytes = 1 * 1024 * 1024;
    internal const int EvidenceManifestHashBytes = 256;
    internal const long EvidenceBundleBytes = 256L * 1024 * 1024;
    internal const int EvidenceFileCount = 4_096;

    internal static byte[] ReadBytes(
        string path,
        int maxBytes,
        string unreadableCode,
        string tooLargeCode,
        int exitCode)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4_096, FileOptions.SequentialScan);
            if (stream.Length > maxBytes)
                throw new AuditRejectedException(tooLargeCode, exitCode);
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
                throw new AuditRejectedException(tooLargeCode, exitCode);
            return bytes;
        }
        catch (AuditRejectedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AuditRejectedException(unreadableCode, exitCode);
        }
    }

    internal static string ReadText(
        string path,
        int maxBytes,
        string unreadableCode,
        string tooLargeCode,
        int exitCode)
    {
        var text = Encoding.UTF8.GetString(
            ReadBytes(path, maxBytes, unreadableCode, tooLargeCode, exitCode));
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
}
