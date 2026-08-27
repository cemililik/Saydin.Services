using System.Security.Cryptography;
using System.Text;
using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseRoleBootstrap;

internal sealed class SensitivePassword : IDisposable
{
    private readonly byte[] bytes;
    private bool disposed;

    private SensitivePassword(byte[] bytes) => this.bytes = bytes;

    public static SensitivePassword Read(string path) =>
        new(SecureSecretFile.ReadPasswordBytes(path));

    public string CreateVerifier() =>
        PostgresScramSha256Verifier.Create(Material);

    public string RevealForAuthentication() =>
        new UTF8Encoding(false, true).GetString(Material);

    internal bool IsCleared => bytes.All(value => value == 0);

    public void Dispose()
    {
        if (disposed) return;
        CryptographicOperations.ZeroMemory(bytes);
        disposed = true;
    }

    private ReadOnlySpan<byte> Material => disposed
        ? throw new ObjectDisposedException(nameof(SensitivePassword))
        : bytes;
}

internal sealed class LoadedSecrets(
    IReadOnlyDictionary<LoginPurpose, SensitivePassword> loginPasswords,
    SensitivePassword? backupPassword) : IDisposable
{
    public IReadOnlyDictionary<LoginPurpose, SensitivePassword> LoginPasswords { get; } =
        loginPasswords;
    public SensitivePassword? BackupPassword { get; } = backupPassword;

    public void Dispose()
    {
        foreach (var secret in LoginPasswords.Values)
            secret.Dispose();
        BackupPassword?.Dispose();
    }
}
