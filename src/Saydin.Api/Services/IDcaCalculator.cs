using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;

namespace Saydin.Api.Services;

public interface IDcaCalculator
{
    Task<DcaResponse> CalculateAsync(string deviceId, DcaRequest request, CancellationToken ct);
}
