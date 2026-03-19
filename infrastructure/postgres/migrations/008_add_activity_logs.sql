-- Kullanıcı aktivite loglama tablosu
-- Bkz: docs/architecture/activity-logging.md

BEGIN;

CREATE TABLE activity_logs (
    id              UUID            NOT NULL DEFAULT gen_random_uuid(),

    -- Kim?
    user_id         UUID            REFERENCES users(id) ON DELETE SET NULL,
    device_id       VARCHAR(200)    NOT NULL,

    -- Ne?
    action          VARCHAR(30)     NOT NULL,

    -- Cihaz bilgisi
    ip_address      INET,
    device_os       VARCHAR(20),
    os_version      VARCHAR(20),
    app_version     VARCHAR(30),

    -- İşlem verisi (türe göre değişen)
    data            JSONB,

    -- Sonuç
    status_code     SMALLINT        NOT NULL,
    duration_ms     INT,
    error_code      VARCHAR(50),

    -- Zaman
    created_at      TIMESTAMPTZ     NOT NULL DEFAULT now(),

    PRIMARY KEY (id, created_at)
);

-- TimescaleDB hypertable (haftalık chunk — log verisi hızlı büyür)
SELECT create_hypertable('activity_logs', 'created_at',
    chunk_time_interval => INTERVAL '1 week');

-- Raporlama indexleri
CREATE INDEX idx_activity_logs_user
    ON activity_logs (user_id, created_at DESC);

CREATE INDEX idx_activity_logs_action
    ON activity_logs (action, created_at DESC);

-- JSONB üzerinde asset bazlı sorgular için
CREATE INDEX idx_activity_logs_asset_symbol
    ON activity_logs USING GIN (data jsonb_path_ops);

-- Action türleri CHECK constraint
ALTER TABLE activity_logs ADD CONSTRAINT chk_activity_action
    CHECK (action IN (
        'what_if_calculate',
        'what_if_compare',
        'what_if_dca',
        'what_if_reverse',
        'scenario_save',
        'scenario_delete',
        'scenario_list',
        'assets_list',
        'asset_price',
        'asset_price_range',
        'config_fetch'
    ));

COMMENT ON TABLE activity_logs IS 'Kullanıcı aktivite logları — Channel pattern ile asenkron yazılır';
COMMENT ON COLUMN activity_logs.action IS 'what_if_calculate | what_if_compare | what_if_dca | what_if_reverse | scenario_save | scenario_delete | scenario_list | assets_list | asset_price | asset_price_range | config_fetch';
COMMENT ON COLUMN activity_logs.data IS 'Action türüne göre değişen JSONB veri (bkz: docs/architecture/activity-logging.md §3.2)';
COMMENT ON COLUMN activity_logs.ip_address IS 'Son oktet maskelenmiş IP adresi (KVKK uyumlu)';

-- 7 günden eski chunk''ları otomatik sıkıştır
ALTER TABLE activity_logs SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'action',
    timescaledb.compress_orderby = 'created_at DESC'
);
SELECT add_compression_policy('activity_logs', INTERVAL '7 days');

COMMIT;
