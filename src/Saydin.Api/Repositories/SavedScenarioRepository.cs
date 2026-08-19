using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Npgsql;
using Saydin.Api.Models;
using Saydin.Api.Services;
using Saydin.Shared.Constants;
using Saydin.Shared.Data;
using Saydin.Shared.Entities;
using Saydin.Shared.Exceptions;

namespace Saydin.Api.Repositories;

public sealed class SavedScenarioRepository(
    SaydinDbContext context,
    TimeProvider timeProvider,
    IStringLocalizer<ErrorMessages> localizer) : ISavedScenarioRepository
{
    private const string AdvisoryLockNamespace = "saydin.saved_scenarios:";
    private const string HardCapConstraint = "chk_saved_scenarios_hard_cap";

    public Task<User?> GetUserByIdAsync(Guid principalId, CancellationToken ct)
        => context.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == principalId, ct);

    public async Task UpdateUserLastSeenAsync(User user, CancellationToken ct)
    {
        // F2.2-12 ([C-B-SavedScenario-3]): Throttled UPDATE. Repository katmanı sadece
        // SQL'i atar; "ne sıklıkta" kararı SavedScenarioService tarafında verilir
        // (in-memory throttle map). Buradan global NoTracking nedeniyle tracking-
        // free UPDATE atılır.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE users SET last_seen_at = {timeProvider.GetUtcNow()} WHERE id = {user.Id}
            """,
            ct);
    }

    public async Task<Asset?> GetActiveAssetBySymbolAsync(string symbol, CancellationToken ct)
        => await context.Assets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Symbol == symbol && a.IsActive, ct);

    public async Task<IReadOnlyList<SavedScenario>> GetByUserIdAsync(
        Guid userId,
        int limit,
        CancellationToken ct)
        => await context.SavedScenarios
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<SavedScenario>> GetPageByUserIdAsync(
        Guid userId,
        ScenarioCursor? cursor,
        int take,
        CancellationToken ct)
        => await BuildPageQuery(context.SavedScenarios.AsNoTracking(), userId, cursor, take)
            .ToListAsync(ct);

    /// <summary>
    /// Keyset pagination is kept in one query builder so its tenant predicate,
    /// tie-breaker and SQL translation can be contract-tested together.
    /// </summary>
    internal static IQueryable<SavedScenario> BuildPageQuery(
        IQueryable<SavedScenario> source,
        Guid userId,
        ScenarioCursor? cursor,
        int take)
    {
        var query = source.Where(s => s.UserId == userId);
        if (cursor is { } boundary)
        {
            query = query.Where(s =>
                s.CreatedAt < boundary.CreatedAt
                || (s.CreatedAt == boundary.CreatedAt && s.Id.CompareTo(boundary.Id) < 0));
        }

        return query
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Take(take);
    }

    public async Task<SavedScenario> CreateWithinLimitAsync(
        SavedScenario scenario,
        int effectiveLimit,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(effectiveLimit, 1);

        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        try
        {
            // Migration 018's BEFORE INSERT trigger takes the same transaction-
            // scoped lock. The UUID is formatted by PostgreSQL on both paths so
            // API and direct writers cannot acquire different keys for one user.
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({AdvisoryLockNamespace} || {scenario.UserId}::text, 0))",
                ct);

            var count = await context.SavedScenarios
                .AsNoTracking()
                .CountAsync(saved => saved.UserId == scenario.UserId, ct);
            if (count >= effectiveLimit)
                throw new ScenarioLimitExceededException(effectiveLimit);

            // Tracking sadece insert/update için gerekli; Add() çağrısı NoTracking
            // konfigürasyonu altında bile entity'yi Added state'ine alır.
            context.SavedScenarios.Add(scenario);
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return scenario;
        }
        catch (DbUpdateException exception)
            when (TryMapExpectedConstraint(exception, effectiveLimit, out var mapped))
        {
            context.Entry(scenario).State = EntityState.Detached;
            throw mapped;
        }
    }

    public async Task<SavedScenario?> GetByIdAndUserIdAsync(Guid id, Guid userId, CancellationToken ct)
        => await context.SavedScenarios
            // Delete path'inde caller bu entity'yi Remove() yapıyor → tracking şart.
            .AsTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);

    public async Task DeleteAsync(SavedScenario scenario, CancellationToken ct)
    {
        context.SavedScenarios.Remove(scenario);
        await context.SaveChangesAsync(ct);
    }

    private bool TryMapExpectedConstraint(
        DbUpdateException exception,
        int effectiveLimit,
        out Exception mapped)
    {
        if (exception.InnerException is not PostgresException postgres)
        {
            mapped = null!;
            return false;
        }

        mapped = postgres.ConstraintName switch
        {
            HardCapConstraint => new ScenarioLimitExceededException(
                Math.Min(effectiveLimit, ScenarioLimits.SystemSaveHardLimit)),
            "chk_saved_scenarios_extra_data_object" => new ValidationException(
                localizer["ExtraDataMustBeObject"], field: "ExtraData"),
            "chk_saved_scenarios_extra_data_size" => new ValidationException(
                string.Format(localizer["ExtraDataTooLarge"], ScenarioExtraDataValidator.MaxUtf8Bytes),
                field: "ExtraData"),
            "chk_saved_scenarios_type" => new ValidationException(
                localizer["ScenarioDataConstraintViolation"], field: "Type"),
            "chk_saved_scenarios_unit" or "chk_saved_scenarios_type_unit" => new ValidationException(
                localizer["ScenarioDataConstraintViolation"], field: "AmountType"),
            "chk_saved_scenarios_dates" => new ValidationException(
                localizer["SellDateMustBeAfterBuyDate"], field: "SellDate"),
            _ => null!,
        };
        return mapped is not null;
    }
}
