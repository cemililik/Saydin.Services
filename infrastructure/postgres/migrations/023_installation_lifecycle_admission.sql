-- Migration 023: installation lifecycle audit actions and pending-commit admission.
--
-- This is an append-only security migration. The normal installation resolver remains
-- active-only. A separate resolver accepts only a credential bound to one exact rotation
-- id and is granted solely to the API capability role for the commit endpoint's
-- pre-mutation principal admission.

BEGIN;

DO $contract_preflight$
DECLARE
    owner_role text;
    scheduler_role text;
    api_cap text;
    audit_cap text;
BEGIN
    SELECT contract.owner_role,contract.timescale_scheduler_role,
           contract.api_capability_role,contract.audit_capability_role
      INTO owner_role,scheduler_role,api_cap,audit_cap
      FROM public.saydin_role_contract contract
     WHERE contract.singleton=1
       AND contract.contract_schema_version=1
       AND contract.database_name=pg_catalog.current_database();

    IF owner_role IS NULL OR scheduler_role IS NULL OR api_cap IS NULL OR audit_cap IS NULL
       OR CURRENT_USER IS DISTINCT FROM owner_role
       OR pg_catalog.pg_get_userbyid(
              (SELECT relowner FROM pg_catalog.pg_class
                WHERE oid='public.installation_credentials'::pg_catalog.regclass))
              IS DISTINCT FROM owner_role
       OR pg_catalog.pg_get_userbyid(
              (SELECT relowner FROM pg_catalog.pg_class
                WHERE oid='public.activity_logs'::pg_catalog.regclass))
              IS DISTINCT FROM scheduler_role
       OR pg_catalog.has_table_privilege(
              owner_role,'public.activity_logs','TRIGGER')
       OR NOT EXISTS (
            SELECT 1
              FROM pg_catalog.pg_auth_members membership
              JOIN pg_catalog.pg_roles granted ON granted.oid=membership.roleid
              JOIN pg_catalog.pg_roles member_role ON member_role.oid=membership.member
             WHERE granted.rolname=scheduler_role
               AND member_role.rolname=owner_role
               AND NOT membership.admin_option
               AND NOT membership.inherit_option
               AND membership.set_option)
       OR pg_catalog.to_regprocedure(
              'public.installation_verifier_matches(bytea,bytea)') IS NOT NULL
       OR pg_catalog.to_regprocedure(
              'public.resolve_installation_rotation_commit(uuid,bytea,smallint)') IS NOT NULL
       OR pg_catalog.to_regprocedure(
              'public.enforce_activity_action_allowlist()') IS NOT NULL
       OR pg_catalog.has_schema_privilege(scheduler_role,'public','CREATE')
       OR EXISTS (
            SELECT 1 FROM pg_catalog.pg_trigger trigger_row
             WHERE trigger_row.tgrelid='public.activity_logs'::pg_catalog.regclass
               AND trigger_row.tgname='trg_activity_action_allowlist'
               AND NOT trigger_row.tgisinternal)
       OR (SELECT pg_catalog.count(*)=1
                  AND pg_catalog.bool_and(
                      constraint_row.contype='c'
                      AND constraint_row.convalidated
                      AND pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                              pg_catalog.pg_get_constraintdef(
                                  constraint_row.oid,true),'UTF8')),'hex')=
                          'dba399e0aa984f4a4c81b4c3c244e86ab5ea634f19eceafe8b15a4fa2c4c7c80')
             FROM pg_catalog.pg_constraint constraint_row
            WHERE constraint_row.conrelid='public.activity_logs'::pg_catalog.regclass
              AND constraint_row.conname='chk_activity_action') IS NOT TRUE THEN
        RAISE EXCEPTION 'installation lifecycle admission preflight rejected'
            USING ERRCODE='42501';
    END IF;
END;
$contract_preflight$;

-- Select the unique rotation row before comparing the verifier. This keeps the secret
-- out of an index predicate and lets the fixed 32-byte comparison execute every byte.
CREATE FUNCTION public.installation_verifier_matches(
    p_expected bytea,
    p_candidate bytea)
RETURNS boolean
LANGUAGE plpgsql
IMMUTABLE
STRICT
PARALLEL SAFE
SET search_path=pg_catalog,pg_temp
AS $body$
DECLARE
    difference integer:=0;
    byte_index integer;
BEGIN
    IF pg_catalog.octet_length(p_expected)<>32
       OR pg_catalog.octet_length(p_candidate)<>32 THEN
        RETURN false;
    END IF;

    FOR byte_index IN 0..31 LOOP
        difference:=difference |
            (pg_catalog.get_byte(p_expected,byte_index) #
             pg_catalog.get_byte(p_candidate,byte_index));
    END LOOP;
    RETURN difference=0;
END;
$body$;

CREATE FUNCTION public.resolve_installation_rotation_commit(
    p_rotation_id uuid,
    p_secret_hash bytea,
    p_key_version smallint)
RETURNS TABLE(
    principal_id uuid,
    credential_id uuid,
    generation integer,
    tier varchar,
    principal_status varchar,
    credential_state varchar)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path=pg_catalog,pg_temp
AS $body$
    SELECT principal.id,credential.id,credential.generation,principal.tier,
           principal.principal_status,credential.state
      FROM public.installation_credentials credential
      JOIN public.users principal ON principal.id=credential.principal_id
     WHERE p_rotation_id IS NOT NULL
       AND p_rotation_id<>'00000000-0000-0000-0000-000000000000'::uuid
       AND p_key_version IS NOT NULL AND p_key_version>0
       AND p_secret_hash IS NOT NULL
       AND pg_catalog.octet_length(p_secret_hash)=32
       AND credential.rotation_id=p_rotation_id
       AND credential.hash_key_version=p_key_version
       AND public.installation_verifier_matches(
               credential.secret_hash,p_secret_hash)
       AND credential.state IN ('pending','active')
       AND (credential.expires_at IS NULL
            OR credential.expires_at>pg_catalog.statement_timestamp())
       AND ((credential.state='pending'
             AND credential.pending_expires_at>pg_catalog.statement_timestamp()
             AND credential.activated_at IS NULL)
            OR (credential.state='active'
                AND credential.activated_at<=pg_catalog.statement_timestamp()
                AND credential.pending_expires_at IS NULL))
       AND principal.principal_status='active'
       AND (principal.principal_expires_at IS NULL
            OR principal.principal_expires_at>pg_catalog.statement_timestamp());
$body$;

CREATE FUNCTION public.enforce_activity_action_allowlist()
RETURNS trigger
LANGUAGE plpgsql
VOLATILE
SECURITY INVOKER
SET search_path=pg_catalog,pg_temp
AS $body$
BEGIN
    IF NEW.action IS NULL OR NOT (NEW.action=ANY(ARRAY[
        'what_if_calculate','what_if_compare','what_if_dca','what_if_reverse',
        'scenario_save','scenario_delete','scenario_list','assets_list',
        'asset_price','asset_price_range','config_fetch',
        'installation_register','installation_rotation_begin',
        'installation_rotation_commit','installation_revoke']::varchar[])) THEN
        RAISE EXCEPTION 'activity action rejected'
            USING ERRCODE='23514',CONSTRAINT='chk_activity_action';
    END IF;
    RETURN NEW;
END;
$body$;

DO $function_ownership_and_acl$
DECLARE
    owner_role text;
    scheduler_role text;
    api_cap text;
    audit_cap text;
BEGIN
    SELECT contract.owner_role,contract.timescale_scheduler_role,
           contract.api_capability_role,
           contract.audit_capability_role
      INTO owner_role,scheduler_role,api_cap,audit_cap
      FROM public.saydin_role_contract contract
     WHERE contract.singleton=1
       AND contract.contract_schema_version=1
       AND contract.database_name=pg_catalog.current_database();

    ALTER FUNCTION public.installation_verifier_matches(bytea,bytea) OWNER TO CURRENT_USER;
    ALTER FUNCTION public.resolve_installation_rotation_commit(uuid,bytea,smallint)
        OWNER TO CURRENT_USER;
    ALTER FUNCTION public.enforce_activity_action_allowlist() OWNER TO CURRENT_USER;
    REVOKE ALL ON FUNCTION public.installation_verifier_matches(bytea,bytea) FROM PUBLIC;
    REVOKE ALL ON FUNCTION public.resolve_installation_rotation_commit(uuid,bytea,smallint)
        FROM PUBLIC;
    REVOKE ALL ON FUNCTION public.enforce_activity_action_allowlist() FROM PUBLIC;
    EXECUTE pg_catalog.format(
        'REVOKE ALL ON FUNCTION public.installation_verifier_matches(bytea,bytea),public.resolve_installation_rotation_commit(uuid,bytea,smallint),public.enforce_activity_action_allowlist() FROM %I',
        api_cap);
    EXECUTE pg_catalog.format(
        'REVOKE ALL ON FUNCTION public.installation_verifier_matches(bytea,bytea),public.resolve_installation_rotation_commit(uuid,bytea,smallint),public.enforce_activity_action_allowlist() FROM %I',
        audit_cap);
    EXECUTE pg_catalog.format(
        'GRANT EXECUTE ON FUNCTION public.resolve_installation_rotation_commit(uuid,bytea,smallint) TO %I',
        api_cap);

    IF pg_catalog.pg_get_userbyid(
           (SELECT proowner FROM pg_catalog.pg_proc
             WHERE oid='public.installation_verifier_matches(bytea,bytea)'::pg_catalog.regprocedure))
           IS DISTINCT FROM owner_role
       OR pg_catalog.pg_get_userbyid(
           (SELECT proowner FROM pg_catalog.pg_proc
             WHERE oid='public.resolve_installation_rotation_commit(uuid,bytea,smallint)'::pg_catalog.regprocedure))
           IS DISTINCT FROM owner_role
       OR pg_catalog.pg_get_userbyid(
           (SELECT proowner FROM pg_catalog.pg_proc
             WHERE oid='public.enforce_activity_action_allowlist()'::pg_catalog.regprocedure))
           IS DISTINCT FROM owner_role
       OR pg_catalog.has_schema_privilege(scheduler_role,'public','CREATE')
       OR (SELECT pg_catalog.count(*)
             FROM pg_catalog.pg_proc function_row
             CROSS JOIN LATERAL pg_catalog.aclexplode(
                 COALESCE(function_row.proacl,
                     pg_catalog.acldefault('f',function_row.proowner))) acl
            WHERE function_row.oid=
                  'public.installation_verifier_matches(bytea,bytea)'::pg_catalog.regprocedure
              AND acl.grantee<>function_row.proowner)<>0
       OR (SELECT pg_catalog.count(*)
             FROM pg_catalog.pg_proc function_row
             CROSS JOIN LATERAL pg_catalog.aclexplode(
                 COALESCE(function_row.proacl,
                     pg_catalog.acldefault('f',function_row.proowner))) acl
            WHERE function_row.oid=
                  'public.enforce_activity_action_allowlist()'::pg_catalog.regprocedure
              AND acl.grantee<>function_row.proowner)<>0
       OR (SELECT pg_catalog.count(*)=1
                  AND pg_catalog.bool_and(
                      grantee.rolname=api_cap
                      AND acl.grantor=function_row.proowner
                      AND acl.privilege_type='EXECUTE'
                      AND NOT acl.is_grantable)
             FROM pg_catalog.pg_proc function_row
             CROSS JOIN LATERAL pg_catalog.aclexplode(
                 COALESCE(function_row.proacl,
                     pg_catalog.acldefault('f',function_row.proowner))) acl
             LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
            WHERE function_row.oid=
                  'public.resolve_installation_rotation_commit(uuid,bytea,smallint)'::pg_catalog.regprocedure
              AND acl.grantee<>function_row.proowner) IS NOT TRUE THEN
        RAISE EXCEPTION 'installation admission function owner rejected'
            USING ERRCODE='42501';
    END IF;
END;
$function_ownership_and_acl$;

-- TimescaleDB cannot add a CHECK constraint while historical chunks are compressed.
-- The exact predecessor fingerprint proves all old rows. A row trigger replaces
-- it for new writes and is propagated by TimescaleDB to current and future chunks.
DO $activity_action_contract$
DECLARE
    owner_role text;
    scheduler_role text;
BEGIN
    SELECT contract.owner_role,contract.timescale_scheduler_role
      INTO owner_role,scheduler_role
      FROM public.saydin_role_contract contract
     WHERE contract.singleton=1
       AND contract.contract_schema_version=1
       AND contract.database_name=pg_catalog.current_database();

    -- The scheduler owns the hypertable and all chunks. Grant only temporary
    -- EXECUTE on the invoker trigger function so Timescale can propagate the
    -- trigger without granting the function owner any table/chunk capability.
    EXECUTE pg_catalog.format(
        'GRANT EXECUTE ON FUNCTION public.enforce_activity_action_allowlist() TO %I',
        scheduler_role);
    EXECUTE pg_catalog.format('SET LOCAL ROLE %I',scheduler_role);
    EXECUTE pg_catalog.format(
        'GRANT TRIGGER ON TABLE public.activity_logs TO %I',scheduler_role);
    BEGIN
        ALTER TABLE public.activity_logs DROP CONSTRAINT chk_activity_action;
    EXCEPTION WHEN OTHERS THEN
        RAISE EXCEPTION 'activity action constraint transition rejected: %',SQLERRM
            USING ERRCODE=SQLSTATE;
    END;
    BEGIN
        CREATE TRIGGER trg_activity_action_allowlist
            BEFORE INSERT OR UPDATE OF action ON public.activity_logs
            FOR EACH ROW EXECUTE FUNCTION public.enforce_activity_action_allowlist();
    EXCEPTION WHEN OTHERS THEN
        RAISE EXCEPTION 'activity action trigger transition rejected: %',SQLERRM
            USING ERRCODE=SQLSTATE;
    END;
    -- TimescaleDB hypertables reject ENABLE ALWAYS. The regular trigger is
    -- propagated to chunks; session_replication_role remains a privileged path
    -- that is never granted to the API capability role.
    BEGIN
        COMMENT ON COLUMN public.activity_logs.action IS
            'Product and installation lifecycle action; exact allowlist is enforced by trg_activity_action_allowlist.';
    EXCEPTION WHEN OTHERS THEN
        RAISE EXCEPTION 'activity action comment transition rejected: %',SQLERRM
            USING ERRCODE=SQLSTATE;
    END;
    EXECUTE pg_catalog.format('SET LOCAL ROLE %I',owner_role);
    EXECUTE pg_catalog.format(
        'REVOKE EXECUTE ON FUNCTION public.enforce_activity_action_allowlist() FROM %I',
        scheduler_role);
    COMMENT ON FUNCTION public.enforce_activity_action_allowlist() IS
        'Scheduler-owned invoker trigger enforcing the exact activity action allowlist on new writes.';
    EXECUTE pg_catalog.format('GRANT CREATE ON SCHEMA public TO %I',scheduler_role);
    EXECUTE pg_catalog.format(
        'ALTER FUNCTION public.enforce_activity_action_allowlist() OWNER TO %I',
        scheduler_role);
    EXECUTE pg_catalog.format('REVOKE CREATE ON SCHEMA public FROM %I',scheduler_role);

    IF CURRENT_USER IS DISTINCT FROM owner_role
       OR pg_catalog.pg_get_userbyid(
              (SELECT relowner FROM pg_catalog.pg_class
                WHERE oid='public.activity_logs'::pg_catalog.regclass))
              IS DISTINCT FROM scheduler_role
       OR EXISTS (
            SELECT 1 FROM pg_catalog.pg_constraint constraint_row
             WHERE constraint_row.conrelid='public.activity_logs'::pg_catalog.regclass
               AND constraint_row.conname='chk_activity_action')
       OR (SELECT pg_catalog.count(*)=1
                  AND pg_catalog.bool_and(
                      trigger_row.tgenabled='O'
                      AND trigger_row.tgtype=23
                      AND trigger_row.tgattr::text=(
                          SELECT attribute.attnum::text
                            FROM pg_catalog.pg_attribute attribute
                           WHERE attribute.attrelid='public.activity_logs'::pg_catalog.regclass
                             AND attribute.attname='action'
                             AND attribute.attnum>0 AND NOT attribute.attisdropped)
                      AND trigger_row.tgfoid=
                          'public.enforce_activity_action_allowlist()'::pg_catalog.regprocedure)
             FROM pg_catalog.pg_trigger trigger_row
            WHERE trigger_row.tgrelid='public.activity_logs'::pg_catalog.regclass
              AND trigger_row.tgname='trg_activity_action_allowlist'
              AND NOT trigger_row.tgisinternal) IS NOT TRUE THEN
        RAISE EXCEPTION 'installation lifecycle activity action postcondition rejected'
            USING ERRCODE='42501';
    END IF;
END;
$activity_action_contract$;

COMMENT ON FUNCTION public.installation_verifier_matches(bytea,bytea) IS
    'Owner-only fixed-length comparison helper for installation verifier admission.';
COMMENT ON FUNCTION public.resolve_installation_rotation_commit(uuid,bytea,smallint) IS
    'Commit-only pending/active retry resolver scoped to one exact rotation id; normal active resolver remains unchanged.';

COMMIT;
