using System.Runtime.InteropServices;
using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseRoleBootstrap.Tests;

public sealed class SecretFileTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"saydin-role-secret-{Guid.NewGuid():N}");

    public SecretFileTests()
    {
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(directory, UnixFileMode.UserRead |
            UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void Password_is_read_without_normalization()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Write("password", "Correct-Horse-Battery-Staple-123!");

        Assert.Equal("Correct-Horse-Battery-Staple-123!", SecureSecretFile.ReadPassword(path));
    }

    [Fact]
    public void Linux_read_only_owner_secret_is_accepted()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Write("read-only-password", "Correct-Horse-Battery-Staple-123!");
        File.SetUnixFileMode(path, UnixFileMode.UserRead);

        Assert.Equal("Correct-Horse-Battery-Staple-123!", SecureSecretFile.ReadPassword(path));
    }

    [Fact]
    public void Linux_bounded_raw_secret_preserves_bytes_and_caller_owns_buffer()
    {
        if (!OperatingSystem.IsLinux()) return;
        var expected = new byte[] { 0, 1, 2, 3, 0xff };
        var path = Path.Combine(directory, "opaque-secret");
        File.WriteAllBytes(path, expected);
        File.SetUnixFileMode(path, UnixFileMode.UserRead);

        var actual = SecureSecretFile.ReadBytes(
            path, minimumBytes: expected.Length, maximumBytes: expected.Length, "opaque_secret_invalid");

        Assert.Equal(expected, actual);
        Array.Clear(actual);
        Assert.All(actual, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Linux_bounded_raw_secret_uses_same_rewrite_race_rejection()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Write("opaque-rewrite-race", "0123456789abcdef0123456789abcdef");
        LinuxSecretFile.AfterOpenBeforeReadForTests = () =>
            File.WriteAllText(path, "fedcba9876543210fedcba9876543210");
        try
        {
            var exception = Assert.Throws<DatabaseSecurityRejectedException>(() =>
                SecureSecretFile.ReadBytes(path, 32, 32, "opaque_secret_invalid"));
            Assert.Equal("opaque_secret_invalid", exception.Code);
        }
        finally
        {
            LinuxSecretFile.AfterOpenBeforeReadForTests = null;
        }
    }

    [Theory]
    [InlineData("too-short")]
    [InlineData("Correct-Horse-Battery-Staple-123!\n")]
    [InlineData(" Correct-Horse-Battery-Staple-123!")]
    public void Invalid_password_file_is_rejected(string value)
    {
        var path = Write(Guid.NewGuid().ToString("N"), value);

        var exception = Assert.Throws<DatabaseSecurityRejectedException>(() => SecureSecretFile.ReadPassword(path));
        Assert.Equal("login_password_secret_invalid", exception.Code);
    }

    [Fact]
    public void Symlink_secret_is_rejected()
    {
        var target = Write("target", "Correct-Horse-Battery-Staple-123!");
        var link = Path.Combine(directory, "link");
        File.CreateSymbolicLink(link, target);

        var exception = Assert.Throws<DatabaseSecurityRejectedException>(() => SecureSecretFile.ReadPassword(link));
        Assert.Equal("login_password_secret_invalid", exception.Code);
    }

    [Fact]
    public void Symlinked_secret_directory_is_rejected()
    {
        var realDirectory = Path.Combine(directory, "real-directory");
        Directory.CreateDirectory(realDirectory);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(realDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var secret = Path.Combine(realDirectory, "password");
        File.WriteAllText(secret, "Correct-Horse-Battery-Staple-123!");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(secret,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var link = Path.Combine(directory, "linked-directory");
        Directory.CreateSymbolicLink(link, realDirectory);

        try
        {
            var exception = Assert.Throws<DatabaseSecurityRejectedException>(() =>
                SecureSecretFile.ReadPassword(Path.Combine(link, "password")));
            Assert.Equal("login_password_secret_invalid", exception.Code);
        }
        finally
        {
            File.Delete(secret);
            Directory.Delete(link);
            Directory.Delete(realDirectory);
        }
    }

    [Fact]
    public void Linux_group_readable_secret_is_rejected()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Write("group-readable", "Correct-Horse-Battery-Staple-123!");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                  UnixFileMode.GroupRead);

        var exception = Assert.Throws<DatabaseSecurityRejectedException>(() => SecureSecretFile.ReadPassword(path));
        Assert.Equal("login_password_secret_invalid", exception.Code);
    }

    [Theory]
    [InlineData(0x120)]
    [InlineData(0x124)]
    public void Linux_read_only_non_owner_secret_is_rejected(int rawMode)
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Write($"non-owner-readable-{rawMode}", "Correct-Horse-Battery-Staple-123!");
        File.SetUnixFileMode(path, (UnixFileMode)rawMode);

        var exception = Assert.Throws<DatabaseSecurityRejectedException>(() => SecureSecretFile.ReadPassword(path));
        Assert.Equal("login_password_secret_invalid", exception.Code);
    }

    [Fact]
    public void Linux_non_private_secret_directory_is_rejected()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Write("public-directory", "Correct-Horse-Battery-Staple-123!");
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        try
        {
            var exception = Assert.Throws<DatabaseSecurityRejectedException>(() => SecureSecretFile.ReadPassword(path));
            Assert.Equal("login_password_secret_invalid", exception.Code);
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Linux_secret_owned_by_different_effective_user_is_rejected()
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0) return;
        var path = Write("wrong-file-owner", "Correct-Horse-Battery-Staple-123!");
        Assert.Equal(0, ChangeOwner(path, 65534, 65534));

        var exception = Assert.Throws<DatabaseSecurityRejectedException>(() =>
            SecureSecretFile.ReadPassword(path));

        Assert.Equal("login_password_secret_invalid", exception.Code);
    }

    [Fact]
    public void Linux_secret_parent_owned_by_different_effective_user_is_rejected()
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0) return;
        var path = Write("wrong-parent-owner", "Correct-Horse-Battery-Staple-123!");
        Assert.Equal(0, ChangeOwner(directory, 65534, 65534));
        try
        {
            var exception = Assert.Throws<DatabaseSecurityRejectedException>(() =>
                SecureSecretFile.ReadPassword(path));
            Assert.Equal("login_password_secret_invalid", exception.Code);
        }
        finally
        {
            Assert.Equal(0, ChangeOwner(directory, 0, 0));
        }
    }

    [Fact]
    public void Linux_same_inode_same_size_rewrite_race_is_rejected()
    {
        if (!OperatingSystem.IsLinux()) return;
        var original = "Correct-Horse-Battery-Staple-123!";
        var replacement = "Changed-Horse-Battery-Staple-123!";
        Assert.Equal(original.Length, replacement.Length);
        var path = Write("rewrite-race", original);
        LinuxSecretFile.AfterOpenBeforeReadForTests = () => File.WriteAllText(path, replacement);
        try
        {
            var exception = Assert.Throws<DatabaseSecurityRejectedException>(() => SecureSecretFile.ReadPassword(path));
            Assert.Equal("login_password_secret_invalid", exception.Code);
        }
        finally
        {
            LinuxSecretFile.AfterOpenBeforeReadForTests = null;
        }
    }

    [Fact]
    public void Linux_hard_linked_secret_is_rejected()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Write("hard-link-target", "Correct-Horse-Battery-Staple-123!");
        var hardLink = Path.Combine(directory, "hard-link");
        Assert.Equal(0, CreateHardLink(path, hardLink));

        var exception = Assert.Throws<DatabaseSecurityRejectedException>(() => SecureSecretFile.ReadPassword(path));
        Assert.Equal("login_password_secret_invalid", exception.Code);
    }

    [Fact]
    public void Linux_mount_id_is_part_of_the_file_identity_contract()
    {
        Assert.Equal(0x17ffU, LinuxSecretFile.RequestedMaskForTests);
        Assert.Equal(0x13cfU, LinuxSecretFile.RequiredMaskForTests);
        Assert.Equal(24, LinuxSecretFile.OpenHowSizeForTests);
        Assert.Equal(256, LinuxSecretFile.FileStatusSizeForTests);
        Assert.Equal(144, LinuxSecretFile.MountIdOffsetForTests);
        Assert.True(LinuxSecretFile.MountIdentityMatchesForTests(42, 42));
        Assert.False(LinuxSecretFile.MountIdentityMatchesForTests(42, 43));
    }

    [Fact]
    public void Linux_mount_change_between_path_and_opened_handle_is_rejected()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Write("mount-race", "Correct-Horse-Battery-Staple-123!");
        LinuxSecretFile.MountIdForTests = (stage, mountId) =>
            stage == SecretFileObservationStage.OpenedHandle ? mountId + 1 : mountId;
        try
        {
            var exception = Assert.Throws<DatabaseSecurityRejectedException>(() =>
                SecureSecretFile.ReadPassword(path));
            Assert.Equal("login_password_secret_invalid", exception.Code);
        }
        finally
        {
            LinuxSecretFile.MountIdForTests = null;
        }
    }

    [Fact]
    public async Task Stable_error_output_never_contains_secret_or_path()
    {
        const string sentinel = "SENTINEL-SHOULD-NEVER-BE-LOGGED-123";
        var path = Write("admin-secret-sentinel", sentinel + "\n");
        var output = new StringWriter();
        var error = new StringWriter();
        var hash = new string('a', 64);
        var prefix = RoleContract.DerivePrefix("prod-a", "saydin", hash);
        var args = new[]
        {
            "verify", "--admin-connection-file", path,
            "--deployment-id", "prod-a", "--target-database", "saydin",
            "--system-identifier-sha256", hash, "--role-prefix", prefix,
            "--timescaledb-version", "2.23.1", "--uuid-ossp-version", "1.1",
            "--backup-v1-valid-until", "2026-10-19T00:00:00Z",
        };

        var exit = await BootstrapApplication.RunAsync(args, output, error);

        Assert.Equal(BootstrapExitCodes.SecretRejected, exit);
        Assert.DoesNotContain(sentinel, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(path, error.ToString(), StringComparison.Ordinal);
        Assert.Equal("role-bootstrap failed: code=admin_connection_secret_invalid" + Environment.NewLine,
            error.ToString());
    }

    [Fact]
    public void Rejected_secret_exception_does_not_retain_io_exception_or_secret()
    {
        var path = Path.Combine(directory, "missing-secret-sentinel");

        var exception = Assert.Throws<DatabaseSecurityRejectedException>(() => SecureSecretFile.ReadPassword(path));

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(path, exception.ToString(), StringComparison.Ordinal);
    }

    private string Write(string name, string value)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, value);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    public void Dispose()
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            File.Delete(path);
        Directory.Delete(directory);
    }

    [DllImport("libc", EntryPoint = "link", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int CreateHardLink(string existingPath, string newPath);

    [DllImport("libc", EntryPoint = "chown", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int ChangeOwner(string path, uint owner, uint group);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
