using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;

namespace Saydin.Api.Services;

public interface IWhatIfCalculator
{
    // Authentication identity stays out of the domain method surface.
    Task<WhatIfResponse>        CalculateAsync       (WhatIfRequest        request, CancellationToken ct);
    Task<CompareResponse>       CompareAsync         (CompareRequest       request, CancellationToken ct);
    Task<ReverseWhatIfResponse> CalculateReverseAsync(ReverseWhatIfRequest request, CancellationToken ct);
}
