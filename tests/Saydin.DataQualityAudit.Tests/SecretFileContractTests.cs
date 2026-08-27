using FluentAssertions;

namespace Saydin.DataQualityAudit.Tests;

public sealed class SecretFileContractTests
{
    [Fact]
    public void HmacSecret_RejectsSymlinkAndGroupReadableMode()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("SecureSecretFile contract is Linux-only.");
        using var files = new TestFiles();
        var link = Path.Combine(files.Root, "hmac-link");
        File.CreateSymbolicLink(link, files.HmacKeyPath);

        var linkAction = () => AuditCryptography.ReadHmacKey(link);
        linkAction.Should().Throw<AuditRejectedException>().Which.Code
            .Should().Be("hmac_key_file_rejected");

        File.SetUnixFileMode(files.HmacKeyPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var modeAction = () => AuditCryptography.ReadHmacKey(files.HmacKeyPath);
        modeAction.Should().Throw<AuditRejectedException>().Which.Code
            .Should().Be("hmac_key_file_rejected");
    }

    [Fact]
    public void PrivatePem_RejectsSymlinkAndGroupReadableMode()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("SecureSecretFile contract is Linux-only.");
        using var files = new TestFiles();
        var link = Path.Combine(files.Root, "private-link.pem");
        File.CreateSymbolicLink(link, files.EvidencePrivateKeyPath);

        var linkAction = () => AuditCryptography.PrivateKeyId(link);
        linkAction.Should().Throw<AuditRejectedException>().Which.Code
            .Should().Be("evidence_private_key_file_rejected");

        File.SetUnixFileMode(files.EvidencePrivateKeyPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var modeAction = () => AuditCryptography.PrivateKeyId(files.EvidencePrivateKeyPath);
        modeAction.Should().Throw<AuditRejectedException>().Which.Code
            .Should().Be("evidence_private_key_file_rejected");
    }
}
