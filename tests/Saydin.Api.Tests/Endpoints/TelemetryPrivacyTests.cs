using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Saydin.Api.Endpoints;
using Saydin.Api.Helpers;
using Saydin.Api.Models.Requests;
using Saydin.Api.Models.Responses;
using Saydin.Shared.Constants;

namespace Saydin.Api.Tests.Endpoints;

public class TelemetryPrivacyTests
{
    [Fact]
    public void WhatIfActivityFactories_RealRequestsAndResults_RedactEveryFinancialSentinel()
    {
        const decimal requestSentinel = 8_765_432m;
        const decimal resultSentinel = 7_654_321m;
        var date = new DateOnly(2025, 1, 1);
        var calculation = FinancialWhatIfResponse(date, resultSentinel);
        var calculateRequest = new WhatIfRequest(
            "USDTRY", date, date, requestSentinel, "try");
        var compareRequest = new CompareRequest(
            ["USDTRY", "EURTRY"], date, date, requestSentinel, "try");
        var reverseRequest = new ReverseWhatIfRequest(
            "USDTRY", date, date, requestSentinel, "try");
        var reverseResult = new ReverseWhatIfResponse(
            "USDTRY", "Dollar/TRY", date, date,
            1m, 1m, resultSentinel, 1m, resultSentinel,
            resultSentinel, 1m, true, [], null, null, null, null, null);

        var payloads = new[]
        {
            WhatIfEndpoints.CreateCalculateActivityData(calculateRequest, calculation),
            WhatIfEndpoints.CreateCompareActivityData(
                compareRequest,
                new CompareResponse([new CompareResultItem(1, calculation)])),
            WhatIfEndpoints.CreateReverseActivityData(reverseRequest, reverseResult),
        };

        foreach (var payload in payloads)
        {
            var activity = new ActivityLogBuilder(new DefaultHttpContext())
                .WithAction("what_if_calculate")
                .WithData(payload)
                .Build();
            var raw = activity.Data!.Value.GetRawText();
            var data = activity.Data.Value;
            var bucket = data.TryGetProperty("amountBucket", out var amountBucket)
                ? amountBucket
                : data.GetProperty("targetAmountBucket");
            bucket.GetString().Should().Be("1M+");
            raw.Should().NotContain("8765432");
            raw.Should().NotContain("7654321");
        }

        var calculateData = SerializeActivityData(payloads[0]);
        calculateData.GetProperty("result").GetProperty("outcome")
            .GetString().Should().Be("profit");
        var compareData = SerializeActivityData(payloads[1]);
        compareData.GetProperty("result").GetProperty("rankings")[0]
            .GetProperty("outcome").GetString().Should().Be("profit");
        var reverseData = SerializeActivityData(payloads[2]);
        reverseData.GetProperty("result").GetProperty("outcome")
            .GetString().Should().Be("profit");
    }

    [Fact]
    public void DcaActivityFactory_RealRequestAndResult_RedactsEveryFinancialSentinel()
    {
        const decimal requestSentinel = 8_765_432m;
        const decimal resultSentinel = 7_654_321m;
        var date = new DateOnly(2025, 1, 1);
        var request = new DcaRequest(
            "USDTRY", date, date, requestSentinel, "monthly", "try");
        var result = new DcaResponse(
            "USDTRY", "Dollar/TRY", date, date, "monthly", requestSentinel,
            1, resultSentinel, resultSentinel, resultSentinel, 1m, true,
            1m, 1m, 1m, null, null, null, [], []);

        var activity = new ActivityLogBuilder(new DefaultHttpContext())
            .WithAction("what_if_dca")
            .WithData(DcaEndpoints.CreateCalculationActivityData(request, result))
            .Build();

        var raw = activity.Data!.Value.GetRawText();
        activity.Data.Value.GetProperty("amountBucket").GetString().Should().Be("1M+");
        activity.Data.Value.GetProperty("result").GetProperty("outcome")
            .GetString().Should().Be("profit");
        raw.Should().NotContain("8765432");
        raw.Should().NotContain("7654321");
    }

    [Fact]
    public void ScenarioSaveActivityData_FreeTextLabel_IsReplacedByPresenceFlag()
    {
        const string labelSentinel = "PRIVATE-LABEL-7f0d8c2e";
        var scenarioId = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var request = new SaveScenarioRequest(
            AssetSymbol: " raw-private-symbol ",
            AssetDisplayName: "Dolar/TL",
            BuyDate: new DateOnly(2024, 1, 1),
            SellDate: new DateOnly(2025, 1, 1),
            Amount: 10_000m,
            AmountType: "try",
            Label: labelSentinel);
        var scenario = new ScenarioResponse(
            Id: scenarioId,
            AssetSymbol: "USDTRY",
            AssetDisplayName: request.AssetDisplayName,
            BuyDate: request.BuyDate,
            SellDate: request.SellDate,
            Amount: request.Amount,
            AmountType: request.AmountType,
            Label: request.Label,
            CreatedAt: DateTimeOffset.Parse("2026-08-18T00:00:00Z"));

        var activity = new ActivityLogBuilder(new DefaultHttpContext())
            .WithAction("scenario_save")
            .WithData(ScenariosEndpoints.CreateSaveActivityData(request, scenario))
            .Build();

        activity.Data.Should().NotBeNull();
        var data = activity.Data!.Value;
        data.TryGetProperty("label", out _).Should().BeFalse();
        data.GetProperty("hasLabel").GetBoolean().Should().BeTrue();
        data.GetProperty("assetSymbol").GetString().Should().Be("USDTRY");
        data.GetRawText().Should().NotContain(labelSentinel);
        data.GetRawText().Should().NotContain("raw-private-symbol");
    }

    [Fact]
    public void ScenarioPageActivity_UsesExistingAllowlistedActionAndPaginationMarker()
    {
        var activity = new ActivityLogBuilder(new DefaultHttpContext())
            .WithAction(ActivityActions.ScenarioList)
            .WithData(ScenariosEndpoints.CreateListActivityData(
                scenarioCount: 20,
                paginated: true,
                hasNextPage: true))
            .Build();

        activity.Action.Should().Be(ActivityActions.ScenarioList);
        ActivityActions.Lookup.Should().Contain(activity.Action);
        activity.Data!.Value.GetProperty("paginated").GetBoolean().Should().BeTrue();
        activity.Data!.Value.GetProperty("hasNextPage").GetBoolean().Should().BeTrue();
    }

    private static WhatIfResponse FinancialWhatIfResponse(DateOnly date, decimal resultSentinel) =>
        new(
            "USDTRY", "Dollar/TRY", date, date,
            1m, 1m, 1m, 1m, resultSentinel, resultSentinel,
            1m, true, [], null, resultSentinel, null, null, null);

    private static System.Text.Json.JsonElement SerializeActivityData(object payload) =>
        new ActivityLogBuilder(new DefaultHttpContext())
            .WithAction("what_if_calculate")
            .WithData(payload)
            .Build()
            .Data!.Value;
}
