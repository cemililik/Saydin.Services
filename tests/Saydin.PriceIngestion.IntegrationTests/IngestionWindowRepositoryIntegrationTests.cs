using FluentAssertions;
using System.Globalization;
using Npgsql;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;
using Saydin.Shared.Constants;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.IntegrationTests;

[Collection(IngestionDatabaseCollection.Name)]
public sealed class IngestionWindowRepositoryIntegrationTests(IngestionDatabaseFixture database)
{
    [Theory]
    [InlineData("2026-08-17")]
    [InlineData("2026-08-16")]
    public async Task TcmbTarget_IsLatestAuthoritativeExpectedDayAtOrBeforeCutoff(
        string cutoffText)
    {
        var cutoff = DateOnly.ParseExact(cutoffText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var expected = await database.ScalarAsync<DateOnly>("""
            SELECT max(d.calendar_date)
              FROM market_calendar_active_releases a
              JOIN market_calendar_days d ON d.release_id=a.release_id
             WHERE a.calendar_code='tcmb_indicative_fx'
               AND d.calendar_date <= @cutoff
               AND d.observation_expected
            """, new NpgsqlParameter("cutoff", cutoff));

        var target = await database.Repository().ResolveLatestExpectedObservationAsync(
            "tcmb_indicative_fx", cutoff, default);

        target.Ready.Should().BeTrue();
        target.TargetDate.Should().Be(expected);
        target.TargetDate.Should().BeOnOrBefore(cutoff);
        target.ReleaseId.Should().NotBeNull();
    }

    [Fact]
    public async Task CalendarTarget_UnsupportedCodeFailsClosed()
    {
        var target = await database.Repository().ResolveLatestExpectedObservationAsync(
            "untrusted_calendar", new DateOnly(2026, 8, 17), default);

        target.Ready.Should().BeFalse();
        target.TargetDate.Should().BeNull();
        target.OutcomeCode.Should().Be("calendar_code_unsupported");
    }

    [Theory]
    [InlineData("tcmb", "tcmb_indicative_fx", "2026-08-17")]
    [InlineData("twelvedata", "bist_pay_xist", "2026-03-19")]
    public async Task NewAuthoritativeAsset_AutoBindsAndIsCalendarReady(
        string source, string expectedCalendar, string coveredDate)
    {
        var assetId = await database.CreateAssetAsync(source);
        try
        {
            (await database.ScalarAsync<string>("""
                SELECT source || ':' || calendar_code
                  FROM asset_market_calendars WHERE asset_id=@id
                """, new NpgsqlParameter("id", assetId)))
                .Should().Be($"{source}:{expectedCalendar}");

            var date = DateOnly.ParseExact(coveredDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var readiness = await database.Repository().CheckCalendarReadinessAsync(
                new IngestionWindowScope(
                    source, assetId, IngestionJobTypes.HistoricalBackfill, 2),
                date, date, default);

            readiness.Ready.Should().BeTrue();
            readiness.CalendarCode.Should().Be(expectedCalendar);
            readiness.ReleaseId.Should().NotBeNull();
        }
        finally
        {
            await database.CleanupAssetAsync(assetId);
        }
    }

    [Fact]
    public async Task TwoReplicas_OverlapWhileFirstClaimTransactionIsOpen_AndLaterGapIsNotSkipped()
    {
        var assetId = await database.CreateAssetAsync("it-two-replica");
        try
        {
            var scope = Scope("it-two-replica", assetId);
            var fault = new BlockingClaimFault();
            var repositoryA = database.Repository(fault);
            var repositoryB = database.Repository();
            await repositoryA.EnsureWindowsAsync(scope,
                [Range(1), Range(2)], default);

            var first = repositoryA.ClaimNextAsync(
                scope, "replica-a", TimeSpan.FromMinutes(1), default);
            await fault.Entered.WaitAsync(TimeSpan.FromSeconds(5));

            try
            {
                var overlapping = await repositoryB.ClaimNextAsync(
                        scope, "replica-b", TimeSpan.FromMinutes(1), default)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                overlapping.Status.Should().Be(WindowClaimStatus.Busy,
                    "the first transaction still owns the scope advisory lock");
            }
            finally
            {
                // Never strand the first transaction when an assertion or timeout fails.
                fault.Release();
            }

            var claimed = await first.WaitAsync(TimeSpan.FromSeconds(5));
            claimed.Status.Should().Be(WindowClaimStatus.Claimed);
            claimed.Claim!.From.Should().Be(new DateOnly(2040, 1, 1));
        }
        finally { await database.CleanupAssetAsync(assetId); }
    }

    [Fact]
    public async Task ExpiredLease_IsReclaimed_AndStaleOwnerCannotWriteOrFinalize()
    {
        var assetId = await database.CreateAssetAsync("it-fencing");
        try
        {
            var repository = database.Repository();
            var scope = Scope("it-fencing", assetId);
            await repository.EnsureWindowsAsync(scope, [Range(1)], default);
            var stale = (await repository.ClaimNextAsync(
                scope, "owner-a", TimeSpan.FromMilliseconds(50), default)).Claim!;
            await Task.Delay(100);
            var current = (await repository.ClaimNextAsync(
                scope, "owner-b", TimeSpan.FromMinutes(1), default)).Claim!;
            current.AttemptCount.Should().Be(2);

            var staleOutcome = PriceOutcome(assetId, stale.From);
            var act = () => repository.CompletePriceAsync(stale, staleOutcome, Counts(1), default);
            await act.Should().ThrowAsync<IngestionLeaseLostException>();
            (await database.ScalarAsync<long>(
                "SELECT count(*) FROM price_points WHERE asset_id=@id",
                new NpgsqlParameter("id", assetId))).Should().Be(0);
            (await database.ScalarAsync<long>(
                "SELECT count(*) FROM ingestion_jobs WHERE window_id=@id AND outcome_code='lease_expired'",
                new NpgsqlParameter("id", stale.WindowId))).Should().Be(1);
        }
        finally { await database.CleanupAssetAsync(assetId); }
    }

    [Fact]
    public async Task BeforeCommitFault_RollsBackDataWindowAndJobTogether()
    {
        var assetId = await database.CreateAssetAsync(ProviderSources.CoinGecko);
        try
        {
            var fault = new ThrowingFault(beforeCommit: true);
            var repository = database.Repository(fault);
            var scope = Scope(ProviderSources.CoinGecko, assetId);
            await repository.EnsureWindowsAsync(scope, [Range(1)], default);
            var claim = (await repository.ClaimNextAsync(
                scope, "owner", TimeSpan.FromMinutes(1), default)).Claim!;

            var act = () => repository.CompletePriceAsync(
                claim, PriceOutcome(assetId, claim.From), Counts(1), default);
            await act.Should().ThrowAsync<InjectedFaultException>();

            (await database.ScalarAsync<long>("SELECT count(*) FROM price_points WHERE asset_id=@id",
                new NpgsqlParameter("id", assetId))).Should().Be(0);
            (await database.ScalarAsync<string>("SELECT state FROM ingestion_windows WHERE id=@id",
                new NpgsqlParameter("id", claim.WindowId))).Should().Be(IngestionWindowStates.Running);
            (await database.ScalarAsync<string>("SELECT status FROM ingestion_jobs WHERE id=@id",
                new NpgsqlParameter("id", claim.JobId))).Should().Be(IngestionJobStatuses.Running);
        }
        finally { await database.CleanupAssetAsync(assetId); }
    }

    [Fact]
    public async Task CommitAckLoss_RerunConvergesWithoutDuplicate()
    {
        var assetId = await database.CreateAssetAsync(ProviderSources.CoinGecko);
        try
        {
            var repository = database.Repository(new ThrowingFault(afterCommit: true));
            var scope = Scope(ProviderSources.CoinGecko, assetId);
            await repository.EnsureWindowsAsync(scope, [Range(1)], default);
            var claim = (await repository.ClaimNextAsync(
                scope, "owner", TimeSpan.FromMinutes(1), default)).Claim!;

            var act = () => repository.CompletePriceAsync(
                claim, PriceOutcome(assetId, claim.From), Counts(1), default);
            await act.Should().ThrowAsync<InjectedFaultException>();

            (await repository.GetTerminalStateAsync(claim.WindowId, default))!.State
                .Should().Be(IngestionWindowStates.Succeeded);
            (await database.ScalarAsync<long>("SELECT count(*) FROM price_points WHERE asset_id=@id",
                new NpgsqlParameter("id", assetId))).Should().Be(1);
            (await database.Repository().ClaimNextAsync(
                scope, "restart", TimeSpan.FromMinutes(1), default)).Status
                .Should().Be(WindowClaimStatus.Complete);
        }
        finally { await database.CleanupAssetAsync(assetId); }
    }

    [Fact]
    public async Task InteriorLedgerGap_IsReplanned_EvenWhenLaterDataAndWindowExist()
    {
        var assetId = await database.CreateAssetAsync("it-interior-gap");
        try
        {
            var repository = database.Repository();
            var scope = Scope("it-interior-gap", assetId);
            await repository.PlanWindowsAsync(scope, new(2040, 1, 1), new(2040, 1, 3),
                1, IngestionCadence.Daily, default);
            await database.ExecuteAsync("""
                DELETE FROM ingestion_windows
                 WHERE source='it-interior-gap' AND asset_id=@id AND range_start='2040-01-02'
                """, new NpgsqlParameter("id", assetId));
            await database.SeedPricePointForLedgerPlanningTestAsync(
                assetId, new DateOnly(2040, 1, 3));

            await repository.PlanWindowsAsync(scope, new(2040, 1, 1), new(2040, 1, 3),
                1, IngestionCadence.Daily, default);

            (await database.ScalarAsync<long>("""
                SELECT count(*) FROM ingestion_windows
                 WHERE source='it-interior-gap' AND asset_id=@id AND range_start='2040-01-02'
                """, new NpgsqlParameter("id", assetId))).Should().Be(1);
        }
        finally { await database.CleanupAssetAsync(assetId); }
    }

    [Fact]
    public async Task PermanentFailure_BlocksNewerWindow_UntilExplicitOperatorRequeue()
    {
        var assetId = await database.CreateAssetAsync("it-permanent");
        try
        {
            var repository = database.Repository();
            var scope = Scope("it-permanent", assetId);
            await repository.EnsureWindowsAsync(scope, [Range(1), Range(2)], default);
            var first = (await repository.ClaimNextAsync(
                scope, "owner", TimeSpan.FromMinutes(1), default)).Claim!;
            await repository.RecordFailureAsync(first, IngestionWindowStates.PermanentFailed,
                AdapterOutcomeKind.PermanentFailure, FailureCounts(1),
                "auth_rejected", "auth_rejected", "401", DateTimeOffset.UtcNow, default);

            var blocked = await repository.ClaimNextAsync(
                scope, "other", TimeSpan.FromMinutes(1), default);
            blocked.Status.Should().Be(WindowClaimStatus.PermanentBlocked);
            await repository.RequeuePermanentAsync(first.WindowId, DateTimeOffset.UtcNow, default);
            var reclaimed = await repository.ClaimNextAsync(
                scope, "operator", TimeSpan.FromMinutes(1), default);
            reclaimed.Claim!.WindowId.Should().Be(first.WindowId);
        }
        finally { await database.CleanupAssetAsync(assetId); }
    }

    [Fact]
    public async Task DirectRepo_CannotMarkWeekdayAsExpectedNoData()
    {
        var assetId = await database.CreateAssetAsync("it-calendar");
        try
        {
            var repository = database.Repository();
            var scope = Scope("it-calendar", assetId);
            await repository.EnsureWindowsAsync(scope, [Range(1)], default);
            var claim = (await repository.ClaimNextAsync(
                scope, "owner", TimeSpan.FromMinutes(1), default)).Claim!;
            var outcome = AdapterOutcome<PricePoint>.ExpectedNoData(
                new HashSet<DateOnly> { claim.From }, "fake_closed");
            var counts = new IngestionWindowCounts(1, 0, 0, 0, 0, 1);

            var act = () => repository.CompletePriceAsync(claim, outcome, counts, default);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally { await database.CleanupAssetAsync(assetId); }
    }

    [Fact]
    public async Task RegisteredWeekdayHoliday_CanTerminalizeAsExpectedNoData()
    {
        var assetId = await database.CreateAssetAsync("tcmb");
        try
        {
            var repository = database.Repository();
            var day = new DateOnly(2040, 1, 2);
            var scope = Scope("tcmb", assetId);
            await database.ExecuteAsync("""
                INSERT INTO market_holidays(asset_id,holiday_date,reason)
                VALUES (@id,'2040-01-02','integration-test')
                """, new NpgsqlParameter("id", assetId));
            await repository.EnsureWindowsAsync(scope, [new(day, day)], default);
            var claim = (await repository.ClaimNextAsync(
                scope, "owner", TimeSpan.FromMinutes(1), default)).Claim!;
            var outcome = AdapterOutcome<PricePoint>.ExpectedNoData(
                new HashSet<DateOnly> { day }, "market_closed");
            await repository.CompletePriceAsync(claim, outcome,
                new IngestionWindowCounts(1, 0, 0, 0, 0, 1), default);

            (await database.ScalarAsync<string>(
                "SELECT state FROM ingestion_windows WHERE id=@id",
                new NpgsqlParameter("id", claim.WindowId)))
                .Should().Be(IngestionWindowStates.ExpectedNoData);
        }
        finally { await database.CleanupAssetAsync(assetId); }
    }

    [Fact]
    public async Task ContractV2_AuthoritativeClosedDay_BindsReleaseAndCompletesExpectedNoData()
    {
        var assetId = await database.CreateAssetAsync("tcmb");
        await database.BindCalendarAsync(assetId, "tcmb");
        try
        {
            var repository = database.Repository();
            var day = new DateOnly(2026, 8, 16);
            var scope = new IngestionWindowScope(
                "tcmb", assetId, IngestionJobTypes.HistoricalBackfill, 2);
            var readiness = await repository.CheckCalendarReadinessAsync(scope, day, day, default);
            readiness.Ready.Should().BeTrue();
            await repository.EnsureWindowsAsync(scope, [new(day, day)], default);
            var claim = (await repository.ClaimNextAsync(
                scope, "owner", TimeSpan.FromMinutes(1), default)).Claim!;

            claim.CalendarReleaseId.Should().Be(readiness.ReleaseId);
            var closed = await repository.GetExpectedNoDataDatesAsync(
                claim.CalendarReleaseId!.Value, day, day, default);
            closed.Should().BeEquivalentTo([day]);
            await repository.CompletePriceAsync(claim,
                AdapterOutcome<PricePoint>.ExpectedNoData(closed, "official_no_publication"),
                new IngestionWindowCounts(1, 0, 0, 0, 0, 1), default);
            (await database.ScalarAsync<string>(
                "SELECT state FROM ingestion_windows WHERE id=@id",
                new NpgsqlParameter("id", claim.WindowId)))
                .Should().Be(IngestionWindowStates.ExpectedNoData);
        }
        finally { await database.CleanupAssetAsync(assetId); }
    }

    [Fact]
    public async Task ContractV2_StaleCoverage_FailsBeforeWindowMutation()
    {
        var assetId = await database.CreateAssetAsync("tcmb");
        await database.BindCalendarAsync(assetId, "tcmb");
        try
        {
            var repository = database.Repository();
            var day = new DateOnly(2026, 8, 18);
            var scope = new IngestionWindowScope(
                "tcmb", assetId, IngestionJobTypes.DailyUpdate, 2);

            var act = () => repository.EnsureWindowsAsync(scope, [new(day, day)], default);

            (await act.Should().ThrowAsync<CalendarNotReadyException>())
                .Which.OutcomeCode.Should().Be("calendar_coverage_missing");
            (await database.ScalarAsync<long>("""
                SELECT count(*) FROM ingestion_windows WHERE asset_id=@id
                """, new NpgsqlParameter("id", assetId))).Should().Be(0);
        }
        finally { await database.CleanupAssetAsync(assetId); }
    }

    [Fact]
    public async Task ContractV2_PointerChange_KeepsRetryBoundAndNewWindowUsesNewRelease()
    {
        var assetId = await database.CreateAssetAsync("twelvedata");
        await database.BindCalendarAsync(assetId, "twelvedata");
        var originalActive = await database.ScalarAsync<Guid>("""
            SELECT release_id FROM market_calendar_active_releases
             WHERE calendar_code='bist_pay_xist'
            """);
        var bootstrap = Guid.Parse("ca100000-0000-7000-8000-000000000002");
        var switchedActive = originalActive == bootstrap
            ? Guid.Parse("ca100000-0000-7000-8000-000000000088") : bootstrap;
        try
        {
            if (originalActive == bootstrap)
            {
                await database.ExecuteAsync("""
                    INSERT INTO market_calendar_releases(
                        id,calendar_code,snapshot_set_id,release_version,coverage_from,coverage_through,
                        row_count,normalized_sha256,source_bundle_sha256,released_at)
                    SELECT 'ca100000-0000-7000-8000-000000000088',calendar_code,
                           'integration-pointer',88,coverage_from,coverage_through,row_count,
                           normalized_sha256,source_bundle_sha256,released_at
                      FROM market_calendar_releases
                     WHERE id='ca100000-0000-7000-8000-000000000002';
                    INSERT INTO market_calendar_release_sources
                    SELECT 'ca100000-0000-7000-8000-000000000088',source_id,source_kind,
                           source_role,source_uri,media_type,retrieved_at,raw_sha256,
                           snapshot_path,source_year,source_month
                      FROM market_calendar_release_sources
                     WHERE release_id='ca100000-0000-7000-8000-000000000002';
                    INSERT INTO market_calendar_days
                    SELECT 'ca100000-0000-7000-8000-000000000088',calendar_date,
                           observation_expected,market_state,reason_code,evidence_raw_sha256
                      FROM market_calendar_days
                     WHERE release_id='ca100000-0000-7000-8000-000000000002';
                    SELECT public.seal_market_calendar_release(
                        'ca100000-0000-7000-8000-000000000088');
                    """);
            }

            var repository = database.Repository();
            var partialDay = new DateOnly(2026, 3, 19);
            var historical = new IngestionWindowScope(
                "twelvedata", assetId, IngestionJobTypes.HistoricalBackfill, 2);
            await repository.EnsureWindowsAsync(historical, [new(partialDay, partialDay)], default);
            var first = (await repository.ClaimNextAsync(
                historical, "owner-a", TimeSpan.FromMinutes(1), default)).Claim!;
            first.CalendarReleaseId.Should().Be(originalActive);
            (await repository.GetExpectedNoDataDatesAsync(
                originalActive, partialDay, partialDay, default)).Should().BeEmpty(
                "official BIST partial sessions still require an observation");

            await database.ExecuteAsync("""
                UPDATE market_calendar_active_releases
                   SET release_id=@release,activated_at=clock_timestamp()
                 WHERE calendar_code='bist_pay_xist'
                """, new NpgsqlParameter("release", switchedActive));
            await repository.RecordFailureAsync(first, IngestionWindowStates.RetryableFailed,
                AdapterOutcomeKind.RetryableFailure,
                new IngestionWindowCounts(1, 1, 0, 0, 0, 0),
                "provider_pending", "provider_pending", null, DateTimeOffset.UtcNow, default);
            await database.ExecuteAsync("""
                UPDATE ingestion_windows SET next_attempt_at=clock_timestamp() WHERE id=@id
                """, new NpgsqlParameter("id", first.WindowId));
            var retry = (await repository.ClaimNextAsync(
                historical, "owner-b", TimeSpan.FromMinutes(1), default)).Claim!;
            retry.CalendarReleaseId.Should().Be(originalActive,
                "active pointer changes cannot alter an in-flight window contract");
            await repository.CompletePriceAsync(retry,
                AdapterOutcome<PricePoint>.Data(
                    [AuthorityTestData.TwelveData(assetId, $"it-{assetId:N}", partialDay)], 1),
                Counts(1), default);

            var nextDay = new DateOnly(2026, 3, 20);
            var daily = new IngestionWindowScope(
                "twelvedata", assetId, IngestionJobTypes.DailyUpdate, 2);
            await repository.EnsureWindowsAsync(daily, [new(nextDay, nextDay)], default);
            var next = (await repository.ClaimNextAsync(
                daily, "owner-c", TimeSpan.FromMinutes(1), default)).Claim!;
            next.CalendarReleaseId.Should().Be(switchedActive,
                "new claims use the new sealed active release");
        }
        finally
        {
            await database.ExecuteAsync("""
                UPDATE market_calendar_active_releases
                   SET release_id=@release,activated_at=clock_timestamp()
                 WHERE calendar_code='bist_pay_xist'
                """, new NpgsqlParameter("release", originalActive));
            await database.CleanupAssetAsync(assetId);
            if (originalActive == bootstrap)
                await database.ExecuteAsync("""
                    SET session_replication_role='replica';
                    DELETE FROM market_calendar_days
                     WHERE release_id='ca100000-0000-7000-8000-000000000088';
                    DELETE FROM market_calendar_release_sources
                     WHERE release_id='ca100000-0000-7000-8000-000000000088';
                    DELETE FROM market_calendar_releases
                     WHERE id='ca100000-0000-7000-8000-000000000088';
                    SET session_replication_role='origin';
                    """);
        }
    }

    [Fact]
    public async Task NullableAssetLogicalKey_RejectsDuplicateGlobalWindow()
    {
        const string source = "it-null-unique";
        try
        {
            const string sql = """
                INSERT INTO ingestion_windows(
                    source,asset_id,job_type,range_start,range_end,contract_version)
                VALUES (@source,NULL,'inflation_backfill','2091-01-01','2091-01-01',1)
                """;
            await database.ExecuteAsync(sql, new NpgsqlParameter("source", source));
            var act = () => database.ExecuteAsync(sql, new NpgsqlParameter("source", source));
            var exception = await act.Should().ThrowAsync<PostgresException>();
            exception.Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        }
        finally { await database.CleanupGlobalAsync(source); }
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(10)]
    public async Task AppClockSkew_DoesNotControlDatabaseLeaseClaim(int yearOffset)
    {
        var source = $"it-clock-{yearOffset}";
        var assetId = await database.CreateAssetAsync(source);
        try
        {
            var clock = new SkewTimeProvider(DateTimeOffset.UtcNow.AddYears(yearOffset));
            var repository = new IngestionWindowRepository(database.ContextFactory,
                new NoopIngestionPersistenceFaultInjector(), clock,
                new NoopIngestionFreshnessTelemetry());
            var scope = Scope(source, assetId);
            await repository.EnsureWindowsAsync(scope, [Range(1)], default);
            var claim = await repository.ClaimNextAsync(
                scope, "owner", TimeSpan.FromMinutes(1), default);
            claim.Status.Should().Be(WindowClaimStatus.Claimed);
            var leaseSeconds = await database.ScalarAsync<double>("""
                SELECT EXTRACT(EPOCH FROM (lease_until-clock_timestamp()))
                  FROM ingestion_windows WHERE id=@id
                """, new NpgsqlParameter("id", claim.Claim!.WindowId));
            leaseSeconds.Should().BeInRange(50, 61);
        }
        finally { await database.CleanupAssetAsync(assetId); }
    }

    [Fact]
    public async Task WrongInflationSource_IsRejectedAndRolledBack()
    {
        const string source = "it-inflation-source";
        const string jobType = IngestionJobTypes.InflationBackfill;
        var repository = database.Repository();
        var scope = new IngestionWindowScope(source, null, jobType, 1);
        try
        {
            await repository.EnsureWindowsAsync(scope,
                [new(new DateOnly(2090, 1, 1), new DateOnly(2090, 1, 1))], default);
            var claim = (await repository.ClaimNextAsync(
                scope, "owner", TimeSpan.FromMinutes(1), default)).Claim!;
            var outcome = AdapterOutcome<InflationRate>.Data(
                [new InflationRate
                {
                    PeriodDate = claim.From, IndexValue = 100,
                    Source = InflationSources.SeedApproximation,
                }], 1);

            var act = () => repository.CompleteInflationAsync(claim, outcome, Counts(1), default);
            await act.Should().ThrowAsync<InvalidOperationException>();
            (await database.ScalarAsync<long>("""
                SELECT count(*) FROM inflation_rates
                 WHERE period_date='2090-01-01' AND source='seed-approximation'
                """)).Should().Be(0);
        }
        finally { await database.CleanupGlobalAsync(source); }
    }

    [Fact]
    public async Task ForgedClaimScopeOrRange_IsRejectedBeforeDataWrite()
    {
        var assetId = await database.CreateAssetAsync("it-forged");
        try
        {
            var repository = database.Repository();
            var scope = Scope("it-forged", assetId);
            await repository.EnsureWindowsAsync(scope, [Range(1)], default);
            var claim = (await repository.ClaimNextAsync(
                scope, "owner", TimeSpan.FromMinutes(1), default)).Claim!;
            var forged = claim with
            {
                From = claim.From.AddDays(1),
                To = claim.To.AddDays(1),
            };
            var act = () => repository.CompletePriceAsync(
                forged, PriceOutcome(assetId, forged.From), Counts(1), default);
            await act.Should().ThrowAsync<IngestionLeaseLostException>();
            (await database.ScalarAsync<long>("SELECT count(*) FROM price_points WHERE asset_id=@id",
                new NpgsqlParameter("id", assetId))).Should().Be(0);
        }
        finally { await database.CleanupAssetAsync(assetId); }
    }

    [Fact]
    public async Task Schema_HasNullSafeLogicalUniquenessAndPartialIndexes()
    {
        var definition = await database.ScalarAsync<string>("""
            SELECT pg_get_constraintdef(oid)
              FROM pg_constraint
             WHERE conname='uq_ingestion_windows_logical'
            """);
        definition.Should().Contain("NULLS NOT DISTINCT");
        (await database.ScalarAsync<long>("""
            SELECT count(*) FROM pg_indexes
             WHERE schemaname='public'
               AND indexname IN ('idx_ingestion_windows_claim','idx_ingestion_windows_lease_expiry','idx_ingestion_jobs_window_started')
               AND indexdef ILIKE '% WHERE %'
            """)).Should().Be(3);
        (await database.ScalarAsync<long>("""
            SELECT count(*) FROM pg_constraint
             WHERE conrelid='ingestion_windows'::regclass
               AND conname IN (
                 'chk_ingestion_windows_range','chk_ingestion_windows_contract',
                 'chk_ingestion_windows_attempt','chk_ingestion_windows_counts',
                 'chk_ingestion_windows_terminal_completeness','chk_ingestion_windows_state',
                 'chk_ingestion_windows_lease','chk_ingestion_windows_completed',
                 'chk_ingestion_windows_outcome_codes','chk_ingestion_windows_error_codes')
            """)).Should().Be(10);
        (await database.ScalarAsync<long>("""
            SELECT count(*) FROM information_schema.columns
             WHERE table_schema='public' AND table_name='ingestion_windows'
               AND column_name IN (
                 'requested_calendar_count','expected_observation_count','raw_item_count',
                 'accepted_distinct_count','rejected_count','expected_no_data_count','lease_token')
            """)).Should().Be(7);
    }

    private static IngestionWindowScope Scope(string source, Guid assetId) =>
        new(source, assetId, IngestionJobTypes.HistoricalBackfill, 1);
    private static IngestionWindowRange Range(int day) =>
        new(new DateOnly(2040, 1, day), new DateOnly(2040, 1, day));
    private static AdapterOutcome<PricePoint> PriceOutcome(Guid assetId, DateOnly date) =>
        AdapterOutcome<PricePoint>.Data(
            [AuthorityTestData.CoinGecko(assetId, $"it-{assetId:N}", date)], 1);
    private static IngestionWindowCounts Counts(int count) => new(count, count, count, count, 0, 0);
    private static IngestionWindowCounts FailureCounts(int count) => new(count, count, 0, 0, 0, 0);

    private sealed class InjectedFaultException : Exception;
    private sealed class SkewTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    private sealed class ThrowingFault(bool beforeCommit = false, bool afterCommit = false)
        : IIngestionPersistenceFaultInjector
    {
        public Task BeforeClaimCommitAsync(Guid windowId, CancellationToken ct) => Task.CompletedTask;
        public Task BeforeCommitAsync(Guid windowId, CancellationToken ct) =>
            beforeCommit ? Task.FromException(new InjectedFaultException()) : Task.CompletedTask;
        public Task AfterCommitAsync(Guid windowId, CancellationToken ct) =>
            afterCommit ? Task.FromException(new InjectedFaultException()) : Task.CompletedTask;
    }

    private sealed class BlockingClaimFault : IIngestionPersistenceFaultInjector
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task BeforeClaimCommitAsync(Guid windowId, CancellationToken ct)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(ct);
        }

        public Task BeforeCommitAsync(Guid windowId, CancellationToken ct) => Task.CompletedTask;
        public Task AfterCommitAsync(Guid windowId, CancellationToken ct) => Task.CompletedTask;
    }
}
