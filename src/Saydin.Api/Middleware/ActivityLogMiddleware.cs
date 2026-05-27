using Saydin.Api.Helpers;
using Saydin.Api.Services;

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
                    builder.WithStatusCode((short)context.Response.StatusCode);
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
            return existing.WithAction(action);
        }

        var builder = new ActivityLogBuilder(
            context,
            context.RequestServices.GetService<IGeoIpResolver>())
            .WithAction(action);

        context.Items[ActivityLogMiddleware.BuilderItemKey] = builder;
        return builder;
    }
}
