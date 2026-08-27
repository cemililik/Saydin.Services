using Saydin.Api.Helpers;
using Saydin.Api.Services;
using Saydin.Shared.Constants;

namespace Saydin.Api.Middleware;

/// <summary>
/// Endpoint handler'ların oluşturduğu <see cref="ActivityLogBuilder"/> nesnelerini
/// pipeline sonunda otomatik olarak <see cref="IActivityLogger"/>'a yazar. Bu sayede:
/// <list type="bullet">
///   <item>Başarılı/başarısız tüm istekler activity_logs tablosuna yazılır
///         (önceden yalnız success path log atıyordu — observability boşluğu).</item>
///   <item>Status code response yazıldıktan sonra okunur; exception handler chain'in
///         set ettiği 4xx/5xx kodları doğru yansır.</item>
///   <item>Builder'ın <c>StatusCode</c> değeri response'tan alınır, endpoint
///         handler'ın <c>WithStatusCode</c> çağırma yükümlülüğü kalmaz.</item>
/// </list>
/// </summary>
public sealed class ActivityLogMiddleware(
    IActivityLogger activityLogger,
    ILogger<ActivityLogMiddleware> logger) : IMiddleware
{
    public const string BuilderItemKey = "__saydin-activity-log";

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Items.TryGetValue(ApiPortBoundary.RequestKindItemKey, out var kind)
            && kind is not ApiPortRequestKind.PublicProduct)
        {
            await next(context);
            return;
        }

        var action = ResolveAction(context);
        if (action is not null)
            context.GetOrCreateActivityLog(action);

        try
        {
            await next(context);
        }
        finally
        {
            // Activity log Send hatasının request pipeline'ını maskelemesini engelle:
            // builder.Build veya Send fırlatırsa orijinal exception (varsa) korunmalı.
            if (context.Items.TryGetValue(BuilderItemKey, out var raw)
                && raw is ActivityLogBuilder builder)
            {
                try
                {
                    // Credential endpoint filters run after this middleware creates the
                    // builder. Bind the server-resolved principal as late as possible so
                    // authentication/rate-limit/handler failures retain attribution.
                    var principal = context.RequestServices
                        .GetService<IInstallationPrincipalContext>();
                    if (principal?.IsResolved == true)
                        builder.WithUserId(principal.PrincipalId);
                    builder.WithResponseStatus((short)context.Response.StatusCode);
                    builder.Send(activityLogger);
                }
                catch (Exception logEx)
                {
                    logger.LogError(logEx,
                        "Activity log gönderimi başarısız (status={StatusCode})",
                        context.Response.StatusCode);
                }
            }
        }
    }

    internal static string? ResolveAction(HttpContext context)
    {
        var endpointName = context.GetEndpoint()?.Metadata
            .GetMetadata<IEndpointNameMetadata>()?.EndpointName;
        return endpointName switch
        {
            "CalculateWhatIf" => ActivityActions.WhatIfCalculate,
            "CompareWhatIf" => ActivityActions.WhatIfCompare,
            "ReverseCalculateWhatIf" => ActivityActions.WhatIfReverse,
            "CalculateDca" => ActivityActions.WhatIfDca,
            "GetAssets" => ActivityActions.AssetsList,
            "GetAssetPrice" => ActivityActions.AssetPrice,
            "GetAssetPriceRange" => ActivityActions.AssetPriceRange,
            "GetScenarios" or "GetScenarioPage" => ActivityActions.ScenarioList,
            "SaveScenario" => ActivityActions.ScenarioSave,
            "DeleteScenario" => ActivityActions.ScenarioDelete,
            "GetAppConfig" => ActivityActions.ConfigFetch,
            "RegisterInstallation" => ActivityActions.InstallationRegister,
            "BeginInstallationRotation" => ActivityActions.InstallationRotationBegin,
            "CommitInstallationRotation" => ActivityActions.InstallationRotationCommit,
            "RevokeInstallation" => ActivityActions.InstallationRevoke,
            _ => null,
        };
    }
}

public static class ActivityLogContextExtensions
{
    /// <summary>
    /// Bu request'e bağlı (henüz var değilse yaratılan) <see cref="ActivityLogBuilder"/>'ı döner.
    /// Endpoint handler bir kere alır, üzerinde WithAction/WithData/WithUserId çağırır;
    /// pipeline sonunda <see cref="ActivityLogMiddleware"/> otomatik Send eder.
    /// </summary>
    public static ActivityLogBuilder GetOrCreateActivityLog(this HttpContext context, string action)
    {
        if (context.Items.TryGetValue(ActivityLogMiddleware.BuilderItemKey, out var raw)
            && raw is ActivityLogBuilder existing)
        {
            var resolved = context.RequestServices.GetService<IInstallationPrincipalContext>();
            if (resolved?.IsResolved == true)
                existing.WithUserId(resolved.PrincipalId);
            return existing.WithAction(action);
        }

        var builder = new ActivityLogBuilder(
            context,
            context.RequestServices.GetService<IGeoIpResolver>(),
            context.RequestServices.GetService<TimeProvider>())
            .WithAction(action);

        var principal = context.RequestServices.GetService<IInstallationPrincipalContext>();
        if (principal?.IsResolved == true)
            builder.WithUserId(principal.PrincipalId);

        context.Items[ActivityLogMiddleware.BuilderItemKey] = builder;
        return builder;
    }
}
