using System.Diagnostics;
using System.Security.Cryptography;

namespace Saydin.CalendarData.Tests;

public sealed class VerifyCandidateBehaviorTests
{
    [Theory]
    [InlineData("valid", 0, "calendar_candidate_signature_and_offline_replay_verified")]
    [InlineData("manifest", 65, "manifest_hash_mismatch")]
    [InlineData("extra", 65, "candidate_contains_untracked_file")]
    [InlineData("foreign-key", 65, "signature_invalid")]
    [InlineData("owner", 65, "candidate_owner_identity_mismatch")]
    public async Task RealVerifier_EnforcesSignatureHashesInventoryAndOfflineReplay(
        string mutation,
        int expectedExit,
        string expectedText)
    {
        if (!OperatingSystem.IsLinux())
            throw Xunit.Sdk.SkipException.ForSkip(
                "GNU find/stat verifier behavior is executed in the required Linux Docker gate.");

        using var temp = new TempRoot();
        var candidate = Path.Combine(temp.Path, "candidate");
        CopyTree(CalendarDataTestRoot.DataRoot, candidate);
        var envelopePath = Path.Combine(candidate, "review-envelope.json");
        var manifestPath = Path.Combine(candidate, "source-manifest.json");
        var expectedPath = Path.Combine(candidate, "expected-output.json");
        await File.WriteAllBytesAsync(envelopePath, ManifestJson.Write(new CalendarReviewEnvelope
        {
            SchemaVersion = 1,
            SnapshotSetId = CalendarDataTestRoot.ReadManifest().SnapshotSetId,
            SourceManifestSha256 = Hash(await File.ReadAllBytesAsync(manifestPath)),
            ExpectedOutputSha256 = Hash(await File.ReadAllBytesAsync(expectedPath)),
        }));

        var privateKey = Path.Combine(temp.Path, "reviewer.pem");
        var publicKey = Path.Combine(temp.Path, "reviewer-public.pem");
        await RunAsync("openssl", ["genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048", "-out", privateKey]);
        await RunAsync("openssl", ["pkey", "-in", privateKey, "-pubout", "-out", publicKey]);
        var signature = Path.Combine(temp.Path, "envelope.sig");
        await RunAsync("openssl", ["dgst", "-sha256", "-sign", privateKey, "-out", signature, envelopePath]);

        if (mutation == "manifest")
            await File.AppendAllTextAsync(manifestPath, "\n");
        else if (mutation == "extra")
            await File.WriteAllTextAsync(Path.Combine(candidate, "untracked"), "x");
        else if (mutation == "foreign-key")
        {
            var foreign = Path.Combine(temp.Path, "foreign.pem");
            await RunAsync("openssl", ["genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048", "-out", foreign]);
            await RunAsync("openssl", ["pkey", "-in", foreign, "-pubout", "-out", publicKey]);
        }

        var bin = Path.Combine(temp.Path, "bin");
        Directory.CreateDirectory(bin);
        var docker = Path.Combine(bin, "docker");
        await File.WriteAllTextAsync(docker, "#!/bin/sh\ncase \" $* \" in *' --network none '*) exit 0;; *) exit 91;; esac\n");
        File.SetUnixFileMode(docker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var jq = Path.Combine(bin, "jq");
        await File.WriteAllTextAsync(jq, """
            #!/bin/sh
            set -eu
            mode=$1
            expression=$2
            input=$3
            if [ "$expression" = '.sources[].snapshotPath, .calendars[].outputPath' ]; then
              sed -nE 's/.*"(snapshotPath|outputPath)"[[:space:]]*:[[:space:]]*"([^"]+)".*/\2/p' "$input"
              exit 0
            fi
            case "$expression" in
              .schemaVersion*) key=schemaVersion ;;
              .snapshotSetId*) key=snapshotSetId ;;
              .sourceManifestSha256*) key=sourceManifestSha256 ;;
              .expectedOutputSha256*) key=expectedOutputSha256 ;;
              *) exit 2 ;;
            esac
            value=$(sed -nE "s/.*\"$key\"[[:space:]]*:[[:space:]]*(\"[^\"]+\"|[0-9]+).*/\\1/p" "$input" | head -1)
            [ -n "$value" ] || exit 1
            printf '%s\n' "$value" | sed 's/^"//;s/"$//'
            """);
        File.SetUnixFileMode(jq, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var verifier = Path.Combine(RepositoryRoot(), "infrastructure/calendar/verify-candidate.sh");
        var start = new ProcessStartInfo(verifier)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var value in new[] { candidate, signature, publicKey,
                     $"calendar.invalid/test@sha256:{new string('a', 64)}" })
            start.ArgumentList.Add(value);
        start.Environment["PATH"] = $"{bin}:{Environment.GetEnvironmentVariable("PATH")}";
        start.Environment["SAYDIN_CALENDAR_REVIEWER_PUBLIC_KEY_SHA256"] =
            Hash(await File.ReadAllBytesAsync(publicKey));
        start.Environment["SAYDIN_CALENDAR_RUNTIME_UID"] = GetUnixId("-u");
        start.Environment["SAYDIN_CALENDAR_RUNTIME_GID"] = GetUnixId("-g");
        if (mutation == "owner")
            start.Environment["SAYDIN_CALENDAR_RUNTIME_UID"] = "2147483646";
        using var process = Process.Start(start)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var output = stdout + stderr;
        Assert.True(process.ExitCode == expectedExit,
            $"expected exit {expectedExit}, actual {process.ExitCode}: {output}");
        Assert.True(output.Contains(expectedText, StringComparison.Ordinal),
            $"expected '{expectedText}' in: {output}");
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string GetUnixId(string argument)
    {
        var start = new ProcessStartInfo("id")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var value = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return value;
    }

    private static async Task RunAsync(string fileName, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, stderr);
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == ".DS_Store") continue;
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(CalendarDataTestRoot.DataRoot, "..", "..", ".."));

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"saydin-calendar-verifier-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
