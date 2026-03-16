using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;

namespace Saydin.Api.Services;

public interface IWhatIfCalculator
{
    Task<WhatIfResponse> CalculateAsync(string deviceId, WhatIfRequest request, CancellationToken ct);
}
