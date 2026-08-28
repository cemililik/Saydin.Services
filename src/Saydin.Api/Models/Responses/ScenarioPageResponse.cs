namespace Saydin.Api.Models.Responses;

/// <summary>
/// Additive keyset-pagination contract. The legacy <c>GET /v1/scenarios</c>
/// endpoint intentionally continues to return a bare JSON array.
/// </summary>
public sealed record ScenarioPageResponse(
    IReadOnlyList<ScenarioResponse> Items,
    string? NextCursor);
