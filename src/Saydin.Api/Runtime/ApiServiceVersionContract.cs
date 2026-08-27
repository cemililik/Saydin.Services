namespace Saydin.Api.Runtime;

public static class ApiServiceVersionContract
{
    public const string ConfigurationKey = "SAYDIN_SERVICE_VERSION";
    public const string DevelopmentFallback = "development-local";

    private static readonly HashSet<string> Placeholders = new(
        [
            "0.0.0",
            "1.0.0",
            "dev",
            "development",
            "latest",
            "local",
            "snapshot",
            "todo",
            "unknown",
            "unset",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static string Parse(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var configured = configuration[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (environment.IsProduction())
                throw Invalid("service_version_required_in_production");

            return DevelopmentFallback;
        }

        if (configured.Length > 128
            || !string.Equals(configured, configured.Trim(), StringComparison.Ordinal)
            || !char.IsAsciiLetterOrDigit(configured[0])
            || configured.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not '.' and not '_' and not '+' and not ':' and not '-'))
            throw Invalid("service_version_invalid");

        if (Placeholders.Contains(configured))
            throw Invalid("service_version_placeholder_forbidden");

        return configured;
    }

    private static InvalidOperationException Invalid(string code) => new(code);
}
