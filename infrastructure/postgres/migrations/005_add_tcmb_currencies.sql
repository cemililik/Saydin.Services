-- ============================================================
-- Migration 005: TCMB döviz kurları genişletme
-- Mevcut 7 dövize ek olarak 13 döviz daha eklenir → toplam 20.
-- TCMB'den günlük olarak çekilir; backfill 20 yıl geriye gider.
-- Kapsanan para birimleri:
--   Mevcut: USD EUR GBP CHF JPY SAR AED
--   Yeni:   AUD AZN CAD CNY DKK KRW KWD KZT NOK QAR RON RUB SEK
-- XDR (Özel Çekme Hakkı) ve PKR (Pakistan) dahil edilmedi.
-- ============================================================

INSERT INTO assets (symbol, display_name, category, source, source_id, is_active)
VALUES
  ('AUDTRY', 'Avustralya Doları',    'currency', 'tcmb', 'AUD', true),
  ('AZNTRY', 'Azerbaycan Manatı',   'currency', 'tcmb', 'AZN', true),
  ('CADTRY', 'Kanada Doları',        'currency', 'tcmb', 'CAD', true),
  ('CNYTRY', 'Çin Yuanı',            'currency', 'tcmb', 'CNY', true),
  ('DKKTRY', 'Danimarka Kronu',      'currency', 'tcmb', 'DKK', true),
  ('KRWTRY', 'Güney Kore Wonu',      'currency', 'tcmb', 'KRW', true),
  ('KWDTRY', 'Kuveyt Dinarı',        'currency', 'tcmb', 'KWD', true),
  ('KZTTRY', 'Kazakistan Tengesi',   'currency', 'tcmb', 'KZT', true),
  ('NOKTRY', 'Norveç Kronu',         'currency', 'tcmb', 'NOK', true),
  ('QARTRY', 'Katar Riyali',         'currency', 'tcmb', 'QAR', true),
  ('RONTRY', 'Romanya Leyi',         'currency', 'tcmb', 'RON', true),
  ('RUBTRY', 'Rus Rublesi',          'currency', 'tcmb', 'RUB', true),
  ('SEKTRY', 'İsveç Kronu',          'currency', 'tcmb', 'SEK', true)
ON CONFLICT (symbol) DO NOTHING;
