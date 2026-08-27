using Microsoft.Extensions.Options;

namespace Saydin.Api.Security;

public static class DistributedSecurityLimiterServiceCollectionExtensions
{
    public static IServiceCollection AddDistributedSecurityLimiter(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<DistributedSecurityLimiterOptions>,
            DistributedSecurityLimiterOptionsValidator>();
        services.AddOptions<DistributedSecurityLimiterOptions>()
            .Bind(configuration.GetSection(DistributedSecurityLimiterOptions.SectionName))
            .Validate(DistributedSecurityLimiterOptions.HasValidShape,
                "security_limiter_options_invalid")
            .ValidateOnStart();
        services.AddSingleton<SecurityLimiterPseudonymizer>();
        services.AddSingleton<IDistributedSecurityLimiter, DistributedSecurityLimiter>();
        services.AddTransient<DistributedSecurityLimiterMiddleware>();
        return services;
    }
}
