using Saydin.DatabaseSecurity;

namespace Saydin.DatabaseRoleBootstrap.Tests;

public sealed class BootstrapOptionsTests
{
    private const string SystemHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Verify_requires_no_login_password_argument()
    {
        var options = BootstrapOptions.Parse(Common("verify"));

        Assert.Equal(BootstrapCommand.Verify, options.Command);
        Assert.Empty(options.PasswordFiles);
        Assert.Null(options.RotatePasswordFile);
    }

    [Fact]
    public void Ensure_requires_every_login_password_file()
    {
        var exception = Assert.Throws<BootstrapRejectedException>(() =>
            BootstrapOptions.Parse(Common("ensure")));

        Assert.Equal("argument_required", exception.Code);
    }

    [Fact]
    public void Ensure_pins_backup_secret_file_and_canonical_v1_expiry()
    {
        var args = Common("ensure").ToList();
        foreach (var purpose in Enum.GetValues<LoginPurpose>())
        {
            args.Add($"--{RoleContract.PurposeName(purpose).Replace('_', '-')}-password-file");
            args.Add($"/run/secrets/{RoleContract.PurposeName(purpose)}-v1");
        }
        args.Add("--backup-password-file");
        args.Add("/run/secrets/backup-v1");

        var options = BootstrapOptions.Parse(args.ToArray());

        Assert.Equal("/run/secrets/backup-v1", options.BackupPasswordFile);
        Assert.Equal(DateTimeOffset.Parse("2026-10-19T00:00:00Z"),
            options.BackupV1ValidUntilUtc);
        Assert.Equal(6, options.PasswordFiles.Count);
    }

    [Fact]
    public void Backup_rotation_requires_v2_new_secret_and_canonical_finite_expiry()
    {
        var options = BootstrapOptions.Parse(Common("rotate").Concat(new[]
        {
            "--login", "backup",
            "--login-version", "2",
            "--password-file", "/run/secrets/backup-v2",
            "--valid-until", "2026-11-19T00:00:00Z",
        }).ToArray());

        Assert.True(options.RotateBackup);
        Assert.Null(options.RotatePurpose);
        Assert.Equal(2, options.RotateVersion);
        Assert.Equal("/run/secrets/backup-v2", options.RotatePasswordFile);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T00:00:00Z"),
            options.RotateBackupValidUntilUtc);
    }

    [Theory]
    [InlineData("2026-11-19T00:00:00+00:00")]
    [InlineData("2026-11-19t00:00:00z")]
    [InlineData("2026-11-19T00:00:00.000Z")]
    [InlineData("not-a-timestamp")]
    public void Backup_rotation_rejects_noncanonical_expiry(string timestamp)
    {
        var action = () => BootstrapOptions.Parse(Common("rotate").Concat(new[]
        {
            "--login", "backup",
            "--login-version", "2",
            "--password-file", "/run/secrets/backup-v2",
            "--valid-until", timestamp,
        }).ToArray());

        var exception = Assert.Throws<BootstrapRejectedException>(action);
        Assert.Equal("backup_valid_until_invalid", exception.Code);
    }

    [Fact]
    public void Nonbackup_rotation_rejects_backup_expiry_argument()
    {
        var action = () => BootstrapOptions.Parse(Common("rotate").Concat(new[]
        {
            "--login", "api",
            "--login-version", "2",
            "--password-file", "/run/secrets/api-v2",
            "--valid-until", "2026-11-19T00:00:00Z",
        }).ToArray());

        var exception = Assert.Throws<BootstrapRejectedException>(action);
        Assert.Equal("argument_unsupported", exception.Code);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("not-a-version")]
    public void Rotate_accepts_only_version_two(string version)
    {
        var args = Common("rotate").Concat(new[]
        {
            "--login", "api", "--login-version", version, "--password-file", "/run/secrets/api_v2",
        }).ToArray();

        var exception = Assert.Throws<BootstrapRejectedException>(() => BootstrapOptions.Parse(args));
        Assert.Equal("rotate_version_must_be_v2", exception.Code);
    }

    [Fact]
    public void Unknown_argument_is_rejected()
    {
        var args = Common("verify").Concat(new[] { "--mystery", "value" }).ToArray();

        var exception = Assert.Throws<BootstrapRejectedException>(() => BootstrapOptions.Parse(args));
        Assert.Equal("argument_unsupported", exception.Code);
    }

    private static string[] Common(string command)
    {
        var prefix = RoleContract.DerivePrefix("prod-a", "saydin", SystemHash);
        return
        [
            command,
            "--admin-connection-file", "/run/secrets/admin",
            "--deployment-id", "prod-a",
            "--target-database", "saydin",
            "--system-identifier-sha256", SystemHash,
            "--role-prefix", prefix,
            "--timescaledb-version", "2.23.1",
            "--uuid-ossp-version", "1.1",
            "--backup-v1-valid-until", "2026-10-19T00:00:00Z",
        ];
    }
}
