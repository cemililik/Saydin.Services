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

    public string HmacKeyFile { get; set; } = string.Empty;

    public string RedisKeyPrefix { get; set; } = "security:rate:v1:";

    internal static bool HasValidShape(DistributedSecurityLimiterOptions value) =>
        value.WindowSeconds is >= 1 and <= 3600 &&
        value.ExactIpLimit is >= 1 and <= 1_000_000 &&
        value.NetworkLimit is >= 1 and <= 1_000_000 &&
        value.PrincipalLimit is >= 1 and <= 1_000_000 &&
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
