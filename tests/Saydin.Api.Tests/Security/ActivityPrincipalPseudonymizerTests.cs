using System.Security.Cryptography;
using System.Runtime.Versioning;
using FluentAssertions;
using Saydin.Api.Options;
using Saydin.Api.Services;

namespace Saydin.Api.Tests.Security;

[SupportedOSPlatform("linux")]
public sealed class ActivityPrincipalPseudonymizerTests
{
    [Fact]
    public void StableAuthority_IsIndependentFromCredentialKeyringRotation()
    {
        OperatingSystem.IsLinux().Should().BeTrue(
            "the private-file authority contract is exercised only in the canonical Linux container");
        var principal = Guid.Parse("01989a4a-6580-7000-8000-000000000001");
        using var stable = PseudonymFixture.Create(Enumerable.Repeat((byte)0x41, 32).ToArray());
        using var credentialV1 = KeyringFixture.Create(1, (byte)0x11);
        using var credentialV2 = KeyringFixture.Create(2, (byte)0x22);

        var before = stable.Pseudonymizer.Pseudonymize(principal);
        using var v1 = credentialV1.Keyring.Generate();
        using var v2 = credentialV2.Keyring.Generate();
        var after = stable.Pseudonymizer.Pseudonymize(principal);

        before.Should().Be("p1:87410fc9c04477d426b53c62de24d9fc")
            .And.Be(after).And.StartWith("p1:").And.HaveLength(35);
        before.Should().NotContain(principal.ToString("N"));
        var quota = stable.Pseudonymizer.PseudonymizeQuotaSubject(principal.ToString("N"));
        quota.Should().StartWith("q1:").And.HaveLength(35)
            .And.NotContain(principal.ToString("N"));
        quota.Should().NotBe(before.Replace("p1:", "q1:", StringComparison.Ordinal));
        credentialV1.Keyring.ActiveKeyVersion.Should().Be(1);
        credentialV2.Keyring.ActiveKeyVersion.Should().Be(2);
    }

    [Fact]
    public void DifferentStableAuthority_ProducesDifferentDomainSeparatedPseudonym()
    {
        OperatingSystem.IsLinux().Should().BeTrue();
        var principal = Guid.Parse("01989a4a-6580-7000-8000-000000000002");
        using var first = PseudonymFixture.Create(Enumerable.Repeat((byte)0x31, 32).ToArray());
        using var second = PseudonymFixture.Create(Enumerable.Repeat((byte)0x32, 32).ToArray());

        first.Pseudonymizer.Pseudonymize(principal).Should()
            .NotBe(second.Pseudonymizer.Pseudonymize(principal));
    }

    [Fact]
    public void InvalidSecretPath_FailsWithStableRedactedError()
    {
        const string secretPath = "relative/activity-principal-canary";
        var action = () => ActivityPrincipalPseudonymizer.Load(
            new ActivityPrincipalPseudonymOptions { SecretFile = secretPath });

        var failure = action.Should().Throw<InvalidOperationException>().Which;
        failure.Message.Should().Be("Activity principal pseudonym secret file is invalid.")
            .And.NotContain(secretPath);
        failure.InnerException.Should().BeNull();
    }

    private sealed class PseudonymFixture : IDisposable
    {
        private PseudonymFixture(string directory, string path,
            ActivityPrincipalPseudonymizer pseudonymizer)
        {
            Directory = directory;
            Path = path;
            Pseudonymizer = pseudonymizer;
        }

        private string Directory { get; }
        private string Path { get; }
        internal ActivityPrincipalPseudonymizer Pseudonymizer { get; }

        internal static PseudonymFixture Create(byte[] key)
        {
            var directory = PrivateDirectory();
            var path = System.IO.Path.Combine(directory, "activity-principal-hmac");
            File.WriteAllBytes(path, key);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            CryptographicOperations.ZeroMemory(key);
            return new PseudonymFixture(directory, path,
                ActivityPrincipalPseudonymizer.Load(
                    new ActivityPrincipalPseudonymOptions { SecretFile = path }));
        }

        public void Dispose()
        {
            Pseudonymizer.Dispose();
            File.Delete(Path);
            System.IO.Directory.Delete(Directory);
        }
    }

    private sealed class KeyringFixture : IDisposable
    {
        private KeyringFixture(string directory, string path,
            InstallationCredentialKeyring keyring)
        {
            Directory = directory;
            Path = path;
            Keyring = keyring;
        }

        private string Directory { get; }
        private string Path { get; }
        internal InstallationCredentialKeyring Keyring { get; }

        internal static KeyringFixture Create(short version, byte value)
        {
            var directory = PrivateDirectory();
            var path = System.IO.Path.Combine(directory, "credential-keyring.json");
            File.WriteAllText(path,
                $"{{\"{version}\":\"{Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray())}\"}}");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new KeyringFixture(directory, path,
                InstallationCredentialKeyring.Load(new InstallationCredentialOptions
                {
                    SecretFile = path,
                    ActiveKeyVersion = version,
                }));
        }

        public void Dispose()
        {
            Keyring.Dispose();
            File.Delete(Path);
            System.IO.Directory.Delete(Directory);
        }
    }

    private static string PrivateDirectory()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"saydin-principal-key-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return directory;
    }
}
