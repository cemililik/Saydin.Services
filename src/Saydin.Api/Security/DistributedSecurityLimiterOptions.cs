using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Saydin.DatabaseSecurity;

namespace Saydin.Api.Security;

public sealed class DistributedSecurityLimiterOptions
{
    public const string SectionName = "DistributedSecurityLimiter";

    public bool Enabled { get; set; } = true;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;

    [Range(1, 1_000_000)]
    public int ExactIpLimit { get; set; } = 60;

    [Range(1, 1_000_000)]
    public int NetworkLimit { get; set; } = 300;

    [Range(1, 1_000_000)]
    public int PrincipalLimit { get; set; } = 120;

    [Range(1, 100_000)]
    public int RegistrationExactHourlyLimit { get; set; } = 3;

    [Range(1, 100_000)]
    public int RegistrationExactDailyLimit { get; set; } = 5;

    [Range(1, 100_000)]
    public int RegistrationNetworkHourlyLimit { get; set; } = 20;

    [Range(1, 100_000)]
    public int RegistrationNetworkDailyLimit { get; set; } = 100;

    [Range(1, 1_000_000)]
    public int RegistrationIpv4ExactHourlyLimit { get; set; } = 60;

    [Range(1, 1_000_000)]
    public int RegistrationIpv4NetworkHourlyLimit { get; set; } = 1_000;

    // IPv4 shared-address traffic does not consume this scarce daily bucket.
    // It applies only to IPv6 /64 subscriber networks.
    [Range(1, 100_000)]
    public int CalculationNetworkDailyLimit { get; set; } = 500;

    public string HmacKeyFile { get; set; } = string.Empty;

    public string RedisKeyPrefix { get; set; } = "security:rate:v1:";

    internal static bool HasValidShape(DistributedSecurityLimiterOptions value) =>
        value.WindowSeconds is >= 1 and <= 3600 &&
        value.ExactIpLimit is >= 1 and <= 1_000_000 &&
        value.NetworkLimit is >= 1 and <= 1_000_000 &&
        value.PrincipalLimit is >= 1 and <= 1_000_000 &&
        value.RegistrationExactHourlyLimit is >= 1 and <= 100_000 &&
        value.RegistrationExactDailyLimit is >= 1 and <= 100_000 &&
        value.RegistrationNetworkHourlyLimit is >= 1 and <= 100_000 &&
        value.RegistrationNetworkDailyLimit is >= 1 and <= 100_000 &&
        value.RegistrationIpv4ExactHourlyLimit is >= 1 and <= 1_000_000 &&
        value.RegistrationIpv4NetworkHourlyLimit is >= 1 and <= 1_000_000 &&
        value.CalculationNetworkDailyLimit is >= 1 and <= 100_000 &&
        value.RegistrationExactHourlyLimit <= value.RegistrationExactDailyLimit &&
        value.RegistrationNetworkHourlyLimit <= value.RegistrationNetworkDailyLimit &&
        value.RegistrationExactHourlyLimit <= value.RegistrationNetworkHourlyLimit &&
        value.RegistrationExactDailyLimit <= value.RegistrationNetworkDailyLimit &&
        value.RegistrationIpv4ExactHourlyLimit <= value.RegistrationIpv4NetworkHourlyLimit &&
        value.RedisKeyPrefix.Length is >= 1 and <= 96 &&
        value.RedisKeyPrefix.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is ':' or '-' or '_');
}

internal sealed class DistributedSecurityLimiterOptionsValidator
    : IValidateOptions<DistributedSecurityLimiterOptions>
{
    public ValidateOptionsResult Validate(string? name, DistributedSecurityLimiterOptions options)
    {
        if (!DistributedSecurityLimiterOptions.HasValidShape(options))
            return ValidateOptionsResult.Fail("security_limiter_options_invalid");
        if (!options.Enabled) return ValidateOptionsResult.Success;
        if (!Path.IsPathFullyQualified(options.HmacKeyFile))
            return ValidateOptionsResult.Fail("security_limiter_secret_invalid");

        try
        {
            _ = SecureSecretFile.ReadPassword(options.HmacKeyFile);
            return ValidateOptionsResult.Success;
        }
        catch (DatabaseSecurityRejectedException)
        {
            return ValidateOptionsResult.Fail("security_limiter_secret_invalid");
        }
    }
}
