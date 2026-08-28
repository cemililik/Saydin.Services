namespace Saydin.CalendarData.Tests;

using Xunit.Sdk;

public sealed class InfrastructureCalendarContractTests
{
    [Fact]
    public void Timers_PinDailyTcmbAndAnnualBistInIstanbul()
    {
        var root = RepositoryRoot();
        var tcmb = File.ReadAllText(Path.Combine(root,
            "infrastructure/calendar/systemd/calendar-acquisition-tcmb.timer"));
        var bist = File.ReadAllText(Path.Combine(root,
            "infrastructure/calendar/systemd/calendar-acquisition-bist.timer"));

        Assert.Contains("OnCalendar=*-*-* 06:00:00 Europe/Istanbul", tcmb);
        Assert.Contains("Unit=calendar-acquisition@tcmb.service", tcmb);
        Assert.Contains("OnCalendar=*-10-15 07:00:00 Europe/Istanbul", bist);
        Assert.Contains("Unit=calendar-acquisition@bist.service", bist);
        Assert.Contains("Persistent=true", tcmb);
        Assert.Contains("Persistent=true", bist);
    }

    [Fact]
    public void AcquisitionAndPromotionArtifacts_KeepSeparateFailClosedBoundaries()
    {
        var root = RepositoryRoot();
        var acquisition = File.ReadAllText(Path.Combine(root,
            "infrastructure/calendar/run-acquisition.sh"));
        var verifier = File.ReadAllText(Path.Combine(root,
            "infrastructure/calendar/verify-candidate.sh"));
        var promotion = File.ReadAllText(Path.Combine(root,
            "infrastructure/calendar/promote-reviewed-bundle.sh"));

        Assert.Contains("flock -n", acquisition);
        Assert.Contains("--kill-after=30s 15m", acquisition);
        Assert.Contains("while [ \"$attempt\" -le 3 ]", acquisition);
        Assert.Contains("@sha256:[0-9a-f]{64}", acquisition);
        Assert.Contains("--memory 256m", acquisition);
        Assert.Contains("--pids-limit 128", acquisition);
        Assert.Contains("materialize-plan", acquisition);
        Assert.Contains("calendar_acquisition_noop", acquisition);
        Assert.Contains("openssl dgst -sha256 -verify", verifier);
        Assert.Contains("--network none", verifier);
        Assert.Contains("SAYDIN_CALENDAR_REVIEWER_PUBLIC_KEY_SHA256", verifier);
        Assert.Contains("candidate_owner_identity_mismatch", verifier);
        Assert.Contains("candidate_contains_untracked_file", verifier);
        Assert.Contains("mv -T -n", promotion);
        Assert.Contains("SAYDIN_CALENDAR_VERIFY_CANDIDATE", promotion);
        Assert.Contains("candidate_not_in_quarantine", promotion);
        Assert.Contains("calendar_quarantine_candidate_removed", promotion);
        Assert.Contains("database_activation_not_performed", promotion);
        Assert.DoesNotContain(" import ", acquisition);
        Assert.DoesNotContain(" activate ", acquisition);
        Assert.DoesNotContain(" import ", promotion);
        Assert.DoesNotContain(" activate ", promotion);
    }

    [Fact]
    public void CalendarImage_IsLockedMultiStageAndNonRoot1001()
    {
        var dockerfile = File.ReadAllText(Path.Combine(RepositoryRoot(),
            "tools/calendar-data/Dockerfile"));

        Assert.Contains("AS build", dockerfile);
        Assert.Contains("-p:RestoreLockedMode=true", dockerfile);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/runtime@sha256:", dockerfile);
        Assert.Contains("groupadd -g 1001", dockerfile);
        Assert.Contains("useradd -r -u 1001 -g 1001", dockerfile);
        Assert.Contains("USER appuser", dockerfile);
        Assert.DoesNotContain("FROM mcr.microsoft.com/dotnet/sdk:10.0", dockerfile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Promotion_SourceMutationAfterFirstVerification_FailsWithoutPendingOrFinal(
        bool replaceWithSymlink)
    {
        if (!OperatingSystem.IsLinux())
            throw SkipException.ForSkip(
                "Linux-specific symlink/rename race; required Docker CI executes this test with zero skips.");
        using var temp = new TempRoot();
        var root = RepositoryRoot();
        var scriptDirectory = Path.Combine(temp.Path, "scripts");
        var staging = Path.Combine(temp.Path, "quarantine");
        var candidate = Path.Combine(staging, "candidate-under-test");
        var promotion = Path.Combine(temp.Path, "promotion");
        var barrier = Path.Combine(temp.Path, "barrier");
        Directory.CreateDirectory(scriptDirectory);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(candidate);
        Directory.CreateDirectory(promotion);
        Directory.CreateDirectory(barrier);
        File.Copy(Path.Combine(root, "infrastructure/calendar/promote-reviewed-bundle.sh"),
            Path.Combine(scriptDirectory, "promote-reviewed-bundle.sh"));
        await File.WriteAllTextAsync(Path.Combine(candidate, "payload"), "trusted\n");
        var external = Path.Combine(temp.Path, "external");
        await File.WriteAllTextAsync(external, "untrusted\n");

        var verifier = Path.Combine(scriptDirectory, "verify-candidate.sh");
        await File.WriteAllTextAsync(verifier, """
            #!/bin/sh
            set -eu
            candidate=$1
            barrier=${SAYDIN_TEST_PROMOTION_BARRIER:?}
            if mkdir "$barrier/first" 2>/dev/null; then
              : > "$barrier/verified"
              while [ ! -f "$barrier/continue" ]; do sleep 0.01; done
              exit 0
            fi
            [ -f "$candidate/payload" ] && [ ! -L "$candidate/payload" ]
            [ "$(cat "$candidate/payload")" = trusted ]
            """);
        File.SetUnixFileMode(verifier,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var promote = Path.Combine(scriptDirectory, "promote-reviewed-bundle.sh");
        File.SetUnixFileMode(promote,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = promote,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add(candidate);
        process.StartInfo.ArgumentList.Add(Path.Combine(temp.Path, "signature"));
        process.StartInfo.ArgumentList.Add(Path.Combine(temp.Path, "public.pem"));
        process.StartInfo.ArgumentList.Add(promotion);
        process.StartInfo.ArgumentList.Add("release-under-test");
        process.StartInfo.ArgumentList.Add($"calendar.invalid/test@sha256:{new string('a', 64)}");
        process.StartInfo.Environment["SAYDIN_TEST_PROMOTION_BARRIER"] = barrier;
        process.StartInfo.Environment["SAYDIN_CALENDAR_VERIFY_CANDIDATE"] = verifier;
        process.StartInfo.Environment["SAYDIN_CALENDAR_STAGING_ROOT"] = staging;
        process.StartInfo.Environment["SAYDIN_CALENDAR_RUNTIME_UID"] = GetUnixId("-u");
        process.StartInfo.Environment["SAYDIN_CALENDAR_RUNTIME_GID"] = GetUnixId("-g");
        Assert.True(process.Start());

        var verified = Path.Combine(barrier, "verified");
        await WaitForAsync(() => File.Exists(verified), process);
        var payload = Path.Combine(candidate, "payload");
        File.Delete(payload);
        if (replaceWithSymlink)
            File.CreateSymbolicLink(payload, external);
        else
            await File.WriteAllTextAsync(payload, "untrusted\n");
        await File.WriteAllTextAsync(Path.Combine(barrier, "continue"), "continue\n");

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(0, process.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(promotion, "release-under-test")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            promotion, ".pending-release-under-test-*"));
    }

    private static async Task WaitForAsync(Func<bool> predicate, System.Diagnostics.Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate())
        {
            if (process.HasExited)
                Assert.Fail(
                    $"promotion exited before barrier: {await process.StandardError.ReadToEndAsync()}");
            await Task.Delay(10, timeout.Token);
        }
    }

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(CalendarDataTestRoot.DataRoot, "..", "..", ".."));

    private static string GetUnixId(string argument)
    {
        var start = new System.Diagnostics.ProcessStartInfo("id")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var value = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return value;
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"saydin-calendar-promotion-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
