using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using Saydin.DatabaseSecurity;

namespace Saydin.DataQualityAudit.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuditDatabaseCollection : ICollectionFixture<AuditDatabaseFixture>
{
    public const string Name = "data-quality-audit-database";
}

public sealed class AuditDatabaseFixture : IAsyncLifetime
{
    internal sealed record AuthorityAnomaly(
        AuditInputManifest Manifest,
        Guid WindowId,
        string Provider,
        byte[] PayloadSha256,
        string Canary);
    internal sealed record InflationAuthorityAnomaly(
        AuditInputManifest Manifest,
        Guid WindowId,
        byte[] PayloadSha256);
    internal sealed record LegacyAuthorityAnomaly(
        AuditInputManifest Manifest,
        Guid InflationWindowId,
        DateOnly InflationPeriod);
    internal sealed record Dq009DataDrift(Guid? WindowId, byte[]? PayloadSha256);

    private IntegrationEnvironment _environment = null!;
    private string _roleName = null!;
    private Guid[] _anomalyWindowIds = [];
    private DateOnly _anomalyUnattestedDate;

    public string Root { get; private set; } = null!;
    public string InputPrivateKeyPath { get; private set; } = null!;
    public string InputPublicKeyPath { get; private set; } = null!;
    public string EvidencePrivateKeyPath { get; private set; } = null!;
    public string PublicKeyPath { get; private set; } = null!;
    public string HmacKeyPath { get; private set; } = null!;
    public string AuditPasswordFile => _environment.AuditPasswordFile;
    public Guid AssetId { get; private set; }
    public DateOnly From { get; } = new(2024, 1, 1);
    public DateOnly Through { get; } = new(2024, 1, 2);
    public string DatabaseName => _environment.DatabaseName;

    public async Task InitializeAsync()
    {
        _environment = IntegrationEnvironment.Require();
        _roleName = _environment.AuditLogin;
        Root = Path.Combine(Path.GetTempPath(), $"saydin-audit-integration-{_environment.RunId}");
        if (Directory.Exists(Root))
            throw new InvalidOperationException("Audit fixture directory already exists.");
        Directory.CreateDirectory(Root);
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            InputPrivateKeyPath = Write("input-private.pem", key.ExportECPrivateKeyPem());
            InputPublicKeyPath = Write("input-public.pem", key.ExportSubjectPublicKeyInfoPem());
        }
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            EvidencePrivateKeyPath = Write("evidence-private.pem", key.ExportECPrivateKeyPem());
            PublicKeyPath = Write("evidence-public.pem", key.ExportSubjectPublicKeyInfoPem());
        }
        HmacKeyPath = Path.Combine(Root, "hmac.key");
        await File.WriteAllBytesAsync(HmacKeyPath, RandomNumberGenerator.GetBytes(32));

        await using var admin = new NpgsqlConnection(_environment.AdminConnectionString);
        await admin.OpenAsync();
        AssetId = await FindCoinGeckoAssetAsync(admin);
        await SeedCleanLaneAsync(admin);
    }

    public async Task DisposeAsync()
    {
        try
        {
            NpgsqlConnection.ClearAllPools();
        }
        finally
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    internal async Task<(int ExitCode, string Output, string Error, string Bundle)> RunAuditAsync(
        AuditInputManifest? overrideManifest = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = overrideManifest ?? await CreateManifestAsync();
        var raw = CanonicalJson.SerializeCanonical(manifest, AuditJsonContext.Default.AuditInputManifest);
        var inputPath = Path.Combine(Root, $"input-{Guid.NewGuid():N}.json");
        var inputSignaturePath = Path.Combine(Root, $"input-{Guid.NewGuid():N}.sig");
        var bundle = Path.Combine(Root, $"bundle-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(inputPath, raw, CancellationToken.None);
        await File.WriteAllBytesAsync(inputSignaturePath,
            AuditCryptography.Sign(raw, InputPrivateKeyPath), CancellationToken.None);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await AuditApplication.RunAsync([
            "scan",
            "--input", inputPath,
            "--input-signature", inputSignaturePath,
            "--input-public-key", InputPublicKeyPath,
            "--evidence-private-key", EvidencePrivateKeyPath,
            "--hmac-key-file", HmacKeyPath,
            "--output", bundle,
        ], output, error, TimeProvider.System, cancellationToken, RuntimeEnvironment);
        return (exit, output.ToString(), error.ToString(), bundle);
    }

    internal async Task<(int ExitCode, string Output, string Error, string Bundle)> RunKmsAuditAsync(
        IOciKmsSigningClient kmsClient,
        CancellationToken cancellationToken = default)
    {
        var baseline = await CreateManifestAsync();
        var manifest = baseline with
        {
            Target = baseline.Target with { Environment = "production" },
        };
        var raw = CanonicalJson.SerializeCanonical(
            manifest, AuditJsonContext.Default.AuditInputManifest);
        var inputPath = Path.Combine(Root, $"input-kms-{Guid.NewGuid():N}.json");
        var inputSignaturePath = Path.Combine(Root, $"input-kms-{Guid.NewGuid():N}.sig");
        var bundle = Path.Combine(Root, $"bundle-kms-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(inputPath, raw, CancellationToken.None);
        await File.WriteAllBytesAsync(inputSignaturePath,
            AuditCryptography.Sign(raw, InputPrivateKeyPath), CancellationToken.None);
        var evidenceKeyId = AuditCryptography.PublicKeyId(PublicKeyPath);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await AuditApplication.RunAsync([
            "scan",
            "--input", inputPath,
            "--input-signature", inputSignaturePath,
            "--input-public-key", InputPublicKeyPath,
            "--hmac-key-file", HmacKeyPath,
            "--output", bundle,
            "--signer-mode", "oci-kms-instance-principal",
            "--kms-key-id", "ocid1.key.oc1.eu-frankfurt-1.integration-test",
            "--kms-key-version-id", "ocid1.keyversion.oc1.eu-frankfurt-1.integration-test",
            "--kms-crypto-endpoint",
            "https://integration-test-crypto.kms.eu-frankfurt-1.oraclecloud.com/",
            "--oci-region", "eu-frankfurt-1",
            "--evidence-public-key", PublicKeyPath,
            "--allowed-evidence-key-ids", evidenceKeyId,
            "--kms-timeout-seconds", "1",
        ], output, error, TimeProvider.System, cancellationToken, RuntimeEnvironment,
            _ => kmsClient);
        return (exit, output.ToString(), error.ToString(), bundle);
    }

    internal async Task<AuditInputManifest> CreateManifestAsync(
        string? database = null,
        long maxDatabaseBytes = 50_000_000_000,
        long maxRelationBytes = 20_000_000_000,
        int statementTimeoutMilliseconds = 30_000)
    {
        await using var admin = new NpgsqlConnection(_environment.AdminConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT system_identifier::text FROM pg_control_system()", admin);
        var identifier = (string)(await command.ExecuteScalarAsync())!;
        var now = DateTimeOffset.UtcNow;
        return new AuditInputManifest(
            1,
            AuditCryptography.PublicKeyId(InputPublicKeyPath),
            AuditCryptography.PublicKeyId(PublicKeyPath),
            now.AddMinutes(-1),
            now.AddHours(1),
            new AuditTarget(
                database ?? _environment.DatabaseName,
                AuditCryptography.Sha256Hex(Encoding.UTF8.GetBytes(identifier)),
                "integration",
                $"fixture-{_environment.RunId}"),
            new AuditBudget(
                maxDatabaseBytes,
                maxRelationBytes,
                1_000_000_000,
                366,
                100,
                20,
                10_000_000,
                statementTimeoutMilliseconds,
                1_000,
                120),
            new AuditScope(
                now,
                now.AddHours(-1),
                [new AuditLane(
                    "coingecko",
                    AssetId,
                    "historical_backfill",
                    1,
                    From,
                    Through,
                    "day")]));
    }

    public async Task MakePriceGapAndInvalidAsync()
    {
        await using var connection = new NpgsqlConnection(_environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL session_replication_role=replica");
        await ExecuteAsync(connection, transaction,
            "DELETE FROM public.price_observation_attributions WHERE asset_id=$1 AND price_date=$2",
            AssetId, Through);
        await ExecuteAsync(connection, transaction,
            "DELETE FROM public.price_points WHERE asset_id=$1 AND price_date=$2", AssetId, Through);
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.price_points DROP CONSTRAINT chk_price_points_numeric");
        await ExecuteAsync(connection, transaction,
            """
            UPDATE public.price_points
               SET close=-1,
                   source_raw=jsonb_set(source_raw,'{close}',to_jsonb(-1::numeric)),
                   observation_sha256=sha256(convert_to(public.saydin_canonical_observation(
                     jsonb_set(source_raw,'{close}',to_jsonb(-1::numeric)))::text,'UTF8'))
             WHERE asset_id=$1 AND price_date=$2
            """, AssetId, From);
        await ExecuteAsync(connection, transaction, """
            ALTER TABLE public.price_points ADD CONSTRAINT chk_price_points_numeric CHECK (
              close::text NOT IN ('NaN','Infinity','-Infinity') AND close>0
              AND (volume IS NULL OR (volume::text NOT IN ('NaN','Infinity','-Infinity') AND volume>=0))
              AND (open IS NULL OR open::text NOT IN ('NaN','Infinity','-Infinity'))
              AND (high IS NULL OR high::text NOT IN ('NaN','Infinity','-Infinity'))
              AND (low IS NULL OR low::text NOT IN ('NaN','Infinity','-Infinity'))
              AND ((open IS NULL AND high IS NULL AND low IS NULL)
                OR (open IS NOT NULL AND high IS NOT NULL AND low IS NOT NULL
                    AND open>0 AND high>0 AND low>0
                    AND high>=GREATEST(open,close,low) AND low<=LEAST(open,close,high)))) NOT VALID
            """);
        await transaction.CommitAsync();
    }

    public async Task RestoreCleanPricesAsync()
    {
        await using var admin = new NpgsqlConnection(_environment.AdminConnectionString);
        await admin.OpenAsync();
        await SeedCleanLaneAsync(admin);
    }

    internal async Task<LegacyAuthorityAnomaly> CreateLegacyAuthorityAnomalyAsync()
    {
        // Keep this outside the immutable seed-approximation range so the batch
        // contains exactly the synthetic TÜİK/EVDS legacy observation.
        var inflationPeriod = new DateOnly(2026, 1, 1);
        await using var connection = new NpgsqlConnection(_environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL session_replication_role=replica");
        await ExecuteAsync(connection, transaction, """
            DELETE FROM public.price_observation_attributions
             WHERE asset_id=$1 AND price_date=$2
            """, AssetId, From);
        await ExecuteAsync(connection, transaction, """
            DELETE FROM public.provider_fetch_payloads payload
             WHERE provider_source='coingecko'
               AND payload_sha256=sha256(convert_to('audit-clean-payload:'||$1::date::text,'UTF8'))
               AND NOT EXISTS (
                 SELECT 1 FROM public.price_observation_attributions attribution
                  WHERE attribution.provider_source=payload.provider_source
                    AND attribution.payload_sha256=payload.payload_sha256)
            """, From);
        await ExecuteAsync(connection, transaction, """
            UPDATE public.price_points
               SET provider_source=NULL,source_observation_id=NULL,as_of_at=NULL,
                   price_kind=NULL,is_final=NULL,observation_sha256=NULL,
                   authority_contract_version=NULL,source_raw=NULL
             WHERE asset_id=$1 AND price_date=$2
            """, AssetId, From);

        var windowId = await ScalarAsync<Guid>(connection, transaction, """
            INSERT INTO public.ingestion_windows(
              source,asset_id,job_type,range_start,range_end,contract_version,state,
              requested_calendar_count,expected_observation_count,raw_item_count,
              accepted_distinct_count,rejected_count,expected_no_data_count,
              outcome_code,completed_at)
            VALUES ('evds',NULL,'inflation_backfill',$1,$1,1,'succeeded',
                    1,1,1,1,0,0,'data_complete',clock_timestamp())
            RETURNING id
            """, inflationPeriod);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO public.ingestion_jobs(
              asset_id,job_type,started_at,finished_at,status,records_upserted,
              date_range_start,date_range_end,source,window_id,outcome_code)
            VALUES (NULL,'inflation_backfill',clock_timestamp(),clock_timestamp(),'success',1,
                    $1,$1,'evds',$2,'data_complete')
            """, inflationPeriod, windowId);
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates DISABLE TRIGGER trg_inflation_rates_ingestion_fence");
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates DISABLE TRIGGER trg_inflation_rates_authority");
        await ExecuteAsync(connection, transaction, """
            INSERT INTO public.inflation_rates(period_date,index_value,source)
            VALUES ($1,100,'tuik')
            ON CONFLICT (period_date,source) DO UPDATE
              SET index_value=EXCLUDED.index_value,
                  provider_source=NULL,source_observation_id=NULL,as_of_at=NULL,
                  price_kind=NULL,is_final=NULL,observation_sha256=NULL,
                  authority_contract_version=NULL,source_raw=NULL
            """, inflationPeriod);
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates ENABLE ALWAYS TRIGGER trg_inflation_rates_ingestion_fence");
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates ENABLE ALWAYS TRIGGER trg_inflation_rates_authority");
        await transaction.CommitAsync();

        var baseline = await CreateManifestAsync();
        var inflationLane = new AuditLane(
            "evds", null, "inflation_backfill", 1,
            inflationPeriod, inflationPeriod, "month");
        return new LegacyAuthorityAnomaly(
            baseline with
            {
                Scope = baseline.Scope with
                {
                    Lanes = [.. baseline.Scope.Lanes, inflationLane],
                },
            },
            windowId,
            inflationPeriod);
    }

    internal async Task CleanupLegacyAuthorityAnomalyAsync(LegacyAuthorityAnomaly anomaly)
    {
        await using var connection = new NpgsqlConnection(_environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL session_replication_role=replica");
        await ExecuteAsync(connection, transaction,
            "DELETE FROM public.ingestion_jobs WHERE window_id=$1", anomaly.InflationWindowId);
        await ExecuteAsync(connection, transaction,
            "DELETE FROM public.ingestion_windows WHERE id=$1", anomaly.InflationWindowId);
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates DISABLE TRIGGER trg_inflation_rates_ingestion_fence");
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates DISABLE TRIGGER trg_inflation_rates_authority");
        await ExecuteAsync(connection, transaction,
            "DELETE FROM public.inflation_rates WHERE period_date=$1 AND source='tuik'",
            anomaly.InflationPeriod);
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates ENABLE ALWAYS TRIGGER trg_inflation_rates_ingestion_fence");
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates ENABLE ALWAYS TRIGGER trg_inflation_rates_authority");
        await transaction.CommitAsync();
        await RestoreCleanPricesAsync();
    }

    internal async Task<Dq009DataDrift> ApplyDq009DataDriftAsync(string drift)
    {
        var payloadSha = SHA256.HashData(Encoding.UTF8.GetBytes($"audit-dq009:{drift}"));
        await using var connection = new NpgsqlConnection(_environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL session_replication_role=replica");
        Guid? windowId = null;
        switch (drift)
        {
            case "partial_authority_tuple":
                await ExecuteAsync(connection, transaction,
                    "ALTER TABLE public.price_points DROP CONSTRAINT chk_price_points_authority_tuple");
                await ExecuteAsync(connection, transaction, """
                    UPDATE public.price_points SET provider_source=NULL
                     WHERE asset_id=$1 AND price_date=$2
                    """, AssetId, From);
                await RestorePriceAuthorityTupleAsync(connection, transaction);
                break;
            case "wrong_canonical_hash":
                await ExecuteAsync(connection, transaction,
                    "ALTER TABLE public.price_points DROP CONSTRAINT chk_price_points_authority_tuple");
                await ExecuteAsync(connection, transaction, """
                    UPDATE public.price_points SET observation_sha256=decode(repeat('11',32),'hex')
                     WHERE asset_id=$1 AND price_date=$2
                    """, AssetId, From);
                await ExecuteAsync(connection, transaction, """
                    UPDATE public.price_observation_attributions
                       SET observation_sha256=decode(repeat('11',32),'hex')
                     WHERE asset_id=$1 AND price_date=$2
                    """, AssetId, From);
                await RestorePriceAuthorityTupleAsync(connection, transaction);
                break;
            case "missing_attribution":
                await ExecuteAsync(connection, transaction, """
                    DELETE FROM public.price_observation_attributions
                     WHERE asset_id=$1 AND price_date=$2
                    """, AssetId, From);
                await ExecuteAsync(connection, transaction, """
                    DELETE FROM public.provider_fetch_payloads payload
                     WHERE provider_source='coingecko'
                       AND payload_sha256=sha256(convert_to('audit-clean-payload:'||$2::date::text,'UTF8'))
                       AND NOT EXISTS (SELECT 1 FROM public.price_observation_attributions attribution
                         WHERE attribution.provider_source=payload.provider_source
                           AND attribution.payload_sha256=payload.payload_sha256)
                    """, AssetId, From);
                break;
            case "orphan_fetch_payload":
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO public.provider_fetch_payloads(
                      provider_source,payload_sha256,payload_byte_length)
                    VALUES ('coingecko',$1,64)
                    """, payloadSha);
                break;
            case "forged_price_attribution":
                await ExecuteAsync(connection, transaction, """
                    UPDATE public.price_observation_attributions
                       SET source_observation_id='coingecko:forged:try:0'
                     WHERE asset_id=$1 AND price_date=$2
                    """, AssetId, From);
                break;
            case "forged_inflation_attribution":
                windowId = await ScalarAsync<Guid>(connection, transaction, """
                    INSERT INTO public.ingestion_windows(
                      source,asset_id,job_type,range_start,range_end,contract_version,state)
                    VALUES ('evds',NULL,'inflation_backfill','2003-01-01','2003-01-01',1,'pending')
                    RETURNING id
                    """);
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO public.provider_fetch_payloads(
                      provider_source,payload_sha256,payload_byte_length)
                    VALUES ('evds',$1,64)
                    """, payloadSha);
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO public.inflation_observation_attributions(
                      period_date,source,ingestion_window_id,provider_source,payload_sha256,
                      source_observation_id,observation_sha256,authority_contract_version)
                    VALUES ('2003-01-01','seed-approximation',$2,'evds',$1,
                            'evds:forged',decode(repeat('22',32),'hex'),1)
                    """, payloadSha, windowId.Value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(drift));
        }
        await transaction.CommitAsync();
        return new Dq009DataDrift(windowId,
            drift is "orphan_fetch_payload" or "forged_inflation_attribution" ? payloadSha : null);
    }

    internal async Task CleanupDq009DataDriftAsync(Dq009DataDrift drift)
    {
        await using var connection = new NpgsqlConnection(_environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL session_replication_role=replica");
        if (drift.WindowId is Guid windowId)
        {
            await ExecuteAsync(connection, transaction,
                "DELETE FROM public.inflation_observation_attributions WHERE ingestion_window_id=$1",
                windowId);
            await ExecuteAsync(connection, transaction,
                "DELETE FROM public.ingestion_windows WHERE id=$1", windowId);
        }
        if (drift.PayloadSha256 is { } payloadSha)
            await ExecuteAsync(connection, transaction,
                "DELETE FROM public.provider_fetch_payloads WHERE payload_sha256=$1", payloadSha);
        await transaction.CommitAsync();
        await RestoreCleanPricesAsync();
    }

    public async Task SetPriceRawAsync(string json)
    {
        await using var connection = new NpgsqlConnection(_environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL session_replication_role=replica");
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.price_points DROP CONSTRAINT chk_price_points_authority_tuple");
        await ExecuteAsync(connection, transaction, """
            UPDATE public.price_points
               SET source_raw=$1::jsonb,
                   observation_sha256=sha256(convert_to(
                     public.saydin_canonical_observation($1::jsonb)::text,'UTF8'))
             WHERE asset_id=$2 AND price_date=$3
            """, json, AssetId, From);
        await RestorePriceAuthorityTupleAsync(connection, transaction);
        await transaction.CommitAsync();
    }

    internal async Task<AuditInputManifest> CreateAnomalyMatrixAsync()
    {
        await using var connection = new NpgsqlConnection(_environment.AdminConnectionString);
        await connection.OpenAsync();
        var tcmbAsset = await ScalarAsync<Guid>(connection,
            "SELECT id FROM public.assets WHERE source='tcmb' ORDER BY symbol LIMIT 1");
        var calendarRelease = await ScalarAsync<Guid>(connection, """
            SELECT active.release_id
            FROM public.asset_market_calendars binding
            JOIN public.market_calendar_active_releases active USING(calendar_code)
            WHERE binding.asset_id=$1
            """, tcmbAsset);
        var calendarThrough = await ScalarAsync<DateOnly>(connection,
            "SELECT coverage_through FROM public.market_calendar_releases WHERE id=$1", calendarRelease);
        var uncoveredDate = calendarThrough.AddDays(1);
        var evdsDate = new DateOnly(2010, 1, 1);
        var expiredDate = Through.AddDays(1);
        var unattestedDate = Through.AddDays(2);

        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL session_replication_role=replica");
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates DISABLE TRIGGER trg_inflation_rates_authority");
        var evdsToken = Guid.NewGuid();
        var evdsWindow = await ScalarAsync<Guid>(connection, transaction, """
            INSERT INTO public.ingestion_windows(
                source,asset_id,job_type,range_start,range_end,contract_version,state,
                lease_owner,lease_token,lease_until,attempt_count)
            VALUES ('evds',NULL,'inflation_backfill',$1,$1,1,'running',
                    'audit-fixture',$2,clock_timestamp()+interval '10 minutes',1)
            RETURNING id
            """, evdsDate, evdsToken);
        var evdsJob = await ScalarAsync<Guid>(connection, transaction, """
            INSERT INTO public.ingestion_jobs(
                asset_id,job_type,status,date_range_start,date_range_end,source,window_id)
            VALUES (NULL,'inflation_backfill','running',$1,$1,'evds',$2)
            RETURNING id
            """, evdsDate, evdsWindow);
        await ExecuteAsync(connection, transaction,
            "SELECT set_config('saydin.ingestion_window_id',$1,true)", evdsWindow.ToString("D"));
        await ExecuteAsync(connection, transaction,
            "SELECT set_config('saydin.ingestion_lease_token',$1,true)", evdsToken.ToString("D"));
        await ExecuteAsync(connection, transaction, """
            INSERT INTO public.inflation_rates(period_date,index_value,source,created_at,updated_at)
            VALUES ($1,158.3700,'tuik',clock_timestamp(),clock_timestamp())
            ON CONFLICT(period_date,source) DO UPDATE
              SET index_value=excluded.index_value,updated_at=excluded.updated_at
            """, evdsDate);
        await ExecuteAsync(connection, transaction, """
            UPDATE public.ingestion_windows SET state='succeeded',lease_owner=NULL,lease_token=NULL,
                lease_until=NULL,requested_calendar_count=1,expected_observation_count=1,
                raw_item_count=1,accepted_distinct_count=1,rejected_count=0,
                expected_no_data_count=0,outcome_code='data_complete',completed_at=clock_timestamp(),
                updated_at=clock_timestamp()
            WHERE id=$1
            """, evdsWindow);
        await ExecuteAsync(connection, transaction, """
            UPDATE public.ingestion_jobs SET status='success',finished_at=clock_timestamp(),
                records_upserted=1,outcome_code='data_complete' WHERE id=$1
            """, evdsJob);

        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates DISABLE TRIGGER trg_inflation_rates_ingestion_fence");
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates DROP CONSTRAINT chk_inflation_rates_numeric");
        await ExecuteAsync(connection, transaction, """
            UPDATE public.inflation_rates SET index_value=-1,updated_at=clock_timestamp()
            WHERE period_date=$1 AND source='seed-approximation'
            """, evdsDate);
        await ExecuteAsync(connection, transaction,
            "DELETE FROM public.inflation_rates WHERE period_date=$1 AND source='tuik'", evdsDate);
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates ENABLE ALWAYS TRIGGER trg_inflation_rates_ingestion_fence");
        await ExecuteAsync(connection, transaction, """
            ALTER TABLE public.inflation_rates ADD CONSTRAINT chk_inflation_rates_numeric CHECK (
              index_value::text NOT IN ('NaN','Infinity','-Infinity') AND index_value>0
              AND EXTRACT(day FROM period_date)=1) NOT VALID
            """);

        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.ingestion_windows DISABLE TRIGGER trg_ingestion_window_calendar_release");
        var calendarWindow = await ScalarAsync<Guid>(connection, transaction, """
            INSERT INTO public.ingestion_windows(
                source,asset_id,job_type,range_start,range_end,contract_version,state,
                requested_calendar_count,expected_observation_count,raw_item_count,
                accepted_distinct_count,rejected_count,expected_no_data_count,
                outcome_code,completed_at,calendar_release_id)
            VALUES ('tcmb',$1,'historical_backfill',$2,$2,2,'expected_no_data',
                    1,0,0,0,0,1,'calendar_closed',clock_timestamp(),$3)
            RETURNING id
            """, tcmbAsset, uncoveredDate, calendarRelease);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO public.ingestion_jobs(
                asset_id,job_type,started_at,finished_at,status,records_upserted,
                date_range_start,date_range_end,source,window_id,outcome_code)
            VALUES ($1,'historical_backfill',clock_timestamp(),clock_timestamp(),'success',0,
                    $2,$2,'tcmb',$3,'calendar_closed')
            """, tcmbAsset, uncoveredDate, calendarWindow);
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.ingestion_windows ENABLE TRIGGER trg_ingestion_window_calendar_release");

        var expiredWindow = await ScalarAsync<Guid>(connection, transaction, """
            INSERT INTO public.ingestion_windows(
                source,asset_id,job_type,range_start,range_end,contract_version,state,
                lease_owner,lease_token,lease_until,attempt_count)
            VALUES ('coingecko',$1,'daily_update',$2,$2,1,'running',
                    'dead-owner',$3,clock_timestamp()-interval '1 minute',1)
            RETURNING id
            """, AssetId, expiredDate, Guid.NewGuid());
        await ExecuteAsync(connection, transaction, """
            INSERT INTO public.ingestion_jobs(
                asset_id,job_type,status,date_range_start,date_range_end,source,window_id)
            VALUES ($1,'daily_update','running',$2,$2,'twelvedata',$3)
            """, AssetId, expiredDate, expiredWindow);

        await ExecuteAsync(connection, transaction,
            "DROP TRIGGER trg_price_points_ingestion_fence ON public.price_points");
        await ExecuteAsync(connection, transaction, """
            INSERT INTO public.price_points(asset_id,price_date,close,source_raw,ingested_at)
            VALUES ($1,$2,102,'{"provider":"legacy"}'::jsonb,clock_timestamp())
            ON CONFLICT(asset_id,price_date) DO UPDATE
              SET close=excluded.close,source_raw=excluded.source_raw,ingested_at=excluded.ingested_at
            """, AssetId, unattestedDate);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO public.ingestion_jobs(
                asset_id,job_type,started_at,finished_at,status,records_upserted,
                date_range_start,date_range_end,source,window_id,outcome_code)
            VALUES ($1,'daily_update',clock_timestamp(),clock_timestamp(),'success',1,
                    $2,$2,'coingecko',NULL,'legacy_success')
            """, AssetId, unattestedDate);
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates ENABLE ALWAYS TRIGGER trg_inflation_rates_authority");
        await transaction.CommitAsync();
        _anomalyWindowIds = [evdsWindow, calendarWindow, expiredWindow];
        _anomalyUnattestedDate = unattestedDate;

        var baseline = await CreateManifestAsync();
        return baseline with
        {
            Scope = baseline.Scope with
            {
                Lanes =
                [
                    baseline.Scope.Lanes[0] with { Through = expiredDate },
                    new AuditLane("evds", null, "inflation_backfill", 1,
                        evdsDate, evdsDate, "month"),
                    new AuditLane("tcmb", tcmbAsset, "historical_backfill", 2,
                        uncoveredDate, uncoveredDate, "day"),
                    new AuditLane("coingecko", AssetId, "daily_update", 1,
                        expiredDate, expiredDate, "day"),
                    new AuditLane("coingecko", AssetId, "daily_update", 1,
                        unattestedDate, unattestedDate, "day"),
                ],
            },
        };
    }

    internal async Task CleanupAnomalyMatrixAsync()
    {
        if (_anomalyWindowIds.Length != 3)
            throw new InvalidOperationException("Anomaly fixture identity is incomplete.");
        await using var connection = new NpgsqlConnection(_environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction,
            """
            CREATE TRIGGER trg_price_points_ingestion_fence
            BEFORE INSERT OR UPDATE ON public.price_points
            FOR EACH ROW EXECUTE FUNCTION public.enforce_price_point_ingestion_fence()
            """);
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates DISABLE TRIGGER trg_inflation_rates_ingestion_fence");
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates DISABLE TRIGGER trg_inflation_rates_authority");
        await ExecuteAsync(connection, transaction, """
            UPDATE public.inflation_rates SET index_value=158.3700,updated_at=clock_timestamp()
            WHERE period_date='2010-01-01' AND source='seed-approximation'
            """);
        await ExecuteAsync(connection, transaction,
            "DELETE FROM public.inflation_rates WHERE period_date='2010-01-01' AND source='tuik'");
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates ENABLE ALWAYS TRIGGER trg_inflation_rates_ingestion_fence");
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates ENABLE ALWAYS TRIGGER trg_inflation_rates_authority");
        await ExecuteAsync(connection, transaction, """
            DELETE FROM public.price_points
            WHERE asset_id=$1 AND price_date=$2
            """, AssetId, _anomalyUnattestedDate);
        await ExecuteAsync(connection, transaction, """
            DELETE FROM public.ingestion_jobs
            WHERE window_id=ANY($1::uuid[])
               OR (window_id IS NULL AND source='coingecko' AND asset_id=$2
                   AND date_range_start=$3)
            """, _anomalyWindowIds, AssetId, _anomalyUnattestedDate);
        await ExecuteAsync(connection, transaction, """
            DELETE FROM public.ingestion_windows
            WHERE id=ANY($1::uuid[])
            """, _anomalyWindowIds);
        await transaction.CommitAsync();
        _anomalyWindowIds = [];
    }

    public async Task<NpgsqlConnection> OpenAuditConnectionAsync()
    {
        var password = SecureSecretFile.ReadPassword(AuditPasswordFile);
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = _environment.ExpectedHost,
            Database = _environment.DatabaseName,
            Username = _environment.AuditLogin,
            Password = password,
            Pooling = false,
            IncludeErrorDetail = false,
            LogParameters = false,
        }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    internal async Task<NpgsqlConnection> OpenAdminConnectionAsync()
    {
        var connection = new NpgsqlConnection(_environment.AdminConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    internal async Task<AuthorityAnomaly> CreateAuthorityEvidenceAnomalyAsync(string provider)
    {
        var ordinal = provider switch
        {
            "coingecko" => 0,
            "tcmb" => 1,
            "openexchangerates" => 2,
            "twelvedata" => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        var date = new DateOnly(2024, 7, 1).AddDays(ordinal);
        var canary = provider == "openexchangerates"
            ? "Authorization Bearer TOP-SECRET-AUTHORITY-CANARY"
            : string.Empty;
        await using var admin = new NpgsqlConnection(_environment.AdminConnectionString);
        await admin.OpenAsync();
        var (assetId, sourceId) = await ReadProviderAssetAsync(admin, provider);
        var asOf = provider == "twelvedata"
            ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)).ToUniversalTime()
            : new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var observationId = provider switch
        {
            "coingecko" => $"coingecko:{sourceId}:try:{asOf.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}",
            "tcmb" => $"tcmb:{TcmbCurrency(sourceId)}:{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}:forex_buying",
            "openexchangerates" => $"openexchangerates:{sourceId}:{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            "twelvedata" => $"twelvedata:{sourceId}:{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}:1day",
            _ => throw new InvalidOperationException("Unsupported provider fixture."),
        };
        var evidence = new JsonObject
        {
            ["as_of_at"] = asOf.ToString("O", CultureInfo.InvariantCulture),
            ["close"] = 100m,
            ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["observation_id"] = observationId,
            ["provider_source"] = provider,
        };
        switch (provider)
        {
            case "coingecko":
                evidence["quote_currency"] = "TRY";
                evidence["source_timestamp_ms"] = asOf.ToUnixTimeMilliseconds();
                evidence["symbol"] = sourceId;
                evidence["as_of_at"] = "malformed-timestamp";
                break;
            case "tcmb":
                evidence["currency"] = "WRONG";
                evidence["unit"] = 1m;
                break;
            case "openexchangerates":
                evidence["base_currency"] = "USD";
                evidence["quote_currency"] = "TRY";
                evidence["symbol"] = canary;
                evidence["unit"] = "gram";
                break;
            case "twelvedata":
                evidence["currency"] = "TRY";
                evidence["exchange"] = "BIST";
                evidence["exchange_timezone"] = "Europe/Istanbul";
                evidence["high"] = 110m;
                evidence["instrument_type"] = "Common Stock";
                evidence["interval"] = "1day";
                evidence["low"] = 90m;
                evidence["mic_code"] = "WRONG";
                evidence["open"] = 100m;
                evidence["symbol"] = sourceId.Split(':', 2)[0];
                evidence["volume"] = 1_000m;
                break;
        }
        var raw = evidence.ToJsonString();
        var observationSha = await ScalarAsync<byte[]>(admin, """
            SELECT sha256(convert_to(public.saydin_canonical_observation($1::jsonb)::text,'UTF8'))
            """, raw);
        var payloadSha = SHA256.HashData(Encoding.UTF8.GetBytes($"audit-authority:{provider}:{date:yyyy-MM-dd}"));

        await using var transaction = await admin.BeginTransactionAsync();
        await ExecuteAsync(admin, transaction, "SET LOCAL session_replication_role=replica");
        if (provider == "openexchangerates")
            await ExecuteAsync(admin, transaction,
                "ALTER TABLE public.price_points DROP CONSTRAINT chk_price_points_authority_tuple");
        var windowId = await ScalarAsync<Guid>(admin, transaction, """
            INSERT INTO public.ingestion_windows(
              source,asset_id,job_type,range_start,range_end,contract_version,state,
              requested_calendar_count,expected_observation_count,raw_item_count,
              accepted_distinct_count,rejected_count,expected_no_data_count,outcome_code,completed_at)
            VALUES ($1,$2,'historical_backfill',$3,$3,1,'succeeded',1,1,1,1,0,0,
                    'data_complete',clock_timestamp()) RETURNING id
            """, provider, assetId, date);
        await ExecuteAsync(admin, transaction, """
            INSERT INTO public.ingestion_jobs(
              asset_id,job_type,started_at,finished_at,status,records_upserted,
              date_range_start,date_range_end,source,window_id,outcome_code)
            VALUES ($1,'historical_backfill',clock_timestamp(),clock_timestamp(),'success',1,
                    $2,$2,$3,$4,'data_complete')
            """, assetId, date, provider, windowId);
        await ExecuteAsync(admin, transaction, """
            INSERT INTO public.price_points(
              asset_id,price_date,close,open,high,low,volume,provider_source,
              source_observation_id,as_of_at,price_kind,is_final,observation_sha256,
              authority_contract_version,source_raw)
            VALUES ($1,$2,100,
                    CASE WHEN $3='twelvedata' THEN 100 ELSE NULL END,
                    CASE WHEN $3='twelvedata' THEN 110 ELSE NULL END,
                    CASE WHEN $3='twelvedata' THEN 90 ELSE NULL END,
                    CASE WHEN $3='twelvedata' THEN 1000 ELSE NULL END,
                    $3,$4,$5,CASE $3 WHEN 'tcmb' THEN 'official_reference'
                      WHEN 'coingecko' THEN 'daily_utc_reference'
                      WHEN 'openexchangerates' THEN 'daily_reference' ELSE 'daily_close' END,
                    true,$6,1,$7::jsonb)
            """, assetId, date, provider, observationId, asOf, observationSha, raw);
        await ExecuteAsync(admin, transaction, """
            INSERT INTO public.provider_fetch_payloads(provider_source,payload_sha256,payload_byte_length)
            VALUES ($1,$2,1024)
            """, provider, payloadSha);
        await ExecuteAsync(admin, transaction, """
            INSERT INTO public.price_observation_attributions(
              asset_id,price_date,ingestion_window_id,provider_source,payload_sha256,
              source_observation_id,observation_sha256,authority_contract_version)
            VALUES ($1,$2,$3,$4,$5,$6,$7,1)
            """, assetId, date, windowId, provider, payloadSha, observationId, observationSha);
        if (provider == "openexchangerates")
            await RestorePriceAuthorityTupleAsync(admin, transaction);
        await transaction.CommitAsync();

        var baseline = await CreateManifestAsync();
        var manifest = baseline with
        {
            Scope = baseline.Scope with
            {
                Lanes = [new AuditLane(provider, assetId, "historical_backfill", 1, date, date, "day")],
            },
        };
        return new AuthorityAnomaly(manifest, windowId, provider, payloadSha, canary);
    }

    internal async Task CleanupAuthorityEvidenceAnomalyAsync(AuthorityAnomaly anomaly)
    {
        await ExecuteAdminTransactionAsync(
            ("SET LOCAL session_replication_role=replica", []),
            ("DELETE FROM public.price_observation_attributions WHERE ingestion_window_id=$1", [anomaly.WindowId]),
            ("DELETE FROM public.provider_fetch_payloads WHERE provider_source=$1 AND payload_sha256=$2",
                [anomaly.Provider, anomaly.PayloadSha256]),
            ("DELETE FROM public.price_points WHERE asset_id=(SELECT asset_id FROM public.ingestion_windows WHERE id=$1) AND price_date=(SELECT range_start FROM public.ingestion_windows WHERE id=$1)", [anomaly.WindowId]),
            ("DELETE FROM public.ingestion_jobs WHERE window_id=$1", [anomaly.WindowId]),
            ("DELETE FROM public.ingestion_windows WHERE id=$1", [anomaly.WindowId]));
    }

    internal async Task<InflationAuthorityAnomaly> CreateInflationAuthorityAnomalyAsync()
    {
        var date = new DateOnly(2014, 1, 1);
        var asOf = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var observationId = $"evds:TP_FG_J0:{date.ToString("yyyy-MM", CultureInfo.InvariantCulture)}";
        var evidence = new JsonObject
        {
            ["as_of_at"] = "malformed-timestamp",
            ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["index_value"] = 158.37m,
            ["observation_id"] = observationId,
            ["provider_source"] = "evds",
            ["series"] = "TP.FG.J0",
        };
        var raw = evidence.ToJsonString();
        var payloadSha = SHA256.HashData(Encoding.UTF8.GetBytes("audit-authority:evds:2014-01"));
        await using var admin = new NpgsqlConnection(_environment.AdminConnectionString);
        await admin.OpenAsync();
        var observationSha = await ScalarAsync<byte[]>(admin, """
            SELECT sha256(convert_to(public.saydin_canonical_observation($1::jsonb)::text,'UTF8'))
            """, raw);
        await using var transaction = await admin.BeginTransactionAsync();
        await ExecuteAsync(admin, transaction, "SET LOCAL session_replication_role=replica");
        var windowId = await ScalarAsync<Guid>(admin, transaction, """
            INSERT INTO public.ingestion_windows(
              source,asset_id,job_type,range_start,range_end,contract_version,state,
              requested_calendar_count,expected_observation_count,raw_item_count,
              accepted_distinct_count,rejected_count,expected_no_data_count,outcome_code,completed_at)
            VALUES ('evds',NULL,'inflation_backfill',$1,$1,1,'succeeded',1,1,1,1,0,0,
                    'data_complete',clock_timestamp()) RETURNING id
            """, date);
        await ExecuteAsync(admin, transaction, """
            INSERT INTO public.ingestion_jobs(
              asset_id,job_type,started_at,finished_at,status,records_upserted,
              date_range_start,date_range_end,source,window_id,outcome_code)
            VALUES (NULL,'inflation_backfill',clock_timestamp(),clock_timestamp(),'success',1,
                    $1,$1,'evds',$2,'data_complete')
            """, date, windowId);
        await ExecuteAsync(admin, transaction,
            "ALTER TABLE public.inflation_rates DISABLE TRIGGER trg_inflation_rates_ingestion_fence");
        await ExecuteAsync(admin, transaction,
            "ALTER TABLE public.inflation_rates DISABLE TRIGGER trg_inflation_rates_authority");
        await ExecuteAsync(admin, transaction, """
            INSERT INTO public.inflation_rates(
              period_date,index_value,source,provider_source,source_observation_id,as_of_at,
              price_kind,is_final,observation_sha256,authority_contract_version,source_raw)
            VALUES ($1,158.37,'tuik','evds',$2,$3,'cpi_index',true,$4,1,$5::jsonb)
            """, date, observationId, asOf, observationSha, raw);
        await ExecuteAsync(admin, transaction,
            "ALTER TABLE public.inflation_rates ENABLE ALWAYS TRIGGER trg_inflation_rates_authority");
        await ExecuteAsync(admin, transaction,
            "ALTER TABLE public.inflation_rates ENABLE ALWAYS TRIGGER trg_inflation_rates_ingestion_fence");
        await ExecuteAsync(admin, transaction, """
            INSERT INTO public.provider_fetch_payloads(provider_source,payload_sha256,payload_byte_length)
            VALUES ('evds',$1,1024)
            """, payloadSha);
        await ExecuteAsync(admin, transaction, """
            INSERT INTO public.inflation_observation_attributions(
              period_date,source,ingestion_window_id,provider_source,payload_sha256,
              source_observation_id,observation_sha256,authority_contract_version)
            VALUES ($1,'tuik',$2,'evds',$3,$4,$5,1)
            """, date, windowId, payloadSha, observationId, observationSha);
        await transaction.CommitAsync();
        var baseline = await CreateManifestAsync();
        return new InflationAuthorityAnomaly(baseline with
        {
            Scope = baseline.Scope with
            {
                Lanes = [new AuditLane("evds", null, "inflation_backfill", 1, date, date, "month")],
            },
        }, windowId, payloadSha);
    }

    internal async Task CleanupInflationAuthorityAnomalyAsync(InflationAuthorityAnomaly anomaly)
    {
        await ExecuteAdminTransactionAsync(
            ("SET LOCAL session_replication_role=replica", []),
            ("DELETE FROM public.inflation_observation_attributions WHERE ingestion_window_id=$1", [anomaly.WindowId]),
            ("DELETE FROM public.provider_fetch_payloads WHERE provider_source='evds' AND payload_sha256=$1",
                [anomaly.PayloadSha256]),
            ("DELETE FROM public.inflation_rates WHERE period_date=(SELECT range_start FROM public.ingestion_windows WHERE id=$1) AND source='tuik'", [anomaly.WindowId]),
            ("DELETE FROM public.ingestion_jobs WHERE window_id=$1", [anomaly.WindowId]),
            ("DELETE FROM public.ingestion_windows WHERE id=$1", [anomaly.WindowId]));
    }

    private static async Task<(Guid AssetId, string SourceId)> ReadProviderAssetAsync(
        NpgsqlConnection connection,
        string provider)
    {
        await using var command = new NpgsqlCommand("""
            SELECT id,source_id FROM public.assets WHERE source=$1 AND is_active ORDER BY symbol LIMIT 1
            """, connection);
        command.Parameters.AddWithValue(provider);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Provider fixture asset is missing.");
        return (reader.GetGuid(0), reader.GetString(1));
    }

    private static string TcmbCurrency(string sourceId)
    {
        var parts = sourceId.Split('.');
        return parts.Length >= 3 ? parts[2] : sourceId;
    }

    private static Task RestorePriceAuthorityTupleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) => ExecuteAsync(connection, transaction, """
        ALTER TABLE public.price_points ADD CONSTRAINT chk_price_points_authority_tuple CHECK (
          (provider_source IS NULL AND source_observation_id IS NULL AND as_of_at IS NULL
           AND price_kind IS NULL AND is_final IS NULL AND observation_sha256 IS NULL
           AND authority_contract_version IS NULL)
          OR (provider_source IS NOT NULL AND source_observation_id IS NOT NULL AND as_of_at IS NOT NULL
           AND price_kind IS NOT NULL AND is_final IS TRUE AND observation_sha256 IS NOT NULL
           AND authority_contract_version>0 AND source_raw IS NOT NULL
           AND octet_length(source_observation_id) BETWEEN 1 AND 256
           AND octet_length(observation_sha256)=32
           AND observation_sha256<>decode(repeat('00',32),'hex')
           AND public.saydin_source_raw_allowed(source_raw)
           AND source_raw->>'provider_source'=provider_source
           AND source_raw->>'observation_id'=source_observation_id
           AND observation_sha256=sha256(convert_to(
               public.saydin_canonical_observation(source_raw)::text,'UTF8')))
        ) NOT VALID
        """);

    internal async Task GrantAuditRolePrivilegeAsync(string table, string privilege, bool grant)
    {
        var allowed = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["users"] = ["INSERT"],
            ["saved_scenarios"] = ["UPDATE"],
            ["activity_logs"] = ["DELETE"],
            ["market_holidays"] = ["TRUNCATE"],
            ["market_calendars"] = ["INSERT"],
        };
        if (!allowed.TryGetValue(table, out var privileges) || !privileges.Contains(privilege))
            throw new ArgumentOutOfRangeException(nameof(table));
        await using var admin = new NpgsqlConnection(_environment.AdminConnectionString);
        await admin.OpenAsync();
        var quotedRole = new NpgsqlCommandBuilder().QuoteIdentifier(_roleName);
        var quotedTable = new NpgsqlCommandBuilder().QuoteIdentifier(table);
        var verb = grant ? "GRANT" : "REVOKE";
        var direction = grant ? "TO" : "FROM";
        await using var command = new NpgsqlCommand(
            $"{verb} {privilege} ON TABLE public.{quotedTable} {direction} {quotedRole}", admin);
        await command.ExecuteNonQueryAsync();
    }

    internal Task SetFetchPayloadLeaseTriggerEnabledAsync(bool enabled) => ExecuteAdminTransactionAsync(
        ($"ALTER TABLE public.provider_fetch_payloads {(enabled ? "ENABLE" : "DISABLE")} TRIGGER trg_fetch_payload_live_lease", []));

    internal async Task<string?> ApplyAuthorityFunctionDriftAsync(string drift)
    {
        switch (drift)
        {
            case "replacement":
                {
                    await using var admin = await OpenAdminConnectionAsync();
                    var original = await ScalarAsync<string>(admin,
                        "SELECT pg_get_functiondef('public.saydin_source_raw_allowed(jsonb)'::regprocedure)");
                    await ExecuteAdminTransactionAsync(("""
                    CREATE OR REPLACE FUNCTION public.saydin_source_raw_allowed(payload jsonb)
                    RETURNS boolean LANGUAGE sql IMMUTABLE STRICT
                    SET search_path=pg_catalog,pg_temp AS $$ SELECT true $$
                    """, []));
                    return original;
                }
            case "wrong_overload":
                await ExecuteAdminTransactionAsync(("""
                    CREATE FUNCTION public.saydin_source_raw_allowed(payload text)
                    RETURNS boolean LANGUAGE sql IMMUTABLE STRICT
                    SET search_path=pg_catalog,pg_temp AS $$ SELECT true $$
                    """, []));
                return null;
            case "public_execute":
                await ExecuteAdminTransactionAsync((
                    "GRANT EXECUTE ON FUNCTION public.saydin_source_raw_allowed(jsonb) TO PUBLIC", []));
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(drift));
        }
    }

    internal Task RestoreAuthorityFunctionDriftAsync(string drift, string? originalDefinition) => drift switch
    {
        "replacement" when originalDefinition is not null =>
            ExecuteAdminTransactionAsync((originalDefinition, [])),
        "wrong_overload" => ExecuteAdminTransactionAsync((
            "DROP FUNCTION public.saydin_source_raw_allowed(text)", [])),
        "public_execute" => ExecuteAdminTransactionAsync((
            "REVOKE ALL ON FUNCTION public.saydin_source_raw_allowed(jsonb) FROM PUBLIC", [])),
        _ => throw new ArgumentOutOfRangeException(nameof(drift)),
    };

    internal async Task<string?> ApplyApiTrustStructureDriftAsync(string drift)
    {
        switch (drift)
        {
            case "user_default":
                await ExecuteAdminTransactionAsync((
                    "ALTER TABLE public.users ALTER COLUMN principal_status SET DEFAULT 'active'", []));
                return null;
            case "raw_secret_column":
                await ExecuteAdminTransactionAsync((
                    "ALTER TABLE public.installation_credentials ADD COLUMN raw_secret text DEFAULT 'TOP-SECRET-INSTALLATION-CANARY'", []));
                return null;
            case "function_replacement":
                {
                    await using var admin = await OpenAdminConnectionAsync();
                    var original = await ScalarAsync<string>(admin, """
                    SELECT pg_get_functiondef(
                      'public.resolve_installation(bytea,smallint)'::regprocedure)
                    """);
                    await ExecuteAdminTransactionAsync(("""
                    CREATE OR REPLACE FUNCTION public.resolve_installation(
                        p_secret_hash bytea,p_key_version smallint)
                    RETURNS TABLE(
                        principal_id uuid,credential_id uuid,generation integer,tier varchar,
                        principal_status varchar,credential_state varchar)
                    LANGUAGE sql STABLE SECURITY DEFINER
                    SET search_path=pg_catalog,pg_temp
                    AS $$ SELECT NULL::uuid,NULL::uuid,NULL::integer,NULL::varchar,
                                 NULL::varchar,NULL::varchar WHERE false $$
                    """, []));
                    return original;
                }
            case "wrong_overload":
                await ExecuteAdminTransactionAsync(("""
                    CREATE FUNCTION public.resolve_installation(payload text,key_version smallint)
                    RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER
                    SET search_path=pg_catalog,pg_temp AS $$ SELECT false $$
                    """, []));
                return null;
            case "public_execute":
                await ExecuteAdminTransactionAsync((
                    "GRANT EXECUTE ON FUNCTION public.resolve_installation(bytea,smallint) TO PUBLIC", []));
                return null;
            case "trigger_disabled":
                await ExecuteAdminTransactionAsync(("""
                    ALTER TABLE public.assets DISABLE TRIGGER trg_asset_catalog_revision_insert
                    """, []));
                return null;
            case "table_grant":
                await ExecuteAdminTransactionAsync((await ApiTrustGrantSqlAsync(
                    "GRANT SELECT ON public.installation_credentials TO {0}"), []));
                return null;
            case "column_grant":
                await ExecuteAdminTransactionAsync((await ApiTrustGrantSqlAsync(
                    "GRANT SELECT(secret_hash) ON public.installation_credentials TO {0}"), []));
                return null;
            case "catalog_select_revoked":
                await ExecuteAdminTransactionAsync((await ApiTrustGrantSqlAsync(
                    "REVOKE SELECT ON public.asset_catalog_state FROM {0}"), []));
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(drift));
        }
    }

    internal Task RestoreApiTrustStructureDriftAsync(string drift, string? originalDefinition) =>
        drift switch
        {
            "user_default" => ExecuteAdminTransactionAsync(("""
                ALTER TABLE public.users ALTER COLUMN principal_status
                  SET DEFAULT 'legacy_quarantined'
                """, [])),
            "raw_secret_column" => ExecuteAdminTransactionAsync((
                "ALTER TABLE public.installation_credentials DROP COLUMN raw_secret", [])),
            "function_replacement" when originalDefinition is not null =>
                ExecuteAdminTransactionAsync((originalDefinition, [])),
            "wrong_overload" => ExecuteAdminTransactionAsync((
                "DROP FUNCTION public.resolve_installation(text,smallint)", [])),
            "public_execute" => ExecuteAdminTransactionAsync((
                "REVOKE ALL ON FUNCTION public.resolve_installation(bytea,smallint) FROM PUBLIC", [])),
            "trigger_disabled" => ExecuteAdminTransactionAsync(("""
                ALTER TABLE public.assets ENABLE TRIGGER trg_asset_catalog_revision_insert
                """, [])),
            "table_grant" => RestoreApiTrustGrantAsync(
                "REVOKE ALL ON public.installation_credentials FROM {0}"),
            "column_grant" => RestoreApiTrustGrantAsync(
                "REVOKE SELECT(secret_hash) ON public.installation_credentials FROM {0}"),
            "catalog_select_revoked" => RestoreApiTrustGrantAsync(
                "GRANT SELECT ON public.asset_catalog_state TO {0}"),
            _ => throw new ArgumentOutOfRangeException(nameof(drift)),
        };

    internal async Task<string?> ApplyPrincipalRetentionStructureDriftAsync(string drift)
    {
        switch (drift)
        {
            case "function_replacement":
                await using (var admin = await OpenAdminConnectionAsync())
                {
                    var original = await ScalarAsync<string>(admin, """
                        SELECT pg_get_functiondef(
                          'public.redact_activity_logs_before_principal_delete()'::regprocedure)
                        """);
                    await ExecuteAdminTransactionAsync(("""
                        CREATE OR REPLACE FUNCTION
                            public.redact_activity_logs_before_principal_delete()
                        RETURNS trigger LANGUAGE plpgsql VOLATILE SECURITY DEFINER
                        SET search_path=pg_catalog,pg_temp
                        AS $$ BEGIN RETURN OLD; END $$
                        """, []));
                    return original;
                }
            case "public_execute":
                await ExecuteAdminTransactionAsync(("""
                    GRANT EXECUTE ON FUNCTION
                      public.redact_activity_logs_before_principal_delete() TO PUBLIC
                    """, []));
                return null;
            case "trigger_disabled":
                await ExecuteAdminTransactionAsync(("""
                    ALTER TABLE public.users
                      DISABLE TRIGGER trg_users_principal_retention_redact
                    """, []));
                return null;
            case "foreign_key_action":
                await ExecuteAdminTransactionAsync(("""
                    UPDATE pg_catalog.pg_constraint SET confdeltype='n'
                     WHERE conrelid='public.activity_logs'::regclass
                       AND conname='activity_logs_user_id_fkey'
                    """, []));
                return null;
            case "scheduler_acl_removed":
                await ExecuteAdminTransactionAsync(("""
                    DO $drift$
                    DECLARE scheduler_role text;
                    BEGIN
                      SELECT timescale_scheduler_role INTO STRICT scheduler_role
                        FROM public.saydin_role_contract WHERE singleton=1;
                      EXECUTE format('REVOKE UPDATE ON public.activity_logs FROM %I',
                                     scheduler_role);
                    END
                    $drift$
                    """, []));
                return null;
            case "broad_owner_update":
                await ExecuteAdminTransactionAsync(("""
                    DO $drift$
                    DECLARE owner_role text;
                    BEGIN
                      SELECT contract.owner_role INTO STRICT owner_role
                        FROM public.saydin_role_contract contract WHERE singleton=1;
                      EXECUTE format('GRANT UPDATE ON public.activity_logs TO %I',owner_role);
                    END
                    $drift$
                    """, []));
                return null;
            case "compressed_chunk_acl":
                await ExecuteAdminTransactionAsync(("""
                    INSERT INTO public.activity_logs(
                        id,user_id,device_id,action,status_code,created_at)
                    VALUES ('a0220000-0000-7000-8000-000000000902',NULL,
                            'dqa-compressed-acl-drift','config_fetch',200,
                            '2022-01-03T12:00:00Z')
                    ON CONFLICT(id,created_at) DO NOTHING;
                    DO $drift$
                    DECLARE source_chunk regclass; compressed_relation regclass;
                            admin_role text:=current_user; scheduler_role text;
                            owner_role text;
                    BEGIN
                      SELECT contract.timescale_scheduler_role,contract.owner_role
                        INTO STRICT scheduler_role,owner_role
                        FROM public.saydin_role_contract contract WHERE singleton=1;
                      SELECT activity.tableoid INTO STRICT source_chunk
                        FROM public.activity_logs activity
                       WHERE activity.id='a0220000-0000-7000-8000-000000000902';
                      EXECUTE format('SET LOCAL ROLE %I',scheduler_role);
                      PERFORM public.compress_chunk(source_chunk,if_not_compressed=>true);
                      EXECUTE format('SET LOCAL ROLE %I',admin_role);
                      SELECT format('%I.%I',compressed_chunk.schema_name,
                                    compressed_chunk.table_name)::regclass
                        INTO STRICT compressed_relation
                        FROM _timescaledb_catalog.chunk source
                        JOIN _timescaledb_catalog.chunk compressed_chunk
                          ON compressed_chunk.id=source.compressed_chunk_id
                       WHERE format('%I.%I',source.schema_name,source.table_name)::regclass=
                             source_chunk;
                      EXECUTE format('GRANT UPDATE ON %s TO %I',
                                     compressed_relation,owner_role);
                    END
                    $drift$
                    """, []));
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(drift));
        }
    }

    internal Task RestorePrincipalRetentionStructureDriftAsync(
        string drift,
        string? originalDefinition) => drift switch
        {
            "function_replacement" when originalDefinition is not null =>
                ExecuteAdminTransactionAsync((originalDefinition, [])),
            "public_execute" => ExecuteAdminTransactionAsync(("""
                REVOKE ALL ON FUNCTION
                  public.redact_activity_logs_before_principal_delete() FROM PUBLIC
                """, [])),
            "trigger_disabled" => ExecuteAdminTransactionAsync(("""
                ALTER TABLE public.users
                  ENABLE TRIGGER trg_users_principal_retention_redact
                """, [])),
            "foreign_key_action" => ExecuteAdminTransactionAsync(("""
                UPDATE pg_catalog.pg_constraint SET confdeltype='a'
                 WHERE conrelid='public.activity_logs'::regclass
                   AND conname='activity_logs_user_id_fkey'
                """, [])),
            "scheduler_acl_removed" => ExecuteAdminTransactionAsync(("""
                DO $restore$
                DECLARE scheduler_role text;
                BEGIN
                  SELECT timescale_scheduler_role INTO STRICT scheduler_role
                    FROM public.saydin_role_contract WHERE singleton=1;
                  EXECUTE format('GRANT UPDATE ON public.activity_logs TO %I',scheduler_role);
                END
                $restore$
                """, [])),
            "broad_owner_update" => ExecuteAdminTransactionAsync(("""
                DO $restore$
                DECLARE owner_role text;
                BEGIN
                  SELECT contract.owner_role INTO STRICT owner_role
                    FROM public.saydin_role_contract contract WHERE singleton=1;
                  EXECUTE format('REVOKE UPDATE ON public.activity_logs FROM %I',owner_role);
                END
                $restore$
                """, [])),
            "compressed_chunk_acl" => ExecuteAdminTransactionAsync(("""
                DO $restore$
                DECLARE source_chunk regclass; compressed_relation regclass;
                        owner_role text;
                BEGIN
                  SELECT contract.owner_role INTO STRICT owner_role
                    FROM public.saydin_role_contract contract WHERE singleton=1;
                  SELECT activity.tableoid INTO STRICT source_chunk
                    FROM public.activity_logs activity
                   WHERE activity.id='a0220000-0000-7000-8000-000000000902';
                  SELECT format('%I.%I',compressed_chunk.schema_name,
                                compressed_chunk.table_name)::regclass
                    INTO STRICT compressed_relation
                    FROM _timescaledb_catalog.chunk source
                    JOIN _timescaledb_catalog.chunk compressed_chunk
                      ON compressed_chunk.id=source.compressed_chunk_id
                   WHERE format('%I.%I',source.schema_name,source.table_name)::regclass=
                         source_chunk;
                  EXECUTE format('REVOKE UPDATE ON %s FROM %I',
                                 compressed_relation,owner_role);
                END
                $restore$
                """, [])),
            _ => throw new ArgumentOutOfRangeException(nameof(drift)),
        };

    internal Task ApplyMalformedAssetCatalogEvidenceAsync() => ExecuteAdminTransactionAsync(
        ("ALTER TABLE public.asset_catalog_state DROP CONSTRAINT chk_asset_catalog_state_sha256", []),
        ("UPDATE public.asset_catalog_state SET catalog_sha256=decode('01','hex') WHERE singleton=1", []));

    internal Task RestoreAssetCatalogEvidenceAsync() => ExecuteAdminTransactionAsync(
        ("""
         UPDATE public.asset_catalog_state
            SET catalog_sha256=public.compute_asset_catalog_sha256(),
                updated_at=clock_timestamp()
          WHERE singleton=1
         """, []),
        ("""
         ALTER TABLE public.asset_catalog_state
           ADD CONSTRAINT chk_asset_catalog_state_sha256
           CHECK(octet_length(catalog_sha256)=32)
         """, []));

    private async Task<string> ApiTrustGrantSqlAsync(string format)
    {
        await using var admin = await OpenAdminConnectionAsync();
        var role = await ScalarAsync<string>(admin, """
            SELECT audit_capability_role
              FROM public.saydin_role_contract
             WHERE singleton=1 AND database_name=current_database()
            """);
        return string.Format(CultureInfo.InvariantCulture, format,
            new NpgsqlCommandBuilder().QuoteIdentifier(role));
    }

    private async Task RestoreApiTrustGrantAsync(string format) =>
        await ExecuteAdminTransactionAsync((await ApiTrustGrantSqlAsync(format), []));

    internal async Task ApplyAuthorityStructureDriftAsync(string drift)
    {
        var sql = drift switch
        {
            "wrong_relation" => """
                ALTER TABLE public.price_points DROP CONSTRAINT chk_price_points_authority_tuple;
                ALTER TABLE public.inflation_rates ADD CONSTRAINT chk_price_points_authority_tuple CHECK (true) NOT VALID
                """,
            "wrong_kind" => """
                ALTER TABLE public.provider_fetch_payloads DROP CONSTRAINT pk_provider_fetch_payloads CASCADE;
                ALTER TABLE public.provider_fetch_payloads ADD CONSTRAINT pk_provider_fetch_payloads
                  UNIQUE(provider_source,payload_sha256)
                """,
            "replaced_pk" => """
                ALTER TABLE public.price_observation_attributions
                  DROP CONSTRAINT pk_price_observation_attributions CASCADE;
                CREATE UNIQUE INDEX pk_price_observation_attributions
                  ON public.price_observation_attributions(price_date,asset_id,ingestion_window_id,payload_sha256)
                """,
            "fk_action" => """
                ALTER TABLE public.price_observation_attributions
                  DROP CONSTRAINT fk_price_attribution_window;
                ALTER TABLE public.price_observation_attributions
                  ADD CONSTRAINT fk_price_attribution_window FOREIGN KEY(ingestion_window_id)
                  REFERENCES public.ingestion_windows(id) ON DELETE CASCADE
                """,
            "foreign_table_grant" => await AuthorityGrantSqlAsync(
                "api_capability_role", "GRANT UPDATE ON public.provider_fetch_payloads TO {0}"),
            "column_grant" => await AuthorityGrantSqlAsync(
                "ingestion_capability_role",
                "GRANT INSERT(first_observed_at) ON public.provider_fetch_payloads TO {0}"),
            _ => throw new ArgumentOutOfRangeException(nameof(drift)),
        };
        await ExecuteAdminTransactionAsync((sql, []));
    }

    internal async Task RestoreAuthorityStructureDriftAsync(string drift)
    {
        if (drift == "wrong_relation")
        {
            await using var connection = new NpgsqlConnection(_environment.AdminConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction,
                "ALTER TABLE public.inflation_rates DROP CONSTRAINT chk_price_points_authority_tuple");
            await RestorePriceAuthorityTupleAsync(connection, transaction);
            await transaction.CommitAsync();
            return;
        }

        var sql = drift switch
        {
            "wrong_kind" => """
                ALTER TABLE public.provider_fetch_payloads DROP CONSTRAINT pk_provider_fetch_payloads;
                ALTER TABLE public.provider_fetch_payloads ADD CONSTRAINT pk_provider_fetch_payloads
                  PRIMARY KEY(provider_source,payload_sha256);
                ALTER TABLE public.price_observation_attributions
                  ADD CONSTRAINT fk_price_attribution_payload
                  FOREIGN KEY(provider_source,payload_sha256)
                  REFERENCES public.provider_fetch_payloads(provider_source,payload_sha256)
                  ON DELETE RESTRICT;
                ALTER TABLE public.inflation_observation_attributions
                  ADD CONSTRAINT fk_inflation_attribution_payload
                  FOREIGN KEY(provider_source,payload_sha256)
                  REFERENCES public.provider_fetch_payloads(provider_source,payload_sha256)
                  ON DELETE RESTRICT
                """,
            "replaced_pk" => """
                DROP INDEX public.pk_price_observation_attributions;
                ALTER TABLE public.price_observation_attributions
                  ADD CONSTRAINT pk_price_observation_attributions
                  PRIMARY KEY(asset_id,price_date,ingestion_window_id,payload_sha256)
                """,
            "fk_action" => """
                ALTER TABLE public.price_observation_attributions
                  DROP CONSTRAINT fk_price_attribution_window;
                ALTER TABLE public.price_observation_attributions
                  ADD CONSTRAINT fk_price_attribution_window FOREIGN KEY(ingestion_window_id)
                  REFERENCES public.ingestion_windows(id) ON DELETE RESTRICT
                """,
            "foreign_table_grant" => await AuthorityGrantSqlAsync(
                "api_capability_role", "REVOKE UPDATE ON public.provider_fetch_payloads FROM {0}"),
            "column_grant" => await AuthorityGrantSqlAsync(
                "ingestion_capability_role",
                "REVOKE INSERT(first_observed_at) ON public.provider_fetch_payloads FROM {0}"),
            _ => throw new ArgumentOutOfRangeException(nameof(drift)),
        };
        await ExecuteAdminTransactionAsync((sql, []));
    }

    private async Task<string> AuthorityGrantSqlAsync(string contractColumn, string format)
    {
        if (contractColumn is not ("api_capability_role" or "ingestion_capability_role"))
            throw new ArgumentOutOfRangeException(nameof(contractColumn));
        await using var admin = new NpgsqlConnection(_environment.AdminConnectionString);
        await admin.OpenAsync();
        var role = await ScalarAsync<string>(admin,
            $"SELECT {contractColumn} FROM public.saydin_role_contract WHERE singleton=1");
        return string.Format(CultureInfo.InvariantCulture, format,
            new NpgsqlCommandBuilder().QuoteIdentifier(role));
    }

    internal async Task SetBackupIdentityDriftAsync(string drift, bool enabled)
    {
        var contract = RoleContract.Create(
            _environment.DeploymentId,
            _environment.DatabaseName,
            _environment.SystemIdentifierSha256,
            _environment.RolePrefix);
        var backup = new NpgsqlCommandBuilder().QuoteIdentifier(
            contract.BackupLogin(1, _environment.BackupV1ValidUntilUtc).Name);
        var auditCapability = new NpgsqlCommandBuilder().QuoteIdentifier(
            contract.AuditCapability.Name);
        var database = new NpgsqlCommandBuilder().QuoteIdentifier(_environment.DatabaseName);
        var sql = (drift, enabled) switch
        {
            ("attribute", true) => $"ALTER ROLE {backup} NOREPLICATION",
            ("attribute", false) => $"ALTER ROLE {backup} REPLICATION",
            ("membership", true) => $"GRANT {auditCapability} TO {backup}",
            ("membership", false) => $"REVOKE {auditCapability} FROM {backup}",
            ("connect", true) => $"GRANT CONNECT ON DATABASE {database} TO {backup}",
            ("connect", false) => $"REVOKE CONNECT ON DATABASE {database} FROM {backup}",
            ("app_select", true) => $"GRANT SELECT ON public.assets TO {backup}",
            ("app_select", false) => $"REVOKE SELECT ON public.assets FROM {backup}",
            _ => throw new ArgumentOutOfRangeException(nameof(drift)),
        };
        await using var admin = new NpgsqlConnection(_environment.AdminConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand(sql, admin);
        await command.ExecuteNonQueryAsync();
    }

    private string? RuntimeEnvironment(string name) => name switch
    {
        "PGHOST" => _environment.ExpectedHost,
        "PGPORT" => "5432",
        "PGDATABASE" => _environment.DatabaseName,
        "PGUSER" => _environment.AuditLogin,
        "PGSSLMODE" => "disable",
        "SAYDIN_DEPLOYMENT_ID" => _environment.DeploymentId,
        "SAYDIN_DATABASE_SYSTEM_IDENTIFIER_SHA256" => _environment.SystemIdentifierSha256,
        "SAYDIN_DATABASE_ROLE_PREFIX" => _environment.RolePrefix,
        "SAYDIN_DATABASE_LOGIN_VERSION" => "1",
        "SAYDIN_AUDIT_DATABASE_PASSWORD_FILE" => _environment.AuditPasswordFile,
        "SAYDIN_BACKUP_V1_VALID_UNTIL" =>
            RoleContract.FormatBackupValidUntil(_environment.BackupV1ValidUntilUtc),
        _ => null,
    };

    internal async Task<(int Existing, int Selectable, int Unsafe)> ReadAuditTablePrivilegeSummaryAsync()
    {
        await using var connection = await OpenAuditConnectionAsync();
        await using var command = new NpgsqlCommand("""
            WITH required(name) AS (SELECT unnest($1::text[])), access AS (
                SELECT name, to_regclass('public.' || name) AS relation
                FROM required
            )
            SELECT count(*) FILTER (WHERE relation IS NOT NULL)::integer,
                   count(*) FILTER (WHERE has_table_privilege(current_user,relation,'SELECT'))::integer,
                   count(*) FILTER (WHERE pg_get_userbyid(c.relowner)=current_user
                       OR has_table_privilege(current_user,relation,'INSERT')
                       OR has_table_privilege(current_user,relation,'UPDATE')
                       OR has_table_privilege(current_user,relation,'DELETE')
                       OR has_table_privilege(current_user,relation,'TRUNCATE'))::integer
            FROM access LEFT JOIN pg_class c ON c.oid=access.relation
            """, connection);
        command.Parameters.AddWithValue(AuditRunner.RequiredTableNames.ToArray());
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    internal async Task SetCleanLaneNonTerminalAsync(string state)
    {
        if (state is not ("pending" or "permanent_failed"))
            throw new ArgumentOutOfRangeException(nameof(state));
        await using var connection = new NpgsqlConnection(_environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL session_replication_role=replica");
        await ExecuteAsync(connection, transaction,
            "DELETE FROM public.price_points WHERE asset_id=$1 AND price_date BETWEEN $2 AND $3",
            AssetId, From, Through);
        if (state == "pending")
        {
            await ExecuteAsync(connection, transaction, """
                UPDATE public.ingestion_windows
                   SET state='pending',lease_owner=NULL,lease_token=NULL,lease_until=NULL,
                       attempt_count=0,next_attempt_at=clock_timestamp(),outcome_code=NULL,
                       error_code=NULL,completed_at=NULL,updated_at=clock_timestamp()
                 WHERE source='coingecko' AND asset_id=$1 AND job_type='historical_backfill'
                   AND contract_version=1 AND range_start=$2 AND range_end=$3
                """, AssetId, From, Through);
        }
        else
        {
            await ExecuteAsync(connection, transaction, """
                UPDATE public.ingestion_windows
                   SET state='permanent_failed',lease_owner=NULL,lease_token=NULL,lease_until=NULL,
                       attempt_count=1,outcome_code='contract_failure',error_code='fixture_permanent',
                       completed_at=clock_timestamp(),updated_at=clock_timestamp()
                 WHERE source='coingecko' AND asset_id=$1 AND job_type='historical_backfill'
                   AND contract_version=1 AND range_start=$2 AND range_end=$3
                """, AssetId, From, Through);
        }
        await transaction.CommitAsync();
    }

    internal async Task RestoreCleanLaneAsync()
    {
        await using var admin = new NpgsqlConnection(_environment.AdminConnectionString);
        await admin.OpenAsync();
        await SeedCleanLaneAsync(admin);
    }

    internal async Task<AuditInputManifest> CreateOverlapShortLaneAsync()
    {
        var from = new DateOnly(2024, 2, 1);
        var through = new DateOnly(2024, 2, 10);
        await ExecuteAdminTransactionAsync(
            ("""
             INSERT INTO public.ingestion_windows(
                source,asset_id,job_type,range_start,range_end,contract_version,state)
             VALUES ('coingecko',$1,'audit_overlap',$2,$3,1,'pending'),
                    ('coingecko',$1,'audit_overlap',$4,$4,1,'pending')
             """, [AssetId, from, new DateOnly(2024, 2, 5), new DateOnly(2024, 2, 2)]));
        var baseline = await CreateManifestAsync();
        return baseline with
        {
            Scope = baseline.Scope with
            {
                Lanes = [new AuditLane("coingecko", AssetId, "audit_overlap", 1,
                    from, through, "day")],
            },
        };
    }

    internal async Task CleanupOverlapShortLaneAsync() =>
        await ExecuteAdminTransactionAsync(("""
            DELETE FROM public.ingestion_windows
             WHERE source='coingecko' AND asset_id=$1 AND job_type='audit_overlap'
            """, [AssetId]));

    internal async Task ApplyWrongTypePriceFenceAndBypassInsertAsync()
    {
        await ExecuteAdminTransactionAsync(
            ("SET LOCAL session_replication_role=replica", []),
            ("DROP TRIGGER trg_price_points_ingestion_fence ON public.price_points", []),
            ("""
             CREATE TRIGGER trg_price_points_ingestion_fence
             AFTER DELETE ON public.price_points
             FOR EACH ROW EXECUTE FUNCTION public.enforce_price_point_ingestion_fence()
             """, []),
            ("""
             INSERT INTO public.price_points(asset_id,price_date,close,source_raw,ingested_at)
             VALUES ($1,'2024-04-01',999,'{"provider":"fence-bypass-fixture"}'::jsonb,clock_timestamp())
             """, [AssetId]));
    }

    internal async Task ApplyPriceFenceBodyDriftAsync() =>
        await ExecuteAdminTransactionAsync(("""
            CREATE OR REPLACE FUNCTION public.enforce_price_point_ingestion_fence()
            RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
            BEGIN RETURN NEW; END
            $$
            """, []));

    internal async Task RestoreWriterFencesAsync()
    {
        await ExecuteAdminTransactionAsync(
            ("SET LOCAL session_replication_role=replica", []),
            ("DELETE FROM public.price_points WHERE asset_id=$1 AND price_date='2024-04-01'",
                [AssetId]));
        var migration = Path.Combine(FindRepositoryRoot(), "infrastructure", "postgres", "migrations",
            "016_ingestion_write_fence.sql");
        await using var connection = new NpgsqlConnection(_environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(migration), connection);
        await command.ExecuteNonQueryAsync();
        await using var harden = new NpgsqlCommand("""
            ALTER FUNCTION public.enforce_price_point_ingestion_fence()
                SET search_path TO pg_catalog, pg_temp;
            ALTER FUNCTION public.enforce_inflation_rate_ingestion_fence()
                SET search_path TO pg_catalog, pg_temp;
            """, connection);
        await harden.ExecuteNonQueryAsync();
    }

    internal async Task ApplyWindowUniqueFingerprintDriftAndDuplicatesAsync()
    {
        await ExecuteAdminTransactionAsync(
            ("ALTER TABLE public.ingestion_windows DROP CONSTRAINT uq_ingestion_windows_logical", []),
            ("""
             ALTER TABLE public.ingestion_windows ADD CONSTRAINT uq_ingestion_windows_logical
             UNIQUE NULLS NOT DISTINCT(id)
             """, []),
            ("""
             INSERT INTO public.ingestion_windows(
                source,asset_id,job_type,range_start,range_end,contract_version,state)
             VALUES ('coingecko',$1,'audit_constraint','2024-05-01','2024-05-01',1,'pending'),
                    ('coingecko',$1,'audit_constraint','2024-05-01','2024-05-01',1,'pending')
             """, [AssetId]));
    }

    internal async Task RestoreWindowUniqueConstraintAsync()
    {
        await ExecuteAdminTransactionAsync(
            ("""
             DELETE FROM public.ingestion_windows
              WHERE source='coingecko' AND asset_id=$1 AND job_type='audit_constraint'
             """, [AssetId]),
            ("ALTER TABLE public.ingestion_windows DROP CONSTRAINT uq_ingestion_windows_logical", []),
            ("""
             ALTER TABLE public.ingestion_windows ADD CONSTRAINT uq_ingestion_windows_logical
             UNIQUE NULLS NOT DISTINCT(source,asset_id,job_type,range_start,range_end,contract_version)
             """, []));
    }

    internal async Task<AuditInputManifest> CreateContainingMonthlyLaneAsync()
    {
        var from = new DateOnly(2004, 1, 1);
        var through = new DateOnly(2004, 3, 1);
        await using var connection = new NpgsqlConnection(_environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL session_replication_role=replica");
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates DISABLE TRIGGER trg_inflation_rates_authority");
        await ExecuteAsync(connection, transaction, """
            DELETE FROM public.ingestion_jobs
             WHERE source='evds' AND job_type='inflation_backfill' AND date_range_start=$1
            """, from);
        await ExecuteAsync(connection, transaction, """
            DELETE FROM public.ingestion_windows
             WHERE source='evds' AND asset_id IS NULL AND job_type='inflation_backfill' AND range_start=$1
            """, from);
        var leaseToken = Guid.NewGuid();
        var windowId = await ScalarAsync<Guid>(connection, transaction, """
            INSERT INTO public.ingestion_windows(
                source,asset_id,job_type,range_start,range_end,contract_version,state,
                lease_owner,lease_token,lease_until,attempt_count)
            VALUES ('evds',NULL,'inflation_backfill',$1,$2,1,'running',
                    'audit-fixture',$3,clock_timestamp()+interval '10 minutes',1) RETURNING id
            """, from, through, leaseToken);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO public.ingestion_jobs(
                asset_id,job_type,started_at,status,records_upserted,
                date_range_start,date_range_end,source,window_id)
            VALUES (NULL,'inflation_backfill',clock_timestamp(),'running',0,$1,$2,'evds',$3)
            """, from, through, windowId);
        await ExecuteAsync(connection, transaction,
            "SELECT set_config('saydin.ingestion_window_id',$1,true)", windowId.ToString("D"));
        await ExecuteAsync(connection, transaction,
            "SELECT set_config('saydin.ingestion_lease_token',$1,true)", leaseToken.ToString("D"));
        await ExecuteAsync(connection, transaction, """
            INSERT INTO public.inflation_rates(period_date,index_value,source,created_at,updated_at)
            VALUES ($1,100,'tuik',clock_timestamp(),clock_timestamp()),
                   ($2,101,'tuik',clock_timestamp(),clock_timestamp()),
                   ($3,102,'tuik',clock_timestamp(),clock_timestamp())
            ON CONFLICT(period_date,source) DO UPDATE SET index_value=excluded.index_value,
                updated_at=excluded.updated_at
            """, from, from.AddMonths(1), through);
        await ExecuteAsync(connection, transaction, """
            UPDATE public.ingestion_windows
               SET state='succeeded',lease_owner=NULL,lease_token=NULL,lease_until=NULL,
                   requested_calendar_count=3,expected_observation_count=3,raw_item_count=3,
                   accepted_distinct_count=3,rejected_count=0,expected_no_data_count=0,
                   outcome_code='data_complete',completed_at=clock_timestamp(),updated_at=clock_timestamp()
             WHERE id=$1
            """, windowId);
        await ExecuteAsync(connection, transaction, """
            UPDATE public.ingestion_jobs SET status='success',finished_at=clock_timestamp(),
                   records_upserted=3,outcome_code='data_complete' WHERE window_id=$1
            """, windowId);
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE public.inflation_rates ENABLE ALWAYS TRIGGER trg_inflation_rates_authority");
        await transaction.CommitAsync();
        var manifest = await CreateManifestAsync();
        return manifest with
        {
            Scope = manifest.Scope with
            {
                Lanes = [new AuditLane("evds", null, "inflation_backfill", 1,
                    from.AddMonths(1), from.AddMonths(1), "month")],
            },
        };
    }

    internal async Task CleanupContainingMonthlyLaneAsync()
    {
        var from = new DateOnly(2004, 1, 1);
        await ExecuteAdminTransactionAsync(
            ("SET LOCAL session_replication_role=replica", []),
            ("""
             DELETE FROM public.inflation_rates
              WHERE source='tuik' AND period_date BETWEEN $1 AND $2
             """, [from, from.AddMonths(2)]),
            ("DELETE FROM public.ingestion_jobs WHERE source='evds' AND job_type='inflation_backfill' AND date_range_start=$1", [from]),
            ("DELETE FROM public.ingestion_windows WHERE source='evds' AND job_type='inflation_backfill' AND range_start=$1", [from]));
    }

    internal async Task AddOlderRunningJobToSucceededWindowAsync()
    {
        await ExecuteAdminTransactionAsync(("""
            INSERT INTO public.ingestion_jobs(
                asset_id,job_type,started_at,status,records_upserted,
                date_range_start,date_range_end,source,window_id)
            SELECT asset_id,job_type,clock_timestamp()-interval '1 hour','running',0,
                   range_start,range_end,source,id
              FROM public.ingestion_windows
             WHERE source='coingecko' AND asset_id=$1 AND job_type='historical_backfill'
               AND range_start=$2 AND range_end=$3
            """, [AssetId, From, Through]));
    }

    internal async Task RemoveOlderRunningJobAsync() =>
        await ExecuteAdminTransactionAsync(("""
            DELETE FROM public.ingestion_jobs
             WHERE source='coingecko' AND asset_id=$1 AND job_type='historical_backfill'
               AND status='running' AND date_range_start=$2 AND date_range_end=$3
            """, [AssetId, From, Through]));

    internal async Task RemoveCalendarPointerAndEligibleBindingAsync()
    {
        await ExecuteAdminTransactionAsync(
            ("SET LOCAL session_replication_role=replica", []),
            ("DELETE FROM public.market_calendar_active_releases WHERE calendar_code='tcmb_indicative_fx'", []),
            ("""
             DELETE FROM public.asset_market_calendars
              WHERE asset_id=(SELECT id FROM public.assets WHERE source='tcmb' AND is_active
                               ORDER BY symbol LIMIT 1)
             """, []));
    }

    internal async Task RestoreCalendarPointerAndEligibleBindingAsync()
    {
        await ExecuteAdminTransactionAsync(
            ("SET LOCAL session_replication_role=replica", []),
            ("""
             INSERT INTO public.market_calendar_active_releases(calendar_code,release_id,activated_at)
             VALUES ('tcmb_indicative_fx','ca100000-0000-7000-8000-000000000001',clock_timestamp())
             ON CONFLICT(calendar_code) DO UPDATE SET release_id=excluded.release_id,
                 activated_at=excluded.activated_at
             """, []),
            ("""
             INSERT INTO public.asset_market_calendars(asset_id,source,calendar_code)
             SELECT id,'tcmb','tcmb_indicative_fx' FROM public.assets
              WHERE source='tcmb' AND is_active ORDER BY symbol LIMIT 1
             ON CONFLICT(asset_id) DO UPDATE SET source=excluded.source,
                 calendar_code=excluded.calendar_code
             """, []));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Saydin.Services.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private async Task ExecuteAdminTransactionAsync(
        params (string Sql, object[] Values)[] statements)
    {
        await using var connection = new NpgsqlConnection(_environment.AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var statement in statements)
        {
            await using var command = new NpgsqlCommand(statement.Sql, connection, transaction);
            foreach (var value in statement.Values)
                command.Parameters.AddWithValue(value);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private async Task SeedCleanLaneAsync(NpgsqlConnection admin)
    {
        await using var transaction = await admin.BeginTransactionAsync();
        await ExecuteAsync("SET LOCAL session_replication_role=replica");
        await ExecuteAsync("""
            DELETE FROM public.price_observation_attributions
             WHERE asset_id=$1 AND price_date BETWEEN $2 AND $3
            """, AssetId, From, Through);
        await ExecuteAsync("""
            DELETE FROM public.provider_fetch_payloads
             WHERE provider_source='coingecko'
               AND payload_sha256 IN (
                 sha256(convert_to('audit-clean-payload:'||$1::date::text,'UTF8')),
                 sha256(convert_to('audit-clean-payload:'||$2::date::text,'UTF8')))
            """, From, Through);
        await ExecuteAsync("""
            DELETE FROM public.ingestion_jobs
             WHERE source='coingecko' AND asset_id=$1 AND date_range_start=$2 AND date_range_end=$3
            """, AssetId, From, Through);
        await ExecuteAsync(
            "DELETE FROM public.price_points WHERE asset_id=$1 AND price_date BETWEEN $2 AND $3",
            AssetId, From, Through);
        await ExecuteAsync("""
            DELETE FROM public.ingestion_windows
             WHERE source='coingecko' AND asset_id=$1 AND job_type='historical_backfill'
               AND contract_version=1 AND range_start=$2 AND range_end=$3
            """, AssetId, From, Through);
        Guid windowId;
        await using (var insertWindow = new NpgsqlCommand("""
            INSERT INTO public.ingestion_windows(
                source,asset_id,job_type,range_start,range_end,contract_version,state,
                requested_calendar_count,expected_observation_count,raw_item_count,
                accepted_distinct_count,rejected_count,expected_no_data_count,
                outcome_code,completed_at)
            VALUES ('coingecko',$1,'historical_backfill',$2,$3,1,'succeeded',
                    2,2,2,2,0,0,'data_complete',clock_timestamp())
            RETURNING id
            """, admin, transaction))
        {
            insertWindow.Parameters.AddWithValue(AssetId);
            insertWindow.Parameters.AddWithValue(From);
            insertWindow.Parameters.AddWithValue(Through);
            windowId = (Guid)(await insertWindow.ExecuteScalarAsync())!;
        }
        await ExecuteAsync("""
            INSERT INTO public.ingestion_jobs(
                asset_id,job_type,started_at,finished_at,status,records_upserted,
                date_range_start,date_range_end,source,window_id,outcome_code)
            VALUES ($1,'historical_backfill',clock_timestamp(),clock_timestamp(),'success',2,
                    $2,$3,'coingecko',$4,'data_complete')
            """, AssetId, From, Through, windowId);
        await ExecuteAsync("""
            WITH input(price_date,close) AS (VALUES ($2::date,100.000000::numeric),
                                                    ($3::date,101.000000::numeric)),
            evidence AS (
              SELECT input.*,
                     a.source_id,
                     (input.price_date::timestamp AT TIME ZONE 'UTC') AS as_of_at,
                     jsonb_build_object(
                       'as_of_at',to_char(input.price_date,'YYYY-MM-DD')||'T00:00:00+00:00',
                       'close',input.close,
                       'date',to_char(input.price_date,'YYYY-MM-DD'),
                       'observation_id',concat('coingecko:',a.source_id,':try:',
                         (extract(epoch FROM input.price_date::timestamp AT TIME ZONE 'UTC')*1000)::bigint),
                       'provider_source','coingecko','quote_currency','TRY',
                       'source_timestamp_ms',
                         (extract(epoch FROM input.price_date::timestamp AT TIME ZONE 'UTC')*1000)::bigint,
                       'symbol',a.source_id) AS source_raw
                FROM input JOIN public.assets a ON a.id=$1),
            normalized AS (
              SELECT evidence.*,
                     sha256(convert_to(public.saydin_canonical_observation(source_raw)::text,'UTF8')) AS observation_sha,
                     sha256(convert_to('audit-clean-payload:'||price_date::text,'UTF8')) AS payload_sha
                FROM evidence),
            inserted AS (
              INSERT INTO public.price_points(
                asset_id,price_date,close,provider_source,source_observation_id,as_of_at,
                price_kind,is_final,observation_sha256,authority_contract_version,source_raw)
              SELECT $1,price_date,close,'coingecko',source_raw->>'observation_id',as_of_at,
                     'daily_utc_reference',true,observation_sha,1,source_raw
                FROM normalized)
            INSERT INTO public.provider_fetch_payloads(
              provider_source,payload_sha256,payload_byte_length)
            SELECT 'coingecko',payload_sha,1024 FROM normalized
            """, AssetId, From, Through);
        await ExecuteAsync("""
            WITH normalized AS (
              SELECT p.asset_id,p.price_date,p.provider_source,p.source_observation_id,
                     p.observation_sha256,p.authority_contract_version,
                     sha256(convert_to('audit-clean-payload:'||p.price_date::text,'UTF8')) AS payload_sha
                FROM public.price_points p
               WHERE p.asset_id=$1 AND p.price_date BETWEEN $2 AND $3)
            INSERT INTO public.price_observation_attributions(
              asset_id,price_date,ingestion_window_id,provider_source,payload_sha256,
              source_observation_id,observation_sha256,authority_contract_version)
            SELECT asset_id,price_date,$4,provider_source,payload_sha,source_observation_id,
                   observation_sha256,authority_contract_version
              FROM normalized
            """, AssetId, From, Through, windowId);
        await transaction.CommitAsync();
        return;

        async Task ExecuteAsync(string sql, params object[] values)
        {
            await using var command = new NpgsqlCommand(sql, admin, transaction);
            foreach (var value in values)
                command.Parameters.AddWithValue(value);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<Guid> FindCoinGeckoAssetAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "SELECT id FROM public.assets WHERE source='coingecko' ORDER BY symbol LIMIT 1", connection);
        return (Guid)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("CoinGecko fixture asset is missing."));
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
            command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
            command.Parameters.AddWithValue(value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
            command.Parameters.AddWithValue(value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private string Write(string name, string value)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, value, Encoding.UTF8);
        return path;
    }
}
