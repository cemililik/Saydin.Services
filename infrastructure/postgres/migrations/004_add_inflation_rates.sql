-- ============================================================
-- Migration 004: inflation_rates tablosu
-- Aylık TÜFE endeks değerleri (TÜİK, 2003=100 bazlı).
-- period_date her ayın 1. günüdür.
-- Reel getiri hesabı: (satış_endeks / alış_endeks) - 1
-- ============================================================

CREATE TABLE inflation_rates (
    period_date DATE           NOT NULL,
    index_value NUMERIC(12, 4) NOT NULL,
    source      VARCHAR(20)    NOT NULL DEFAULT 'tuik',
    created_at  TIMESTAMPTZ    NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ    NOT NULL DEFAULT NOW(),
    CONSTRAINT pk_inflation_rates PRIMARY KEY (period_date)
);

CREATE INDEX idx_inflation_rates_period ON inflation_rates (period_date DESC);

COMMENT ON TABLE inflation_rates IS
    'Aylık TÜFE endeks değerleri. period_date her ayın 1. günüdür. index_value TÜİK 2003=100 bazlıdır.';

COMMENT ON COLUMN inflation_rates.period_date  IS 'Ayın ilk günü (örn. 2024-01-01 = Ocak 2024)';
COMMENT ON COLUMN inflation_rates.index_value  IS 'TÜFE endeks değeri (TÜİK, 2003=100 bazlı)';
COMMENT ON COLUMN inflation_rates.source       IS 'Veri kaynağı: ''tuik'' (EVDS worker yazımı), ''seed-approximation'' (initial seed yaklaşık)';

-- ============================================================
-- SEED DATA — Yaklaşık TÜİK TÜFE endeks değerleri (2003=100)
-- Kaynak: TÜİK TÜFE Genel Endeksi (yaklaşık değerler)
--
-- ÖNEMLİ: Bu seed satırları "kalıcı doğru" varsayılmaz. EVDS worker
-- (`EvdsInflationWorker`) gerçek TÜİK verilerini çektiğinde
-- ON CONFLICT DO UPDATE ile bu kayıtların üzerine `source = 'tuik'` yazar.
--
-- Seed satırları `source = 'seed-approximation'` ile işaretlenir; böylece
-- audit/raporlamada hangi satırların hâlâ yaklaşık olduğu görülür.
-- ============================================================

INSERT INTO inflation_rates (period_date, index_value, source) VALUES
-- 2010
('2010-01-01', 158.37, 'seed-approximation'), ('2010-02-01', 157.85, 'seed-approximation'), ('2010-03-01', 159.49, 'seed-approximation'),
('2010-04-01', 163.06, 'seed-approximation'), ('2010-05-01', 163.35, 'seed-approximation'), ('2010-06-01', 162.15, 'seed-approximation'),
('2010-07-01', 163.68, 'seed-approximation'), ('2010-08-01', 163.51, 'seed-approximation'), ('2010-09-01', 163.35, 'seed-approximation'),
('2010-10-01', 164.47, 'seed-approximation'), ('2010-11-01', 165.97, 'seed-approximation'), ('2010-12-01', 167.83, 'seed-approximation'),
-- 2011
('2011-01-01', 170.33, 'seed-approximation'), ('2011-02-01', 170.49, 'seed-approximation'), ('2011-03-01', 172.41, 'seed-approximation'),
('2011-04-01', 175.36, 'seed-approximation'), ('2011-05-01', 176.32, 'seed-approximation'), ('2011-06-01', 175.82, 'seed-approximation'),
('2011-07-01', 178.84, 'seed-approximation'), ('2011-08-01', 179.91, 'seed-approximation'), ('2011-09-01', 181.71, 'seed-approximation'),
('2011-10-01', 183.59, 'seed-approximation'), ('2011-11-01', 186.56, 'seed-approximation'), ('2011-12-01', 188.74, 'seed-approximation'),
-- 2012
('2012-01-01', 192.43, 'seed-approximation'), ('2012-02-01', 192.96, 'seed-approximation'), ('2012-03-01', 194.21, 'seed-approximation'),
('2012-04-01', 195.74, 'seed-approximation'), ('2012-05-01', 195.41, 'seed-approximation'), ('2012-06-01', 194.82, 'seed-approximation'),
('2012-07-01', 196.21, 'seed-approximation'), ('2012-08-01', 197.15, 'seed-approximation'), ('2012-09-01', 198.09, 'seed-approximation'),
('2012-10-01', 199.14, 'seed-approximation'), ('2012-11-01', 200.68, 'seed-approximation'), ('2012-12-01', 201.85, 'seed-approximation'),
-- 2013
('2013-01-01', 205.44, 'seed-approximation'), ('2013-02-01', 205.92, 'seed-approximation'), ('2013-03-01', 207.34, 'seed-approximation'),
('2013-04-01', 207.88, 'seed-approximation'), ('2013-05-01', 207.62, 'seed-approximation'), ('2013-06-01', 209.24, 'seed-approximation'),
('2013-07-01', 212.08, 'seed-approximation'), ('2013-08-01', 214.17, 'seed-approximation'), ('2013-09-01', 215.39, 'seed-approximation'),
('2013-10-01', 216.32, 'seed-approximation'), ('2013-11-01', 217.93, 'seed-approximation'), ('2013-12-01', 219.52, 'seed-approximation'),
-- 2014
('2014-01-01', 224.18, 'seed-approximation'), ('2014-02-01', 226.35, 'seed-approximation'), ('2014-03-01', 229.26, 'seed-approximation'),
('2014-04-01', 231.47, 'seed-approximation'), ('2014-05-01', 231.84, 'seed-approximation'), ('2014-06-01', 231.94, 'seed-approximation'),
('2014-07-01', 234.19, 'seed-approximation'), ('2014-08-01', 236.72, 'seed-approximation'), ('2014-09-01', 238.44, 'seed-approximation'),
('2014-10-01', 239.77, 'seed-approximation'), ('2014-11-01', 241.37, 'seed-approximation'), ('2014-12-01', 242.62, 'seed-approximation'),
-- 2015
('2015-01-01', 245.38, 'seed-approximation'), ('2015-02-01', 245.74, 'seed-approximation'), ('2015-03-01', 248.13, 'seed-approximation'),
('2015-04-01', 249.62, 'seed-approximation'), ('2015-05-01', 250.18, 'seed-approximation'), ('2015-06-01', 250.33, 'seed-approximation'),
('2015-07-01', 253.21, 'seed-approximation'), ('2015-08-01', 254.66, 'seed-approximation'), ('2015-09-01', 257.35, 'seed-approximation'),
('2015-10-01', 259.94, 'seed-approximation'), ('2015-11-01', 261.08, 'seed-approximation'), ('2015-12-01', 262.45, 'seed-approximation'),
-- 2016
('2016-01-01', 265.73, 'seed-approximation'), ('2016-02-01', 266.24, 'seed-approximation'), ('2016-03-01', 268.12, 'seed-approximation'),
('2016-04-01', 269.35, 'seed-approximation'), ('2016-05-01', 269.88, 'seed-approximation'), ('2016-06-01', 270.19, 'seed-approximation'),
('2016-07-01', 273.47, 'seed-approximation'), ('2016-08-01', 275.21, 'seed-approximation'), ('2016-09-01', 277.83, 'seed-approximation'),
('2016-10-01', 280.36, 'seed-approximation'), ('2016-11-01', 284.52, 'seed-approximation'), ('2016-12-01', 289.74, 'seed-approximation'),
-- 2017
('2017-01-01', 296.48, 'seed-approximation'), ('2017-02-01', 299.17, 'seed-approximation'), ('2017-03-01', 303.54, 'seed-approximation'),
('2017-04-01', 306.28, 'seed-approximation'), ('2017-05-01', 306.94, 'seed-approximation'), ('2017-06-01', 307.11, 'seed-approximation'),
('2017-07-01', 308.83, 'seed-approximation'), ('2017-08-01', 310.52, 'seed-approximation'), ('2017-09-01', 315.74, 'seed-approximation'),
('2017-10-01', 319.38, 'seed-approximation'), ('2017-11-01', 323.16, 'seed-approximation'), ('2017-12-01', 327.65, 'seed-approximation'),
-- 2018
('2018-01-01', 333.22, 'seed-approximation'), ('2018-02-01', 336.48, 'seed-approximation'), ('2018-03-01', 339.73, 'seed-approximation'),
('2018-04-01', 343.87, 'seed-approximation'), ('2018-05-01', 351.24, 'seed-approximation'), ('2018-06-01', 362.91, 'seed-approximation'),
('2018-07-01', 375.48, 'seed-approximation'), ('2018-08-01', 393.27, 'seed-approximation'), ('2018-09-01', 413.54, 'seed-approximation'),
('2018-10-01', 426.83, 'seed-approximation'), ('2018-11-01', 436.19, 'seed-approximation'), ('2018-12-01', 440.28, 'seed-approximation'),
-- 2019
('2019-01-01', 439.47, 'seed-approximation'), ('2019-02-01', 438.93, 'seed-approximation'), ('2019-03-01', 442.17, 'seed-approximation'),
('2019-04-01', 447.83, 'seed-approximation'), ('2019-05-01', 453.26, 'seed-approximation'), ('2019-06-01', 455.38, 'seed-approximation'),
('2019-07-01', 455.74, 'seed-approximation'), ('2019-08-01', 457.21, 'seed-approximation'), ('2019-09-01', 461.83, 'seed-approximation'),
('2019-10-01', 467.34, 'seed-approximation'), ('2019-11-01', 471.52, 'seed-approximation'), ('2019-12-01', 474.28, 'seed-approximation'),
-- 2020
('2020-01-01', 480.37, 'seed-approximation'), ('2020-02-01', 482.94, 'seed-approximation'), ('2020-03-01', 487.63, 'seed-approximation'),
('2020-04-01', 489.28, 'seed-approximation'), ('2020-05-01', 491.74, 'seed-approximation'), ('2020-06-01', 495.38, 'seed-approximation'),
('2020-07-01', 502.17, 'seed-approximation'), ('2020-08-01', 510.84, 'seed-approximation'), ('2020-09-01', 518.43, 'seed-approximation'),
('2020-10-01', 524.87, 'seed-approximation'), ('2020-11-01', 533.26, 'seed-approximation'), ('2020-12-01', 543.18, 'seed-approximation'),
-- 2021
('2021-01-01', 553.84, 'seed-approximation'), ('2021-02-01', 561.27, 'seed-approximation'), ('2021-03-01', 574.83, 'seed-approximation'),
('2021-04-01', 582.17, 'seed-approximation'), ('2021-05-01', 591.43, 'seed-approximation'), ('2021-06-01', 598.74, 'seed-approximation'),
('2021-07-01', 607.28, 'seed-approximation'), ('2021-08-01', 618.43, 'seed-approximation'), ('2021-09-01', 633.27, 'seed-approximation'),
('2021-10-01', 656.84, 'seed-approximation'), ('2021-11-01', 693.47, 'seed-approximation'), ('2021-12-01', 756.38, 'seed-approximation'),
-- 2022
('2022-01-01', 852.74, 'seed-approximation'), ('2022-02-01', 934.28, 'seed-approximation'), ('2022-03-01', 1021.47, 'seed-approximation'),
('2022-04-01', 1098.83, 'seed-approximation'), ('2022-05-01', 1163.27, 'seed-approximation'), ('2022-06-01', 1228.47, 'seed-approximation'),
('2022-07-01', 1291.83, 'seed-approximation'), ('2022-08-01', 1326.47, 'seed-approximation'), ('2022-09-01', 1347.83, 'seed-approximation'),
('2022-10-01', 1391.28, 'seed-approximation'), ('2022-11-01', 1451.74, 'seed-approximation'), ('2022-12-01', 1514.38, 'seed-approximation'),
-- 2023
('2023-01-01', 1586.47, 'seed-approximation'), ('2023-02-01', 1641.83, 'seed-approximation'), ('2023-03-01', 1712.28, 'seed-approximation'),
('2023-04-01', 1798.47, 'seed-approximation'), ('2023-05-01', 1893.74, 'seed-approximation'), ('2023-06-01', 2001.83, 'seed-approximation'),
('2023-07-01', 2112.47, 'seed-approximation'), ('2023-08-01', 2218.38, 'seed-approximation'), ('2023-09-01', 2324.83, 'seed-approximation'),
('2023-10-01', 2419.47, 'seed-approximation'), ('2023-11-01', 2487.83, 'seed-approximation'), ('2023-12-01', 2516.28, 'seed-approximation'),
-- 2024
('2024-01-01', 2621.47, 'seed-approximation'), ('2024-02-01', 2758.83, 'seed-approximation'), ('2024-03-01', 2903.27, 'seed-approximation'),
('2024-04-01', 3047.84, 'seed-approximation'), ('2024-05-01', 3183.47, 'seed-approximation'), ('2024-06-01', 3287.83, 'seed-approximation'),
('2024-07-01', 3373.28, 'seed-approximation'), ('2024-08-01', 3432.47, 'seed-approximation'), ('2024-09-01', 3471.83, 'seed-approximation'),
('2024-10-01', 3508.27, 'seed-approximation'), ('2024-11-01', 3543.84, 'seed-approximation'), ('2024-12-01', 3574.28, 'seed-approximation'),
-- 2025
('2025-01-01', 3612.47, 'seed-approximation'), ('2025-02-01', 3648.83, 'seed-approximation'), ('2025-03-01', 3681.28, 'seed-approximation')
ON CONFLICT (period_date) DO NOTHING;
-- NOT: ON CONFLICT DO NOTHING bilinçli — seed satırları yalnızca tablo
-- boşken eklenir. Worker tarafından eklenen gerçek TÜİK kayıtları (source='tuik')
-- üzerine yazılmaz. Seed üzerine yazma EVDS worker'ın kendi UPSERT'inden gelir
-- (InflationIngestionRepository.UpsertInflationRatesAsync).
