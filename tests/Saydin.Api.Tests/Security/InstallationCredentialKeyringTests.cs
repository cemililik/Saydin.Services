using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
using Saydin.Api.Models.Responses;
using Saydin.Api.Options;
using Saydin.Api.Services;

namespace Saydin.Api.Tests.Security;

[SupportedOSPlatform("linux")]
public sealed class InstallationCredentialKeyringTests
{
    [Fact]
    public void Suite_RequiresCanonicalLinuxSecretContract()
    {
        OperatingSystem.IsLinux().Should().BeTrue(
            "security-critical secret-file assertions must run in the canonical Linux test container");
    }

    [Fact]
    public void SecretBearingResponseText_IsAlwaysRedacted()
    {
        var credential = new string('A', InstallationCredentialKeyring.CredentialTextLength);

        new InstallationRegistrationResponse(Guid.NewGuid(), credential)
            .ToString().Should().NotContain(credential).And.Contain("[REDACTED]");
        new InstallationRotationResponse(Guid.NewGuid(), credential)
            .ToString().Should().NotContain(credential).And.Contain("[REDACTED]");
    }

    [Fact]
    public void Generate_ProducesExactOpaqueCredential_AndDisposeZeroesSecret()
    {
        RequireLinux();
        using var fixture = KeyringFixture.Create();
        var generated = fixture.Keyring.Generate();
        var original = generated.Secret.ToArray();

        generated.Token.Should().HaveLength(InstallationCredentialKeyring.CredentialTextLength);
        generated.Token.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        fixture.Keyring.TryDecode(generated.Token, out var decoded).Should().BeTrue();
        decoded.Should().Equal(original);
        generated.ToString().Should().NotContain(generated.Token)
            .And.Contain("[REDACTED]");

        generated.Dispose();
        generated.Secret.Should().OnlyContain(value => value == 0);
        CryptographicOperations.ZeroMemory(decoded);
        CryptographicOperations.ZeroMemory(original);
    }

    [Fact]
    public void HashAccepted_IsDeterministicVersioned_AndNeverStoresRawSecret()
    {
        RequireLinux();
        using var fixture = KeyringFixture.Create(includePrevious: true);
        using var generated = fixture.Keyring.Generate();

        var first = fixture.Keyring.HashAccepted(generated.Secret);
        var second = fixture.Keyring.HashAccepted(generated.Secret);
        try
        {
            first.Should().HaveCount(2);
            first.Select(item => item.KeyVersion).Should().Equal(2, 1);
            first.Zip(second).Should().OnlyContain(pair =>
                pair.First.SecretHash.SequenceEqual(pair.Second.SecretHash));
            first.Should().OnlyContain(item => item.SecretHash.Length == 32);
            first.Should().OnlyContain(item => !item.SecretHash.SequenceEqual(generated.Secret));
        }
        finally
        {
            foreach (var candidate in first.Concat(second))
                CryptographicOperations.ZeroMemory(candidate.SecretHash);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    public void TryDecode_RejectsNonCanonicalCredential(string token)
    {
        RequireLinux();
        using var fixture = KeyringFixture.Create();

        fixture.Keyring.TryDecode(token, out var secret).Should().BeFalse();
        secret.Should().BeEmpty();
    }

    [Fact]
    public void Load_WorldReadableSecretFile_FailsWithoutLeakingPathOrValue()
    {
        RequireLinux();

        var directory = CreatePrivateDirectory();
        var path = Path.Combine(directory, "keyring.json");
        var secretValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["1"] = secretValue,
            }));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead);

            var act = () => InstallationCredentialKeyring.Load(new InstallationCredentialOptions
            {
                SecretFile = path,
                ActiveKeyVersion = 1,
            });

            var exception = act.Should().Throw<InvalidOperationException>().Which;
            exception.Message.Should().NotContain(path).And.NotContain(secretValue);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void Load_SymlinkAndHardlinkSecrets_AreRejectedWithStableError()
    {
        RequireLinux();
        var directory = CreatePrivateDirectory();
        var target = WriteKeyring(directory, "target.json");
        var symlink = Path.Combine(directory, "symlink.json");
        var hardlink = Path.Combine(directory, "hardlink.json");
        File.CreateSymbolicLink(symlink, target);
        CreateHardLink(target, hardlink).Should().Be(0);
        try
        {
            foreach (var path in new[] { symlink, target })
            {
                var exception = Assert.Throws<InvalidOperationException>(() =>
                    InstallationCredentialKeyring.Load(new InstallationCredentialOptions
                    {
                        SecretFile = path,
                        ActiveKeyVersion = 1,
                    }));
                exception.Message.Should().Be("Installation credential keyring secret file is invalid.");
                exception.InnerException.Should().BeNull();
            }
        }
        finally
        {
            File.Delete(symlink);
            File.Delete(hardlink);
            File.Delete(target);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void Load_NonPrivateParentDirectory_IsRejected()
    {
        RequireLinux();
        var directory = CreatePrivateDirectory();
        var path = WriteKeyring(directory, "keyring.json");
        File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        try
        {
            var act = () => InstallationCredentialKeyring.Load(new InstallationCredentialOptions
            {
                SecretFile = path,
                ActiveKeyVersion = 1,
            });
            act.Should().Throw<InvalidOperationException>()
                .Which.InnerException.Should().BeNull();
        }
        finally
        {
            File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void Load_ProductionSecretReader_ExposesNoMutableStaticTestHook()
    {
        var writableStaticState = typeof(Saydin.DatabaseSecurity.LinuxSecretFile)
            .GetProperties(System.Reflection.BindingFlags.Static |
                           System.Reflection.BindingFlags.Public |
                           System.Reflection.BindingFlags.NonPublic)
            .Where(property => property.SetMethod is not null)
            .Select(property => property.Name)
            .ToArray();

        writableStaticState.Should().BeEmpty();
    }

    [Fact]
    public void Load_RelativePath_IsRejectedWithoutRetainingPath()
    {
        const string relativePath = "private/keyring.json";
        var exception = Assert.Throws<InvalidOperationException>(() =>
            InstallationCredentialKeyring.Load(new InstallationCredentialOptions
            {
                SecretFile = relativePath,
                ActiveKeyVersion = 1,
            }));

        exception.Message.Should().NotContain(relativePath);
        exception.InnerException.Should().BeNull();
    }

    [Fact]
    public void Load_ActiveVersionMustBeCanonicalHighestAcceptedVersion()
    {
        RequireLinux();
        var directory = CreatePrivateDirectory();
        var path = Path.Combine(directory, "keyring.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["2"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        }));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            var act = () => InstallationCredentialKeyring.Load(new InstallationCredentialOptions
            {
                SecretFile = path,
                ActiveKeyVersion = 1,
            });
            act.Should().Throw<InvalidOperationException>()
                .Which.InnerException.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void HashOperations_RejectNonCredentialLength()
    {
        RequireLinux();
        using var fixture = KeyringFixture.Create();

        var active = () => fixture.Keyring.HashActive(new byte[31]);
        var accepted = () => fixture.Keyring.HashAccepted(new byte[33]);

        active.Should().Throw<ArgumentException>();
        accepted.Should().Throw<ArgumentException>();
    }

    private sealed class KeyringFixture : IDisposable
    {
        private KeyringFixture(string directory, string path, InstallationCredentialKeyring keyring)
        {
            Directory = directory;
            Path = path;
            Keyring = keyring;
        }

        private string Directory { get; }
        private string Path { get; }
        internal InstallationCredentialKeyring Keyring { get; }

        internal static KeyringFixture Create(bool includePrevious = false)
        {
            var directory = CreatePrivateDirectory();
            var path = System.IO.Path.Combine(directory, "keyring.json");
            var keys = new Dictionary<string, string>
            {
                [includePrevious ? "2" : "1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            };
            if (includePrevious)
                keys["1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

            File.WriteAllText(path, JsonSerializer.Serialize(keys));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var keyring = InstallationCredentialKeyring.Load(new InstallationCredentialOptions
            {
                SecretFile = path,
                ActiveKeyVersion = includePrevious ? (short)2 : (short)1,
            });
            return new KeyringFixture(directory, path, keyring);
        }

        public void Dispose()
        {
            Keyring.Dispose();
            File.Delete(Path);
            System.IO.Directory.Delete(Directory);
        }
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(
                "Installation credential secret-file tests require Linux and may not pass without executing assertions.");
    }

    private static string CreatePrivateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"saydin-keyring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return directory;
    }

    private static string WriteKeyring(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        }));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    [DllImport("libc", EntryPoint = "link", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int CreateHardLink(string existingPath, string newPath);
}
