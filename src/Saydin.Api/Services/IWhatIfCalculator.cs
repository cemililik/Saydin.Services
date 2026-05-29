using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;

namespace Saydin.Api.Services;

public interface IWhatIfCalculator
{
    // F2.2-3: deviceId artık IDeviceContext üzerinden (scoped) okunur — arayüz yalnız domain parametreleri taşır.
    Task<WhatIfResponse>        CalculateAsync       (WhatIfRequest        request, CancellationToken ct);
    Task<CompareResponse>       CompareAsync         (CompareRequest       request, CancellationToken ct);
    Task<ReverseWhatIfResponse> CalculateReverseAsync(ReverseWhatIfRequest request, CancellationToken ct);
}
