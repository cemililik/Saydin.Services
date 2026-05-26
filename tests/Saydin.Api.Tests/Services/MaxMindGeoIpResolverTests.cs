using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Saydin.Api.Services;

namespace Saydin.Api.Tests.Services;

public class MaxMindGeoIpResolverTests
{
    private static MaxMindGeoIpResolver CreateResolver(string? dbPath = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                dbPath is not null
                    ? new Dictionary<string, string?> { ["GeoIp:DatabasePath"] = dbPath }
                    : [])
            .Build();

        return new MaxMindGeoIpResolver(config, NullLogger<MaxMindGeoIpResolver>.Instance);
    }

    [Fact]
    public void Resolve_Null_ReturnsNulls()
    {
        var resolver = CreateResolver();

        var (country, city) = resolver.Resolve(null);

        country.Should().BeNull();
        city.Should().BeNull();
    }

    [Fact]
    public void Resolve_Loopback_ReturnsNulls()
    {
        var resolver = CreateResolver();

        var (country, city) = resolver.Resolve(IPAddress.Loopback);

        country.Should().BeNull();
        city.Should().BeNull();
    }

    [Fact]
    public void Resolve_PrivateIp_ReturnsNulls()
    {
        var resolver = CreateResolver();

        var (country, city) = resolver.Resolve(IPAddress.Parse("192.168.1.42"));

        country.Should().BeNull();
        city.Should().BeNull();
    }

    [Fact]
    public void Resolve_NoDatabaseConfigured_ReturnsNulls()
    {
        var resolver = CreateResolver();

        var (country, city) = resolver.Resolve(IPAddress.Parse("8.8.8.8"));

        country.Should().BeNull();
        city.Should().BeNull();
    }

    [Fact]
    public void Resolve_InvalidDatabasePath_ReturnsNulls()
    {
        var resolver = CreateResolver("/nonexistent/path.mmdb");

        var (country, city) = resolver.Resolve(IPAddress.Parse("8.8.8.8"));

        country.Should().BeNull();
        city.Should().BeNull();
    }
}
