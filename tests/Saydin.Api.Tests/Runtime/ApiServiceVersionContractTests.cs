using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Saydin.Api.Runtime;

namespace Saydin.Api.Tests.Runtime;

public sealed class ApiServiceVersionContractTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Parse_ProductionRequiresVersion(string? value)
    {
        var act = () => Parse(Environments.Production, value);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("service_version_required_in_production");
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("unknown")]
    [InlineData("latest")]
    [InlineData("development")]
    public void Parse_ProductionRejectsPlaceholders(string value)
    {
        var act = () => Parse(Environments.Production, value);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("service_version_placeholder_forbidden");
    }

    [Theory]
    [InlineData(" release-abc123")]
    [InlineData("release/abc123")]
    [InlineData("${RELEASE_SHA}")]
    public void Parse_RejectsNonCanonicalValues(string value)
    {
        var act = () => Parse(Environments.Production, value);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("service_version_invalid");
    }

    [Theory]
    [InlineData("v2.7.1+git.8ca2f0a")]
    [InlineData("sha256:87d1f13d0b49dd389e23e203149f6c97")]
    [InlineData("2026.08.19-8ca2f0a")]
    public void Parse_AcceptsCanonicalReleaseCommitOrDigest(string value)
    {
        Parse(Environments.Production, value).Should().Be(value);
    }

    [Fact]
    public void Parse_DevelopmentUsesExplicitFallbackWhenVersionIsAbsent()
    {
        Parse(Environments.Development, null)
            .Should().Be(ApiServiceVersionContract.DevelopmentFallback);
    }

    private static string Parse(string environmentName, string? value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApiServiceVersionContract.ConfigurationKey] = value,
            })
            .Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        return ApiServiceVersionContract.Parse(configuration, environment);
    }
}
