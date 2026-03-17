namespace Saydin.Api.Models.Responses;

public record CompareResponse(IReadOnlyList<CompareResultItem> Results);

public record CompareResultItem(
    int     Rank,
    WhatIfResponse Calculation
);
