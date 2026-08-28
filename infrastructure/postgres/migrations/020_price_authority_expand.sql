-- Migration 020: normalized observation authority and append-only fetch attribution.
-- Schema expansion only: historical rows are not rewritten or VALIDATEd in this phase.
-- The write contract is an explicit stop/drain cutover: migration 020 rejects
-- authority-unaware writers, so old and new ingestion binaries must not overlap.

BEGIN;

-- MigrationRunner verifies the role contract and performs its session-level
-- managed-migrator -> owner transition before opening migration transactions.
-- Do not add a weaker fallback here: a raw migrator-capability session must fail.
DO $owner_transition$
DECLARE
    owner_role text;
    migrator_cap text;
    role_prefix text;
BEGIN
    SELECT contract.owner_role,contract.migrator_capability_role,contract.role_prefix
      INTO owner_role,migrator_cap,role_prefix
      FROM public.saydin_role_contract contract
     WHERE singleton=1 AND contract_schema_version=1
       AND database_name=pg_catalog.current_database();
    IF owner_role IS NULL OR migrator_cap IS NULL OR role_prefix IS NULL
       OR CURRENT_USER IS DISTINCT FROM owner_role THEN
        RAISE EXCEPTION 'price authority role contract rejected' USING ERRCODE='42501';
    END IF;
END;
$owner_transition$;

ALTER TABLE public.price_points
    ADD COLUMN provider_source varchar(32) NULL,
    ADD COLUMN source_observation_id varchar(256) NULL,
    ADD COLUMN as_of_at timestamptz NULL,
    ADD COLUMN price_kind varchar(32) NULL,
    ADD COLUMN is_final boolean NULL,
    ADD COLUMN observation_sha256 bytea NULL,
    ADD COLUMN authority_contract_version integer NULL;

ALTER TABLE public.inflation_rates
    ADD COLUMN provider_source varchar(32) NULL,
    ADD COLUMN source_observation_id varchar(256) NULL,
    ADD COLUMN as_of_at timestamptz NULL,
    ADD COLUMN price_kind varchar(32) NULL,
    ADD COLUMN is_final boolean NULL,
    ADD COLUMN observation_sha256 bytea NULL,
    ADD COLUMN authority_contract_version integer NULL,
    ADD COLUMN source_raw jsonb NULL;

CREATE TABLE public.provider_fetch_payloads (
    provider_source varchar(32) NOT NULL,
    payload_sha256 bytea NOT NULL,
    payload_byte_length integer NOT NULL,
    first_observed_at timestamptz NOT NULL DEFAULT pg_catalog.clock_timestamp(),
    CONSTRAINT pk_provider_fetch_payloads PRIMARY KEY(provider_source,payload_sha256),
    CONSTRAINT chk_provider_fetch_payloads_source CHECK (provider_source IN
        ('tcmb','coingecko','openexchangerates','twelvedata','evds')),
    CONSTRAINT chk_provider_fetch_payloads_sha CHECK (
        octet_length(payload_sha256)=32
        AND payload_sha256<>pg_catalog.decode(pg_catalog.repeat('00',32),'hex')),
    CONSTRAINT chk_provider_fetch_payloads_length CHECK
        (payload_byte_length BETWEEN 1 AND 65536)
);

CREATE TABLE public.price_observation_attributions (
    asset_id uuid NOT NULL,
    price_date date NOT NULL,
    ingestion_window_id uuid NOT NULL,
    provider_source varchar(32) NOT NULL,
    payload_sha256 bytea NOT NULL,
    source_observation_id varchar(256) NOT NULL,
    observation_sha256 bytea NOT NULL,
    authority_contract_version integer NOT NULL,
    attributed_at timestamptz NOT NULL DEFAULT pg_catalog.clock_timestamp(),
    CONSTRAINT pk_price_observation_attributions PRIMARY KEY
        (asset_id,price_date,ingestion_window_id,payload_sha256),
    CONSTRAINT fk_price_attribution_window FOREIGN KEY(ingestion_window_id)
        REFERENCES public.ingestion_windows(id) ON DELETE RESTRICT,
    CONSTRAINT fk_price_attribution_payload FOREIGN KEY(provider_source,payload_sha256)
        REFERENCES public.provider_fetch_payloads(provider_source,payload_sha256) ON DELETE RESTRICT,
    CONSTRAINT chk_price_attribution_sha CHECK(
        octet_length(observation_sha256)=32
        AND observation_sha256<>pg_catalog.decode(pg_catalog.repeat('00',32),'hex')),
    CONSTRAINT chk_price_attribution_contract CHECK(authority_contract_version>0)
);

CREATE TABLE public.inflation_observation_attributions (
    period_date date NOT NULL,
    source varchar(20) NOT NULL,
    ingestion_window_id uuid NOT NULL,
    provider_source varchar(32) NOT NULL,
    payload_sha256 bytea NOT NULL,
    source_observation_id varchar(256) NOT NULL,
    observation_sha256 bytea NOT NULL,
    authority_contract_version integer NOT NULL,
    attributed_at timestamptz NOT NULL DEFAULT pg_catalog.clock_timestamp(),
    CONSTRAINT pk_inflation_observation_attributions PRIMARY KEY
        (period_date,source,ingestion_window_id,payload_sha256),
    CONSTRAINT fk_inflation_attribution_observation FOREIGN KEY(period_date,source)
        REFERENCES public.inflation_rates(period_date,source) ON DELETE RESTRICT,
    CONSTRAINT fk_inflation_attribution_window FOREIGN KEY(ingestion_window_id)
        REFERENCES public.ingestion_windows(id) ON DELETE RESTRICT,
    CONSTRAINT fk_inflation_attribution_payload FOREIGN KEY(provider_source,payload_sha256)
        REFERENCES public.provider_fetch_payloads(provider_source,payload_sha256) ON DELETE RESTRICT,
    CONSTRAINT chk_inflation_attribution_sha CHECK(
        octet_length(observation_sha256)=32
        AND observation_sha256<>pg_catalog.decode(pg_catalog.repeat('00',32),'hex')),
    CONSTRAINT chk_inflation_attribution_contract CHECK(authority_contract_version>0)
);

CREATE OR REPLACE FUNCTION public.saydin_source_raw_allowed(payload jsonb)
RETURNS boolean LANGUAGE sql IMMUTABLE STRICT
SET search_path=pg_catalog,pg_temp
AS $body$
    SELECT jsonb_typeof(payload)='object'
       AND octet_length(payload::text)<=65536
       AND NOT EXISTS (
            SELECT 1 FROM jsonb_each(payload) item(key,value)
             WHERE item.key <> ALL(ARRAY[
                 'as_of_at','base_currency','close','currency','date','exchange',
                 'exchange_timezone','high','index_value','instrument_type','interval','low',
                 'mic_code','observation_id','open','provider_source','quote_currency',
                 'series','source_timestamp_ms','symbol','unit','volume']::text[])
                OR jsonb_typeof(item.value) NOT IN ('string','number','boolean','null')
                OR (jsonb_typeof(item.value)='string' AND octet_length(item.value#>>'{}')>512)
                OR (jsonb_typeof(item.value)='string'
                    AND item.value#>>'{}' ~* '(api[_-]?key|app[_-]?id|authorization|bearer|credential|password|secret|token)')
                OR (item.key IN ('close','high','index_value','low','open','source_timestamp_ms','volume')
                    AND jsonb_typeof(item.value)<>'number')
                OR (item.key IN ('as_of_at','base_currency','currency','date','exchange',
                                 'exchange_timezone','instrument_type','interval','mic_code',
                                 'observation_id','provider_source','quote_currency','series','symbol')
                    AND jsonb_typeof(item.value)<>'string'));
$body$;

CREATE OR REPLACE FUNCTION public.saydin_canonical_observation(payload jsonb)
RETURNS jsonb LANGUAGE sql IMMUTABLE STRICT
SET search_path=pg_catalog,pg_temp
AS $body$
    SELECT pg_catalog.jsonb_object_agg(item.key,
        CASE WHEN pg_catalog.jsonb_typeof(item.value)='number'
             THEN pg_catalog.to_jsonb(pg_catalog.trim_scale((item.value#>>'{}')::numeric))
             ELSE item.value END)
      FROM pg_catalog.jsonb_each(payload) item(key,value);
$body$;

ALTER TABLE public.price_points
    ADD CONSTRAINT chk_price_points_authority_tuple CHECK (
        (provider_source IS NULL AND source_observation_id IS NULL AND as_of_at IS NULL
         AND price_kind IS NULL AND is_final IS NULL AND observation_sha256 IS NULL
         AND authority_contract_version IS NULL)
        OR (provider_source IS NOT NULL AND source_observation_id IS NOT NULL AND as_of_at IS NOT NULL
         AND price_kind IS NOT NULL AND is_final IS TRUE AND observation_sha256 IS NOT NULL
         AND authority_contract_version>0 AND source_raw IS NOT NULL
         AND octet_length(source_observation_id) BETWEEN 1 AND 256
         AND octet_length(observation_sha256)=32
         AND observation_sha256<>pg_catalog.decode(pg_catalog.repeat('00',32),'hex')
         AND public.saydin_source_raw_allowed(source_raw)
         AND source_raw->>'provider_source'=provider_source
         AND source_raw->>'observation_id'=source_observation_id
         AND observation_sha256=pg_catalog.sha256(pg_catalog.convert_to(
             public.saydin_canonical_observation(source_raw)::text,'UTF8')))
    ) NOT VALID,
    ADD CONSTRAINT chk_price_points_provider_kind CHECK (
        provider_source IS NULL OR (provider_source,price_kind) IN (
          ('tcmb','official_reference'),('coingecko','daily_utc_reference'),
          ('openexchangerates','daily_reference'),('twelvedata','daily_close'))) NOT VALID,
    ADD CONSTRAINT chk_price_points_numeric CHECK (
        close::text NOT IN ('NaN','Infinity','-Infinity') AND close>0
        AND (volume IS NULL OR (volume::text NOT IN ('NaN','Infinity','-Infinity') AND volume>=0))
        AND (open IS NULL OR open::text NOT IN ('NaN','Infinity','-Infinity'))
        AND (high IS NULL OR high::text NOT IN ('NaN','Infinity','-Infinity'))
        AND (low IS NULL OR low::text NOT IN ('NaN','Infinity','-Infinity'))
        AND ((open IS NULL AND high IS NULL AND low IS NULL)
          OR (open IS NOT NULL AND high IS NOT NULL AND low IS NOT NULL
              AND open>0 AND high>0 AND low>0
              AND high>=GREATEST(open,close,low) AND low<=LEAST(open,close,high)))) NOT VALID,
    ADD CONSTRAINT chk_price_points_provider_shape CHECK (
        provider_source IS NULL
        OR (provider_source IN ('tcmb','coingecko','openexchangerates')
            AND open IS NULL AND high IS NULL AND low IS NULL AND volume IS NULL)
        OR (provider_source='twelvedata' AND open IS NOT NULL AND high IS NOT NULL AND low IS NOT NULL)) NOT VALID,
    ADD CONSTRAINT chk_price_points_as_of CHECK (
        provider_source IS NULL
        OR (provider_source='twelvedata'
            AND (as_of_at AT TIME ZONE 'Europe/Istanbul')::date=price_date
            AND (as_of_at AT TIME ZONE 'Europe/Istanbul')::time=time '00:00:00')
        OR (provider_source<>'twelvedata' AND (as_of_at AT TIME ZONE 'UTC')::date=price_date
            AND (provider_source<>'coingecko'
                 OR (as_of_at AT TIME ZONE 'UTC')::time=time '00:00:00'))) NOT VALID;

ALTER TABLE public.inflation_rates
    ADD CONSTRAINT chk_inflation_rates_authority_tuple CHECK (
        (provider_source IS NULL AND source_observation_id IS NULL AND as_of_at IS NULL
         AND price_kind IS NULL AND is_final IS NULL AND observation_sha256 IS NULL
         AND authority_contract_version IS NULL AND source_raw IS NULL)
        OR (provider_source='evds' AND source_observation_id IS NOT NULL AND as_of_at IS NOT NULL
         AND price_kind='cpi_index' AND is_final IS TRUE AND observation_sha256 IS NOT NULL
         AND authority_contract_version>0 AND source_raw IS NOT NULL
         AND octet_length(source_observation_id) BETWEEN 1 AND 256
         AND octet_length(observation_sha256)=32
         AND observation_sha256<>pg_catalog.decode(pg_catalog.repeat('00',32),'hex')
         AND public.saydin_source_raw_allowed(source_raw)
         AND source_raw->>'provider_source'=provider_source
         AND source_raw->>'observation_id'=source_observation_id
         AND observation_sha256=pg_catalog.sha256(pg_catalog.convert_to(
             public.saydin_canonical_observation(source_raw)::text,'UTF8')))
    ) NOT VALID,
    ADD CONSTRAINT chk_inflation_rates_numeric CHECK (
        index_value::text NOT IN ('NaN','Infinity','-Infinity') AND index_value>0
        AND EXTRACT(day FROM period_date)=1) NOT VALID,
    ADD CONSTRAINT chk_inflation_rates_as_of CHECK (
        provider_source IS NULL OR ((as_of_at AT TIME ZONE 'UTC')::date=period_date
            AND (as_of_at AT TIME ZONE 'UTC')::time=time '00:00:00')) NOT VALID;

CREATE OR REPLACE FUNCTION public.enforce_price_point_authority()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp
AS $authority$
DECLARE presented_window uuid; asset_source_id text; evidence_keys text[];
BEGIN
    IF TG_OP='INSERT' THEN
        NEW.ingested_at:=pg_catalog.clock_timestamp();
    END IF;
    IF NEW.provider_source IS NULL OR NEW.source_observation_id IS NULL OR NEW.as_of_at IS NULL
       OR NEW.price_kind IS NULL OR NEW.is_final IS DISTINCT FROM TRUE
       OR NEW.observation_sha256 IS NULL OR NEW.authority_contract_version IS NULL
       OR NEW.source_raw IS NULL THEN
        RAISE EXCEPTION 'complete final price authority required'
            USING ERRCODE='23514',CONSTRAINT='chk_price_points_authority_tuple';
    END IF;
    BEGIN
        presented_window:=NULLIF(pg_catalog.current_setting('saydin.ingestion_window_id',true),'')::uuid;
    EXCEPTION WHEN invalid_text_representation THEN
        RAISE EXCEPTION 'invalid price ingestion capability' USING ERRCODE='42501';
    END;
    SELECT a.source_id INTO asset_source_id
      FROM public.ingestion_windows iw JOIN public.assets a ON a.id=NEW.asset_id
         WHERE iw.id=presented_window AND iw.asset_id=NEW.asset_id
           AND iw.source=NEW.provider_source AND a.source=NEW.provider_source
           AND iw.contract_version=NEW.authority_contract_version
           AND iw.job_type IN ('historical_backfill','daily_update')
           AND NEW.price_date BETWEEN iw.range_start AND iw.range_end;
    IF presented_window IS NULL OR asset_source_id IS NULL THEN
        RAISE EXCEPTION 'price authority window/source/contract mismatch' USING ERRCODE='42501';
    END IF;
    SELECT pg_catalog.array_agg(key ORDER BY key) INTO evidence_keys
      FROM pg_catalog.jsonb_object_keys(NEW.source_raw) key;
    IF NOT public.saydin_source_raw_allowed(NEW.source_raw)
       OR NEW.source_raw->>'date' IS DISTINCT FROM pg_catalog.to_char(NEW.price_date,'YYYY-MM-DD')
       OR (NEW.source_raw->>'as_of_at')::timestamptz IS DISTINCT FROM NEW.as_of_at
       OR (NEW.source_raw->>'close')::numeric IS DISTINCT FROM NEW.close
       OR (NEW.source_raw ? 'open') IS DISTINCT FROM (NEW.open IS NOT NULL)
       OR (NEW.source_raw ? 'high') IS DISTINCT FROM (NEW.high IS NOT NULL)
       OR (NEW.source_raw ? 'low') IS DISTINCT FROM (NEW.low IS NOT NULL)
       OR (NEW.source_raw ? 'volume') IS DISTINCT FROM (NEW.volume IS NOT NULL)
       OR (NEW.open IS NOT NULL AND (NEW.source_raw->>'open')::numeric IS DISTINCT FROM NEW.open)
       OR (NEW.high IS NOT NULL AND (NEW.source_raw->>'high')::numeric IS DISTINCT FROM NEW.high)
       OR (NEW.low IS NOT NULL AND (NEW.source_raw->>'low')::numeric IS DISTINCT FROM NEW.low)
       OR (NEW.volume IS NOT NULL AND (NEW.source_raw->>'volume')::numeric IS DISTINCT FROM NEW.volume)
       OR (NEW.provider_source='coingecko' AND (
            evidence_keys IS DISTINCT FROM ARRAY['as_of_at','close','date','observation_id','provider_source','quote_currency','source_timestamp_ms','symbol']::text[]
            OR NEW.source_raw->>'symbol' IS DISTINCT FROM asset_source_id
            OR NEW.source_raw->>'quote_currency' IS DISTINCT FROM 'TRY'
            OR (NEW.source_raw->>'source_timestamp_ms')::bigint IS DISTINCT FROM
               (extract(epoch FROM NEW.as_of_at)*1000)::bigint
            OR NEW.source_observation_id IS DISTINCT FROM pg_catalog.concat(
               'coingecko:',asset_source_id,':try:',(extract(epoch FROM NEW.as_of_at)*1000)::bigint::text)))
       OR (NEW.provider_source='tcmb' AND (
            evidence_keys IS DISTINCT FROM ARRAY['as_of_at','close','currency','date','observation_id','provider_source','unit']::text[]
            OR NEW.source_raw->>'currency' IS DISTINCT FROM CASE
               WHEN asset_source_id LIKE '%.%.%' THEN pg_catalog.split_part(asset_source_id,'.',3)
               ELSE asset_source_id END
            OR (NEW.source_raw->>'unit')::numeric<=0
            OR NEW.source_observation_id IS DISTINCT FROM pg_catalog.concat(
               'tcmb:',CASE WHEN asset_source_id LIKE '%.%.%'
                            THEN pg_catalog.split_part(asset_source_id,'.',3)
                            ELSE asset_source_id END,':',
               pg_catalog.to_char(NEW.price_date,'YYYY-MM-DD'),':forex_buying')))
       OR (NEW.provider_source='openexchangerates' AND (
            evidence_keys IS DISTINCT FROM ARRAY['as_of_at','base_currency','close','date','observation_id','provider_source','quote_currency','symbol','unit']::text[]
            OR NEW.source_raw->>'base_currency' IS DISTINCT FROM 'USD'
            OR NEW.source_raw->>'quote_currency' IS DISTINCT FROM 'TRY'
            OR NEW.source_raw->>'symbol' IS DISTINCT FROM asset_source_id
            OR NEW.source_raw->>'unit' IS DISTINCT FROM 'gram'
            OR NEW.source_observation_id IS DISTINCT FROM pg_catalog.concat(
               'openexchangerates:',asset_source_id,':',pg_catalog.to_char(NEW.price_date,'YYYY-MM-DD'))))
       OR (NEW.provider_source='twelvedata' AND (
            evidence_keys IS DISTINCT FROM ARRAY['as_of_at','close','currency','date','exchange','exchange_timezone','high','instrument_type','interval','low','mic_code','observation_id','open','provider_source','symbol','volume']::text[]
            OR NEW.source_raw->>'symbol' IS DISTINCT FROM pg_catalog.split_part(asset_source_id,':',1)
            OR NEW.source_raw->>'interval' IS DISTINCT FROM '1day'
            OR NEW.source_raw->>'exchange' IS DISTINCT FROM 'BIST'
            OR NEW.source_raw->>'mic_code' IS DISTINCT FROM 'XIST'
            OR NEW.source_raw->>'exchange_timezone' IS DISTINCT FROM 'Europe/Istanbul'
            OR NEW.source_raw->>'currency' IS DISTINCT FROM 'TRY'
            OR NEW.source_raw->>'instrument_type' NOT IN ('Common Stock','Stock')
            OR NEW.source_observation_id IS DISTINCT FROM pg_catalog.concat(
               'twelvedata:',asset_source_id,':',pg_catalog.to_char(NEW.price_date,'YYYY-MM-DD'),':1day'))) THEN
        RAISE EXCEPTION 'price normalized evidence mismatch'
            USING ERRCODE='23514',CONSTRAINT='chk_price_points_authority_tuple';
    END IF;
    IF TG_OP='UPDATE' AND OLD.provider_source IS NOT NULL AND (
       NEW.provider_source IS DISTINCT FROM OLD.provider_source
       OR NEW.source_observation_id IS DISTINCT FROM OLD.source_observation_id
       OR NEW.as_of_at IS DISTINCT FROM OLD.as_of_at OR NEW.price_kind IS DISTINCT FROM OLD.price_kind
       OR NEW.is_final IS DISTINCT FROM OLD.is_final
       OR NEW.observation_sha256 IS DISTINCT FROM OLD.observation_sha256
       OR NEW.authority_contract_version IS DISTINCT FROM OLD.authority_contract_version
       OR NEW.source_raw IS DISTINCT FROM OLD.source_raw OR NEW.close IS DISTINCT FROM OLD.close
       OR NEW.open IS DISTINCT FROM OLD.open OR NEW.high IS DISTINCT FROM OLD.high
       OR NEW.low IS DISTINCT FROM OLD.low OR NEW.volume IS DISTINCT FROM OLD.volume
       OR NEW.ingested_at IS DISTINCT FROM OLD.ingested_at) THEN
        RAISE EXCEPTION 'normalized price authority is immutable; repair authorization unavailable'
            USING ERRCODE='23514',CONSTRAINT='chk_price_points_authority_immutable';
    END IF;
    RETURN NEW;
END;
$authority$;

CREATE OR REPLACE FUNCTION public.enforce_inflation_rate_authority()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp
AS $authority$
DECLARE presented_window uuid; evidence_keys text[];
BEGIN
    IF TG_OP='INSERT' THEN
        NEW.created_at:=pg_catalog.clock_timestamp();
        NEW.updated_at:=NEW.created_at;
    END IF;
    IF NEW.provider_source IS NULL OR NEW.source_observation_id IS NULL OR NEW.as_of_at IS NULL
       OR NEW.price_kind IS NULL OR NEW.is_final IS DISTINCT FROM TRUE
       OR NEW.observation_sha256 IS NULL OR NEW.authority_contract_version IS NULL
       OR NEW.source_raw IS NULL THEN
        RAISE EXCEPTION 'complete final inflation authority required'
            USING ERRCODE='23514',CONSTRAINT='chk_inflation_rates_authority_tuple';
    END IF;
    BEGIN
        presented_window:=NULLIF(pg_catalog.current_setting('saydin.ingestion_window_id',true),'')::uuid;
    EXCEPTION WHEN invalid_text_representation THEN
        RAISE EXCEPTION 'invalid inflation ingestion capability' USING ERRCODE='42501';
    END;
    IF presented_window IS NULL OR NEW.source<>'tuik' OR NOT EXISTS (
        SELECT 1 FROM public.ingestion_windows iw WHERE iw.id=presented_window
          AND iw.asset_id IS NULL AND iw.source='evds' AND NEW.provider_source='evds'
          AND iw.contract_version=NEW.authority_contract_version
          AND iw.job_type IN ('inflation_backfill','inflation_daily')
          AND NEW.period_date BETWEEN iw.range_start AND iw.range_end) THEN
        RAISE EXCEPTION 'inflation authority window/source/contract mismatch' USING ERRCODE='42501';
    END IF;
    SELECT pg_catalog.array_agg(key ORDER BY key) INTO evidence_keys
      FROM pg_catalog.jsonb_object_keys(NEW.source_raw) key;
    IF NOT public.saydin_source_raw_allowed(NEW.source_raw)
       OR evidence_keys IS DISTINCT FROM ARRAY['as_of_at','date','index_value','observation_id','provider_source','series']::text[]
       OR NEW.source_raw->>'date' IS DISTINCT FROM pg_catalog.to_char(NEW.period_date,'YYYY-MM-DD')
       OR (NEW.source_raw->>'as_of_at')::timestamptz IS DISTINCT FROM NEW.as_of_at
       OR NEW.source_raw->>'series' IS DISTINCT FROM 'TP.FG.J0'
       OR NEW.source_observation_id IS DISTINCT FROM pg_catalog.concat(
          'evds:TP_FG_J0:',pg_catalog.to_char(NEW.period_date,'YYYY-MM'))
       OR (NEW.source_raw->>'index_value')::numeric IS DISTINCT FROM NEW.index_value THEN
        RAISE EXCEPTION 'inflation normalized evidence mismatch'
            USING ERRCODE='23514',CONSTRAINT='chk_inflation_rates_authority_tuple';
    END IF;
    IF TG_OP='UPDATE' AND OLD.provider_source IS NOT NULL AND (
       NEW.provider_source IS DISTINCT FROM OLD.provider_source
       OR NEW.source_observation_id IS DISTINCT FROM OLD.source_observation_id
       OR NEW.as_of_at IS DISTINCT FROM OLD.as_of_at OR NEW.price_kind IS DISTINCT FROM OLD.price_kind
       OR NEW.is_final IS DISTINCT FROM OLD.is_final
       OR NEW.observation_sha256 IS DISTINCT FROM OLD.observation_sha256
       OR NEW.authority_contract_version IS DISTINCT FROM OLD.authority_contract_version
       OR NEW.source_raw IS DISTINCT FROM OLD.source_raw OR NEW.index_value IS DISTINCT FROM OLD.index_value
       OR NEW.source IS DISTINCT FROM OLD.source
       OR NEW.created_at IS DISTINCT FROM OLD.created_at
       OR NEW.updated_at IS DISTINCT FROM OLD.updated_at) THEN
        RAISE EXCEPTION 'normalized inflation authority is immutable; repair authorization unavailable'
            USING ERRCODE='23514',CONSTRAINT='chk_inflation_rates_authority_immutable';
    END IF;
    RETURN NEW;
END;
$authority$;

CREATE OR REPLACE FUNCTION public.enforce_observation_attribution()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp
AS $attribution$
DECLARE presented_window uuid; presented_token uuid;
BEGIN
    IF TG_OP<>'INSERT' THEN
        RAISE EXCEPTION 'observation attribution is append-only' USING ERRCODE='55000';
    END IF;
    BEGIN
        presented_window:=NULLIF(pg_catalog.current_setting('saydin.ingestion_window_id',true),'')::uuid;
        presented_token:=NULLIF(pg_catalog.current_setting('saydin.ingestion_lease_token',true),'')::uuid;
    EXCEPTION WHEN invalid_text_representation THEN
        RAISE EXCEPTION 'invalid attribution capability' USING ERRCODE='42501';
    END;
    IF presented_window IS NULL OR presented_token IS NULL
       OR presented_window IS DISTINCT FROM NEW.ingestion_window_id THEN
        RAISE EXCEPTION 'attribution window differs from presented capability' USING ERRCODE='42501';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM public.ingestion_windows iw
         WHERE iw.id=presented_window AND iw.state='running'
           AND iw.lease_token=presented_token
           AND iw.lease_until>pg_catalog.clock_timestamp()) THEN
        RAISE EXCEPTION 'attribution requires live window lease' USING ERRCODE='42501';
    END IF;
    IF TG_TABLE_NAME='price_observation_attributions' THEN
        IF NOT EXISTS (SELECT 1 FROM public.price_points p
            JOIN public.ingestion_windows iw ON iw.id=NEW.ingestion_window_id
            JOIN public.assets a ON a.id=p.asset_id
            WHERE p.asset_id=NEW.asset_id AND p.price_date=NEW.price_date
              AND p.provider_source=NEW.provider_source
              AND p.source_observation_id=NEW.source_observation_id
              AND p.observation_sha256=NEW.observation_sha256
              AND p.authority_contract_version=NEW.authority_contract_version
              AND iw.asset_id=p.asset_id AND iw.source=p.provider_source
              AND iw.state='running' AND iw.lease_token=presented_token
              AND iw.lease_until>pg_catalog.clock_timestamp()
              AND a.source=p.provider_source
              AND iw.contract_version=p.authority_contract_version
              AND iw.job_type IN ('historical_backfill','daily_update')
              AND p.price_date BETWEEN iw.range_start AND iw.range_end) THEN
            RAISE EXCEPTION 'price attribution does not match observation/window scope' USING ERRCODE='23503';
        END IF;
    ELSE
        IF NOT EXISTS (SELECT 1 FROM public.inflation_rates r
            JOIN public.ingestion_windows iw ON iw.id=NEW.ingestion_window_id
            WHERE r.period_date=NEW.period_date AND r.source=NEW.source
              AND r.provider_source=NEW.provider_source
              AND r.source_observation_id=NEW.source_observation_id
              AND r.observation_sha256=NEW.observation_sha256
              AND r.authority_contract_version=NEW.authority_contract_version
              AND r.source='tuik' AND iw.asset_id IS NULL AND iw.source='evds'
              AND iw.state='running' AND iw.lease_token=presented_token
              AND iw.lease_until>pg_catalog.clock_timestamp()
              AND r.provider_source='evds'
              AND iw.contract_version=r.authority_contract_version
              AND iw.job_type IN ('inflation_backfill','inflation_daily')
              AND r.period_date BETWEEN iw.range_start AND iw.range_end) THEN
            RAISE EXCEPTION 'inflation attribution does not match observation/window scope' USING ERRCODE='23503';
        END IF;
    END IF;
    NEW.attributed_at:=pg_catalog.clock_timestamp();
    RETURN NEW;
END;
$attribution$;

CREATE OR REPLACE FUNCTION public.enforce_fetch_payload_insert()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp
AS $fetch$
DECLARE presented_window uuid; presented_token uuid;
BEGIN
    BEGIN
        presented_window:=NULLIF(pg_catalog.current_setting('saydin.ingestion_window_id',true),'')::uuid;
        presented_token:=NULLIF(pg_catalog.current_setting('saydin.ingestion_lease_token',true),'')::uuid;
    EXCEPTION WHEN invalid_text_representation THEN
        RAISE EXCEPTION 'invalid fetch payload capability' USING ERRCODE='42501';
    END;
    IF presented_window IS NULL OR presented_token IS NULL OR NOT EXISTS (
        SELECT 1 FROM public.ingestion_windows iw
         WHERE iw.id=presented_window AND iw.source=NEW.provider_source
           AND iw.state='running' AND iw.lease_token=presented_token
           AND iw.lease_until>pg_catalog.clock_timestamp()) THEN
        RAISE EXCEPTION 'fetch payload requires live provider window lease' USING ERRCODE='42501';
    END IF;
    NEW.first_observed_at:=pg_catalog.clock_timestamp();
    RETURN NEW;
END;
$fetch$;

CREATE OR REPLACE FUNCTION public.reject_fetch_payload_mutation()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp
AS $immutable$
BEGIN
    RAISE EXCEPTION 'fetch payload ledger is append-only' USING ERRCODE='55000';
END;
$immutable$;

CREATE TRIGGER trg_price_points_authority BEFORE INSERT OR UPDATE ON public.price_points
FOR EACH ROW EXECUTE FUNCTION public.enforce_price_point_authority();
CREATE TRIGGER trg_inflation_rates_authority BEFORE INSERT OR UPDATE ON public.inflation_rates
FOR EACH ROW EXECUTE FUNCTION public.enforce_inflation_rate_authority();
ALTER TABLE public.inflation_rates ENABLE ALWAYS TRIGGER trg_inflation_rates_authority;
CREATE TRIGGER trg_price_attribution_append_only BEFORE INSERT OR UPDATE OR DELETE
ON public.price_observation_attributions FOR EACH ROW EXECUTE FUNCTION public.enforce_observation_attribution();
CREATE TRIGGER trg_inflation_attribution_append_only BEFORE INSERT OR UPDATE OR DELETE
ON public.inflation_observation_attributions FOR EACH ROW EXECUTE FUNCTION public.enforce_observation_attribution();
CREATE TRIGGER trg_fetch_payload_append_only BEFORE UPDATE OR DELETE
ON public.provider_fetch_payloads FOR EACH ROW EXECUTE FUNCTION public.reject_fetch_payload_mutation();
CREATE TRIGGER trg_fetch_payload_live_lease BEFORE INSERT
ON public.provider_fetch_payloads FOR EACH ROW EXECUTE FUNCTION public.enforce_fetch_payload_insert();
CREATE TRIGGER trg_fetch_payload_no_truncate BEFORE TRUNCATE
ON public.provider_fetch_payloads FOR EACH STATEMENT EXECUTE FUNCTION public.reject_fetch_payload_mutation();
CREATE TRIGGER trg_price_attribution_no_truncate BEFORE TRUNCATE
ON public.price_observation_attributions FOR EACH STATEMENT EXECUTE FUNCTION public.reject_fetch_payload_mutation();
CREATE TRIGGER trg_inflation_attribution_no_truncate BEFORE TRUNCATE
ON public.inflation_observation_attributions FOR EACH STATEMENT EXECUTE FUNCTION public.reject_fetch_payload_mutation();

DO $acl$
DECLARE owner_role text; ingestion_cap text; audit_cap text;
BEGIN
    SELECT contract.owner_role,contract.ingestion_capability_role,contract.audit_capability_role
      INTO owner_role,ingestion_cap,audit_cap
      FROM public.saydin_role_contract contract
     WHERE singleton=1 AND contract_schema_version=1
       AND database_name=pg_catalog.current_database();
    IF owner_role IS NULL OR ingestion_cap IS NULL OR audit_cap IS NULL
       OR owner_role IS DISTINCT FROM CURRENT_USER
       OR NOT EXISTS(SELECT 1 FROM pg_catalog.pg_roles WHERE rolname=ingestion_cap)
       OR NOT EXISTS(SELECT 1 FROM pg_catalog.pg_roles WHERE rolname=audit_cap) THEN
        RAISE EXCEPTION 'price authority role contract rejected' USING ERRCODE='22023';
    END IF;
    REVOKE ALL ON public.provider_fetch_payloads,
        public.price_observation_attributions,public.inflation_observation_attributions FROM PUBLIC;
    EXECUTE pg_catalog.format('REVOKE INSERT,UPDATE ON public.price_points,public.inflation_rates FROM %I',ingestion_cap);
    EXECUTE pg_catalog.format('GRANT INSERT(asset_id,price_date,close,open,high,low,volume,provider_source,source_observation_id,as_of_at,price_kind,is_final,observation_sha256,authority_contract_version,source_raw) ON public.price_points TO %I',ingestion_cap);
    EXECUTE pg_catalog.format('GRANT UPDATE(close,open,high,low,volume,provider_source,source_observation_id,as_of_at,price_kind,is_final,observation_sha256,authority_contract_version,source_raw) ON public.price_points TO %I',ingestion_cap);
    EXECUTE pg_catalog.format('GRANT INSERT(period_date,index_value,source,provider_source,source_observation_id,as_of_at,price_kind,is_final,observation_sha256,authority_contract_version,source_raw) ON public.inflation_rates TO %I',ingestion_cap);
    EXECUTE pg_catalog.format('GRANT UPDATE(index_value,provider_source,source_observation_id,as_of_at,price_kind,is_final,observation_sha256,authority_contract_version,source_raw) ON public.inflation_rates TO %I',ingestion_cap);
    EXECUTE pg_catalog.format('GRANT SELECT ON public.provider_fetch_payloads,public.price_observation_attributions,public.inflation_observation_attributions TO %I',ingestion_cap);
    EXECUTE pg_catalog.format('GRANT INSERT(provider_source,payload_sha256,payload_byte_length) ON public.provider_fetch_payloads TO %I',ingestion_cap);
    EXECUTE pg_catalog.format('GRANT INSERT(asset_id,price_date,ingestion_window_id,provider_source,payload_sha256,source_observation_id,observation_sha256,authority_contract_version) ON public.price_observation_attributions TO %I',ingestion_cap);
    EXECUTE pg_catalog.format('GRANT INSERT(period_date,source,ingestion_window_id,provider_source,payload_sha256,source_observation_id,observation_sha256,authority_contract_version) ON public.inflation_observation_attributions TO %I',ingestion_cap);
    EXECUTE pg_catalog.format('GRANT SELECT ON public.provider_fetch_payloads,public.price_observation_attributions,public.inflation_observation_attributions TO %I',audit_cap);
    REVOKE ALL ON FUNCTION public.saydin_source_raw_allowed(jsonb) FROM PUBLIC;
    REVOKE ALL ON FUNCTION public.saydin_canonical_observation(jsonb) FROM PUBLIC;
    EXECUTE pg_catalog.format('GRANT EXECUTE ON FUNCTION public.saydin_source_raw_allowed(jsonb) TO %I',ingestion_cap);
    EXECUTE pg_catalog.format('GRANT EXECUTE ON FUNCTION public.saydin_canonical_observation(jsonb) TO %I',ingestion_cap);
END;
$acl$;

REVOKE ALL ON FUNCTION public.enforce_price_point_authority() FROM PUBLIC;
REVOKE ALL ON FUNCTION public.enforce_inflation_rate_authority() FROM PUBLIC;
REVOKE ALL ON FUNCTION public.enforce_observation_attribution() FROM PUBLIC;
REVOKE ALL ON FUNCTION public.enforce_fetch_payload_insert() FROM PUBLIC;
REVOKE ALL ON FUNCTION public.reject_fetch_payload_mutation() FROM PUBLIC;

COMMENT ON TABLE public.provider_fetch_payloads IS
 'Immutable bounded HTTP-payload hashes; raw response bytes are not persisted.';
COMMENT ON TABLE public.price_observation_attributions IS
 'Append-only many-window/many-envelope provenance for normalized price observations.';
COMMENT ON TABLE public.inflation_observation_attributions IS
 'Append-only many-window/many-envelope provenance for normalized CPI observations.';

COMMIT;
