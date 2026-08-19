using FluentAssertions;

namespace Saydin.DataQualityAudit.Tests;

public sealed class AuditContractTests
{
    [Fact]
    public void LedgerContinuity_OverlapCannotRewindCursorOrHideTrailingGap()
    {
        var lane = Lane(new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 10), "day");
        var windows = new[]
        {
            Window(new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 5)),
            Window(new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 2)),
        };

        var violations = LedgerContinuity.Analyze(lane, windows);

        violations.Select(item => item.ViolationCode)
            .Should().Equal("overlapping_windows", "trailing_gap");
        violations.Single(item => item.ViolationCode == "trailing_gap").BusinessKey
            .Should().EndWith("|2024-01-06|2024-01-10");
    }

    [Theory]
    [InlineData("day")]
    [InlineData("month")]
    public void LedgerContinuity_ContainingWindowDoesNotCreateFalseOverlap(string cadence)
    {
        var through = cadence == "month" ? new DateOnly(2024, 3, 1) : new DateOnly(2024, 2, 20);
        var lane = Lane(new DateOnly(2024, 2, 1), through, cadence);
        var window = Window(new DateOnly(2024, 1, 1), new DateOnly(2024, 4, 1));

        LedgerContinuity.Analyze(lane, [window]).Should().BeEmpty();
    }

    [Fact]
    public void RequiredTableSet_IsExactCurrentTwentyThreeAppAndControlTables()
    {
        AuditRunner.RequiredTableNames.Should().BeEquivalentTo(new[]
        {
            "saydin_migration_control", "schema_migrations", "assets", "price_points",
            "inflation_rates", "ingestion_windows", "ingestion_jobs", "users",
            "saved_scenarios", "market_holidays", "activity_logs", "market_calendars",
            "market_calendar_releases", "market_calendar_release_sources",
            "market_calendar_days", "market_calendar_active_releases",
            "asset_market_calendars",
            "saydin_role_contract",
            "provider_fetch_payloads",
            "price_observation_attributions",
            "inflation_observation_attributions",
            "installation_credentials",
            "asset_catalog_state",
        });
        AuditRunner.RequiredTableNames.Should().OnlyHaveUniqueItems().And.HaveCount(23);
    }

    private static AuditLane Lane(DateOnly from, DateOnly through, string cadence) =>
        new("coingecko", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "historical_backfill", 1, from, through, cadence);

    private static DatabaseWindow Window(DateOnly from, DateOnly through) =>
        new(Guid.NewGuid(), "coingecko", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "historical_backfill", from, through, 1, "pending", 0, 0, 0, 0, 0, null);
}
