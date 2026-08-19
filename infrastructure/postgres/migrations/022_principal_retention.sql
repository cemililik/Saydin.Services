-- Migration 022: preserve activity evidence while deleting installation principals.
--
-- TimescaleDB owns activity_logs through the isolated scheduler role. PostgreSQL's
-- former users -> activity_logs ON DELETE SET NULL action therefore could not run
-- under the users-table owner without granting an unacceptably broad UPDATE right.
-- This forward-only contract moves redaction into one scheduler-owned, locked-path
-- trigger function and leaves the foreign key as a fail-closed NO ACTION guard.

BEGIN;

DO $contract_preflight$
DECLARE
    owner_role text;
    scheduler_role text;
    audit_cap text;
BEGIN
    SELECT contract.owner_role,
           contract.timescale_scheduler_role,
           contract.audit_capability_role
      INTO owner_role,scheduler_role,audit_cap
      FROM public.saydin_role_contract contract
     WHERE contract.singleton=1
       AND contract.contract_schema_version=1
       AND contract.database_name=pg_catalog.current_database();

    IF owner_role IS NULL OR scheduler_role IS NULL OR audit_cap IS NULL
       OR CURRENT_USER IS DISTINCT FROM owner_role
       OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname=owner_role)
       OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname=scheduler_role)
       OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname=audit_cap)
       OR pg_catalog.pg_get_userbyid(
              (SELECT relowner FROM pg_catalog.pg_class
                WHERE oid='public.users'::pg_catalog.regclass)) IS DISTINCT FROM owner_role
       OR pg_catalog.pg_get_userbyid(
              (SELECT relowner FROM pg_catalog.pg_class
                WHERE oid='public.activity_logs'::pg_catalog.regclass)) IS DISTINCT FROM scheduler_role
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
       OR (SELECT pg_catalog.count(*)
             FROM pg_catalog.pg_constraint constraint_row
            WHERE constraint_row.conrelid='public.activity_logs'::pg_catalog.regclass
              AND constraint_row.conname='activity_logs_user_id_fkey'
              AND constraint_row.contype='f'
              AND constraint_row.confrelid='public.users'::pg_catalog.regclass
              AND constraint_row.convalidated
              AND NOT constraint_row.condeferrable
              AND constraint_row.confdeltype='n')<>1
       OR pg_catalog.to_regprocedure(
              'public.redact_activity_logs_before_principal_delete()') IS NOT NULL
       OR pg_catalog.to_regprocedure(
              'saydin_principal_retention_control.consume_principal_retention_transition()') IS NULL
       OR EXISTS (
            SELECT 1 FROM pg_catalog.pg_trigger trigger_row
             WHERE trigger_row.tgrelid='public.users'::pg_catalog.regclass
               AND trigger_row.tgname='trg_users_principal_retention_redact') THEN
        RAISE EXCEPTION 'principal retention role/schema contract rejected'
            USING ERRCODE='42501';
    END IF;
END;
$contract_preflight$;

LOCK TABLE public.users IN ACCESS EXCLUSIVE MODE;

CREATE FUNCTION public.redact_activity_logs_before_principal_delete()
RETURNS trigger
LANGUAGE plpgsql
VOLATILE
SECURITY DEFINER
SET search_path=pg_catalog,pg_temp
AS $body$
BEGIN
    UPDATE public.activity_logs activity
       SET user_id=NULL,
           device_id='server-redacted'
     WHERE activity.user_id=OLD.id;
    RETURN OLD;
END;
$body$;

REVOKE ALL ON FUNCTION public.redact_activity_logs_before_principal_delete() FROM PUBLIC;
COMMENT ON FUNCTION public.redact_activity_logs_before_principal_delete() IS
    'Scheduler-owned fail-closed erasure boundary: removes principal/device linkage while preserving activity evidence before user deletion.';

CREATE TRIGGER trg_users_principal_retention_redact
BEFORE DELETE ON public.users
FOR EACH ROW
EXECUTE FUNCTION public.redact_activity_logs_before_principal_delete();

DO $scheduler_owned_contract$
DECLARE
    owner_role text;
    scheduler_role text;
    capability_role text;
    capability_roles text[];
    compressed_chunk pg_catalog.regclass;
    compressed_chunks pg_catalog.regclass[];
BEGIN
    SELECT contract.owner_role,contract.timescale_scheduler_role,
           ARRAY[contract.owner_role,contract.migrator_capability_role,
                 contract.api_capability_role,contract.ingestion_capability_role,
                 contract.calendar_importer_capability_role,
                 contract.exporter_capability_role,contract.audit_capability_role]
      INTO owner_role,scheduler_role,capability_roles
      FROM public.saydin_role_contract contract
     WHERE contract.singleton=1
       AND contract.contract_schema_version=1
       AND contract.database_name=pg_catalog.current_database();
    IF owner_role IS NULL OR scheduler_role IS NULL
       OR CURRENT_USER IS DISTINCT FROM owner_role THEN
        RAISE EXCEPTION 'principal retention ownership contract rejected'
            USING ERRCODE='42501';
    END IF;

    IF (SELECT pg_catalog.count(*)
          FROM timescaledb_information.jobs job
         WHERE job.hypertable_schema='public'
           AND job.hypertable_name='activity_logs'
           AND job.proc_name='policy_compression'
           AND job.scheduled
           AND job.config->>'compress_after'='7 days')<>1 THEN
        RAISE EXCEPTION 'principal retention compression policy rejected'
            USING ERRCODE='55000';
    END IF;
    SELECT COALESCE(
               pg_catalog.array_agg(
                   pg_catalog.format('%I.%I',chunk.chunk_schema,chunk.chunk_name)::pg_catalog.regclass
                   ORDER BY chunk.chunk_schema,chunk.chunk_name),
               ARRAY[]::pg_catalog.regclass[])
      INTO compressed_chunks
      FROM timescaledb_information.chunks chunk
     WHERE chunk.hypertable_schema='public'
       AND chunk.hypertable_name='activity_logs'
       AND chunk.is_compressed;

    -- PostgreSQL requires the new function owner to have CREATE on its schema.
    -- The grant exists only inside this migration transaction and is revoked
    -- immediately after the ownership transfer.
    EXECUTE pg_catalog.format('GRANT CREATE ON SCHEMA public TO %I',scheduler_role);
    EXECUTE pg_catalog.format(
        'ALTER FUNCTION public.redact_activity_logs_before_principal_delete() OWNER TO %I',
        scheduler_role);
    EXECUTE pg_catalog.format('REVOKE CREATE ON SCHEMA public FROM %I',scheduler_role);

    -- Replacing the scheduler-owned hypertable FK requires REFERENCES on its
    -- owner-owned target. Keep the privilege column-scoped and transaction-local.
    EXECUTE pg_catalog.format(
        'GRANT REFERENCES(id) ON TABLE public.users TO %I',scheduler_role);
    EXECUTE pg_catalog.format('SET LOCAL ROLE %I',scheduler_role);
    EXECUTE 'REVOKE ALL ON FUNCTION public.redact_activity_logs_before_principal_delete() FROM PUBLIC';
    FOREACH capability_role IN ARRAY capability_roles
    LOOP
        EXECUTE pg_catalog.format(
            'REVOKE ALL ON FUNCTION public.redact_activity_logs_before_principal_delete() FROM %I',
            capability_role);
    END LOOP;

    PERFORM public.remove_compression_policy(
        'public.activity_logs'::pg_catalog.regclass,if_exists=>false);
    FOREACH compressed_chunk IN ARRAY compressed_chunks
    LOOP
        PERFORM public.decompress_chunk(compressed_chunk,if_compressed=>true);
    END LOOP;
    EXECUTE 'ALTER TABLE public.activity_logs SET (timescaledb.compress=false)';
    EXECUTE 'ALTER TABLE public.activity_logs DROP CONSTRAINT activity_logs_user_id_fkey';
    EXECUTE 'ALTER TABLE public.activity_logs ADD CONSTRAINT activity_logs_user_id_fkey
        FOREIGN KEY(user_id) REFERENCES public.users(id) ON DELETE NO ACTION';

    EXECUTE pg_catalog.format('SET LOCAL ROLE %I',owner_role);
    PERFORM saydin_principal_retention_control.consume_principal_retention_transition();
    IF CURRENT_USER IS DISTINCT FROM owner_role THEN
        RAISE EXCEPTION 'principal retention owner restore rejected'
            USING ERRCODE='42501';
    END IF;

    EXECUTE pg_catalog.format('SET LOCAL ROLE %I',scheduler_role);
    -- Timescale's hypertable permission path does not treat the scheduler's
    -- relowner status as effective SELECT/UPDATE capabilities. A self-grant
    -- restores owner-equivalent access without exposing it to owner/API/audit.
    EXECUTE pg_catalog.format(
        'GRANT SELECT,UPDATE ON TABLE public.activity_logs TO %I',scheduler_role);
    FOREACH compressed_chunk IN ARRAY compressed_chunks
    LOOP
        PERFORM public.compress_chunk(compressed_chunk,if_not_compressed=>true);
    END LOOP;
    PERFORM public.add_compression_policy(
        'public.activity_logs'::pg_catalog.regclass,
        INTERVAL '7 days',if_not_exists=>false);

    EXECUTE pg_catalog.format('SET LOCAL ROLE %I',owner_role);
    EXECUTE pg_catalog.format(
        'REVOKE REFERENCES(id) ON TABLE public.users FROM %I',scheduler_role);
    EXECUTE 'DROP SCHEMA saydin_principal_retention_control CASCADE';
    IF CURRENT_USER IS DISTINCT FROM owner_role THEN
        RAISE EXCEPTION 'principal retention owner restore rejected'
            USING ERRCODE='42501';
    END IF;
END;
$scheduler_owned_contract$;

DO $terminal_contract$
DECLARE
    owner_role text;
    scheduler_role text;
    api_role text;
BEGIN
    SELECT contract.owner_role,contract.timescale_scheduler_role,
           contract.api_capability_role
      INTO owner_role,scheduler_role,api_role
      FROM public.saydin_role_contract contract
     WHERE contract.singleton=1
       AND contract.contract_schema_version=1
       AND contract.database_name=pg_catalog.current_database();

    IF owner_role IS NULL OR scheduler_role IS NULL OR api_role IS NULL
       OR CURRENT_USER IS DISTINCT FROM owner_role
       OR pg_catalog.pg_get_userbyid(
              (SELECT relation.relowner FROM pg_catalog.pg_class relation
                WHERE relation.oid='public.activity_logs'::pg_catalog.regclass))
          IS DISTINCT FROM scheduler_role
       OR (SELECT pg_catalog.count(*)
             FROM pg_catalog.pg_proc function_row
             JOIN pg_catalog.pg_language language ON language.oid=function_row.prolang
            WHERE function_row.oid=
                  'public.redact_activity_logs_before_principal_delete()'::pg_catalog.regprocedure
              AND pg_catalog.pg_get_userbyid(function_row.proowner)=scheduler_role
              AND pg_catalog.pg_get_function_identity_arguments(function_row.oid)=''
              AND pg_catalog.pg_get_function_result(function_row.oid)='trigger'
              AND language.lanname='plpgsql'
              AND function_row.provolatile='v' AND function_row.proparallel='u'
              AND NOT function_row.proisstrict AND NOT function_row.proleakproof
              AND function_row.prokind='f' AND function_row.prosecdef
              AND function_row.proconfig=
                  ARRAY['search_path=pg_catalog, pg_temp']::text[]
              AND pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                  function_row.prosrc,'UTF8')),'hex')=
                  'be2799e95d3e4abc7621598bcc116b0f8d5df0a931e4e1c5af6cb2c42cae66e6')<>1
       OR (SELECT pg_catalog.count(*)=1 AND pg_catalog.bool_and(
                         grantee.rolname=scheduler_role
                         AND grantor.rolname=scheduler_role
                         AND acl.privilege_type='EXECUTE'
                         AND NOT acl.is_grantable)
             FROM pg_catalog.pg_proc function_row
             CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(
                 function_row.proacl,
                 pg_catalog.acldefault('f',function_row.proowner))) acl
             LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
             LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
            WHERE function_row.oid=
                  'public.redact_activity_logs_before_principal_delete()'::pg_catalog.regprocedure)
          IS NOT TRUE
       OR (SELECT pg_catalog.count(*)
             FROM pg_catalog.pg_trigger trigger_row
             JOIN pg_catalog.pg_proc function_row ON function_row.oid=trigger_row.tgfoid
            WHERE trigger_row.tgrelid='public.users'::pg_catalog.regclass
              AND trigger_row.tgname='trg_users_principal_retention_redact'
              AND NOT trigger_row.tgisinternal
              AND trigger_row.tgenabled='O'
              AND trigger_row.tgtype=11
              AND trigger_row.tgnargs=0
              AND trigger_row.tgqual IS NULL
              AND trigger_row.tgattr=''::pg_catalog.int2vector
              AND function_row.oid=
                  'public.redact_activity_logs_before_principal_delete()'::pg_catalog.regprocedure)<>1
       OR EXISTS (
            SELECT 1 FROM pg_catalog.pg_trigger trigger_row
            JOIN pg_catalog.pg_proc function_row ON function_row.oid=trigger_row.tgfoid
            WHERE trigger_row.tgrelid='public.users'::pg_catalog.regclass
              AND NOT trigger_row.tgisinternal
              AND (trigger_row.tgname='trg_users_principal_retention_redact'
                   OR function_row.proname=
                      'redact_activity_logs_before_principal_delete')
              AND trigger_row.tgname<>'trg_users_principal_retention_redact')
       OR (SELECT pg_catalog.count(*)
             FROM pg_catalog.pg_constraint constraint_row
            WHERE constraint_row.conrelid='public.activity_logs'::pg_catalog.regclass
              AND constraint_row.conname='activity_logs_user_id_fkey'
              AND constraint_row.contype='f'
              AND constraint_row.confrelid='public.users'::pg_catalog.regclass
              AND constraint_row.convalidated
              AND NOT constraint_row.condeferrable
              AND NOT constraint_row.condeferred
              AND constraint_row.confdeltype='a'
              AND pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                  pg_catalog.pg_get_constraintdef(constraint_row.oid,true),'UTF8')),'hex')=
                  '35bba6df01802e7850bd1a753b95ff643a2a01ec56aa476981cbe9dc42705cf3')<>1
       OR EXISTS (
            SELECT 1
              FROM pg_catalog.pg_namespace namespace
              CROSS JOIN LATERAL pg_catalog.aclexplode(
                  COALESCE(namespace.nspacl,
                      pg_catalog.acldefault('n',namespace.nspowner))) acl
              LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
             WHERE namespace.nspname='public'
               AND grantee.rolname=scheduler_role
               AND acl.privilege_type='CREATE')
       OR (SELECT pg_catalog.count(*)=3
                     AND pg_catalog.count(*) FILTER (WHERE
                           grantee.rolname=api_role
                           AND grantor.rolname=scheduler_role
                           AND acl.privilege_type='INSERT'
                           AND NOT acl.is_grantable)=1
                     AND pg_catalog.count(*) FILTER (WHERE
                           grantee.rolname=scheduler_role
                           AND grantor.rolname=scheduler_role
                           AND acl.privilege_type='SELECT'
                           AND NOT acl.is_grantable)=1
                     AND pg_catalog.count(*) FILTER (WHERE
                           grantee.rolname=scheduler_role
                           AND grantor.rolname=scheduler_role
                           AND acl.privilege_type='UPDATE'
                           AND NOT acl.is_grantable)=1
             FROM pg_catalog.pg_class relation
             CROSS JOIN LATERAL pg_catalog.aclexplode(relation.relacl) acl
             LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
             LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
            WHERE relation.oid='public.activity_logs'::pg_catalog.regclass)
          IS NOT TRUE
       OR EXISTS (
            SELECT 1
              FROM pg_catalog.pg_attribute attribute
              CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
             WHERE attribute.attrelid='public.activity_logs'::pg_catalog.regclass
               AND attribute.attnum>0 AND NOT attribute.attisdropped
               AND acl.privilege_type='UPDATE')
       OR (SELECT pg_catalog.count(*)
             FROM timescaledb_information.hypertables hypertable
            WHERE hypertable.hypertable_schema='public'
              AND hypertable.hypertable_name='activity_logs'
              AND hypertable.compression_enabled)<>1
       OR (SELECT pg_catalog.count(*)
             FROM timescaledb_information.jobs job
            WHERE job.hypertable_schema='public'
              AND job.hypertable_name='activity_logs'
              AND job.proc_name='policy_compression'
              AND job.scheduled
              AND job.config->>'compress_after'='7 days')<>1
       OR EXISTS (
            SELECT 1
              FROM _timescaledb_catalog.hypertable source
              JOIN _timescaledb_catalog.hypertable compressed
                ON compressed.id=source.compressed_hypertable_id
              JOIN pg_catalog.pg_namespace namespace
                ON namespace.nspname=compressed.schema_name
              JOIN pg_catalog.pg_class relation
                ON relation.relnamespace=namespace.oid
               AND relation.relname=compressed.table_name
             WHERE source.schema_name='public'
               AND source.table_name='activity_logs'
               AND pg_catalog.pg_get_userbyid(relation.relowner)
                   IS DISTINCT FROM scheduler_role) THEN
        RAISE EXCEPTION 'principal retention terminal contract rejected'
            USING ERRCODE='42501';
    END IF;
    IF pg_catalog.to_regnamespace('saydin_principal_retention_control') IS NOT NULL THEN
        RAISE EXCEPTION 'principal retention transition not consumed'
            USING ERRCODE='42501';
    END IF;
END;
$terminal_contract$;

COMMENT ON TRIGGER trg_users_principal_retention_redact ON public.users IS
    'Redacts retained activity rows before the users foreign key is checked.';

COMMIT;
