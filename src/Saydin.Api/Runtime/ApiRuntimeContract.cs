using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Saydin.Api.Runtime;

public sealed record TrustedNetwork(IPAddress Prefix, int PrefixLength);

public sealed class ApiRuntimeContract
{
    public required int PublicPort { get; init; }
    public required int ManagementPort { get; init; }
    public required IReadOnlyList<string> AllowedHosts { get; init; }
    public required IReadOnlyList<IPAddress> KnownProxies { get; init; }
    public required IReadOnlyList<TrustedNetwork> KnownNetworks { get; init; }

    public static ApiRuntimeContract Parse(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var publicPort = RequiredPort(configuration, "ApiRuntime:PublicPort");
        var managementPort = RequiredPort(configuration, "ApiRuntime:ManagementPort");
        if (publicPort == managementPort)
            throw Invalid("api_runtime_ports_must_be_distinct");

        var allowedHosts = ParseAllowedHosts(configuration["AllowedHosts"]);
        if (environment.IsProduction() && allowedHosts.Count == 0)
            throw Invalid("allowed_hosts_required_in_production");

        var proxies = ParseProxies(configuration["ForwardedHeaders:KnownProxies"]);
        var networks = ParseNetworks(configuration["ForwardedHeaders:KnownNetworks"]);
        if (proxies.Count == 0 && networks.Count == 0)
            throw Invalid("forwarded_headers_trust_required");

        var forwardLimit = configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit");
        if (forwardLimit != 1)
            throw Invalid("forwarded_headers_forward_limit_must_be_one");

        foreach (var proxy in proxies)
        {
            foreach (var network in networks)
            {
                if (Contains(network, proxy))
                    throw Invalid("forwarded_headers_trust_duplicate");
            }
        }

        return new ApiRuntimeContract
        {
            PublicPort = publicPort,
            ManagementPort = managementPort,
            AllowedHosts = allowedHosts,
            KnownProxies = proxies,
            KnownNetworks = networks,
        };
    }

    public void Configure(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                                   ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.RequireHeaderSymmetry = true;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in KnownProxies)
            options.KnownProxies.Add(proxy);
        foreach (var network in KnownNetworks)
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(
                network.Prefix, network.PrefixLength));
    }

    public void Configure(KestrelServerOptions options)
    {
        options.ListenAnyIP(PublicPort);
        options.ListenAnyIP(ManagementPort);
    }

    private static int RequiredPort(IConfiguration configuration, string key)
    {
        var value = configuration.GetValue<int?>(key);
        return value is > 0 and <= 65_535
            ? value.Value
            : throw Invalid("api_runtime_port_invalid");
    }

    private static IReadOnlyList<string> ParseAllowedHosts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var result = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in SplitStrict(value, ';', "allowed_hosts_invalid"))
        {
            if (token is "*" or "+" || token.Contains('*', StringComparison.Ordinal)
                || token.Contains('/', StringComparison.Ordinal)
                || token.Contains("\\", StringComparison.Ordinal)
                || token.Contains("://", StringComparison.Ordinal))
                throw Invalid("allowed_hosts_invalid");

            var candidate = token.Length >= 2 && token[0] == '[' && token[^1] == ']'
                ? token[1..^1]
                : token;
            if (Uri.CheckHostName(candidate) == UriHostNameType.Unknown || !unique.Add(token))
                throw Invalid("allowed_hosts_invalid");
            result.Add(token);
        }
        return result;
    }

    private static IReadOnlyList<IPAddress> ParseProxies(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var result = new List<IPAddress>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in SplitStrict(value, ',', "forwarded_headers_proxy_invalid"))
        {
            if (!IPAddress.TryParse(token, out var parsed))
                throw Invalid("forwarded_headers_proxy_invalid");
            var address = Normalize(parsed);
            if (!IsUsableAddress(address) || !unique.Add(address.ToString()))
                throw Invalid("forwarded_headers_proxy_invalid");
            result.Add(address);
        }
        return result;
    }

    private static IReadOnlyList<TrustedNetwork> ParseNetworks(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var result = new List<TrustedNetwork>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in SplitStrict(value, ',', "forwarded_headers_network_invalid"))
        {
            var slash = token.IndexOf('/');
            if (slash <= 0 || slash != token.LastIndexOf('/')
                || !IPAddress.TryParse(token[..slash], out var parsed)
                || !int.TryParse(token[(slash + 1)..], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var prefixLength))
                throw Invalid("forwarded_headers_network_invalid");

            var prefix = Normalize(parsed);
            var minimum = prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? 24 : 64;
            var maximum = prefix.GetAddressBytes().Length * 8;
            if (!IsUsableAddress(prefix) || prefixLength < minimum || prefixLength > maximum
                || !IsCanonicalNetwork(prefix, prefixLength))
                throw Invalid("forwarded_headers_network_invalid");
            var key = $"{prefix}/{prefixLength}";
            if (!unique.Add(key))
                throw Invalid("forwarded_headers_network_invalid");
            result.Add(new TrustedNetwork(prefix, prefixLength));
        }
        return result;
    }

    private static IEnumerable<string> SplitStrict(string value, char separator, string code)
    {
        foreach (var raw in value.Split(separator, StringSplitOptions.None))
        {
            if (raw.Length == 0 || !string.Equals(raw, raw.Trim(), StringComparison.Ordinal)
                || raw.Any(char.IsWhiteSpace))
                throw Invalid(code);
            yield return raw;
        }
    }

    private static bool IsUsableAddress(IPAddress address)
    {
        if (IPAddress.Any.Equals(address) || IPAddress.None.Equals(address)
            || IPAddress.IPv6Any.Equals(address) || IPAddress.IPv6None.Equals(address)
            || address.IsIPv6Multicast)
            return false;
        var bytes = address.GetAddressBytes();
        return bytes.Length != 4 || bytes[0] is < 224 or > 239;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool IsCanonicalNetwork(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var bit = prefixLength; bit < bytes.Length * 8; bit++)
            if ((bytes[bit / 8] & (1 << (7 - bit % 8))) != 0)
                return false;
        return true;
    }

    private static bool Contains(TrustedNetwork network, IPAddress address)
    {
        var left = network.Prefix.GetAddressBytes();
        var right = Normalize(address).GetAddressBytes();
        if (left.Length != right.Length) return false;
        for (var bit = 0; bit < network.PrefixLength; bit++)
            if ((left[bit / 8] & (1 << (7 - bit % 8))) !=
                (right[bit / 8] & (1 << (7 - bit % 8))))
                return false;
        return true;
    }

    private static InvalidOperationException Invalid(string code) => new(code);
}
