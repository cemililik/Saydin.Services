-- ============================================================
-- Migration: 002_add_assets.sql
-- TCMB döviz kurları genişletildi + CoinGecko top 5 crypto eklendi
-- ============================================================

INSERT INTO assets (symbol, display_name, category, is_active, source, source_id, metadata) VALUES
    -- TCMB: Ek döviz kurları
    ('GBPTRY', 'Sterlin/TL',          'currency', true, 'tcmb', 'TP.DK.GBP.A', '{"decimal_places": 4}'::jsonb),
    ('CHFTRY', 'İsviçre Frangı/TL',   'currency', true, 'tcmb', 'TP.DK.CHF.A', '{"decimal_places": 4}'::jsonb),
    ('JPYTRY', 'Japon Yeni/TL',        'currency', true, 'tcmb', 'TP.DK.JPY.A', '{"decimal_places": 4, "unit": 100}'::jsonb),
    ('SARTRY', 'Suudi Riyali/TL',      'currency', true, 'tcmb', 'TP.DK.SAR.A', '{"decimal_places": 4}'::jsonb),
    ('AEDTRY', 'BAE Dirhemi/TL',       'currency', true, 'tcmb', 'TP.DK.AED.A', '{"decimal_places": 4}'::jsonb),

    -- CoinGecko: Top 5 crypto (BTC ve ETH 001_initial.sql'de mevcut)
    ('BNB',  'BNB',     'crypto', true, 'coingecko', 'binancecoin', '{"decimal_places": 2}'::jsonb),
    ('XRP',  'XRP',     'crypto', true, 'coingecko', 'ripple',      '{"decimal_places": 4}'::jsonb),
    ('SOL',  'Solana',  'crypto', true, 'coingecko', 'solana',      '{"decimal_places": 2}'::jsonb)

ON CONFLICT (symbol) DO NOTHING;
