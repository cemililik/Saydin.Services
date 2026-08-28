using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Saydin.Api.Runtime;

namespace Saydin.Api.Tests.Runtime;

public sealed class ApiRuntimeContractTests
{
    [Fact]
    public void Parse_ExactProductionContract_ConfiguresTwoPortsAndClearsFrameworkTrustDefaults()
    {
        var contract = Parse();
        var options = new ForwardedHeadersOptions();

        contract.Configure(options);

        contract.PublicPort.Should().Be(8080);
        contract.ManagementPort.Should().Be(9090);
        contract.AllowedHosts.Should().Equal("api.example.test", "10.20.30.40");
        options.ForwardLimit.Should().Be(1);
        options.RequireHeaderSymmetry.Should().BeTrue();
        options.ForwardedHeaders.Should().Be(
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
        options.KnownProxies.Should().Equal(IPAddress.Parse("10.10.10.10"));
        options.KnownIPNetworks.Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("api.example.test;*")]
    [InlineData("api.example.test;api.example.test")]
    [InlineData(" api.example.test")]
    [InlineData("https://api.example.test")]
    [InlineData("api.example.test:8080")]
    public void Parse_ProductionRejectsNonExactAllowedHosts(string value)
    {
        var act = () => Parse(("AllowedHosts", value));
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies", "bad")]
    [InlineData("ForwardedHeaders:KnownProxies", "10.10.10.10,10.10.10.10")]
    [InlineData("ForwardedHeaders:KnownProxies", "10.10.10.10,")]
    [InlineData("ForwardedHeaders:KnownNetworks", "10.0.0.0/8")]
    [InlineData("ForwardedHeaders:KnownNetworks", "10.20.30.1/24")]
    [InlineData("ForwardedHeaders:KnownNetworks", "2001:db8::/48")]
    [InlineData("ForwardedHeaders:KnownNetworks", "10.20.30.0/24,10.20.30.0/24")]
    [InlineData("ForwardedHeaders:ForwardLimit", "2")]
    public void Parse_RejectsMalformedDuplicateBroadOrNonCanonicalForwardingTrust(
        string key, string value)
    {
        var act = () => Parse((key, value));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Parse_RequiresExplicitProxyOrNetwork()
    {
        var act = () => Parse(
            ("ForwardedHeaders:KnownProxies", ""),
            ("ForwardedHeaders:KnownNetworks", ""));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("forwarded_headers_trust_required");
    }

    [Fact]
    public void Parse_RejectsProxyDuplicatedByTrustedNetwork()
    {
        var act = () => Parse(
            ("ForwardedHeaders:KnownProxies", "10.20.30.10"),
            ("ForwardedHeaders:KnownNetworks", "10.20.30.0/24"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("forwarded_headers_trust_duplicate");
    }

    private static ApiRuntimeContract Parse(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "api.example.test;10.20.30.40",
            ["ApiRuntime:PublicPort"] = "8080",
            ["ApiRuntime:ManagementPort"] = "9090",
            ["ForwardedHeaders:KnownProxies"] = "10.10.10.10",
            ["ForwardedHeaders:KnownNetworks"] = "10.20.30.0/24",
            ["ForwardedHeaders:ForwardLimit"] = "1",
        };
        foreach (var (key, value) in overrides) values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        return ApiRuntimeContract.Parse(configuration, environment);
    }
}
