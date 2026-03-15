using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;

namespace Saydin.Api.Services;

public interface IWhatIfCalculator
{
    Task<WhatIfResponse> CalculateAsync(WhatIfRequest request, CancellationToken ct);
}
