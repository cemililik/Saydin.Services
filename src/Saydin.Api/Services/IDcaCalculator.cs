using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;

namespace Saydin.Api.Services;

public interface IDcaCalculator
{
    // Quota identity is derived from the authenticated installation principal.
    Task<DcaResponse> CalculateAsync(DcaRequest request, CancellationToken ct);
}
