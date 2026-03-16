-- ============================================================
-- Migration: 003_switch_precious_metals_to_oxr.sql
-- XAU/XAG asset kaynağını goldapi → openexchangerates olarak değiştirir.
-- GoldAPI implementasyonu kodda pasif tutulur, DB kaydı güncellenir.
-- ============================================================

UPDATE assets
SET source = 'openexchangerates'
WHERE symbol IN ('XAU_TRY_GRAM', 'XAG_TRY_GRAM');
