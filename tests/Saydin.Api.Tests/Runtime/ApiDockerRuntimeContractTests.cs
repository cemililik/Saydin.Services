using FluentAssertions;

namespace Saydin.Api.Tests.Runtime;

public sealed class ApiDockerRuntimeContractTests
{
    [Fact]
    public void Dockerfile_ExposesBothPortsAndProbesDependencyFreePublicLiveness()
    {
        var dockerfile = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Saydin.Api", "Dockerfile"));

        dockerfile.Should().Contain("EXPOSE 8080 9090");
        dockerfile.Should().Contain("curl -fsS http://localhost:8080/health/live || exit 1");
        dockerfile.Should().NotContain("localhost:8080/health || exit 1");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Saydin.Services.sln")))
            directory = directory.Parent;

        return directory?.FullName
               ?? throw new InvalidOperationException("Repo root was not found.");
    }
}
