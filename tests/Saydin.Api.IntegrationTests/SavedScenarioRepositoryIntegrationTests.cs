using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Npgsql;
using Saydin.Api.IntegrationTests.Fixtures;
using Saydin.Api.Models;
using Saydin.Api.Repositories;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class SavedScenarioRepositoryIntegrationTests(DatabaseFixture db)
{
    private static readonly IStringLocalizer<ErrorMessages> Localizer = CreateLocalizer();

    [SkippableFact]
    public async Task ExtraData_Canonical8192Accepted_8193AndNonObjectRejected()
    {
        RequireScenarioIntegrity();
        var userId = await CreateUserAsync();
        var exactId = Guid.CreateVersion7();
        try
        {
            await ExecuteAsync("""
                INSERT INTO saved_scenarios(
                    id,user_id,asset_id,buy_date,sell_date,quantity,quantity_unit,
                    label,created_at,asset_symbol,asset_display_name,type,extra_data)
                SELECT $1,$2,NULL,'2020-01-01',NULL,100,'try',NULL,NOW(),
                       'PORTFOLIO','PORTFOLIO','portfolio',
                       jsonb_build_object(
                           'v', repeat('a', 8192-octet_length(jsonb_build_object('v','')::text)))
                """, exactId, userId);

            (await ScalarAsync<int>(
                "SELECT octet_length(extra_data::text) FROM saved_scenarios WHERE id=$1",
                exactId)).Should().Be(8192);

            var tooLarge = () => ExecuteAsync("""
                INSERT INTO saved_scenarios(
                    id,user_id,asset_id,buy_date,sell_date,quantity,quantity_unit,
                    label,created_at,asset_symbol,asset_display_name,type,extra_data)
                SELECT $1,$2,NULL,'2020-01-01',NULL,100,'try',NULL,NOW(),
                       'PORTFOLIO','PORTFOLIO','portfolio',
                       jsonb_build_object(
                           'v', repeat('a', 8193-octet_length(jsonb_build_object('v','')::text)))
                """, Guid.CreateVersion7(), userId);
            var tooLargeError = await tooLarge.Should().ThrowAsync<PostgresException>();
            tooLargeError.Which.ConstraintName.Should().Be("chk_saved_scenarios_extra_data_size");

            var nonObject = () => ExecuteAsync("""
                INSERT INTO saved_scenarios(
                    id,user_id,asset_id,buy_date,sell_date,quantity,quantity_unit,
                    label,created_at,asset_symbol,asset_display_name,type,extra_data)
                VALUES ($1,$2,NULL,'2020-01-01',NULL,100,'try',NULL,NOW(),
                        'PORTFOLIO','PORTFOLIO','portfolio','[]'::jsonb)
                """, Guid.CreateVersion7(), userId);
            var nonObjectError = await nonObject.Should().ThrowAsync<PostgresException>();
            nonObjectError.Which.ConstraintName.Should().Be("chk_saved_scenarios_extra_data_object");
        }
        finally
        {
            await DeleteUserAsync(userId);
        }
    }

    [SkippableFact]
    public async Task Keyset_SameTimestamp_HasNoMissingOrDuplicateAndUsesCoveringIndex()
    {
        RequireScenarioIntegrity();
        var userId = await CreateUserAsync();
        var otherUserId = await CreateUserAsync();
        var createdAt = DateTimeOffset.Parse("2026-08-18T12:00:00Z", CultureInfo.InvariantCulture);
        var expectedIds = Enumerable.Range(1, 37)
            .Select(value => Guid.Parse($"0198beef-0000-7000-8000-{value:D12}"))
            .ToArray();
        var otherId = Guid.Parse("ffffffff-ffff-7fff-bfff-ffffffffffff");
        try
        {
            foreach (var id in expectedIds)
                await InsertPortfolioAsync(userId, id, createdAt);
            await InsertPortfolioAsync(otherUserId, otherId, createdAt);

            var actual = new List<Guid>();
            ScenarioCursor? cursor = null;
            do
            {
                await using var context = db.CreateContext();
                var repository = CreateRepository(context);
                var page = await repository.GetPageByUserIdAsync(userId, cursor, 7, CancellationToken.None);
                actual.AddRange(page.Select(row => row.Id));
                cursor = page.Count == 7
                    ? new ScenarioCursor(page[^1].CreatedAt, page[^1].Id)
                    : null;
                if (page.Count < 7)
                    break;
            } while (cursor is not null);

            var expectedDatabaseOrder = await QueryIdsAsync(
                "SELECT id FROM saved_scenarios WHERE user_id=$1 ORDER BY created_at DESC,id DESC",
                userId);
            actual.Should().Equal(expectedDatabaseOrder);
            actual.Should().HaveCount(expectedIds.Length);
            actual.Should().OnlyHaveUniqueItems();
            actual.Should().NotContain(otherId);

            var plan = await ExplainAsync(userId, createdAt, expectedDatabaseOrder[7]);
            plan.Should().Contain("idx_saved_scenarios_user_created_id_desc");
        }
        finally
        {
            await DeleteUserAsync(userId);
            await DeleteUserAsync(otherUserId);
        }
    }

    [SkippableTheory]
    [InlineData(2)]
    [InlineData(20)]
    public async Task CreateWithinLimit_ConcurrentLastSlot_HasExactlyOneWinner(int contenders)
    {
        RequireScenarioIntegrity();
        var userId = await CreateUserAsync();
        try
        {
            for (var index = 0; index < 4; index++)
                await InsertPortfolioAsync(userId, Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(-index));

            var attempts = Enumerable.Range(0, contenders).Select(async index =>
            {
                await using var context = db.CreateContext();
                var repository = CreateRepository(context);
                try
                {
                    await repository.CreateWithinLimitAsync(
                        NewPortfolio(userId, Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddSeconds(index)),
                        effectiveLimit: 5,
                        CancellationToken.None);
                    return true;
                }
                catch (ScenarioLimitExceededException exception)
                {
                    exception.Limit.Should().Be(5);
                    return false;
                }
            });

            var outcomes = await Task.WhenAll(attempts);

            outcomes.Count(success => success).Should().Be(1);
            outcomes.Count(success => !success).Should().Be(contenders - 1);
            (await ScalarAsync<long>(
                "SELECT count(*) FROM saved_scenarios WHERE user_id=$1", userId)).Should().Be(5);
        }
        finally
        {
            await DeleteUserAsync(userId);
        }
    }

    [SkippableFact]
    public async Task CreateWithinLimit_LockForOneUser_DoesNotBlockDifferentUser()
    {
        RequireScenarioIntegrity();
        var lockedUserId = await CreateUserAsync();
        var otherUserId = await CreateUserAsync();
        try
        {
            await using var blocker = new NpgsqlConnection(db.ConnectionString);
            await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand("""
                SELECT pg_advisory_xact_lock(
                    hashtextextended('saydin.saved_scenarios:' || $1::uuid::text, 0))
                """, blocker, transaction))
            {
                command.Parameters.AddWithValue(lockedUserId);
                await command.ExecuteScalarAsync();
            }

            await using var context = db.CreateContext();
            var save = CreateRepository(context).CreateWithinLimitAsync(
                NewPortfolio(otherUserId, Guid.CreateVersion7(), DateTimeOffset.UtcNow),
                effectiveLimit: 5,
                CancellationToken.None);

            await save.WaitAsync(TimeSpan.FromSeconds(2));
            await transaction.RollbackAsync();
        }
        finally
        {
            await DeleteUserAsync(lockedUserId);
            await DeleteUserAsync(otherUserId);
        }
    }

    [SkippableFact]
    public async Task CreateWithinLimit_CancelledLockWait_RollsBackAndCanRetry()
    {
        RequireScenarioIntegrity();
        var userId = await CreateUserAsync();
        try
        {
            await using var blocker = new NpgsqlConnection(db.ConnectionString);
            await blocker.OpenAsync();
            await using var blockerTransaction = await blocker.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand("""
                SELECT pg_advisory_xact_lock(
                    hashtextextended('saydin.saved_scenarios:' || $1::uuid::text, 0))
                """, blocker, blockerTransaction))
            {
                command.Parameters.AddWithValue(userId);
                await command.ExecuteScalarAsync();
            }

            await using (var cancelledContext = db.CreateContext())
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                var act = () => CreateRepository(cancelledContext).CreateWithinLimitAsync(
                    NewPortfolio(userId, Guid.CreateVersion7(), DateTimeOffset.UtcNow),
                    effectiveLimit: 5,
                    cancellation.Token);
                await act.Should().ThrowAsync<OperationCanceledException>();
            }

            (await ScalarAsync<long>(
                "SELECT count(*) FROM saved_scenarios WHERE user_id=$1", userId)).Should().Be(0);
            await blockerTransaction.RollbackAsync();

            await using var retryContext = db.CreateContext();
            await CreateRepository(retryContext).CreateWithinLimitAsync(
                NewPortfolio(userId, Guid.CreateVersion7(), DateTimeOffset.UtcNow),
                effectiveLimit: 5,
                CancellationToken.None);
        }
        finally
        {
            await DeleteUserAsync(userId);
        }
    }

    [SkippableFact]
    public async Task DirectInsertTrigger_UsesSamePerUserAdvisoryKeyAndReleasesAfterCancellation()
    {
        RequireScenarioIntegrity();
        var userId = await CreateUserAsync();
        try
        {
            await using var blocker = new NpgsqlConnection(db.ConnectionString);
            await blocker.OpenAsync();
            await using var blockerTransaction = await blocker.BeginTransactionAsync();
            await using (var lockCommand = new NpgsqlCommand("""
                SELECT pg_advisory_xact_lock(
                    hashtextextended('saydin.saved_scenarios:' || $1::uuid::text, 0))
                """, blocker, blockerTransaction))
            {
                lockCommand.Parameters.AddWithValue(userId);
                await lockCommand.ExecuteScalarAsync();
            }

            await using (var direct = new NpgsqlConnection(db.ConnectionString))
            {
                await direct.OpenAsync();
                await using var insert = PortfolioInsertCommand(
                    direct, userId, Guid.CreateVersion7(), DateTimeOffset.UtcNow);
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                var blocked = () => insert.ExecuteNonQueryAsync(cancellation.Token);
                await blocked.Should().ThrowAsync<OperationCanceledException>();
            }

            (await ScalarAsync<long>(
                "SELECT count(*) FROM saved_scenarios WHERE user_id=$1", userId)).Should().Be(0);
            await blockerTransaction.RollbackAsync();

            await InsertPortfolioAsync(userId, Guid.CreateVersion7(), DateTimeOffset.UtcNow);
            (await ScalarAsync<long>(
                "SELECT count(*) FROM saved_scenarios WHERE user_id=$1", userId)).Should().Be(1);
        }
        finally
        {
            await DeleteUserAsync(userId);
        }
    }

    [SkippableFact]
    public async Task DirectWriter_OneHundredFirstInsert_IsRejectedByNamedHardCap()
    {
        RequireScenarioIntegrity();
        var userId = await CreateUserAsync();
        try
        {
            await ExecuteAsync("""
                INSERT INTO saved_scenarios(
                    id,user_id,asset_id,buy_date,sell_date,quantity,quantity_unit,
                    label,created_at,asset_symbol,asset_display_name,type,extra_data)
                SELECT gen_random_uuid(),$1,NULL,'2020-01-01',NULL,100,'try',NULL,
                       NOW()-(ordinal || ' seconds')::interval,
                       'PORTFOLIO','PORTFOLIO','portfolio',NULL
                  FROM generate_series(1,100) AS ordinal
                """, userId);

            var act = () => InsertPortfolioAsync(
                userId, Guid.CreateVersion7(), DateTimeOffset.UtcNow);
            var failure = await act.Should().ThrowAsync<PostgresException>();

            failure.Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            failure.Which.ConstraintName.Should().Be("chk_saved_scenarios_hard_cap");
            (await ScalarAsync<long>(
                "SELECT count(*) FROM saved_scenarios WHERE user_id=$1", userId)).Should().Be(100);
        }
        finally
        {
            await DeleteUserAsync(userId);
        }
    }

    [SkippableFact]
    public async Task Repository_MapsOnlyExpectedScenarioConstraints()
    {
        RequireScenarioIntegrity();
        var userId = await CreateUserAsync();
        var duplicateId = Guid.CreateVersion7();
        try
        {
            await using (var invalidContext = db.CreateContext())
            {
                var invalid = NewPortfolio(
                    userId,
                    Guid.CreateVersion7(),
                    DateTimeOffset.UtcNow,
                    JsonSerializer.Deserialize<JsonElement>("[]"));
                var expected = () => CreateRepository(invalidContext).CreateWithinLimitAsync(
                    invalid, effectiveLimit: 5, CancellationToken.None);
                await expected.Should().ThrowAsync<ValidationException>()
                    .Where(exception => exception.Field == "ExtraData");
            }

            await InsertPortfolioAsync(userId, duplicateId, DateTimeOffset.UtcNow);
            await using var duplicateContext = db.CreateContext();
            var unexpected = () => CreateRepository(duplicateContext).CreateWithinLimitAsync(
                NewPortfolio(userId, duplicateId, DateTimeOffset.UtcNow),
                effectiveLimit: 5,
                CancellationToken.None);

            await unexpected.Should().ThrowAsync<DbUpdateException>();
        }
        finally
        {
            await DeleteUserAsync(userId);
        }
    }

    [SkippableTheory]
    [InlineData("dca", "grams", "2020-01-02", "chk_saved_scenarios_type_unit")]
    [InlineData("what_if", "try", "2020-01-01", "chk_saved_scenarios_dates")]
    public async Task DirectWriter_InvalidTypeUnitOrDate_IsRejectedByNamedConstraint(
        string type,
        string quantityUnit,
        string sellDate,
        string expectedConstraint)
    {
        RequireScenarioIntegrity();
        var userId = await CreateUserAsync();
        try
        {
            var act = () => ExecuteAsync("""
                INSERT INTO saved_scenarios(
                    id,user_id,asset_id,buy_date,sell_date,quantity,quantity_unit,
                    label,created_at,asset_symbol,asset_display_name,type,extra_data)
                VALUES ($1,$2,NULL,'2020-01-01',$3::date,100,$4,NULL,NOW(),
                        'TEST','TEST',$5,NULL)
                """, Guid.CreateVersion7(), userId, sellDate, quantityUnit, type);

            var failure = await act.Should().ThrowAsync<PostgresException>();
            failure.Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            failure.Which.ConstraintName.Should().Be(expectedConstraint);
            (await ScalarAsync<long>(
                "SELECT count(*) FROM saved_scenarios WHERE user_id=$1", userId)).Should().Be(0);
        }
        finally
        {
            await DeleteUserAsync(userId);
        }
    }

    private void RequireScenarioIntegrity()
    {
        Skip.IfNot(db.Available, db.SkipReason);
        Skip.IfNot(db.ScenarioIntegrity, "Migration 018 scenario integrity uygulanmamış.");
    }

    private SavedScenarioRepository CreateRepository(Saydin.Shared.Data.SaydinDbContext context) =>
        new(context, TimeProvider.System, Localizer);

    private async Task<Guid> CreateUserAsync()
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync("""
            INSERT INTO users(id,device_id,tier,created_at,last_seen_at)
            VALUES ($1,$2,'premium',NOW(),NOW())
            """, id, $"scenario-it-{id:N}");
        return id;
    }

    private async Task DeleteUserAsync(Guid userId)
    {
        // API intentionally has no DELETE on users. Fixture cleanup must use its setup-only admin
        // identity without widening the SUT capability or masking the managed-login assertions.
        await using var cleanup = db.CreateAdminContext();
        await cleanup.SavedScenarios.Where(scenario => scenario.UserId == userId).ExecuteDeleteAsync();
        await cleanup.ActivityLogs.Where(log => log.UserId == userId).ExecuteDeleteAsync();
        // SET LOCAL is transaction-scoped: commit restores it, while await-using rollback/dispose
        // restores it on every exceptional path. This is disposable-fixture cleanup, not product proof.
        await using var transaction = await cleanup.Database.BeginTransactionAsync();
        await cleanup.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role='replica'");
        await cleanup.Users.Where(user => user.Id == userId).ExecuteDeleteAsync();
        await transaction.CommitAsync();
    }

    private Task InsertPortfolioAsync(Guid userId, Guid id, DateTimeOffset createdAt) =>
        ExecuteAsync("""
            INSERT INTO saved_scenarios(
                id,user_id,asset_id,buy_date,sell_date,quantity,quantity_unit,
                label,created_at,asset_symbol,asset_display_name,type,extra_data)
            VALUES ($1,$2,NULL,'2020-01-01',NULL,100,'try',NULL,$3,
                    'PORTFOLIO','PORTFOLIO','portfolio',NULL)
            """, id, userId, createdAt);

    private static NpgsqlCommand PortfolioInsertCommand(
        NpgsqlConnection connection,
        Guid userId,
        Guid id,
        DateTimeOffset createdAt)
    {
        var command = new NpgsqlCommand("""
            INSERT INTO saved_scenarios(
                id,user_id,asset_id,buy_date,sell_date,quantity,quantity_unit,
                label,created_at,asset_symbol,asset_display_name,type,extra_data)
            VALUES ($1,$2,NULL,'2020-01-01',NULL,100,'try',NULL,$3,
                    'PORTFOLIO','PORTFOLIO','portfolio',NULL)
            """, connection);
        AddParameters(command, [id, userId, createdAt]);
        return command;
    }

    private static SavedScenario NewPortfolio(
        Guid userId,
        Guid id,
        DateTimeOffset createdAt,
        JsonElement? extraData = null) => new()
    {
        Id = id,
        UserId = userId,
        AssetId = null,
        AssetSymbol = "PORTFOLIO",
        AssetDisplayName = "PORTFOLIO",
        Type = "portfolio",
        BuyDate = new DateOnly(2020, 1, 1),
        SellDate = null,
        Quantity = 100m,
        QuantityUnit = "try",
        ExtraData = extraData,
        CreatedAt = createdAt,
    };

    private async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, parameters);
        return (T)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Expected scalar result."));
    }

    private async Task<IReadOnlyList<Guid>> QueryIdsAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<Guid>();
        while (await reader.ReadAsync())
            result.Add(reader.GetGuid(0));
        return result;
    }

    private async Task<string> ExplainAsync(Guid userId, DateTimeOffset createdAt, Guid id)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using (var disableSeqScan = new NpgsqlCommand("SET enable_seqscan=off", connection))
            await disableSeqScan.ExecuteNonQueryAsync();
        await using (var disableSort = new NpgsqlCommand("SET enable_sort=off", connection))
            await disableSort.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand("""
            EXPLAIN (COSTS OFF)
            SELECT * FROM saved_scenarios
             WHERE user_id=$1
               AND (created_at<$2 OR (created_at=$2 AND id<$3))
             ORDER BY created_at DESC,id DESC
             LIMIT 21
            """, connection);
        AddParameters(command, [userId, createdAt, id]);
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
            lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }

    private static void AddParameters(NpgsqlCommand command, IReadOnlyList<object> parameters)
    {
        for (var index = 0; index < parameters.Count; index++)
            command.Parameters.AddWithValue(parameters[index]);
    }

    private static IStringLocalizer<ErrorMessages> CreateLocalizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        return services.BuildServiceProvider().GetRequiredService<IStringLocalizer<ErrorMessages>>();
    }
}
