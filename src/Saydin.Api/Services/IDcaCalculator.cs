using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;

namespace Saydin.Api.Services;

public interface IDcaCalculator
{
    // F2.2-3: deviceId artık IDeviceContext üzerinden (scoped) okunur.
    Task<DcaResponse> CalculateAsync(DcaRequest request, CancellationToken ct);
}
