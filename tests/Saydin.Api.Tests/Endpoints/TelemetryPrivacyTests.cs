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
    public void FinancialResultActivityData_ExactReturn_IsReducedToCoarseOutcome()
    {
        const decimal resultSentinel = 73.492817m;

        var activity = new ActivityLogBuilder(new DefaultHttpContext())
            .WithAction("what_if_calculate")
            .WithData(new { outcome = TelemetryOutcome.From(resultSentinel) })
            .Build();

        activity.Data.Should().NotBeNull();
        var data = activity.Data!.Value;
        data.GetProperty("outcome").GetString().Should().Be("profit");
        data.GetRawText().Should().NotContain("73.492817");
    }

    [Fact]
    public void ScenarioSaveActivityData_FreeTextLabel_IsReplacedByPresenceFlag()
    {
        const string labelSentinel = "PRIVATE-LABEL-7f0d8c2e";
        var scenarioId = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var request = new SaveScenarioRequest(
            AssetSymbol: "USDTRY",
            AssetDisplayName: "Dolar/TL",
            BuyDate: new DateOnly(2024, 1, 1),
            SellDate: new DateOnly(2025, 1, 1),
            Amount: 10_000m,
            AmountType: "try",
            Label: labelSentinel);
        var scenario = new ScenarioResponse(
            Id: scenarioId,
            AssetSymbol: request.AssetSymbol,
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
        data.GetRawText().Should().NotContain(labelSentinel);
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
}
