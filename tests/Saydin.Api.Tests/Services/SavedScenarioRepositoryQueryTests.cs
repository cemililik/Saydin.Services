using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Saydin.Api.Models;
using Saydin.Api.Repositories;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;

namespace Saydin.Api.Tests.Services;

public sealed class SavedScenarioRepositoryQueryTests
{
    [Fact]
    public void BuildPageQuery_SameTimestamp_UsesDescendingIdTieBreakerAndUserBoundary()
    {
        var userId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var otherUserId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
        var createdAt = DateTimeOffset.Parse("2026-08-18T12:00:00Z");
        var id1 = Guid.Parse("0198beef-0000-7000-8000-000000000001");
        var id2 = Guid.Parse("0198beef-0000-7000-8000-000000000002");
        var id3 = Guid.Parse("0198beef-0000-7000-8000-000000000003");
        var rows = new[]
        {
            CreateScenario(userId, id2, createdAt),
            CreateScenario(otherUserId, id1, createdAt.AddDays(-10)),
            CreateScenario(userId, id1, createdAt),
            CreateScenario(userId, id3, createdAt),
            CreateScenario(userId, Guid.NewGuid(), createdAt.AddMinutes(-1)),
        };

        var firstPage = SavedScenarioRepository.BuildPageQuery(
            rows.AsQueryable(), userId, cursor: null, take: 2).ToList();
        var secondPage = SavedScenarioRepository.BuildPageQuery(
            rows.AsQueryable(), userId, new ScenarioCursor(createdAt, id2), take: 10).ToList();

        firstPage.Select(s => s.Id).Should().Equal(id3, id2);
        secondPage.Should().HaveCount(2);
        secondPage[0].Id.Should().Be(id1, "same timestamp must continue below cursor id");
        secondPage.Should().OnlyContain(s => s.UserId == userId);
        firstPage.Select(s => s.Id).Should().NotIntersectWith(secondPage.Select(s => s.Id));
    }

    [Fact]
    public void BuildPageQuery_NpgsqlTranslation_ContainsTenantKeysetAndStableOrder()
    {
        var options = new DbContextOptionsBuilder<SaydinDbContext>()
            .UseNpgsql("Host=localhost;Database=saydin_query_contract;Username=test;Password=test")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var context = new SaydinDbContext(options);
        var boundary = new ScenarioCursor(
            DateTimeOffset.Parse("2026-08-18T12:00:00Z"),
            Guid.Parse("0198beef-0000-7000-8000-000000000002"));

        string? sql = null;
        var act = () => sql = SavedScenarioRepository.BuildPageQuery(
                context.SavedScenarios.AsNoTracking(), Guid.NewGuid(), boundary, take: 21)
            .ToQueryString();

        act.Should().NotThrow("Guid.CompareTo and DateTimeOffset keyset predicates must translate to PostgreSQL SQL");
        sql.Should().NotBeNull();
        sql.Should().Contain("user_id");
        sql.Should().Contain("created_at");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("DESC");
        sql.Should().Contain("LIMIT");
    }

    [Fact]
    public void EfModel_ScenarioIntegrityChecksAndKeysetIndex_MatchMigration018()
    {
        var options = new DbContextOptionsBuilder<SaydinDbContext>()
            .UseNpgsql("Host=localhost;Database=saydin_model_contract;Username=test;Password=test")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var context = new SaydinDbContext(options);
        var entity = context.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(SavedScenario))!;

        entity.GetCheckConstraints().Select(check => check.Name).Should().Contain(
            "chk_saved_scenarios_extra_data_object",
            "chk_saved_scenarios_extra_data_size",
            "chk_saved_scenarios_type_unit",
            "chk_saved_scenarios_dates",
            "chk_saved_scenarios_type",
            "chk_saved_scenarios_unit");

        var keyset = entity.GetIndexes().Single(
            index => index.GetDatabaseName() == "idx_saved_scenarios_user_created_id_desc");
        keyset.Properties.Select(property => property.Name)
            .Should().Equal(nameof(SavedScenario.UserId), nameof(SavedScenario.CreatedAt), nameof(SavedScenario.Id));
        keyset.IsDescending.Should().Equal(false, true, true);
        entity.GetIndexes().Should().NotContain(
            index => index.GetDatabaseName() == "idx_saved_scenarios_user",
            "migration 018 drops the subsumed two-column index");
    }

    private static SavedScenario CreateScenario(
        Guid userId,
        Guid id,
        DateTimeOffset createdAt) => new()
    {
        Id = id,
        UserId = userId,
        AssetSymbol = "BTC",
        AssetDisplayName = "Bitcoin",
        Type = "what_if",
        BuyDate = new DateOnly(2020, 1, 1),
        Quantity = 100m,
        QuantityUnit = "try",
        CreatedAt = createdAt,
    };
}
