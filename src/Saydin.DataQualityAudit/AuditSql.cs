namespace Saydin.DataQualityAudit;

internal static class AuditSql
{
    public const string PriceWindowCompleteness = """
        WITH w AS (
            SELECT base.*,
                   greatest(base.range_start,$2::date) AS audit_start,
                   least(base.range_end,$3::date) AS audit_end,
                   base.range_start >= $2::date AND base.range_end <= $3::date AS full_window
            FROM public.ingestion_windows base WHERE id = $1
        ),
        expected AS (
            SELECT d.calendar_date AS observation_date
            FROM w
            JOIN public.market_calendar_days d
              ON d.release_id = w.calendar_release_id
             AND d.calendar_date BETWEEN w.audit_start AND w.audit_end
            WHERE w.source IN ('tcmb','twelvedata')
              AND d.observation_expected
            UNION ALL
            SELECT gs::date
            FROM w
            CROSS JOIN LATERAL generate_series(
                w.audit_start::timestamp, w.audit_end::timestamp, interval '1 day') gs
            WHERE w.source IN ('coingecko','openexchangerates')
        ),
        actual AS (
            SELECT DISTINCT p.price_date AS observation_date
            FROM w
            JOIN public.price_points p
              ON p.asset_id = w.asset_id
             AND p.price_date BETWEEN w.audit_start AND w.audit_end
        ),
        counts AS (
            SELECT w.*,
                   (w.audit_end - w.audit_start + 1)::integer AS computed_requested,
                   (SELECT count(*)::integer FROM expected) AS computed_expected,
                   (SELECT count(*)::integer FROM actual) AS computed_actual
            FROM w
        ),
        violations AS (
            SELECT 'missing_expected_observation' AS violation_code,
                   w.id::text || '|missing|' || e.observation_date::text AS business_key
            FROM w
            JOIN expected e ON true
            LEFT JOIN actual a USING (observation_date)
            WHERE a.observation_date IS NULL
            UNION ALL
            SELECT 'unexpected_or_closed_observation',
                   w.id::text || '|unexpected|' || a.observation_date::text
            FROM w
            JOIN actual a ON true
            LEFT JOIN expected e USING (observation_date)
            WHERE e.observation_date IS NULL
            UNION ALL
            SELECT 'ledger_requested_count_mismatch', id::text || '|requested'
            FROM counts WHERE full_window AND requested_calendar_count <> computed_requested
            UNION ALL
            SELECT 'ledger_expected_count_mismatch', id::text || '|expected'
            FROM counts WHERE full_window AND expected_observation_count <> computed_expected
            UNION ALL
            SELECT 'ledger_success_actual_count_mismatch', id::text || '|actual'
            FROM counts
            WHERE state = 'succeeded'
              AND (computed_actual <> computed_expected OR full_window AND (
                   accepted_distinct_count <> computed_actual OR rejected_count <> 0
                   OR expected_no_data_count <> computed_requested - computed_expected))
            UNION ALL
            SELECT 'expected_no_data_has_expected_or_actual', id::text || '|no-data'
            FROM counts
            WHERE state = 'expected_no_data'
              AND (computed_expected <> 0 OR computed_actual <> 0 OR full_window AND (
                   expected_observation_count <> 0 OR accepted_distinct_count <> 0
                   OR rejected_count <> 0 OR expected_no_data_count <> computed_requested))
        )
        SELECT violation_code, business_key, count(*) OVER ()::bigint AS total_count
        FROM violations
        ORDER BY violation_code, business_key
        LIMIT $4
        """;

    public const string InflationWindowCompleteness = """
        WITH w AS (
            SELECT base.*,
                   greatest(base.range_start,$2::date) AS audit_start,
                   least(base.range_end,$3::date) AS audit_end,
                   base.range_start >= $2::date AND base.range_end <= $3::date AS full_window
            FROM public.ingestion_windows base WHERE id = $1
        ),
        expected AS (
            SELECT gs::date AS period_date
            FROM w
            CROSS JOIN LATERAL generate_series(
                date_trunc('month', w.audit_start::timestamp),
                date_trunc('month', w.audit_end::timestamp), interval '1 month') gs
        ),
        actual AS (
            SELECT DISTINCT r.period_date
            FROM w
            JOIN public.inflation_rates r
              ON r.source = 'tuik'
             AND r.period_date BETWEEN w.audit_start AND w.audit_end
        ),
        counts AS (
            SELECT w.*,
                   (SELECT count(*)::integer FROM expected) AS computed_expected,
                   (SELECT count(*)::integer FROM actual) AS computed_actual
            FROM w
        ),
        violations AS (
            SELECT 'missing_expected_month' AS violation_code,
                   w.id::text || '|missing|' || e.period_date::text AS business_key
            FROM w JOIN expected e ON true
            LEFT JOIN actual a USING (period_date)
            WHERE a.period_date IS NULL
            UNION ALL
            SELECT 'unexpected_month', w.id::text || '|unexpected|' || a.period_date::text
            FROM w JOIN actual a ON true
            LEFT JOIN expected e USING (period_date)
            WHERE e.period_date IS NULL
            UNION ALL
            SELECT 'ledger_month_count_mismatch', id::text || '|counts'
            FROM counts
            WHERE full_window AND (requested_calendar_count <> computed_expected
               OR expected_observation_count <> computed_expected)
               OR state = 'succeeded' AND (
                    computed_actual <> computed_expected
                    OR full_window AND (accepted_distinct_count <> computed_actual
                    OR rejected_count <> 0 OR expected_no_data_count <> 0))
               OR state = 'expected_no_data' AND (computed_actual <> 0 OR computed_expected <> 0)
        )
        SELECT violation_code, business_key, count(*) OVER ()::bigint AS total_count
        FROM violations
        ORDER BY violation_code, business_key
        LIMIT $4
        """;

    public const string PriceInvariants = """
        WITH violations AS (
            SELECT CASE
                     WHEN p.close <= 0 THEN 'nonpositive_close'
                     WHEN p.volume < 0 THEN 'negative_volume'
                     WHEN num_nonnulls(p.open,p.high,p.low) NOT IN (0,3) THEN 'partial_ohlc'
                     WHEN p.high < p.low THEN 'high_below_low'
                     WHEN p.high IS NOT NULL AND p.high < greatest(p.open,p.close,p.low)
                       THEN 'high_below_component'
                     WHEN p.low IS NOT NULL AND p.low > least(p.open,p.close,p.high)
                       THEN 'low_above_component'
                     WHEN a.source = 'twelvedata' AND num_nonnulls(p.open,p.high,p.low) <> 3
                       THEN 'twelvedata_ohlc_missing'
                   END AS violation_code,
                   p.asset_id::text || '|' || p.price_date::text AS business_key
            FROM public.price_points p
            JOIN public.assets a ON a.id = p.asset_id
            WHERE p.asset_id = $1
              AND p.price_date BETWEEN $2 AND $3
              AND (p.close <= 0 OR p.volume < 0
                   OR num_nonnulls(p.open,p.high,p.low) NOT IN (0,3)
                   OR p.high < p.low
                   OR (p.high IS NOT NULL AND p.high < greatest(p.open,p.close,p.low))
                   OR (p.low IS NOT NULL AND p.low > least(p.open,p.close,p.high))
                   OR (a.source = 'twelvedata' AND num_nonnulls(p.open,p.high,p.low) <> 3))
        )
        SELECT violation_code, business_key, count(*) OVER ()::bigint AS total_count
        FROM violations ORDER BY violation_code, business_key LIMIT $4
        """;

    public const string InflationInvariants = """
        WITH violations AS (
            SELECT CASE
                     WHEN index_value <= 0 THEN 'nonpositive_cpi'
                     WHEN period_date <> date_trunc('month',period_date)::date THEN 'not_month_first'
                     WHEN source NOT IN ('tuik','seed-approximation') THEN 'invalid_source'
                   END AS violation_code,
                   period_date::text || '|' || source AS business_key
            FROM public.inflation_rates
            WHERE period_date BETWEEN $1 AND $2
              AND (index_value <= 0
                   OR period_date <> date_trunc('month',period_date)::date
                   OR source NOT IN ('tuik','seed-approximation'))
        )
        SELECT violation_code, business_key, count(*) OVER ()::bigint AS total_count
        FROM violations ORDER BY violation_code, business_key LIMIT $3
        """;

    public const string PriceProvenance = """
        WITH violations AS (
            SELECT CASE
                     WHEN source_raw IS NULL THEN 'source_raw_missing'
                     WHEN source_raw::text ~* '"(app[_-]?id|api[_-]?key|authorization|access[_-]?token|client[_-]?secret|credentials?|token|password|secret)"[[:space:]]*:'
                       THEN 'possible_secret_key_in_source_raw'
                     WHEN pg_column_size(source_raw) > 65536 THEN 'source_raw_oversized'
                   END AS violation_code,
                   asset_id::text || '|' || price_date::text AS business_key
            FROM public.price_points
            WHERE asset_id = $1 AND price_date BETWEEN $2 AND $3
              AND (source_raw IS NULL
                   OR source_raw::text ~* '"(app[_-]?id|api[_-]?key|authorization|access[_-]?token|client[_-]?secret|credentials?|token|password|secret)"[[:space:]]*:'
                   OR pg_column_size(source_raw) > 65536)
        )
        SELECT violation_code, business_key, count(*) OVER ()::bigint AS total_count
        FROM violations ORDER BY violation_code, business_key LIMIT $4
        """;

    public const string InflationProvenance = """
        WITH violations AS (
            SELECT 'seed_without_tuik' AS violation_code,
                   seed.period_date::text || '|seed-approximation' AS business_key
            FROM public.inflation_rates seed
            LEFT JOIN public.inflation_rates authoritative
              ON authoritative.period_date = seed.period_date
             AND authoritative.source = 'tuik'
            WHERE seed.source = 'seed-approximation'
              AND seed.period_date BETWEEN $1 AND $2
              AND authoritative.period_date IS NULL
        )
        SELECT violation_code, business_key, count(*) OVER ()::bigint AS total_count
        FROM violations ORDER BY business_key LIMIT $3
        """;

    public const string PriceAuthority = """
        WITH scoped AS (
          SELECT p.*,a.source AS asset_source,a.source_id AS asset_source_id,
                 num_nulls(p.provider_source,p.source_observation_id,p.as_of_at,p.price_kind,
                           p.is_final,p.observation_sha256,p.authority_contract_version) AS authority_nulls
            FROM public.price_points p JOIN public.assets a ON a.id=p.asset_id
           WHERE p.asset_id=$1 AND p.price_date BETWEEN $2 AND $3),
        evaluated AS (
          SELECT scoped.*,
            CASE WHEN jsonb_typeof(source_raw)='object' THEN
              observation_sha256 IS NOT DISTINCT FROM sha256(convert_to(
                (SELECT jsonb_object_agg(item.key,CASE
                   WHEN jsonb_typeof(item.value)='number'
                     THEN to_jsonb(trim_scale((item.value#>>'{}')::numeric))
                   ELSE item.value END)
                   FROM jsonb_each(source_raw) item(key,value))::text,'UTF8'))
            ELSE false END AS hash_valid,
            CASE WHEN jsonb_typeof(source_raw)='object' THEN
              octet_length(source_raw::text)<=65536
              AND (SELECT count(*) BETWEEN 1 AND 24 FROM jsonb_each(source_raw))
              AND NOT EXISTS (SELECT 1 FROM jsonb_each(source_raw) item(key,value)
                WHERE octet_length(item.key)=0 OR octet_length(item.key)>64
                   OR item.key NOT IN ('as_of_at','base_currency','close','currency','date','exchange',
                     'exchange_timezone','high','index_value','instrument_type','interval','low',
                     'mic_code','observation_id','open','provider_source','quote_currency','series',
                     'source_timestamp_ms','symbol','unit','volume')
                   OR jsonb_typeof(item.value) NOT IN ('string','number','boolean','null')
                   OR (item.key IN ('close','high','index_value','low','open','source_timestamp_ms','volume')
                       AND jsonb_typeof(item.value)<>'number')
                   OR (item.key IN ('as_of_at','base_currency','currency','date','exchange',
                       'exchange_timezone','instrument_type','interval','mic_code','observation_id',
                       'provider_source','quote_currency','series','symbol')
                       AND jsonb_typeof(item.value)<>'string')
                   OR (jsonb_typeof(item.value)='string' AND (octet_length(item.value#>>'{}')>512
                     OR item.value#>>'{}' ~* '(api[_-]?key|app[_-]?id|authorization|bearer|credential|password|secret|token)')))
              AND source_raw->>'provider_source' IS NOT DISTINCT FROM provider_source
              AND source_raw->>'observation_id' IS NOT DISTINCT FROM source_observation_id
              AND source_raw->>'date' IS NOT DISTINCT FROM to_char(price_date,'YYYY-MM-DD')
              AND CASE WHEN pg_input_is_valid(source_raw->>'as_of_at','timestamptz')
                    THEN (source_raw->>'as_of_at')::timestamptz END IS NOT DISTINCT FROM as_of_at
              AND CASE WHEN pg_input_is_valid(source_raw->>'close','numeric')
                    THEN (source_raw->>'close')::numeric END IS NOT DISTINCT FROM close
            ELSE false END AS common_evidence_valid,
            CASE provider_source
              WHEN 'coingecko' THEN
                (SELECT array_agg(key ORDER BY key) FROM jsonb_object_keys(
                    CASE WHEN jsonb_typeof(source_raw)='object' THEN source_raw ELSE '{}'::jsonb END) key)
                  =ARRAY['as_of_at','close','date','observation_id','provider_source','quote_currency','source_timestamp_ms','symbol']::text[]
                AND source_raw->>'symbol' IS NOT DISTINCT FROM asset_source_id
                AND source_raw->>'quote_currency'='TRY'
                AND CASE WHEN pg_input_is_valid(source_raw->>'source_timestamp_ms','bigint')
                      THEN (source_raw->>'source_timestamp_ms')::bigint END
                    IS NOT DISTINCT FROM (extract(epoch FROM as_of_at)*1000)::bigint
                AND source_observation_id=concat('coingecko:',asset_source_id,':try:',
                    (extract(epoch FROM as_of_at)*1000)::bigint::text)
                AND (as_of_at AT TIME ZONE 'UTC')::date=price_date
                AND (as_of_at AT TIME ZONE 'UTC')::time=time '00:00:00'
              WHEN 'tcmb' THEN
                (SELECT array_agg(key ORDER BY key) FROM jsonb_object_keys(
                    CASE WHEN jsonb_typeof(source_raw)='object' THEN source_raw ELSE '{}'::jsonb END) key)
                  =ARRAY['as_of_at','close','currency','date','observation_id','provider_source','unit']::text[]
                AND source_raw->>'currency'=CASE WHEN asset_source_id LIKE '%.%.%'
                    THEN split_part(asset_source_id,'.',3) ELSE asset_source_id END
                AND CASE WHEN pg_input_is_valid(source_raw->>'unit','numeric')
                      THEN (source_raw->>'unit')::numeric END>0
                AND source_observation_id=concat('tcmb:',CASE WHEN asset_source_id LIKE '%.%.%'
                    THEN split_part(asset_source_id,'.',3) ELSE asset_source_id END,':',
                    to_char(price_date,'YYYY-MM-DD'),':forex_buying')
                AND (as_of_at AT TIME ZONE 'UTC')::date=price_date
              WHEN 'openexchangerates' THEN
                (SELECT array_agg(key ORDER BY key) FROM jsonb_object_keys(
                    CASE WHEN jsonb_typeof(source_raw)='object' THEN source_raw ELSE '{}'::jsonb END) key)
                  =ARRAY['as_of_at','base_currency','close','date','observation_id','provider_source','quote_currency','symbol','unit']::text[]
                AND source_raw->>'base_currency'='USD' AND source_raw->>'quote_currency'='TRY'
                AND source_raw->>'symbol'=asset_source_id AND source_raw->>'unit'='gram'
                AND source_observation_id=concat('openexchangerates:',asset_source_id,':',
                    to_char(price_date,'YYYY-MM-DD'))
                AND (as_of_at AT TIME ZONE 'UTC')::date=price_date
              WHEN 'twelvedata' THEN
                (SELECT array_agg(key ORDER BY key) FROM jsonb_object_keys(
                    CASE WHEN jsonb_typeof(source_raw)='object' THEN source_raw ELSE '{}'::jsonb END) key)
                  =ARRAY['as_of_at','close','currency','date','exchange','exchange_timezone','high','instrument_type','interval','low','mic_code','observation_id','open','provider_source','symbol','volume']::text[]
                AND source_raw->>'symbol'=split_part(asset_source_id,':',1)
                AND source_raw->>'interval'='1day' AND source_raw->>'exchange'='BIST'
                AND source_raw->>'mic_code'='XIST' AND source_raw->>'exchange_timezone'='Europe/Istanbul'
                AND source_raw->>'currency'='TRY'
                AND source_raw->>'instrument_type' IN ('Common Stock','Stock')
                AND CASE WHEN pg_input_is_valid(source_raw->>'open','numeric')
                      THEN (source_raw->>'open')::numeric END IS NOT DISTINCT FROM open
                AND CASE WHEN pg_input_is_valid(source_raw->>'high','numeric')
                      THEN (source_raw->>'high')::numeric END IS NOT DISTINCT FROM high
                AND CASE WHEN pg_input_is_valid(source_raw->>'low','numeric')
                      THEN (source_raw->>'low')::numeric END IS NOT DISTINCT FROM low
                AND CASE WHEN pg_input_is_valid(source_raw->>'volume','numeric')
                      THEN (source_raw->>'volume')::numeric END IS NOT DISTINCT FROM volume
                AND source_observation_id=concat('twelvedata:',asset_source_id,':',
                    to_char(price_date,'YYYY-MM-DD'),':1day')
                AND (as_of_at AT TIME ZONE 'Europe/Istanbul')::date=price_date
                AND (as_of_at AT TIME ZONE 'Europe/Istanbul')::time=time '00:00:00'
              ELSE false END AS provider_evidence_valid,
            EXISTS (SELECT 1 FROM public.price_observation_attributions attribution
              JOIN public.provider_fetch_payloads payload
                ON payload.provider_source=attribution.provider_source
               AND payload.payload_sha256=attribution.payload_sha256
              JOIN public.ingestion_windows iw ON iw.id=attribution.ingestion_window_id
             WHERE attribution.asset_id=scoped.asset_id AND attribution.price_date=scoped.price_date
               AND attribution.provider_source=scoped.provider_source
               AND attribution.source_observation_id=scoped.source_observation_id
               AND attribution.observation_sha256=scoped.observation_sha256
               AND attribution.authority_contract_version=scoped.authority_contract_version
               AND iw.asset_id=scoped.asset_id AND iw.source=scoped.provider_source
               AND iw.contract_version=scoped.authority_contract_version
               AND scoped.price_date BETWEEN iw.range_start AND iw.range_end) AS attribution_valid
          FROM scoped),
        violations AS (
          SELECT CASE
            WHEN authority_nulls BETWEEN 1 AND 6 THEN 'partial_authority_tuple'
            WHEN source_raw IS NULL OR is_final IS DISTINCT FROM true THEN 'authority_not_final_or_evidence_missing'
            WHEN provider_source IS DISTINCT FROM asset_source OR authority_contract_version<=0
              OR (provider_source,price_kind) NOT IN (('tcmb','official_reference'),
                 ('coingecko','daily_utc_reference'),('openexchangerates','daily_reference'),
                 ('twelvedata','daily_close')) THEN 'provider_source_or_kind_mismatch'
            WHEN octet_length(observation_sha256)<>32 OR observation_sha256=decode(repeat('00',32),'hex')
              OR NOT hash_valid THEN 'observation_hash_mismatch'
            WHEN NOT common_evidence_valid OR NOT provider_evidence_valid THEN 'normalized_evidence_mismatch'
            WHEN NOT attribution_valid THEN 'observation_attribution_missing'
          END AS violation_code,asset_id::text||'|'||price_date::text AS business_key
          FROM evaluated WHERE authority_nulls BETWEEN 1 AND 6 OR authority_nulls=0 AND (
            source_raw IS NULL OR is_final IS DISTINCT FROM true OR provider_source IS DISTINCT FROM asset_source
            OR authority_contract_version<=0 OR (provider_source,price_kind) NOT IN (
              ('tcmb','official_reference'),('coingecko','daily_utc_reference'),
              ('openexchangerates','daily_reference'),('twelvedata','daily_close'))
            OR octet_length(observation_sha256)<>32 OR observation_sha256=decode(repeat('00',32),'hex')
            OR NOT hash_valid OR NOT common_evidence_valid OR NOT provider_evidence_valid OR NOT attribution_valid))
        SELECT violation_code,business_key,count(*) OVER()::bigint AS total_count
          FROM violations ORDER BY violation_code,business_key LIMIT $4
        """;

    public const string InflationAuthority = """
        WITH scoped AS (
          SELECT r.*,num_nulls(provider_source,source_observation_id,as_of_at,price_kind,
                 is_final,observation_sha256,authority_contract_version,source_raw) AS authority_nulls
            FROM public.inflation_rates r WHERE period_date BETWEEN $1 AND $2),
        evaluated AS (
          SELECT scoped.*,
            CASE WHEN jsonb_typeof(source_raw)='object' THEN
              observation_sha256 IS NOT DISTINCT FROM sha256(convert_to(
                (SELECT jsonb_object_agg(item.key,CASE WHEN jsonb_typeof(item.value)='number'
                  THEN to_jsonb(trim_scale((item.value#>>'{}')::numeric)) ELSE item.value END)
                   FROM jsonb_each(source_raw) item(key,value))::text,'UTF8')) ELSE false END AS hash_valid,
            CASE WHEN jsonb_typeof(source_raw)='object' THEN
              octet_length(source_raw::text)<=65536
              AND (SELECT array_agg(key ORDER BY key) FROM jsonb_object_keys(source_raw) key)
                =ARRAY['as_of_at','date','index_value','observation_id','provider_source','series']::text[]
              AND NOT EXISTS (SELECT 1 FROM jsonb_each(source_raw) item(key,value)
                WHERE octet_length(item.key)=0 OR octet_length(item.key)>64
                   OR jsonb_typeof(item.value) NOT IN ('string','number','boolean','null')
                   OR (item.key='index_value' AND jsonb_typeof(item.value)<>'number')
                   OR (item.key IN ('as_of_at','date','observation_id','provider_source','series')
                       AND jsonb_typeof(item.value)<>'string')
                   OR (jsonb_typeof(item.value)='string' AND (octet_length(item.value#>>'{}')>512
                     OR item.value#>>'{}' ~* '(api[_-]?key|app[_-]?id|authorization|bearer|credential|password|secret|token)')))
              AND source_raw->>'provider_source'='evds'
              AND source_raw->>'observation_id' IS NOT DISTINCT FROM source_observation_id
              AND source_raw->>'series'='TP.FG.J0'
              AND source_raw->>'date' IS NOT DISTINCT FROM to_char(period_date,'YYYY-MM-DD')
              AND CASE WHEN pg_input_is_valid(source_raw->>'as_of_at','timestamptz')
                    THEN (source_raw->>'as_of_at')::timestamptz END IS NOT DISTINCT FROM as_of_at
              AND CASE WHEN pg_input_is_valid(source_raw->>'index_value','numeric')
                    THEN (source_raw->>'index_value')::numeric END IS NOT DISTINCT FROM index_value
              AND source_observation_id=concat('evds:TP_FG_J0:',to_char(period_date,'YYYY-MM'))
              AND (as_of_at AT TIME ZONE 'UTC')::date=period_date
              AND (as_of_at AT TIME ZONE 'UTC')::time=time '00:00:00'
            ELSE false END AS evidence_valid,
            EXISTS (SELECT 1 FROM public.inflation_observation_attributions attribution
              JOIN public.provider_fetch_payloads payload
                ON payload.provider_source=attribution.provider_source
               AND payload.payload_sha256=attribution.payload_sha256
              JOIN public.ingestion_windows iw ON iw.id=attribution.ingestion_window_id
             WHERE attribution.period_date=scoped.period_date AND attribution.source=scoped.source
               AND attribution.provider_source=scoped.provider_source
               AND attribution.source_observation_id=scoped.source_observation_id
               AND attribution.observation_sha256=scoped.observation_sha256
               AND attribution.authority_contract_version=scoped.authority_contract_version
               AND iw.asset_id IS NULL AND iw.source='evds'
               AND iw.contract_version=scoped.authority_contract_version
               AND scoped.period_date BETWEEN iw.range_start AND iw.range_end) AS attribution_valid
          FROM scoped),
        violations AS (
          SELECT CASE
            WHEN authority_nulls BETWEEN 1 AND 7 THEN 'partial_authority_tuple'
            WHEN source<>'tuik' OR provider_source<>'evds' OR price_kind<>'cpi_index'
              OR is_final IS DISTINCT FROM true OR authority_contract_version<=0
              THEN 'provider_source_or_finality_mismatch'
            WHEN NOT hash_valid THEN 'observation_hash_mismatch'
            WHEN NOT evidence_valid THEN 'normalized_evidence_mismatch'
            WHEN NOT attribution_valid THEN 'observation_attribution_missing'
          END AS violation_code,period_date::text||'|'||source AS business_key
          FROM evaluated WHERE authority_nulls BETWEEN 1 AND 7 OR authority_nulls=0 AND (
            source<>'tuik' OR provider_source<>'evds' OR price_kind<>'cpi_index'
            OR is_final IS DISTINCT FROM true OR authority_contract_version<=0
            OR NOT hash_valid OR NOT evidence_valid OR NOT attribution_valid))
        SELECT violation_code,business_key,count(*) OVER()::bigint AS total_count
          FROM violations ORDER BY violation_code,business_key LIMIT $3
        """;

    public const string PriceLegacyAuthority = """
        SELECT 'legacy_authority_unknown' AS violation_code,
               asset_id::text||'|'||price_date::text AS business_key,
               count(*) OVER()::bigint AS total_count
          FROM public.price_points
         WHERE asset_id=$1 AND price_date BETWEEN $2 AND $3
           AND num_nulls(provider_source,source_observation_id,as_of_at,price_kind,is_final,
                         observation_sha256,authority_contract_version)=7
         ORDER BY business_key LIMIT $4
        """;

    public const string InflationLegacyAuthority = """
        SELECT 'legacy_authority_unknown' AS violation_code,
               period_date::text||'|'||source AS business_key,
               count(*) OVER()::bigint AS total_count
          FROM public.inflation_rates
         WHERE period_date BETWEEN $1 AND $2
           AND num_nulls(provider_source,source_observation_id,as_of_at,price_kind,is_final,
                         observation_sha256,authority_contract_version,source_raw)=8
         ORDER BY business_key LIMIT $3
        """;

    public const string FetchLedger = """
        WITH violations AS (
          SELECT 'orphan_fetch_payload' AS violation_code,
                 payload.provider_source||'|'||encode(payload.payload_sha256,'hex') AS business_key
            FROM public.provider_fetch_payloads payload
           WHERE payload.provider_source=ANY($2::text[])
             AND payload.first_observed_at BETWEEN $3 AND $4
             AND NOT EXISTS (SELECT 1 FROM public.price_observation_attributions price
                              WHERE price.provider_source=payload.provider_source
                                AND price.payload_sha256=payload.payload_sha256)
             AND NOT EXISTS (SELECT 1 FROM public.inflation_observation_attributions inflation
                              WHERE inflation.provider_source=payload.provider_source
                                AND inflation.payload_sha256=payload.payload_sha256)
          UNION ALL
          SELECT 'forged_price_attribution',asset_id::text||'|'||price_date::text||'|'||ingestion_window_id::text
            FROM public.price_observation_attributions attribution
           WHERE attribution.ingestion_window_id=ANY($1::uuid[])
             AND NOT EXISTS (SELECT 1 FROM public.price_points point
                              WHERE point.asset_id=attribution.asset_id
                                AND point.price_date=attribution.price_date
                                AND point.provider_source=attribution.provider_source
                                AND point.source_observation_id=attribution.source_observation_id
                                AND point.observation_sha256=attribution.observation_sha256
                                AND point.authority_contract_version=attribution.authority_contract_version)
          UNION ALL
          SELECT 'forged_inflation_attribution',period_date::text||'|'||source||'|'||ingestion_window_id::text
            FROM public.inflation_observation_attributions attribution
           WHERE attribution.ingestion_window_id=ANY($1::uuid[])
             AND NOT EXISTS (SELECT 1 FROM public.inflation_rates rate
                              WHERE rate.period_date=attribution.period_date
                                AND rate.source=attribution.source
                                AND rate.provider_source=attribution.provider_source
                                AND rate.source_observation_id=attribution.source_observation_id
                                AND rate.observation_sha256=attribution.observation_sha256
                                AND rate.authority_contract_version=attribution.authority_contract_version)),
        bounded AS (SELECT * FROM violations ORDER BY violation_code,business_key LIMIT $5 + 1)
        SELECT violation_code,business_key,count(*) OVER()::bigint AS total_count
          FROM bounded ORDER BY violation_code,business_key LIMIT $6
        """;

    public const string JobWindowState = """
        WITH scoped_windows AS (
            SELECT * FROM public.ingestion_windows
            WHERE source = $1 AND asset_id IS NOT DISTINCT FROM $2::uuid
              AND job_type = $3 AND contract_version = $4
              AND range_end >= $5 AND range_start <= $6
        ),
        job_counts AS (
            SELECT w.id, count(j.*) FILTER (WHERE j.status='running') AS running_jobs
            FROM scoped_windows w LEFT JOIN public.ingestion_jobs j ON j.window_id=w.id
            GROUP BY w.id
        ),
        latest AS (
            SELECT w.id AS window_id, w.state, j.id AS job_id, j.status,
                   j.asset_id, j.source, j.job_type, j.date_range_start, j.date_range_end,
                   row_number() OVER (PARTITION BY w.id ORDER BY j.started_at DESC,j.id) AS rn,
                   w.asset_id AS window_asset_id, w.source AS window_source,
                   w.job_type AS window_job_type, w.range_start, w.range_end
            FROM scoped_windows w LEFT JOIN public.ingestion_jobs j ON j.window_id=w.id
        ),
        violations AS (
            SELECT 'expired_running_lease' AS violation_code, id::text AS business_key
            FROM scoped_windows WHERE state='running' AND lease_until <= clock_timestamp()
            UNION ALL
            SELECT 'running_job_cardinality_mismatch', w.id::text
            FROM scoped_windows w JOIN job_counts c ON c.id=w.id
            WHERE w.state='running' AND c.running_jobs <> 1
            UNION ALL
            SELECT 'terminal_window_has_running_job', w.id::text
            FROM scoped_windows w JOIN job_counts c ON c.id=w.id
            WHERE w.state IN ('succeeded','expected_no_data') AND c.running_jobs > 0
            UNION ALL
            SELECT 'latest_job_terminal_state_mismatch', window_id::text
            FROM latest WHERE rn=1 AND (
                state='running' AND status IS DISTINCT FROM 'running'
                OR state IN ('succeeded','expected_no_data') AND status IS DISTINCT FROM 'success'
                OR state IN ('retryable_failed','permanent_failed','cancelled','abandoned')
                   AND status IS DISTINCT FROM 'failed')
            UNION ALL
            SELECT 'job_window_scope_mismatch', window_id::text || '|' || coalesce(job_id::text,'none')
            FROM latest WHERE rn=1 AND job_id IS NOT NULL AND (
                asset_id IS DISTINCT FROM window_asset_id
                OR source IS DISTINCT FROM window_source
                OR job_type IS DISTINCT FROM window_job_type
                OR date_range_start IS DISTINCT FROM range_start
                OR date_range_end IS DISTINCT FROM range_end)
            UNION ALL
            SELECT 'permanent_failure_has_newer_terminal_window', blocker.id::text
            FROM scoped_windows blocker
            WHERE blocker.state='permanent_failed' AND EXISTS (
                SELECT 1 FROM scoped_windows newer
                WHERE newer.range_start > blocker.range_start
                  AND newer.state IN ('succeeded','expected_no_data'))
            UNION ALL
            SELECT 'retryable_failure_overdue', id::text
            FROM scoped_windows
            WHERE state='retryable_failed' AND next_attempt_at <= clock_timestamp()
        )
        SELECT violation_code,business_key,count(*) OVER ()::bigint AS total_count
        FROM violations ORDER BY violation_code,business_key LIMIT $7
        """;

    public const string UnattestedPrice = """
        WITH fence AS (
            SELECT completed_at FROM public.schema_migrations
            WHERE version='016_ingestion_write_fence' AND state='succeeded'
        ), violations AS (
            SELECT 'post_fence_price_without_succeeded_window' AS violation_code,
                   p.asset_id::text || '|' || p.price_date::text AS business_key
            FROM public.price_points p
            JOIN public.assets a ON a.id=p.asset_id CROSS JOIN fence f
            WHERE p.asset_id=$1 AND p.price_date BETWEEN $2 AND $3
              AND p.ingested_at >= greatest(f.completed_at,$4::timestamptz)
              AND NOT EXISTS (
                  SELECT 1 FROM public.ingestion_windows w
                  WHERE w.asset_id=p.asset_id AND w.source=a.source AND w.state='succeeded'
                    AND p.price_date BETWEEN w.range_start AND w.range_end)
        )
        SELECT violation_code,business_key,count(*) OVER ()::bigint AS total_count
        FROM violations ORDER BY business_key LIMIT $5
        """;

    public const string UnattestedInflation = """
        WITH fence AS (
            SELECT completed_at FROM public.schema_migrations
            WHERE version='016_ingestion_write_fence' AND state='succeeded'
        ), violations AS (
            SELECT 'post_fence_cpi_without_succeeded_window' AS violation_code,
                   r.period_date::text || '|tuik' AS business_key
            FROM public.inflation_rates r CROSS JOIN fence f
            WHERE r.source='tuik' AND r.period_date BETWEEN $1 AND $2
              AND r.updated_at >= greatest(f.completed_at,$3::timestamptz)
              AND NOT EXISTS (
                  SELECT 1 FROM public.ingestion_windows w
                  WHERE w.source='evds' AND w.asset_id IS NULL AND w.state='succeeded'
                    AND r.period_date BETWEEN w.range_start AND w.range_end)
        )
        SELECT violation_code,business_key,count(*) OVER ()::bigint AS total_count
        FROM violations ORDER BY business_key LIMIT $4
        """;

    public const string LegacyJobs = """
        WITH ledger AS (
            SELECT completed_at FROM public.schema_migrations
            WHERE version='015_ingestion_windows' AND state='succeeded'
        ), violations AS (
            SELECT 'post_grace_job_without_window' AS violation_code, j.id::text AS business_key
            FROM public.ingestion_jobs j CROSS JOIN ledger activation
            WHERE j.source=$1 AND j.asset_id IS NOT DISTINCT FROM $2::uuid AND j.job_type=$3
              AND j.date_range_end >= $4 AND j.date_range_start <= $5
              AND j.started_at >= greatest(activation.completed_at,$6::timestamptz)
              AND j.window_id IS NULL
        )
        SELECT violation_code,business_key,count(*) OVER ()::bigint AS total_count
        FROM violations ORDER BY business_key LIMIT $7
        """;

    public const string PriceDuplicates = """
        WITH violations AS (
            SELECT 'duplicate_price_business_key' AS violation_code,
                   asset_id::text || '|' || price_date::text AS business_key
            FROM public.price_points WHERE asset_id=$1 AND price_date BETWEEN $2 AND $3
            GROUP BY asset_id,price_date HAVING count(*)>1
        )
        SELECT violation_code,business_key,count(*) OVER ()::bigint AS total_count
        FROM violations ORDER BY business_key LIMIT $4
        """;

    public const string InflationDuplicates = """
        WITH violations AS (
            SELECT 'duplicate_cpi_business_key' AS violation_code,
                   period_date::text || '|' || source AS business_key
            FROM public.inflation_rates WHERE period_date BETWEEN $1 AND $2
            GROUP BY period_date,source HAVING count(*)>1
        )
        SELECT violation_code,business_key,count(*) OVER ()::bigint AS total_count
        FROM violations ORDER BY business_key LIMIT $3
        """;

    public const string WindowDuplicates = """
        WITH violations AS (
            SELECT 'duplicate_logical_window' AS violation_code,
                   source || '|' || coalesce(asset_id::text,'global') || '|' || job_type || '|' ||
                   range_start::text || '|' || range_end::text || '|' || contract_version::text AS business_key
            FROM public.ingestion_windows
            GROUP BY source,asset_id,job_type,range_start,range_end,contract_version HAVING count(*)>1
        )
        SELECT violation_code,business_key,count(*) OVER ()::bigint AS total_count
        FROM violations ORDER BY business_key LIMIT $1
        """;

    public const string CalendarMetadata = """
        WITH rollup AS (
            SELECT r.id,r.calendar_code,r.coverage_from,r.coverage_through,r.row_count,r.sealed_at,
                   count(d.*)::integer AS actual_count,min(d.calendar_date) AS actual_from,
                   max(d.calendar_date) AS actual_through
            FROM public.market_calendar_releases r
            LEFT JOIN public.market_calendar_days d ON d.release_id=r.id
            WHERE r.id=ANY($1::uuid[])
            GROUP BY r.id
        ), violations AS (
            SELECT 'active_release_missing_or_unsealed' AS violation_code,
                   active.calendar_code || '|' || active.release_id::text AS business_key
            FROM public.market_calendar_active_releases active
            LEFT JOIN public.market_calendar_releases release
              ON release.calendar_code=active.calendar_code AND release.id=active.release_id
            WHERE active.release_id=ANY($1::uuid[])
              AND (release.id IS NULL OR release.sealed_at IS NULL)
            UNION ALL
            SELECT 'calendar_release_row_or_range_mismatch',id::text
            FROM rollup WHERE actual_count<>row_count OR actual_from IS DISTINCT FROM coverage_from
               OR actual_through IS DISTINCT FROM coverage_through
            UNION ALL
            SELECT 'asset_calendar_binding_source_mismatch',binding.asset_id::text
            FROM public.asset_market_calendars binding JOIN public.assets asset ON asset.id=binding.asset_id
            WHERE binding.asset_id=ANY($2::uuid[]) AND asset.source IS DISTINCT FROM binding.source
            UNION ALL
            SELECT 'active_calendar_release_pointer_missing', calendar.code
            FROM public.market_calendars calendar
            LEFT JOIN public.market_calendar_active_releases active
              ON active.calendar_code=calendar.code
            WHERE calendar.code IN (SELECT binding.calendar_code FROM public.asset_market_calendars binding
                                     WHERE binding.asset_id=ANY($2::uuid[]))
              AND active.calendar_code IS NULL
            UNION ALL
            SELECT 'eligible_asset_calendar_binding_missing', asset.id::text
            FROM public.assets asset
            LEFT JOIN public.asset_market_calendars binding ON binding.asset_id=asset.id
            WHERE asset.id=ANY($2::uuid[]) AND asset.is_active AND asset.source IN ('tcmb','twelvedata')
              AND binding.asset_id IS NULL
        )
        SELECT violation_code,business_key,count(*) OVER ()::bigint AS total_count
        FROM violations ORDER BY violation_code,business_key LIMIT $3
        """;

    public const string CalendarWindowCoverage = """
        WITH violations AS (
            SELECT 'window_calendar_release_scope_or_coverage_mismatch' AS violation_code,
                   w.id::text AS business_key
            FROM public.ingestion_windows w
            LEFT JOIN public.market_calendar_releases release ON release.id=w.calendar_release_id
            LEFT JOIN public.asset_market_calendars binding
              ON binding.asset_id=w.asset_id AND binding.source=w.source
            WHERE w.id=$1 AND (
                release.id IS NULL OR release.sealed_at IS NULL
                OR binding.calendar_code IS DISTINCT FROM release.calendar_code
                OR w.range_start < release.coverage_from OR w.range_end > release.coverage_through)
        )
        SELECT violation_code,business_key,count(*) OVER ()::bigint AS total_count
        FROM violations ORDER BY business_key LIMIT $2
        """;
}
