-- ============================================================
-- Saydın — Initial Database Schema
-- Migration: 001_initial.sql
-- ============================================================

-- ============================================================
-- EXTENSIONS
-- ============================================================

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS timescaledb;

-- ============================================================
-- ENUMS
-- ============================================================

CREATE TYPE asset_category AS ENUM (
    'currency',
    'precious_metal',
    'stock',
    'crypto'
);

-- ============================================================
-- ASSETS
-- ============================================================

CREATE TABLE assets (
    id            UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    symbol        VARCHAR(20)   NOT NULL,
    display_name  VARCHAR(100)  NOT NULL,
    category      asset_category NOT NULL,
    is_active     BOOLEAN       NOT NULL DEFAULT true,
    source        VARCHAR(50)   NOT NULL,
    source_id     VARCHAR(100),
    metadata      JSONB,
    created_at    TIMESTAMPTZ   NOT NULL DEFAULT NOW(),

    CONSTRAINT uq_assets_symbol UNIQUE (symbol)
);

COMMENT ON TABLE assets IS 'Desteklenen tüm finansal varlıklar';
COMMENT ON COLUMN assets.symbol IS 'Benzersiz sembol: USDTRY, XAU_TRY_GRAM, BTC, THYAO';
COMMENT ON COLUMN assets.source IS 'Veri kaynağı: tcmb, coingecko, goldapi, twelvedata';
COMMENT ON COLUMN assets.source_id IS 'Dış API tanımlayıcısı: TP.DK.USD.A, bitcoin, THYAO:BIST';
COMMENT ON COLUMN assets.metadata IS 'Esnek ek bilgi: decimal_places, display_unit, lot_size';

-- ============================================================
-- PRICE POINTS (TimescaleDB Hypertable)
-- ============================================================

CREATE TABLE price_points (
    asset_id      UUID          NOT NULL,
    price_date    DATE          NOT NULL,
    open          NUMERIC(18,6),
    high          NUMERIC(18,6),
    low           NUMERIC(18,6),
    close         NUMERIC(18,6) NOT NULL,
    volume        NUMERIC(24,4),
    source_raw    JSONB,
    ingested_at   TIMESTAMPTZ   NOT NULL DEFAULT NOW(),

    CONSTRAINT pk_price_points PRIMARY KEY (asset_id, price_date),
    CONSTRAINT fk_price_points_asset FOREIGN KEY (asset_id) REFERENCES assets(id)
);

COMMENT ON TABLE price_points IS 'Günlük OHLCV fiyat verisi. TimescaleDB hypertable.';
COMMENT ON COLUMN price_points.close IS 'Kapanış fiyatı — tüm ya-alsaydım hesaplamalarında kullanılır';
COMMENT ON COLUMN price_points.source_raw IS 'Ham API yanıtı — veri kalitesi kontrolü ve yeniden işleme için';

-- NOT: float/double KULLANILMAZ. Finansal değerler NUMERIC(18,6) tipindedir.

-- TimescaleDB hypertable: aylık partisyonlama
SELECT create_hypertable(
    'price_points',
    'price_date',
    chunk_time_interval => INTERVAL '1 month',
    if_not_exists => TRUE
);

-- Birincil sorgu indeksi: belirli asset'in belirli tarihteki fiyatı
CREATE INDEX idx_price_points_asset_date
    ON price_points (asset_id, price_date DESC);

-- ============================================================
-- INGESTION JOBS
-- ============================================================

CREATE TABLE ingestion_jobs (
    id                UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    asset_id          UUID        NOT NULL,
    job_type          VARCHAR(50) NOT NULL,
    started_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    finished_at       TIMESTAMPTZ,
    status            VARCHAR(20) NOT NULL DEFAULT 'running',
    records_upserted  INT,
    error_message     TEXT,
    date_range_start  DATE,
    date_range_end    DATE,

    CONSTRAINT fk_ingestion_jobs_asset FOREIGN KEY (asset_id) REFERENCES assets(id),
    CONSTRAINT chk_ingestion_jobs_type CHECK (job_type IN ('historical_backfill', 'daily_update')),
    CONSTRAINT chk_ingestion_jobs_status CHECK (status IN ('running', 'success', 'failed'))
);

COMMENT ON TABLE ingestion_jobs IS 'Veri çekme işlemlerinin takibi';

CREATE INDEX idx_ingestion_jobs_asset_status
    ON ingestion_jobs (asset_id, status, started_at DESC);

-- ============================================================
-- USERS
-- ============================================================

CREATE TABLE users (
    id            UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    device_id     VARCHAR(200),
    email         VARCHAR(200),
    tier          VARCHAR(20)   NOT NULL DEFAULT 'free',
    created_at    TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    last_seen_at  TIMESTAMPTZ,

    CONSTRAINT uq_users_device_id UNIQUE (device_id),
    CONSTRAINT uq_users_email UNIQUE (email),
    CONSTRAINT chk_users_tier CHECK (tier IN ('free', 'premium'))
);

COMMENT ON TABLE users IS 'Kullanıcı hesapları. MVP: device_id ile anonim, Phase 2: email ile kayıtlı.';
COMMENT ON COLUMN users.device_id IS 'Flutter SecureStorage''dan gelen UUID. Kayıt gerektirmez.';
COMMENT ON COLUMN users.email IS 'Phase 2: premium ödeme sırasında bağlanır. NULL olabilir.';

-- ============================================================
-- SAVED SCENARIOS
-- ============================================================

CREATE TABLE saved_scenarios (
    id             UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id        UUID          NOT NULL,
    asset_id       UUID          NOT NULL,
    buy_date       DATE          NOT NULL,
    sell_date      DATE,
    quantity       NUMERIC(18,8) NOT NULL,
    quantity_unit  VARCHAR(20)   NOT NULL,
    label          VARCHAR(200),
    created_at     TIMESTAMPTZ   NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_saved_scenarios_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
    CONSTRAINT fk_saved_scenarios_asset FOREIGN KEY (asset_id) REFERENCES assets(id),
    CONSTRAINT chk_saved_scenarios_unit CHECK (quantity_unit IN ('try', 'units', 'grams')),
    CONSTRAINT chk_saved_scenarios_dates CHECK (sell_date IS NULL OR sell_date > buy_date)
);

COMMENT ON TABLE saved_scenarios IS 'Kullanıcıların kaydettiği ya-alsaydım senaryoları';
COMMENT ON COLUMN saved_scenarios.sell_date IS 'NULL = bugüne kadar hesapla';
COMMENT ON COLUMN saved_scenarios.quantity_unit IS 'try=TL tutarı, units=birim sayısı, grams=gram';

-- Kullanıcı başına senaryo sayısı (uygulama katmanında kontrol edilir)
-- free: max 5, premium: sınırsız

CREATE INDEX idx_saved_scenarios_user
    ON saved_scenarios (user_id, created_at DESC);

-- ============================================================
-- MARKET HOLIDAYS
-- ============================================================

CREATE TABLE market_holidays (
    asset_id      UUID  NOT NULL,
    holiday_date  DATE  NOT NULL,
    reason        VARCHAR(200),

    CONSTRAINT pk_market_holidays PRIMARY KEY (asset_id, holiday_date),
    CONSTRAINT fk_market_holidays_asset FOREIGN KEY (asset_id) REFERENCES assets(id)
);

COMMENT ON TABLE market_holidays IS 'Piyasa tatil günleri. Ingestion worker tatil günlerini eksik veri saymaz.';

-- ============================================================
-- SEED DATA — Başlangıç Asset'leri
-- ============================================================

INSERT INTO assets (symbol, display_name, category, is_active, source, source_id, metadata) VALUES
    ('USDTRY',       'Dolar/TL',                'currency',       true, 'tcmb',       'TP.DK.USD.A',    '{"decimal_places": 4}'::jsonb),
    ('EURTRY',       'Euro/TL',                 'currency',       true, 'tcmb',       'TP.DK.EUR.A',    '{"decimal_places": 4}'::jsonb),
    ('XAU_TRY_GRAM', 'Altın (Gram/TL)',         'precious_metal', true, 'goldapi',    'XAU',            '{"display_unit": "gram", "decimal_places": 2}'::jsonb),
    ('XAG_TRY_GRAM', 'Gümüş (Gram/TL)',         'precious_metal', true, 'goldapi',    'XAG',            '{"display_unit": "gram", "decimal_places": 2}'::jsonb),
    ('BTC',          'Bitcoin',                 'crypto',         true, 'coingecko',  'bitcoin',        '{"decimal_places": 2}'::jsonb),
    ('ETH',          'Ethereum',                'crypto',         true, 'coingecko',  'ethereum',       '{"decimal_places": 2}'::jsonb),
    ('THYAO',        'Türk Hava Yolları',       'stock',          true, 'twelvedata', 'THYAO:BIST',     '{"decimal_places": 2}'::jsonb),
    ('GARAN',        'Garanti BBVA',            'stock',          true, 'twelvedata', 'GARAN:BIST',     '{"decimal_places": 2}'::jsonb);
