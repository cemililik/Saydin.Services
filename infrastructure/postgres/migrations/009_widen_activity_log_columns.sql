-- os_version iOS'ta "Version 18.6 (Build 22G081)" gibi uzun string döner.
-- VARCHAR(20) yetersiz kalıyor; kolonları genişlet.

BEGIN;

ALTER TABLE activity_logs ALTER COLUMN device_os TYPE VARCHAR(30);
ALTER TABLE activity_logs ALTER COLUMN os_version TYPE VARCHAR(100);
ALTER TABLE activity_logs ALTER COLUMN app_version TYPE VARCHAR(50);

COMMIT;
