using Saydin.Shared.Entities;

namespace Saydin.Api.Services;

public interface IActivityLogger
{
    /// <summary>
    /// Aktivite kaydını Channel'a yazar (fire-and-forget, ana akışı bloklamaz).
    /// </summary>
    void Log(ActivityLog entry);
}
