using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using Npgsql;
using Saydin.CalendarData;
using Saydin.DatabaseSecurity;
using Saydin.PriceIngestion.Adapters;
using Saydin.PriceIngestion.Repositories;
using Saydin.PriceIngestion.Workers;
using Saydin.Shared.Entities;

namespace Saydin.PriceIngestion.IntegrationTests;

[Collection(IngestionDatabaseCollection.Name)]
public sealed class CalendarReleaseImporterIntegrationTests(IngestionDatabaseFixture database)
{
    private static readonly Guid BootstrapBist =
        Guid.Parse("ca100000-0000-7000-8000-000000000002");

    [Fact]
    public async Task ExistingRelease_DifferentVerifiedProvenance_IsRejected()
    {
        using var bundle = TemporaryBundle.CopyFrom(FindDataRoot());
        MutateManifestProvenance(bundle.Path, updateExpectedHash: true);
        var options = Command(bundle.Path, BootstrapBist, 1, BootstrapBist);

        var act = () => ImportAsync(options);

        (await act.Should().ThrowAsync<CalendarDataException>()).Which.Message
            .Should().StartWith("release_id_payload_conflict");
        (await database.ScalarAsync<long>("""
            SELECT count(*) FROM market_calendar_releases
             WHERE id=@id AND snapshot_set_id='cal-001-2026-08-17'
            """, new NpgsqlParameter("id", BootstrapBist))).Should().Be(1);
    }

    [Fact]
    public async Task VerifiedManifestMutationBarrier_FailsBeforeDatabaseWrite()
    {
        using var bundle = TemporaryBundle.CopyFrom(FindDataRoot());
        var releaseId = Guid.Parse("ca100000-0000-7000-8000-000000000072");
        var options = Command(bundle.Path, releaseId, 702, BootstrapBist);

        var act = () => ImportAsync(options,
            () => MutateManifestProvenance(bundle.Path, updateExpectedHash: false));

        (await act.Should().ThrowAsync<CalendarDataException>()).Which.Message
            .Should().Be("verified_input_changed: source-manifest.json");
        (await database.ScalarAsync<long>(
            "SELECT count(*) FROM market_calendar_releases WHERE id=@id",
            new NpgsqlParameter("id", releaseId))).Should().Be(0);
    }

    [Fact]
    public async Task WrongExpectedCurrent_RollsBackReleaseSourcesAndDays()
    {
        var releaseId = Guid.Parse("ca100000-0000-7000-8000-000000000073");
        var options = Command(FindDataRoot(), releaseId, 703, Guid.NewGuid());

        var act = () => ImportAsync(options);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.SerializationFailure);
        (await database.ScalarAsync<long>(
            "SELECT count(*) FROM market_calendar_releases WHERE id=@id",
            new NpgsqlParameter("id", releaseId))).Should().Be(0);
        (await database.ScalarAsync<long>(
            "SELECT count(*) FROM market_calendar_release_sources WHERE release_id=@id",
            new NpgsqlParameter("id", releaseId))).Should().Be(0);
        (await database.ScalarAsync<long>(
            "SELECT count(*) FROM market_calendar_days WHERE release_id=@id",
            new NpgsqlParameter("id", releaseId))).Should().Be(0);
    }

    [Fact]
    public async Task TcmbTestOnlyCoveragePlusOne_ProviderMovesZeroToOneAfterSealAndActivate()
    {
        var assetId = await database.CreateAssetAsync("tcmb");
        await database.ExecuteAsync(
            "UPDATE assets SET source_id='USD' WHERE id=@id",
            new NpgsqlParameter("id", assetId));
        var original = await database.ScalarAsync<Guid>("""
            SELECT release_id FROM market_calendar_active_releases
             WHERE calendar_code='tcmb_indicative_fx'
            """);
        var synthetic = Guid.Parse("ca1f0000-0000-7000-8000-000000000018");
        var activeSwitched = false;
        try
        {
            var adapter = new CountingAdapter();
            var repository = database.Repository();
            var worker = new TestWorker(adapter, new UnusedAssetRepository(), repository);
            var asset = new Asset
            {
                Id = assetId, Symbol = "USDTRY", DisplayName = "test-only synthetic",
                Category = AssetCategory.Currency, IsActive = true, Source = "tcmb", SourceId = "USD",
            };
            var nextDay = new DateOnly(2026, 8, 18);

            (await worker.BackfillChunkedAsync(asset, nextDay, nextDay, default))
                .Should().BeFalse();
            adapter.CallCount.Should().Be(0,
                "bootstrap official coverage ends on 2026-08-17");

            var verified = CalendarDataGenerator.LoadVerified(FindDataRoot());
            var tcmb = verified.Calendars.Single(item =>
                item.CalendarCode == CalendarDataGenerator.TcmbCode);
            var evidence = await database.ScalarAsync<string>("""
                SELECT evidence_raw_sha256 FROM market_calendar_days
                 WHERE release_id=@id AND calendar_date='2026-08-17'
                """, new NpgsqlParameter("id", original));
            var extra = System.Text.Encoding.UTF8.GetBytes(
                $"tcmb_indicative_fx,2026-08-18,true,publication,test_only_synthetic_extension,{evidence}\n");
            var content = new byte[tcmb.Content.Length + extra.Length];
            tcmb.Content.CopyTo(content, 0);
            extra.CopyTo(content, tcmb.Content.Length);
            var normalizedHash = Convert.ToHexStringLower(SHA256.HashData(content));

            await database.ExecuteAsync("""
                INSERT INTO market_calendar_releases(
                    id,calendar_code,snapshot_set_id,release_version,coverage_from,coverage_through,
                    row_count,normalized_sha256,source_bundle_sha256,released_at)
                SELECT @synthetic,calendar_code,'test-only-synthetic-coverage-plus-one',704,
                       coverage_from,'2026-08-18',7535,@normalized,source_bundle_sha256,released_at
                  FROM market_calendar_releases WHERE id=@original;
                INSERT INTO market_calendar_release_sources
                SELECT @synthetic,source_id,source_kind,source_role,source_uri,media_type,
                       retrieved_at,raw_sha256,snapshot_path,source_year,source_month
                  FROM market_calendar_release_sources WHERE release_id=@original;
                INSERT INTO market_calendar_days
                SELECT @synthetic,calendar_date,observation_expected,market_state,reason_code,
                       evidence_raw_sha256
                  FROM market_calendar_days WHERE release_id=@original;
                INSERT INTO market_calendar_days(
                    release_id,calendar_date,observation_expected,market_state,reason_code,
                    evidence_raw_sha256)
                VALUES (@synthetic,'2026-08-18',TRUE,'publication',
                        'test_only_synthetic_extension',@evidence);
                SELECT public.seal_market_calendar_release(@synthetic);
                SELECT public.activate_market_calendar_release(
                    'tcmb_indicative_fx',@synthetic,@original);
                """, new NpgsqlParameter("synthetic", synthetic),
                new NpgsqlParameter("original", original),
                new NpgsqlParameter("normalized", normalizedHash),
                new NpgsqlParameter("evidence", evidence));
            activeSwitched = true;

            (await worker.BackfillChunkedAsync(asset, nextDay, nextDay, default))
                .Should().BeTrue();
            adapter.CallCount.Should().Be(1);
        }
        finally
        {
            if (activeSwitched)
                await database.ExecuteAsync("""
                    SELECT public.activate_market_calendar_release(
                        'tcmb_indicative_fx',@original,@synthetic)
                    """, new NpgsqlParameter("original", original),
                    new NpgsqlParameter("synthetic", synthetic));
            await database.ExecuteAsync("""
                SET session_replication_role='replica';
                DELETE FROM market_calendar_days WHERE release_id=@synthetic;
                DELETE FROM market_calendar_release_sources WHERE release_id=@synthetic;
                DELETE FROM market_calendar_releases WHERE id=@synthetic;
                SET session_replication_role='origin';
                """, new NpgsqlParameter("synthetic", synthetic));
            await database.CleanupAssetAsync(assetId);
        }
    }

    private CalendarReleaseCommand Command(
        string dataRoot, Guid releaseId, int version, Guid expectedCurrent) =>
        new(CalendarReleaseCommandName.Import, dataRoot,
            CalendarDataGenerator.BistCode, releaseId, version, expectedCurrent,
            RuntimeOptions());

    private async Task<string> ImportAsync(CalendarReleaseCommand options, Action? barrier = null)
    {
        await using var dataSource = await database.OpenCalendarDataSourceAsync();
        return await CalendarReleaseImporter.ImportAsync(
            options, dataSource, verifiedBundleBarrier: barrier);
    }

    private RuntimeDatabaseOptions RuntimeOptions()
    {
        const string systemHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var databaseName = new NpgsqlConnectionStringBuilder(database.AdminConnectionString).Database!;
        var prefix = RoleContract.DerivePrefix("tst-a", databaseName, systemHash);
        var contract = RoleContract.Create("tst-a", databaseName, systemHash, prefix);
        return new RuntimeDatabaseOptions(
            LoginPurpose.CalendarImporter, contract,
            contract.Login(LoginPurpose.CalendarImporter, 1), "postgres", 5432,
            databaseName, SslMode.Disable, "/run/secrets/unused",
            RuntimeDatabasePooling.Disabled);
    }

    private static void MutateManifestProvenance(string root, bool updateExpectedHash)
    {
        var manifestPath = Path.Combine(root, "source-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        var source = manifest["sources"]!.AsArray().Select(node => node!.AsObject())
            .Single(item => item["id"]!.GetValue<string>() == "bist-holidays-index");
        source["role"] = "policy";
        source["retrievedAt"] = "2026-08-18T14:08:38Z";
        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        }) + Environment.NewLine);

        if (!updateExpectedHash) return;
        var generated = CalendarDataGenerator.Generate(root)
            .Single(calendar => calendar.CalendarCode == CalendarDataGenerator.BistCode);
        var expectedPath = Path.Combine(root, "expected-output.json");
        var expected = JsonNode.Parse(File.ReadAllBytes(expectedPath))!.AsObject();
        var output = expected["outputs"]!.AsArray().Select(node => node!.AsObject())
            .Single(item => item["calendarCode"]!.GetValue<string>() == CalendarDataGenerator.BistCode);
        output["sourceBundleSha256"] = generated.SourceBundleSha256;
        File.WriteAllText(expectedPath, expected.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        }) + Environment.NewLine);
    }

    private static string FindDataRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "calendar-data", "data");
            if (File.Exists(Path.Combine(candidate, "source-manifest.json"))) return candidate;
        }
        throw new InvalidOperationException("calendar-data root bulunamadı");
    }

    private sealed class TemporaryBundle : IDisposable
    {
        private TemporaryBundle(string path) => Path = path;
        public string Path { get; }

        public static TemporaryBundle CopyFrom(string source)
        {
            var target = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"saydin-calendar-bundle-{Guid.NewGuid():N}");
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = System.IO.Path.GetRelativePath(source, file);
                var destination = System.IO.Path.Combine(target, relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            return new(target);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class CountingAdapter : IExternalPriceAdapter
    {
        public string Source => "tcmb";
        public int CallCount { get; private set; }

        public Task<AdapterOutcome<PricePoint>> FetchRangeAsync(
            PriceFetchRequest request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(AdapterOutcome<PricePoint>.Data([
                AuthorityTestData.Tcmb(request.AssetId, request.SourceId, request.From, 1m),
            ], 1, request.CalendarClosedDates));
        }
    }

    private sealed class TestWorker(
        IExternalPriceAdapter adapter,
        IPriceIngestionRepository assets,
        IIngestionWindowRepository windows)
        : BaseAssetWorker(adapter, assets, windows, new ConfigurationBuilder().Build(),
            TimeProvider.System, NullLogger.Instance)
    {
        protected override DateOnly BackfillStartDate => new(2026, 8, 18);
        protected override int ChunkDays => 1;
        protected override string WorkerConfigKey => "TestOnlyCalendarLifecycle";
        protected override TimeOnly DefaultDailyRunUtcTime => TimeOnly.MinValue;
        protected override int ContractVersion => 2;
    }

    private sealed class UnusedAssetRepository : IPriceIngestionRepository
    {
        public Task<IReadOnlyList<Asset>> GetActiveAssetsBySourceAsync(
            string source, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlySet<DateOnly>> GetMarketHolidaysAsync(
            Guid assetId, DateOnly from, DateOnly to, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<DateOnly>>(new HashSet<DateOnly>());
    }
}
